using System.IO;

namespace LightingControl;

/// <summary>Settings this app writes into the mouse's onboard profiles.</summary>
/// <param name="ReportRateHz">125, 250, 500 or 1000.</param>
/// <param name="Dpi">Stored in the profile's default DPI slot.</param>
/// <param name="Led">Color and effect for both LED zones; null leaves the stored LEDs untouched.</param>
public sealed record OnboardSettings(int ReportRateHz, int Dpi, OnboardLed? Led);

public sealed record OnboardLed(LightEffect Effect, byte R, byte G, byte B, double Brightness, double Speed);

/// <summary>
/// Reads and writes onboard profiles through HID++ feature 0x8100 (profile format 4, as on the PRO X Wireless).
/// Every sector is checked against its CRC before it is modified, and read back after writing.
/// Values stored here are what the mouse uses in onboard mode, i.e. whenever G HUB is not running.
/// </summary>
public static class HidppOnboard
{
    private const ushort OnboardProfiles = 0x8100;
    private const byte FnGetInfo = 0, FnMemoryRead = 5, FnMemoryAddrWrite = 6, FnMemoryWrite = 7, FnMemoryWriteEnd = 8;
    private const int SupportedProfileFormat = 4;

    // Profile layout (format 4).
    private const int ReportRateOffset = 0, DefaultDpiIndexOffset = 1, DpiListOffset = 3, DpiSlots = 5;
    private const int LedOffset = 208, LedSize = 11, LedCount = 2;

    // LED modes inside the profile.
    private const byte LedOff = 0x00, LedFixed = 0x01, LedCycle = 0x03, LedBreathing = 0x0A;

    public static readonly int[] ReportRates = { 125, 250, 500, 1000 };

    public static string BackupDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightingControl", "onboard-backup");

    /// <summary>Writes <paramref name="settings"/> into every enabled onboard profile. Returns a user-facing summary.</summary>
    public static string Save(string devicePath, byte index, OnboardSettings settings)
    {
        lock (HidppChannel.Gate)
        {
            using var channel = HidppChannel.Open(devicePath)
                ?? throw new IOException("O mouse não está acessível.");
            var feature = channel.Feature(index, OnboardProfiles)
                ?? throw new IOException("O mouse não tem memória de perfis (0x8100).");

            var info = Require(channel.Request(index, feature, FnGetInfo), "ler as informações da memória");
            if (info[5] != SupportedProfileFormat)
                throw new IOException($"Formato de perfil {info[5]} não suportado (esperado {SupportedProfileFormat}).");
            int sectorSize = info[11] << 8 | info[12];

            var memory = new Memory(channel, index, feature, sectorSize);
            var directory = memory.ReadVerified(0x0000);

            int written = 0;
            for (int i = 0; i + 3 < sectorSize - 2; i += 4)
            {
                if (directory[i] == 0xFF && directory[i + 1] == 0xFF)
                    break;  // end of the profile directory
                ushort sector = (ushort)(directory[i] << 8 | directory[i + 1]);
                bool enabled = directory[i + 2] != 0;
                if (!enabled)
                    continue;

                var profile = memory.ReadVerified(sector);
                Backup(sector, profile);
                Apply(profile, settings);
                memory.WriteVerified(sector, profile);
                written++;
            }

            if (written == 0)
                throw new IOException("Nenhum perfil ativo encontrado na memória do mouse.");
            return $"Gravado na memória do mouse ({written} perfil(is)): {settings.ReportRateHz} Hz, {settings.Dpi} DPI" +
                   (settings.Led is null ? "." : ", iluminação.");
        }
    }

    /// <summary>Writes back every sector saved in <see cref="BackupDirectory"/> (the state before this app's first write).</summary>
    public static string RestoreBackups(string devicePath, byte index)
    {
        var files = Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, "sector_*.bin")
            : Array.Empty<string>();
        if (files.Length == 0)
            throw new IOException("Não há backup da memória do mouse.");

        lock (HidppChannel.Gate)
        {
            using var channel = HidppChannel.Open(devicePath)
                ?? throw new IOException("O mouse não está acessível.");
            var feature = channel.Feature(index, OnboardProfiles)
                ?? throw new IOException("O mouse não tem memória de perfis (0x8100).");
            var info = Require(channel.Request(index, feature, FnGetInfo), "ler as informações da memória");
            int sectorSize = info[11] << 8 | info[12];
            var memory = new Memory(channel, index, feature, sectorSize);

            foreach (var file in files)
            {
                var data = File.ReadAllBytes(file);
                if (data.Length != sectorSize || !memory.HasValidCrc(data))
                    throw new IOException($"Backup {Path.GetFileName(file)} inválido; nada foi restaurado a partir dele.");
                var sector = Convert.ToUInt16(Path.GetFileNameWithoutExtension(file)["sector_".Length..], 16);
                memory.WriteVerified(sector, data);
            }
            return $"Memória do mouse restaurada ({files.Length} setor(es)).";
        }
    }

    private static void Apply(byte[] profile, OnboardSettings s)
    {
        profile[ReportRateOffset] = (byte)(1000 / s.ReportRateHz);  // stored as the polling interval in ms

        int slot = profile[DefaultDpiIndexOffset];
        if (slot >= DpiSlots)
            slot = 0;
        int dpiOffset = DpiListOffset + 2 * slot;
        profile[dpiOffset] = (byte)s.Dpi;           // little-endian
        profile[dpiOffset + 1] = (byte)(s.Dpi >> 8);

        if (s.Led is not { } led)
            return;
        var bytes = EncodeLed(led);
        for (int zone = 0; zone < LedCount; zone++)
            bytes.CopyTo(profile, LedOffset + zone * LedSize);
    }

    private static byte[] EncodeLed(OnboardLed led)
    {
        var b = new byte[LedSize];
        byte intensity = (byte)Math.Clamp(Math.Round(led.Brightness * 100), 1, 100);
        // Matches the app's own effects: a full breath or hue cycle every 2/speed and 6/speed seconds.
        ushort Period(double seconds) => (ushort)Math.Clamp(seconds / Math.Max(led.Speed, 0.1) * 1000, 1000, 20000);

        switch (led.Effect)
        {
            case LightEffect.Off:
                b[0] = LedOff;
                break;
            case LightEffect.Solid:
                b[0] = LedFixed;
                b[1] = Scale(led.R, led.Brightness); b[2] = Scale(led.G, led.Brightness); b[3] = Scale(led.B, led.Brightness);
                break;
            case LightEffect.Rainbow:
                b[0] = LedCycle;
                var cycle = Period(6);
                b[6] = (byte)(cycle >> 8); b[7] = (byte)cycle;  // big-endian
                b[8] = intensity;
                break;
            case LightEffect.Breathing:
                b[0] = LedBreathing;
                b[1] = led.R; b[2] = led.G; b[3] = led.B;
                var breath = Period(2);
                b[4] = (byte)(breath >> 8); b[5] = (byte)breath;
                b[6] = 0;  // default waveform
                b[7] = intensity;
                break;
        }
        return b;
    }

    private static byte Scale(byte channel, double k) => (byte)Math.Round(channel * Math.Clamp(k, 0, 1));

    private static void Backup(ushort sector, byte[] data)
    {
        // Keep the first copy of each sector: that is the state before this app ever wrote to it.
        Directory.CreateDirectory(BackupDirectory);
        var path = Path.Combine(BackupDirectory, $"sector_{sector:X4}.bin");
        if (!File.Exists(path))
            File.WriteAllBytes(path, data);
    }

    private static byte[] Require(byte[]? reply, string what) =>
        reply ?? throw new IOException($"O mouse não respondeu ao {what}.");

    /// <summary>Sector access with CRC-CCITT (0xFFFF seed) in the last two bytes, big-endian.</summary>
    private sealed class Memory(HidppChannel channel, byte index, byte feature, int sectorSize)
    {
        public byte[] ReadVerified(ushort sector)
        {
            var data = Read(sector);
            if (!CrcMatches(data))
                throw new IOException($"Setor {sector:X4} com checksum inválido; nada foi gravado.");
            return data;
        }

        public void WriteVerified(ushort sector, byte[] data)
        {
            ushort crc = Crc(data, sectorSize - 2);
            data[sectorSize - 2] = (byte)(crc >> 8);
            data[sectorSize - 1] = (byte)crc;

            Require(channel.Request(index, feature, FnMemoryAddrWrite,
                (byte)(sector >> 8), (byte)sector, 0, 0, (byte)(sectorSize >> 8), (byte)sectorSize), "início da gravação");
            for (int offset = 0; offset < sectorSize; offset += 16)
            {
                var chunk = new byte[16];
                Array.Copy(data, offset, chunk, 0, Math.Min(16, sectorSize - offset));
                Require(channel.Request(index, feature, FnMemoryWrite, chunk), "envio dos dados");
            }
            Require(channel.Request(index, feature, FnMemoryWriteEnd), "fim da gravação");

            var readBack = Read(sector);
            if (!readBack.AsSpan().SequenceEqual(data))
                throw new IOException($"A verificação do setor {sector:X4} falhou depois da gravação.");
        }

        private byte[] Read(ushort sector)
        {
            var data = new byte[sectorSize];
            for (int offset = 0; offset < sectorSize; offset += 16)
            {
                // Reads return 16 bytes and must stay inside the sector, so the last one overlaps the previous.
                int o = Math.Min(offset, sectorSize - 16);
                var reply = Require(channel.Request(index, feature, FnMemoryRead,
                    (byte)(sector >> 8), (byte)sector, (byte)(o >> 8), (byte)o), $"leitura do setor {sector:X4}");
                Array.Copy(reply, 4, data, o, 16);
            }
            return data;
        }

        public bool HasValidCrc(byte[] data) => CrcMatches(data);

        private bool CrcMatches(byte[] data) =>
            Crc(data, sectorSize - 2) == (data[sectorSize - 2] << 8 | data[sectorSize - 1]);

        private static ushort Crc(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1);
            }
            return crc;
        }
    }
}

using System.IO;

namespace LightingControl;

public sealed record DpiInfo(int Current, int Min, int Max, int Step);

/// <summary>Reads and sets sensor 0's resolution through HID++ feature 0x2201 (Adjustable DPI).</summary>
public static class HidppDpi
{
    private const ushort AdjustableDpi = 0x2201;
    private const byte Sensor = 0;
    private const ushort StepMarker = 0xE000;

    public static DpiInfo? Read(string devicePath, byte index)
    {
        lock (HidppChannel.Gate)
            return ReadUnlocked(devicePath, index);
    }

    private static DpiInfo? ReadUnlocked(string devicePath, byte index)
    {
        try
        {
            using var channel = HidppChannel.Open(devicePath);
            if (channel?.Feature(index, AdjustableDpi) is not { } feature)
                return null;

            // getSensorDpiList: big-endian u16 values after the sensor byte, 0-terminated.
            // 0xE000|n is a step marker: the values around it form a range with step n.
            if (channel.Request(index, feature, 1, Sensor) is not { } list)
                return null;
            var values = new List<int>();
            for (int i = 5; i + 1 < list.Length; i += 2)
            {
                int v = list[i] << 8 | list[i + 1];
                if (v == 0) break;
                values.Add(v);
            }
            if (values.Count == 0)
                return null;
            int step = values.FirstOrDefault(v => (v & StepMarker) == StepMarker) & ~StepMarker;
            var dpis = values.Where(v => (v & StepMarker) != StepMarker).ToList();

            // getSensorDpi: [sensor, current hi, lo, default hi, lo]
            if (channel.Request(index, feature, 2, Sensor) is not { } current)
                return null;
            return new DpiInfo(current[5] << 8 | current[6], dpis.Min(), dpis.Max(), step > 0 ? step : 50);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Applies a DPI value; returns the value the mouse reports afterwards, or null on failure.</summary>
    public static int? Set(string devicePath, byte index, int dpi)
    {
        lock (HidppChannel.Gate)
            return SetUnlocked(devicePath, index, dpi);
    }

    private static int? SetUnlocked(string devicePath, byte index, int dpi)
    {
        try
        {
            using var channel = HidppChannel.Open(devicePath);
            if (channel?.Feature(index, AdjustableDpi) is not { } feature)
                return null;
            if (channel.Request(index, feature, 3, Sensor, (byte)(dpi >> 8), (byte)dpi) is null)
                return null;
            return channel.Request(index, feature, 2, Sensor) is { } r ? r[5] << 8 | r[6] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return null;
        }
    }
}

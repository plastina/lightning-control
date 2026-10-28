using System.IO;

namespace LightingControl;

/// <summary>
/// Stores a lighting effect in a headset's own memory through HID++ feature 0x8070 (Color LED Effects).
/// The device shows the stored effect whenever no software (G HUB or this app) is driving its LEDs.
/// </summary>
public static class HidppLedMemory
{
    private const ushort ColorLedEffects = 0x8070;
    private const byte FnGetInfo = 0, FnGetZoneInfo = 1, FnGetEffectInfo = 2, FnSetZoneEffect = 3, FnGetZoneEffect = 14;

    // setZoneEffect's last byte: 0x02 writes the effect to non-volatile memory (read back with selector 1).
    private const byte Persist = 0x02, StoredSelector = 1;

    private const ushort EffectOff = 0x0000, EffectFixed = 0x0001, EffectCycle = 0x0003, EffectBreathing = 0x000A;

    // Same last byte, other values: 0x00 shows the effect now without storing it.
    private const byte ShowNow = 0x00;

    /// <summary>Shows an effect immediately, without touching what the device has stored.</summary>
    public static bool Show(string devicePath, byte index, OnboardLed led)
    {
        lock (HidppChannel.Gate)
        {
            try
            {
                using var channel = HidppChannel.Open(devicePath);
                if (channel?.Feature(index, ColorLedEffects) is not { } feature
                    || channel.Request(index, feature, FnGetInfo) is not { } info)
                    return false;
                return SetAllZones(channel, index, feature, info[4], led, ShowNow);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>The effect stored for zone 0, so the app can open showing what the device already has.</summary>
    public static OnboardLed? ReadStored(string devicePath, byte index)
    {
        lock (HidppChannel.Gate)
        {
            try
            {
                using var channel = HidppChannel.Open(devicePath);
                if (channel?.Feature(index, ColorLedEffects) is not { } feature
                    || channel.Request(index, feature, FnGetZoneEffect, 0, StoredSelector) is not { } stored
                    || channel.Request(index, feature, FnGetEffectInfo, 0, stored[5]) is not { } effect)
                    return null;

                var p = stored.AsSpan(6, 10);
                double Speed(int periodMs, double baseSeconds) => periodMs > 0 ? baseSeconds * 1000 / periodMs : 1;
                return (effect[6] << 8 | effect[7]) switch
                {
                    EffectOff => new OnboardLed(LightEffect.Off, 0, 184, 252, 1, 1),
                    EffectBreathing => new OnboardLed(LightEffect.Breathing, p[0], p[1], p[2],
                        Math.Clamp(p[6] / 100.0, 0.01, 1), Speed(p[3] << 8 | p[4], 2)),
                    EffectCycle => new OnboardLed(LightEffect.Rainbow, 0, 184, 252,
                        Math.Clamp(p[7] / 100.0, 0.01, 1), Speed(p[5] << 8 | p[6], 6)),
                    _ => new OnboardLed(LightEffect.Solid, p[0], p[1], p[2], 1, 1),
                };
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private static bool SetAllZones(HidppChannel channel, byte index, byte feature, int zones, OnboardLed led, byte persistence)
    {
        var parameters = EncodeParameters(led);
        ushort effectId = EffectIdOf(led.Effect);
        for (byte zone = 0; zone < zones; zone++)
        {
            var args = new byte[13];
            args[0] = zone;
            args[1] = FindEffectIndex(channel, index, feature, zone, effectId);
            parameters.CopyTo(args, 2);
            args[12] = persistence;
            if (channel.Request(index, feature, FnSetZoneEffect, args) is null)
                return false;
        }
        return true;
    }

    private static ushort EffectIdOf(LightEffect effect) => effect switch
    {
        LightEffect.Off => EffectOff,
        LightEffect.Breathing => EffectBreathing,
        LightEffect.Rainbow => EffectCycle,
        _ => EffectFixed,
    };

    public static string Save(string devicePath, byte index, OnboardLed led)
    {
        lock (HidppChannel.Gate)
        {
            using var channel = HidppChannel.Open(devicePath)
                ?? throw new IOException("O headset não está acessível.");
            var feature = channel.Feature(index, ColorLedEffects)
                ?? throw new IOException("O headset não tem a função de LED (0x8070).");
            var info = Require(channel.Request(index, feature, FnGetInfo), "ler as zonas de LED");
            int zones = info[4];

            ushort effectId = EffectIdOf(led.Effect);
            var parameters = EncodeParameters(led);

            for (byte zone = 0; zone < zones; zone++)
            {
                byte effectIndex = FindEffectIndex(channel, index, feature, zone, effectId);
                var args = new byte[13];
                args[0] = zone;
                args[1] = effectIndex;
                parameters.CopyTo(args, 2);
                args[12] = Persist;
                Require(channel.Request(index, feature, FnSetZoneEffect, args), $"gravação da zona {zone}");

                // Read the stored slot back: [zone, effect index, parameters...]
                var stored = Require(channel.Request(index, feature, FnGetZoneEffect, zone, StoredSelector), $"verificação da zona {zone}");
                if (stored[5] != effectIndex || !stored.AsSpan(6, 3).SequenceEqual(parameters.AsSpan(0, 3)))
                    throw new IOException($"A zona {zone} não confirmou o efeito gravado.");
            }
            return $"Gravado no headset ({zones} zona(s)): {Describe(led)}. Vale quando nem o G HUB nem o app controlam a luz.";
        }
    }

    /// <summary>Effects are addressed by their position in each zone's list, so look the id up.</summary>
    private static byte FindEffectIndex(HidppChannel channel, byte index, byte feature, byte zone, ushort effectId)
    {
        var zoneInfo = Require(channel.Request(index, feature, FnGetZoneInfo, zone), $"ler a zona {zone}");
        int effectCount = zoneInfo[7];  // [zone, location hi, lo, effect count, ...]
        for (byte e = 0; e < effectCount; e++)
        {
            var effect = Require(channel.Request(index, feature, FnGetEffectInfo, zone, e), $"ler os efeitos da zona {zone}");
            if ((effect[6] << 8 | effect[7]) == effectId)  // [zone, index, effect id hi, lo, ...]
                return e;
        }
        throw new IOException($"A zona {zone} não suporta esse efeito.");
    }

    /// <summary>Ten parameter bytes, laid out per effect as in the mouse's onboard profiles.</summary>
    private static byte[] EncodeParameters(OnboardLed led)
    {
        var p = new byte[10];
        byte intensity = (byte)Math.Clamp(Math.Round(led.Brightness * 100), 1, 100);
        ushort Period(double seconds) => (ushort)Math.Clamp(seconds / Math.Max(led.Speed, 0.1) * 1000, 1000, 20000);

        switch (led.Effect)
        {
            case LightEffect.Solid:
                p[0] = Scale(led.R, led.Brightness); p[1] = Scale(led.G, led.Brightness); p[2] = Scale(led.B, led.Brightness);
                break;
            case LightEffect.Breathing:
                p[0] = led.R; p[1] = led.G; p[2] = led.B;
                var breath = Period(2);
                p[3] = (byte)(breath >> 8); p[4] = (byte)breath;
                p[5] = 0;  // default waveform
                p[6] = intensity;
                break;
            case LightEffect.Rainbow:
                var cycle = Period(6);
                p[5] = (byte)(cycle >> 8); p[6] = (byte)cycle;
                p[7] = intensity;
                break;
        }
        return p;
    }

    private static string Describe(OnboardLed led) => led.Effect switch
    {
        LightEffect.Off => "luz desligada",
        LightEffect.Rainbow => "ciclo de cores",
        LightEffect.Breathing => $"respiração #{led.R:X2}{led.G:X2}{led.B:X2}",
        _ => $"cor fixa #{led.R:X2}{led.G:X2}{led.B:X2}",
    };

    private static byte Scale(byte channel, double k) => (byte)Math.Round(channel * Math.Clamp(k, 0, 1));

    private static byte[] Require(byte[]? reply, string what) =>
        reply ?? throw new IOException($"O headset não respondeu à {what}.");
}

using System.IO;

namespace LightingControl;

public enum HidppDeviceType { Keyboard = 0, Mouse = 3, Headset = 8, Other = -1 }

/// <param name="DevicePath">HID interface the device answered on, reused for other HID++ features.</param>
/// <param name="Index">HID++ device index: 0xFF for direct connections, 1..6 for receiver slots.</param>
public sealed record BatteryReading(
    HidppDeviceType Type, string Name, int Percent, bool IsCharging, bool IsEstimate, int? Millivolts,
    string DevicePath, byte Index);

/// <summary>Reads battery state from Logitech devices over HID++ 2.0.</summary>
public static class HidppBattery
{
    private const ushort BatteryStatus = 0x1000, UnifiedBattery = 0x1004, AdcMeasurement = 0x1F20;

    // Direct-connected devices answer at 0xFF; receivers host paired devices at 1..6.
    private static readonly byte[] DeviceIndices = { 0xFF, 1, 2, 3, 4, 5, 6 };

    // Li-ion discharge curve (mV -> %) for headsets that only report voltage.
    private static readonly (int Mv, int Pct)[] VoltageCurve =
    {
        (4150, 100), (4050, 90), (3990, 80), (3940, 70), (3890, 60), (3850, 50),
        (3820, 40), (3790, 30), (3750, 20), (3700, 10), (3600, 5), (3300, 0),
    };

    public static List<BatteryReading> ReadAll()
    {
        lock (HidppChannel.Gate)
            return ReadAllUnlocked();
    }

    private static List<BatteryReading> ReadAllUnlocked()
    {
        var results = new List<BatteryReading>();
        var interfaces = HidppChannel.LongInterfaces().ToList();
        Diag.Log($"ReadAll: {interfaces.Count} long interface(s)");
        foreach (var hid in interfaces)
        {
            var tag = hid.DevicePath.Length > 40 ? hid.DevicePath[^40..] : hid.DevicePath;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Diag.Log($"  open {hid.ProductID:X4} ...{tag}");
                using var channel = new HidppChannel(hid);
                Diag.Log($"  opened in {timer.ElapsedMilliseconds} ms");
                foreach (var index in DeviceIndices)
                {
                    var step = timer.ElapsedMilliseconds;
                    var reading = Read(channel, index, hid.DevicePath);
                    Diag.Log($"    index {index}: {(reading is null ? "no answer" : reading.Type + "/" + reading.Name)} ({timer.ElapsedMilliseconds - step} ms)");
                    if (reading is not null)
                        results.Add(reading);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // Interface busy or removed; it is retried on the next poll.
                Diag.Log($"  interface failed: {ex.GetType().Name} {ex.Message}");
            }
        }
        return results;
    }

    private static BatteryReading? Read(HidppChannel channel, byte index, string path)
    {
        // Root feature lookups double as a presence check for this index.
        var nameFeature = channel.Feature(index, HidppChannel.DeviceNameFeature);
        if (nameFeature is null)
            return null;

        var type = channel.Request(index, nameFeature.Value, 2) is { } t ? (HidppDeviceType)t[4] : HidppDeviceType.Other;
        if (!Enum.IsDefined(type))
            type = HidppDeviceType.Other;
        var name = channel.ReadName(index, nameFeature.Value);

        if (channel.Feature(index, UnifiedBattery) is { } ub && channel.Request(index, ub, 1) is { } s)
            // [state of charge %, level flags, charging status (0 = discharging), external power]
            return new BatteryReading(type, name, s[4], s[6] is 1 or 2, false, null, path, index);

        if (channel.Feature(index, BatteryStatus) is { } bs && channel.Request(index, bs, 0) is { } b)
            // [discharge level %, next level, status (0 = discharging)]
            return new BatteryReading(type, name, b[4], b[6] is 1 or 2 or 3, false, null, path, index);

        if (channel.Feature(index, AdcMeasurement) is { } adc && channel.Request(index, adc, 0) is { } a)
        {
            // [millivolts hi, lo, flags (bit 1 = charging)]
            int mv = a[4] << 8 | a[5];
            return new BatteryReading(type, name, VoltageToPercent(mv), (a[6] & 0x02) != 0, true, mv, path, index);
        }
        return null;
    }

    private static int VoltageToPercent(int mv)
    {
        if (mv >= VoltageCurve[0].Mv) return 100;
        for (int i = 1; i < VoltageCurve.Length; i++)
        {
            var (hiMv, hiPct) = VoltageCurve[i - 1];
            var (loMv, loPct) = VoltageCurve[i];
            if (mv >= loMv)
                return loPct + (hiPct - loPct) * (mv - loMv) / (hiMv - loMv);
        }
        return 0;
    }
}

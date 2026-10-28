using System.IO;

namespace LightingControl;

public sealed record ReportRateInfo(int CurrentHz, IReadOnlyList<int> SupportedHz);

/// <summary>Live polling rate through HID++ feature 0x8060 (Report Rate). Rates travel as intervals in ms.</summary>
public static class HidppReportRate
{
    private const ushort ReportRate = 0x8060;
    private const byte FnGetList = 0, FnGet = 1, FnSet = 2;

    public static ReportRateInfo? Read(string devicePath, byte index)
    {
        lock (HidppChannel.Gate)
        {
            try
            {
                using var channel = HidppChannel.Open(devicePath);
                if (channel?.Feature(index, ReportRate) is not { } feature
                    || channel.Request(index, feature, FnGetList) is not { } list
                    || channel.Request(index, feature, FnGet) is not { } current)
                    return null;

                // Bit n set = an interval of (n + 1) ms is supported.
                var supported = Enumerable.Range(0, 8)
                    .Where(bit => (list[4] & 1 << bit) != 0)
                    .Select(bit => 1000 / (bit + 1))
                    .OrderBy(hz => hz)
                    .ToList();
                return new ReportRateInfo(1000 / Math.Max(1, (int)current[4]), supported);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>Applies a rate until the mouse powers off; returns the rate it reports afterwards.</summary>
    public static int? Set(string devicePath, byte index, int hz)
    {
        lock (HidppChannel.Gate)
        {
            try
            {
                using var channel = HidppChannel.Open(devicePath);
                if (channel?.Feature(index, ReportRate) is not { } feature
                    || channel.Request(index, feature, FnSet, (byte)(1000 / hz)) is null
                    || channel.Request(index, feature, FnGet) is not { } current)
                    return null;
                return 1000 / Math.Max(1, (int)current[4]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                return null;
            }
        }
    }
}

using System.IO;

namespace LightingControl;

/// <summary>Tells whether a Logitech device has lighting a normal app can control.</summary>
public static class HidppLighting
{
    private const ushort ColorLedEffects = 0x8070, RgbEffects = 0x8071;

    // getFeature flags byte: bit 6 = hidden, bit 5 = engineering-only.
    private const byte HiddenOrEngineering = 0x60;

    /// <summary>
    /// True when the device exposes a visible LED feature. Mice like the PRO X SUPERLIGHT carry 0x8070 only as a
    /// hidden engineering feature for their DPI indicator, and have no user-controllable lighting.
    /// </summary>
    public static bool? HasUserLighting(string devicePath, byte index)
    {
        lock (HidppChannel.Gate)
        {
            try
            {
                using var channel = HidppChannel.Open(devicePath);
                if (channel is null)
                    return null;
                foreach (var id in new[] { ColorLedEffects, RgbEffects })
                {
                    var reply = channel.Request(index, 0x00, 0, (byte)(id >> 8), (byte)id);
                    if (reply is null)
                        return null;  // no answer: unknown, keep the current state
                    if (reply[4] != 0 && (reply[5] & HiddenOrEngineering) == 0)
                        return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                return null;
            }
        }
    }
}

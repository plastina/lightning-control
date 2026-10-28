using System.Collections.Concurrent;
using System.Text;
using HidSharp;

namespace LightingControl;

/// <summary>
/// HID++ 2.0 request/response channel on a Logitech vendor "long report" interface (report 0x11, 20 bytes).
/// Works alongside G HUB: every request carries its own software id and only the matching reply is taken.
/// </summary>
internal sealed class HidppChannel : IDisposable
{
    public const int LogitechVid = 0x046D;
    public const ushort DeviceNameFeature = 0x0005;

    /// <summary>
    /// Serializes HID++ conversations from this app: every open handle sees every reply, so two channels
    /// talking at once could take each other's replies when their software ids happen to match.
    /// </summary>
    public static readonly object Gate = new();

    private const byte LongReport = 0x11;

    private readonly HidStream _stream;
    private readonly BlockingCollection<byte[]> _replies = new();
    private int _swid;

    /// <summary>Unsolicited reports (software id 0), e.g. button events; raised on the reader thread.</summary>
    public event Action<byte[]>? Notification;

    public HidppChannel(HidDevice hid)
    {
        _stream = hid.Open();
        _stream.ReadTimeout = 500;
        new Thread(Pump) { IsBackground = true }.Start();
    }

    /// <summary>Long-report interfaces of every connected Logitech HID device.</summary>
    public static IEnumerable<HidDevice> LongInterfaces() =>
        DeviceList.Local.GetHidDevices(LogitechVid).Where(h => h.GetMaxOutputReportLength() == 20);

    public static HidppChannel? Open(string devicePath) =>
        LongInterfaces().FirstOrDefault(h => h.DevicePath == devicePath) is { } hid ? new HidppChannel(hid) : null;

    /// <summary>Index of a feature on the device, or null when absent (or the device does not answer).</summary>
    public byte? Feature(byte index, ushort id) =>
        Request(index, 0x00, 0, (byte)(id >> 8), (byte)id) is { } r && r[4] != 0 ? r[4] : null;

    public string ReadName(byte index, byte nameFeature)
    {
        if (Request(index, nameFeature, 0) is not { } lenReply)
            return "";
        var name = new StringBuilder();
        for (int offset = 0; offset < lenReply[4]; offset += 16)
        {
            if (Request(index, nameFeature, 1, (byte)offset) is not { } chunk)
                break;
            name.Append(Encoding.ASCII.GetString(chunk, 4, Math.Min(16, lenReply[4] - offset)));
        }
        return name.ToString().TrimEnd('\0');
    }

    /// <summary>Sends one request; returns the 20-byte reply, or null on HID++ error or timeout.</summary>
    public byte[]? Request(byte index, byte feature, byte function, params byte[] args)
    {
        _swid = _swid % 15 + 1;
        var packet = new byte[20];
        packet[0] = LongReport;
        packet[1] = index;
        packet[2] = feature;
        packet[3] = (byte)(function << 4 | _swid);
        args.CopyTo(packet, 4);
        _stream.Write(packet);

        // Absent receiver slots answer with a short HID++ 1.0 error on another interface, so keep this short.
        var deadline = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < deadline)
        {
            if (!_replies.TryTake(out var reply, 100) || reply[1] != index)
                continue;
            if (reply[2] == feature && reply[3] == packet[3])
                return reply;
            if (reply[2] == 0xFF && reply[4] == packet[3])
                return null;  // HID++ error reply
        }
        return null;
    }

    private void Pump()
    {
        while (true)
        {
            var buffer = new byte[20];
            try
            {
                if (_stream.Read(buffer) < 7)
                    continue;
                // Replies echo our software id (1..15); the device sends its own events with id 0.
                if (buffer[2] != 0xFF && (buffer[3] & 0x0F) == 0)
                    Notification?.Invoke(buffer);
                else
                    _replies.Add(buffer);
            }
            catch (TimeoutException)
            {
            }
            catch
            {
                return;  // stream closed
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}

namespace LightingControl;

/// <summary>Logitech device driven through G HUB's LED SDK, addressed by device type and zone.</summary>
internal sealed class LogiSdkBackend : ILightBackend
{
    private readonly LogiLedSdk.DeviceType _type;
    private readonly int _zoneCount;
    private readonly bool _allDevices;
    private double _brightness = 1.0;
    private (int, int, int)? _lastSent;
    private bool _isAvailable = true;

    /// <param name="allDevices">
    /// Use the SDK's all-devices call instead of per-zone calls. G HUB accepts per-zone calls for the G733 but
    /// does not apply them, while the all-devices call does reach it.
    /// </param>
    public LogiSdkBackend(LogiLedSdk.DeviceType type, int zoneCount, bool allDevices = false)
    {
        _type = type;
        _zoneCount = zoneCount;
        _allDevices = allDevices;
    }

    public event EventHandler? AvailabilityChanged;

    public string Info => _allDevices ? "G HUB LED SDK · todos os aparelhos RGB" : $"G HUB LED SDK · {_zoneCount} zona(s)";

    public bool IsAvailable => _isAvailable;

    public void SetBrightness(double level)
    {
        _brightness = level;
        _lastSent = null;
    }

    public void SetColor(byte r, byte g, byte b)
    {
        // The SDK takes percentages, so brightness is folded into the color.
        var color = (Percent(r), Percent(g), Percent(b));
        if (color == _lastSent)
            return;

        bool ok = true;
        if (_allDevices)
            ok = LogiLedSdk.LogiLedSetLighting(color.Item1, color.Item2, color.Item3);
        else
            for (int zone = 0; zone < _zoneCount; zone++)
                ok &= LogiLedSdk.LogiLedSetLightingForTargetZone(_type, zone, color.Item1, color.Item2, color.Item3);

        _lastSent = ok ? color : null;
        if (ok != _isAvailable)
        {
            _isAvailable = ok;
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Percent(byte channel) => (int)Math.Round(channel / 255.0 * 100 * _brightness);
}

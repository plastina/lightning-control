namespace LightingControl;

/// <summary>
/// Drives a headset's LEDs directly over HID++ 0x8070, without G HUB. The device runs fixed, breathing and
/// color-cycle effects in firmware, so an effect is sent once per change instead of a color per frame.
/// </summary>
public sealed class HidppLedBackend : INativeEffectBackend
{
    private readonly string _devicePath;
    private readonly byte _index;
    private OnboardLed? _lastSent;
    private OnboardLed? _pending;
    private Task _writer = Task.CompletedTask;
    private bool _isAvailable = true;

    public HidppLedBackend(string devicePath, byte index)
    {
        _devicePath = devicePath;
        _index = index;
    }

    public event EventHandler? AvailabilityChanged;

    public string Info => "Controle direto (HID++)";

    public bool IsAvailable => _isAvailable;

    // Brightness travels inside the effect.
    public void SetBrightness(double level) { }

    public void SetColor(byte r, byte g, byte b) => ApplyEffect(new OnboardLed(LightEffect.Solid, r, g, b, 1, 1));

    public void ApplyEffect(OnboardLed led)
    {
        if (led == _lastSent || led == _pending)
            return;
        _pending = led;
        if (!_writer.IsCompleted)
            return;  // the running write picks up the newest effect when it finishes

        _writer = Task.Run(() =>
        {
            while (_pending is { } next && next != _lastSent)
            {
                bool ok = HidppLedMemory.Show(_devicePath, _index, next);
                _lastSent = ok ? next : null;
                if (ok || _isAvailable)
                    Diag.Log($"headset: Show({next.Effect} #{next.R:X2}{next.G:X2}{next.B:X2}) -> {(ok ? "ok" : "failed")}");
                if (ok != _isAvailable)
                {
                    _isAvailable = ok;
                    AvailabilityChanged?.Invoke(this, EventArgs.Empty);
                }
                if (!ok)
                    break;  // retried on the next change or frame
            }
            _pending = null;
        });
    }
}

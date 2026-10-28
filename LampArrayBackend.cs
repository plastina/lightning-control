using Windows.Devices.Lights;

namespace LightingControl;

/// <summary>Windows Dynamic Lighting device.</summary>
public sealed class LampArrayBackend : ILightBackend
{
    private readonly LampArray _lamp;

    public LampArrayBackend(LampArray lamp)
    {
        _lamp = lamp;
        lamp.AvailabilityChanged += (_, _) => AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? AvailabilityChanged;

    public string Info => $"Dynamic Lighting · {_lamp.LampCount} LED(s) · {_lamp.LampArrayKind}";

    public bool IsAvailable => _lamp.IsAvailable;

    public void SetBrightness(double level) => _lamp.BrightnessLevel = level;

    public void SetColor(byte r, byte g, byte b) => _lamp.SetColor(Windows.UI.Color.FromArgb(255, r, g, b));
}

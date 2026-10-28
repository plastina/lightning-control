namespace LightingControl;

/// <summary>Something that can show one color on a physical device.</summary>
public interface ILightBackend
{
    string Info { get; }
    bool IsAvailable { get; }
    event EventHandler? AvailabilityChanged;

    /// <summary>Applies the overall brightness, 0..1.</summary>
    void SetBrightness(double level);

    void SetColor(byte r, byte g, byte b);
}

/// <summary>A backend whose device runs effects itself: it receives the whole effect, not one color per frame.</summary>
public interface INativeEffectBackend : ILightBackend
{
    void ApplyEffect(OnboardLed led);
}

/// <summary>For devices without controllable LEDs.</summary>
public sealed class NoLightBackend : ILightBackend
{
    public string Info => "Sem iluminação controlável";
    public bool IsAvailable => true;
    public event EventHandler? AvailabilityChanged { add { } remove { } }
    public void SetBrightness(double level) { }
    public void SetColor(byte r, byte g, byte b) { }
}

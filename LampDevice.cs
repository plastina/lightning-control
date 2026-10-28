using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LightingControl;

public enum LightEffect { Solid, Breathing, Rainbow, Off }

/// <summary>One lighting device and the lighting settings chosen for it.</summary>
public sealed class LampDevice : INotifyPropertyChanged
{
    private byte _r = 0, _g = 184, _b = 252;
    private double _brightness = 1.0;
    private double _speed = 1.0;
    private LightEffect _effect = LightEffect.Solid;
    private bool _isAvailable;
    private string _name;
    private BatteryReading? _battery;
    private IReadOnlyList<ButtonMapping> _buttons = Array.Empty<ButtonMapping>();
    private IReadOnlyList<int> _reportRates = Array.Empty<int>();
    private int _reportRate;
    private bool _hasLighting = true;
    private bool _canSaveLighting;
    private DpiInfo? _dpiInfo;
    private double _dpi;

    public LampDevice(string id, string name, ILightBackend backend)
    {
        Id = id;
        _name = name;
        Backend = backend;
        _isAvailable = backend.IsAvailable;
        backend.AvailabilityChanged += (_, _) => IsAvailable = backend.IsAvailable;
        backend.SetBrightness(_brightness);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(); }
    }

    public ILightBackend Backend { get; }

    public bool HasBattery => _battery is not null;

    public double BatteryPercent => _battery?.Percent ?? 0;

    public string BatteryText => _battery switch
    {
        null => "",
        { IsCharging: true } b => $"{b.Percent}% · carregando",
        { IsEstimate: true, Millivolts: { } mv } b => $"~{b.Percent}% ({mv / 1000.0:0.00} V)",
        var b => $"{b.Percent}%",
    };

    public Brush BatteryBrush => new SolidColorBrush(_battery switch
    {
        { IsCharging: true } => Color.FromRgb(0x00, 0xB8, 0xFC),
        { Percent: <= 15 } => Color.FromRgb(0xFF, 0x45, 0x45),
        { Percent: <= 35 } => Color.FromRgb(0xFF, 0xB0, 0x20),
        _ => Color.FromRgb(0x3D, 0xDC, 0x84),
    });

    public IReadOnlyList<ButtonMapping> Buttons
    {
        get => _buttons;
        set { _buttons = value; OnChanged(); OnChanged(nameof(HasButtons)); }
    }

    public bool HasButtons => _buttons.Count > 0;

    /// <summary>Raised when the user picks a new polling rate (Hz).</summary>
    public event EventHandler<int>? ReportRateRequested;

    public bool HasReportRate => _reportRates.Count > 0;

    public IReadOnlyList<int> ReportRateOptions => _reportRates;

    public int ReportRate
    {
        get => _reportRate;
        set
        {
            if (value == _reportRate || !_reportRates.Contains(value))
                return;
            _reportRate = value;
            OnChanged();
            ReportRateRequested?.Invoke(this, value);
        }
    }

    /// <summary>Shows what the mouse reports, without raising <see cref="ReportRateRequested"/>.</summary>
    public void UpdateReportRate(ReportRateInfo info)
    {
        _reportRates = info.SupportedHz;
        _reportRate = info.CurrentHz;
        OnChanged(nameof(HasReportRate)); OnChanged(nameof(ReportRateOptions)); OnChanged(nameof(ReportRate));
    }

    public bool IncludeLightingOnSave { get; set; } = true;

    /// <summary>True when the lighting can be stored in the device itself (headsets with HID++ LED memory).</summary>
    public bool CanSaveLighting
    {
        get => _canSaveLighting;
        set { _canSaveLighting = value; OnChanged(); }
    }

    public OnboardLed CurrentLed => new(_effect, _r, _g, _b, _brightness, _speed);

    /// <summary>Loads lighting read from the device into the controls.</summary>
    public void LoadLed(OnboardLed led)
    {
        _effect = led.Effect;
        _r = led.R; _g = led.G; _b = led.B;
        _speed = Math.Clamp(led.Speed, 0.2, 4);
        Brightness = led.Brightness;
        OnColorChanged();
        OnChanged(nameof(Effect)); OnChanged(nameof(Speed)); OnChanged(nameof(SpeedText));
    }

    /// <summary>False for devices without controllable LEDs; their lighting is neither rendered nor saved.</summary>
    public bool HasLighting
    {
        get => _hasLighting;
        set { _hasLighting = value; OnChanged(); OnChanged(nameof(Preview)); }
    }

    /// <summary>The current settings as they would be stored in the mouse's onboard memory.</summary>
    public OnboardSettings ToOnboardSettings(bool includeLighting) => new(
        _reportRate, (int)_dpi,
        includeLighting && _hasLighting ? new OnboardLed(_effect, _r, _g, _b, _brightness, _speed) : null);

    /// <summary>Raised when the user picks a new DPI; the window applies it to the hardware.</summary>
    public event EventHandler<int>? DpiRequested;

    public bool HasDpi => _dpiInfo is not null;
    public double DpiMin => _dpiInfo?.Min ?? 100;
    public double DpiMax => _dpiInfo?.Max ?? 25600;
    public double DpiStep => _dpiInfo?.Step ?? 50;
    public string DpiRangeText => _dpiInfo is { } d ? $"{d.Min}–{d.Max} DPI, passos de {d.Step}" : "";

    public double Dpi
    {
        get => _dpi;
        set
        {
            var snapped = Math.Clamp(Math.Round(value / DpiStep) * DpiStep, DpiMin, DpiMax);
            if (snapped == _dpi)
                return;
            _dpi = snapped;
            OnChanged(); OnChanged(nameof(DpiText));
            DpiRequested?.Invoke(this, (int)snapped);
        }
    }

    public string DpiText
    {
        get => $"{_dpi:0}";
        set { if (int.TryParse(value, out var dpi)) Dpi = dpi; OnChanged(); }
    }

    /// <summary>Shows the range and the value the mouse reports, without raising <see cref="DpiRequested"/>.</summary>
    public void UpdateDpi(DpiInfo info)
    {
        _dpiInfo = info;
        _dpi = info.Current;
        foreach (var name in new[] { nameof(HasDpi), nameof(DpiMin), nameof(DpiMax), nameof(DpiStep),
                                     nameof(DpiRangeText), nameof(Dpi), nameof(DpiText) })
            OnChanged(name);
    }

    public void UpdateBattery(BatteryReading? reading)
    {
        _battery = reading;
        OnChanged(nameof(HasBattery)); OnChanged(nameof(BatteryPercent));
        OnChanged(nameof(BatteryText)); OnChanged(nameof(BatteryBrush));
    }

    public string Info => Backend.Info;

    public static LightEffect[] Effects { get; } = Enum.GetValues<LightEffect>();

    public bool IsAvailable
    {
        get => _isAvailable;
        // AvailabilityChanged is raised off the UI thread; WPF marshals scalar property notifications.
        private set { _isAvailable = value; OnChanged(); OnChanged(nameof(StatusText)); }
    }

    public string StatusText => IsAvailable ? "Controlando" : "Outro app está no controle";

    public double R { get => _r; set => SetChannel(ref _r, value); }
    public double G { get => _g; set => SetChannel(ref _g, value); }
    public double B { get => _b; set => SetChannel(ref _b, value); }

    public string Hex
    {
        get => $"#{_r:X2}{_g:X2}{_b:X2}";
        set
        {
            var text = value.Trim().TrimStart('#');
            if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, null, out var rgb))
                return;
            _r = (byte)(rgb >> 16);
            _g = (byte)(rgb >> 8);
            _b = (byte)rgb;
            OnColorChanged();
        }
    }

    // Devices without LEDs get a neutral accent instead of a color that no light shows.
    public Brush Preview => new SolidColorBrush(_hasLighting ? Color.FromRgb(_r, _g, _b) : Color.FromRgb(0x5A, 0x60, 0x70));

    public double Brightness
    {
        get => _brightness;
        set
        {
            _brightness = Math.Clamp(value, 0, 1);
            Backend.SetBrightness(_brightness);
            OnChanged();
            OnChanged(nameof(BrightnessText));
        }
    }

    public string BrightnessText => $"{_brightness * 100:0}%";

    public double Speed
    {
        get => _speed;
        set { _speed = value; OnChanged(); OnChanged(nameof(SpeedText)); }
    }

    public string SpeedText => $"{_speed:0.0}x";

    public LightEffect Effect
    {
        get => _effect;
        set { _effect = value; OnChanged(); }
    }

    /// <summary>Copies the user-facing settings from another device (used by sync mode).</summary>
    public void CopyFrom(LampDevice other)
    {
        _r = other._r; _g = other._g; _b = other._b;
        _speed = other._speed;
        _effect = other._effect;
        Brightness = other._brightness;
        OnColorChanged();
        OnChanged(nameof(Speed)); OnChanged(nameof(SpeedText)); OnChanged(nameof(Effect));
    }

    /// <summary>Pushes the frame for time <paramref name="t"/> (seconds) to every lamp of the device.</summary>
    public void Render(double t)
    {
        // LampArrays report availability changes; SDK devices are retried every frame.
        if (!_hasLighting || (!IsAvailable && Backend is LampArrayBackend))
            return;

        // Devices that animate in firmware get the effect itself; the backend sends it only when it changes.
        if (Backend is INativeEffectBackend native)
        {
            native.ApplyEffect(CurrentLed);
            return;
        }

        var (r, g, b) = _effect switch
        {
            LightEffect.Breathing => Scale(_r, _g, _b, 0.1 + 0.9 * (0.5 - 0.5 * Math.Cos(Math.PI * t * _speed))),
            LightEffect.Rainbow => HsvToRgb(t * _speed * 60 % 360),
            LightEffect.Off => ((byte)0, (byte)0, (byte)0),
            _ => (_r, _g, _b),
        };
        Backend.SetColor(r, g, b);
    }

    private static (byte, byte, byte) Scale(byte r, byte g, byte b, double k) =>
        ((byte)(r * k), (byte)(g * k), (byte)(b * k));

    private static (byte, byte, byte) HsvToRgb(double hue)
    {
        double x = 1 - Math.Abs(hue / 60 % 2 - 1);
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private void SetChannel(ref byte channel, double value, [CallerMemberName] string? name = null)
    {
        channel = (byte)Math.Clamp(Math.Round(value), 0, 255);
        OnColorChanged();
    }

    private void OnColorChanged()
    {
        OnChanged(nameof(R)); OnChanged(nameof(G)); OnChanged(nameof(B));
        OnChanged(nameof(Hex)); OnChanged(nameof(Preview));
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

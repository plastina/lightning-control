using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LightingControl;

public enum ButtonAction
{
    Default,
    ToggleMicMute,
    PlayPause,
    NextTrack,
    PreviousTrack,
    MuteSpeakers,
    Shortcut,
    LaunchProgram,
}

public sealed record ButtonActionOption(ButtonAction Action, string Label);

/// <summary>One remappable headset button (a G-key bit) and the action chosen for it.</summary>
public sealed class ButtonMapping : INotifyPropertyChanged
{
    private ButtonAction _action;
    private string _argument = "";

    public ButtonMapping(string key, string label, int bit)
    {
        Key = key;
        Label = label;
        Bit = bit;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ButtonActionOption[] Options { get; } =
    {
        new(ButtonAction.Default, "Padrão do headset"),
        new(ButtonAction.ToggleMicMute, "Mutar microfone (Windows)"),
        new(ButtonAction.PlayPause, "Play / Pause"),
        new(ButtonAction.NextTrack, "Próxima faixa"),
        new(ButtonAction.PreviousTrack, "Faixa anterior"),
        new(ButtonAction.MuteSpeakers, "Mutar som"),
        new(ButtonAction.Shortcut, "Atalho de teclado"),
        new(ButtonAction.LaunchProgram, "Abrir programa / arquivo"),
    };

    /// <summary>Stable name used in the settings file.</summary>
    public string Key { get; }
    public string Label { get; }

    /// <summary>Bit of the button in the G-key event bitmask.</summary>
    public int Bit { get; }

    public ButtonAction Action
    {
        get => _action;
        set { _action = value; OnChanged(); OnChanged(nameof(NeedsArgument)); OnChanged(nameof(ArgumentHint)); }
    }

    public string Argument
    {
        get => _argument;
        set { _argument = value; OnChanged(); }
    }

    public bool NeedsArgument => _action is ButtonAction.Shortcut or ButtonAction.LaunchProgram;

    public string ArgumentHint => _action == ButtonAction.Shortcut ? "ex.: Ctrl+Shift+M" : "caminho do .exe, arquivo ou URL";

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Takes over the headset's G-keys (HID++ feature 0x8010) while any button has a custom action, runs the
/// chosen actions on press, and hands the buttons back to the firmware when no mapping remains or on exit.
/// </summary>
public sealed class HeadsetButtonController : IDisposable
{
    private const ushort GKeyFeature = 0x8010;
    private const byte SetSoftwareControl = 2;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightingControl", "buttons.json");

    private readonly HidppChannel _channel;
    private readonly byte _index;
    private readonly byte _feature;
    private readonly Action<string> _report;
    private int _lastMask;
    private bool _softwareControl;

    private HeadsetButtonController(HidppChannel channel, byte index, byte feature, Action<string> report)
    {
        _channel = channel;
        _index = index;
        _feature = feature;
        _report = report;
        _channel.Notification += OnNotification;

        Buttons = new[]
        {
            new ButtonMapping("mute", "Botão de mudo", bit: 0),
            new ButtonMapping("power", "Botão de energia (clique rápido)", bit: 1),
        };
        LoadSettings();
        foreach (var button in Buttons)
            button.PropertyChanged += (_, _) => { SaveSettings(); ApplySoftwareControl(); };
        ApplySoftwareControl();
    }

    public IReadOnlyList<ButtonMapping> Buttons { get; }

    /// <summary>Opens the headset's G-key feature; null when the device does not have one.</summary>
    public static HeadsetButtonController? TryCreate(string devicePath, byte index, Action<string> report)
    {
        lock (HidppChannel.Gate)
        {
            var channel = HidppChannel.Open(devicePath);
            if (channel?.Feature(index, GKeyFeature) is not { } feature)
            {
                channel?.Dispose();
                return null;
            }
            return new HeadsetButtonController(channel, index, feature, report);
        }
    }

    private void ApplySoftwareControl()
    {
        bool wanted = Buttons.Any(b => b.Action != ButtonAction.Default);
        if (wanted == _softwareControl)
            return;
        lock (HidppChannel.Gate)
            _channel.Request(_index, _feature, SetSoftwareControl, (byte)(wanted ? 1 : 0));
        _softwareControl = wanted;
    }

    private void OnNotification(byte[] report)
    {
        // G-key event: function 0, byte 4 = bitmask of keys currently held.
        if (report[1] != _index || report[2] != _feature || report[3] >> 4 != 0)
            return;
        int mask = report[4];
        int pressed = mask & ~_lastMask;
        _lastMask = mask;

        foreach (var button in Buttons.Where(b => (pressed & 1 << b.Bit) != 0))
        {
            try
            {
                Run(button);
            }
            catch (Exception ex)
            {
                _report($"{button.Label}: falhou ({ex.Message}).");
            }
        }
    }

    private void Run(ButtonMapping button)
    {
        switch (button.Action)
        {
            case ButtonAction.Default:
                // Firmware no longer handles keys under software control; mute is the one default we can stand in for.
                if (button.Key == "mute")
                    goto case ButtonAction.ToggleMicMute;
                break;
            case ButtonAction.ToggleMicMute:
                _report(InputActions.ToggleMicrophoneMute() ? "Microfone mutado." : "Microfone ativado.");
                break;
            case ButtonAction.PlayPause:
                InputActions.PressKey(InputActions.VkMediaPlayPause);
                break;
            case ButtonAction.NextTrack:
                InputActions.PressKey(InputActions.VkMediaNext);
                break;
            case ButtonAction.PreviousTrack:
                InputActions.PressKey(InputActions.VkMediaPrev);
                break;
            case ButtonAction.MuteSpeakers:
                InputActions.PressKey(InputActions.VkVolumeMute);
                break;
            case ButtonAction.Shortcut:
                if (!InputActions.SendShortcut(button.Argument))
                    _report($"{button.Label}: atalho inválido \"{button.Argument}\".");
                break;
            case ButtonAction.LaunchProgram:
                InputActions.Launch(button.Argument);
                break;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, SavedMapping>>(File.ReadAllText(SettingsPath));
            foreach (var button in Buttons)
            {
                if (saved?.TryGetValue(button.Key, out var s) == true)
                {
                    button.Action = s.Action;
                    button.Argument = s.Argument ?? "";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _report("Configuração dos botões ilegível; usando o padrão.");
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var data = Buttons.ToDictionary(b => b.Key, b => new SavedMapping(b.Action, b.Argument));
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            _report("Não foi possível salvar a configuração dos botões.");
        }
    }

    private sealed record SavedMapping(ButtonAction Action, string? Argument);

    public void Dispose()
    {
        _channel.Notification -= OnNotification;
        if (_softwareControl)
        {
            // Hand the buttons back to the firmware, or mute stays dead until the headset restarts.
            lock (HidppChannel.Gate)
                _channel.Request(_index, _feature, SetSoftwareControl, 0);
        }
        _channel.Dispose();
    }
}

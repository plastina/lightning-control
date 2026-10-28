using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

namespace LightingControl;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LampDevice> _devices = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _frameTimer;
    private DeviceWatcher? _watcher;
    private bool _syncing;
    private bool _sdkActive;

    private const ushort LogitechVendorId = 0x046D;

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        _tray = CreateTrayIcon();
        StartupRegistration.RefreshPath();
        StartupBox.IsChecked = StartupRegistration.IsEnabled;
        _devices.CollectionChanged += (_, _) =>
        {
            // Keep a peripheral open on the right: the first one found, or the next one after a removal.
            if (DeviceList.SelectedItem is null && _devices.Count > 0)
                DeviceList.SelectedIndex = 0;
            UpdateEmptyState();
        };

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _frameTimer.Tick += (_, _) => RenderFrame();
        _frameTimer.Start();

        StartLogitechSdk();
        StartWatcher();
        _ = PollBatteryAsync(_shutdown.Token);
        Closed += (_, _) => Shutdown();
        Application.Current.SessionEnding += (_, _) => _exiting = true;
    }

    private readonly System.Windows.Forms.NotifyIcon _tray;
    private bool _exiting;

    private System.Windows.Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        menu.Items.Add("Sair", null, (_, _) => ExitApp());

        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = "Controle de Iluminação",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                ShowFromTray();
        };
        return tray;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// The X hides the window to the tray: the app has to keep running to hold the lighting and the headset
    /// buttons. "Sair" in the tray menu quits, and so does logoff/shutdown, which a cancelled close would block.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private readonly CancellationTokenSource _shutdown = new();

    private static readonly TimeSpan BatteryInterval = TimeSpan.FromSeconds(30);

    private (string Path, byte Index)? _mouseAddress;
    private int _pendingDpi;
    private Task _dpiWriter = Task.CompletedTask;

    /// <summary>Re-reads the mouse DPI, which G HUB can also change (profile switches, DPI buttons).</summary>
    private async Task RefreshDpiAsync(LampDevice mouse, BatteryReading reading, CancellationToken token)
    {
        bool firstTime = _mouseAddress is null;
        _mouseAddress = (reading.DevicePath, reading.Index);
        if (firstTime)
        {
            mouse.DpiRequested += (_, dpi) => QueueDpi(mouse, dpi);
            mouse.ReportRateRequested += (_, hz) => _ = ApplyReportRateAsync(hz);
        }

        if (await Task.Run(() => HidppReportRate.Read(reading.DevicePath, reading.Index), token) is { } rate)
            mouse.UpdateReportRate(rate);

        if (!_dpiWriter.IsCompleted)
            return;  // a user change is in flight; its read-back updates the card
        if (await Task.Run(() => HidppDpi.Read(reading.DevicePath, reading.Index), token) is { } info)
            mouse.UpdateDpi(info);
    }

    private async Task ApplyReportRateAsync(int hz)
    {
        if (_mouseAddress is not { } address)
            return;
        var reported = await Task.Run(() => HidppReportRate.Set(address.Path, address.Index, hz));
        StatusLine.Text = reported is { } r
            ? $"Taxa de resposta do mouse: {r} Hz."
            : "Não foi possível alterar a taxa de resposta (o mouse não respondeu).";
    }

    private readonly Dictionary<string, (string Path, byte Index)> _ledMemoryAddress = new();

    private async void SaveLighting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LampDevice device } button
            || !_ledMemoryAddress.TryGetValue(device.Id, out var address))
            return;

        var led = device.CurrentLed;
        button.IsEnabled = false;
        StatusLine.Text = "Gravando no aparelho...";
        try
        {
            StatusLine.Text = await Task.Run(() => HidppLedMemory.Save(address.Path, address.Index, led));
        }
        catch (IOException ex)
        {
            StatusLine.Text = $"Não foi gravado: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Without G HUB nothing else restores the user's choice, so the lighting shown at exit is stored in the
    /// device. Skipped with G HUB, which reapplies its own profile anyway. One write per exit, not per change.
    /// </summary>
    private void SaveLightingOnExit()
    {
        if (_sdkActive)
            return;
        foreach (var device in _devices.Where(d => d.CanSaveLighting && d.HasLighting))
        {
            if (!_ledMemoryAddress.TryGetValue(device.Id, out var address))
                continue;
            try
            {
                HidppLedMemory.Save(address.Path, address.Index, device.CurrentLed);
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"Saving lighting for {device.Name} failed: {ex.Message}");
            }
        }
    }

    private async void SaveOnboard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LampDevice mouse } button || _mouseAddress is not { } address)
            return;

        var settings = mouse.ToOnboardSettings(mouse.IncludeLightingOnSave);
        button.IsEnabled = false;
        StatusLine.Text = "Gravando na memória do mouse...";
        try
        {
            StatusLine.Text = await Task.Run(() => HidppOnboard.Save(address.Path, address.Index, settings));
        }
        catch (IOException ex)
        {
            StatusLine.Text = $"Nada foi gravado: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>Applies the newest requested DPI; requests made while a write is running collapse into one.</summary>
    private void QueueDpi(LampDevice mouse, int dpi)
    {
        _pendingDpi = dpi;
        if (!_dpiWriter.IsCompleted || _mouseAddress is not { } address)
            return;

        _dpiWriter = Task.Run(async () =>
        {
            int applied;
            int? reported;
            do
            {
                applied = _pendingDpi;
                reported = HidppDpi.Set(address.Path, address.Index, applied);
            } while (applied != _pendingDpi);

            await Dispatcher.InvokeAsync(() =>
            {
                StatusLine.Text = reported is { } r
                    ? $"DPI do mouse: {r}."
                    : "Não foi possível alterar o DPI (o mouse não respondeu).";
            });
        });
    }

    private HeadsetButtonController? _headsetButtons;

    private async Task StartHeadsetButtonsAsync(LampDevice headset, BatteryReading reading)
    {
        void Report(string message) => Dispatcher.InvokeAsync(() => StatusLine.Text = message);

        _headsetButtons = await Task.Run(() =>
            HeadsetButtonController.TryCreate(reading.DevicePath, reading.Index, Report));
        if (_headsetButtons is not null)
            headset.Buttons = _headsetButtons.Buttons;
    }

    private void DpiPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LampDevice device, Tag: string value } && int.TryParse(value, out var dpi))
            device.Dpi = dpi;
    }

    // Right after logon the receiver/headset often is not answering yet; poll fast until they show up.
    private static readonly TimeSpan StartupInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StartupWindow = TimeSpan.FromMinutes(3);
    private const int StoredLedAttempts = 6;
    private readonly Dictionary<string, int> _storedLedTries = new();

    private async Task PollBatteryAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool pending = false;
            var readings = await Task.Run(HidppBattery.ReadAll, token);
            pending |= !readings.Any(r => r.Type == HidppDeviceType.Headset);
            foreach (var reading in readings)
            {
                var id = reading.Type switch
                {
                    HidppDeviceType.Mouse => "logi:mouse",
                    HidppDeviceType.Headset => "logi:headset",
                    _ => null,
                };
                if (id is null)
                    continue;
                // Without G HUB's SDK there are no cards yet: the HID++ answer itself creates them.
                if (_devices.FirstOrDefault(d => d.Id == id) is not { } device)
                {
                    ILightBackend backend = reading.Type == HidppDeviceType.Headset
                        ? new HidppLedBackend(reading.DevicePath, reading.Index)
                        : new NoLightBackend();
                    device = new LampDevice(id, reading.Name, backend);
                    if (reading.Type == HidppDeviceType.Headset)
                    {
                        var stored = await Task.Run(() => HidppLedMemory.ReadStored(reading.DevicePath, reading.Index), token);
                        if (stored is null)
                        {
                            // Not answering yet (just after logon): a card created now would push the default
                            // color over the user's. Retry a few times before giving up on the stored light.
                            _storedLedTries.TryGetValue(id, out var tries);
                            _storedLedTries[id] = ++tries;
                            if (tries < StoredLedAttempts)
                            {
                                pending = true;
                                continue;
                            }
                        }
                        else
                        {
                            device.LoadLed(stored);  // before the first frame, so the stored light is not overwritten
                        }
                    }
                    AddDevice(device);
                }
                device.UpdateBattery(reading);
                if (reading.Name.Length > 0)
                    device.Name = $"{reading.Name}";
                if (_lightingChecked.Add(device.Id)
                    && await Task.Run(() => HidppLighting.HasUserLighting(reading.DevicePath, reading.Index), token) is { } hasLighting)
                {
                    device.HasLighting = hasLighting;
                    // Mice keep LEDs in onboard profiles; headsets store the effect through 0x8070 itself.
                    if (hasLighting && reading.Type == HidppDeviceType.Headset)
                    {
                        _ledMemoryAddress[device.Id] = (reading.DevicePath, reading.Index);
                        device.CanSaveLighting = true;
                    }
                    if (device == DeviceList.SelectedItem)
                        EnsureVisibleTab();
                }
                if (reading.Type == HidppDeviceType.Mouse)
                    await RefreshDpiAsync(device, reading, token);
                if (reading.Type == HidppDeviceType.Headset && _headsetButtons is null)
                    await StartHeadsetButtonsAsync(device, reading);
            }

            try
            {
                bool starting = pending && _clock.Elapsed < StartupWindow;
                await Task.Delay(starting ? StartupInterval : BatteryInterval, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Logitech devices are driven through G HUB when it is running: some (like the PRO X Wireless mouse)
    /// expose no Dynamic Lighting interface, and G HUB otherwise keeps control of the ones that do.
    /// </summary>
    private void StartLogitechSdk()
    {
        _sdkActive = LogiLedSdk.TryInit("LightingControl");
        if (!_sdkActive)
            return;
        AddDevice(new LampDevice("logi:mouse", "Mouse", new LogiSdkBackend(LogiLedSdk.DeviceType.Mouse, zoneCount: 2)));
        AddDevice(new LampDevice("logi:headset", "Headset", new LogiSdkBackend(LogiLedSdk.DeviceType.Headset, zoneCount: 2, allDevices: true)));
    }

    private void StartWatcher()
    {
        _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
        _watcher.Added += async (_, info) =>
        {
            var lamp = await LampArray.FromIdAsync(info.Id);
            // Logitech devices get their card from HID++ (or the SDK); a LampArray card would duplicate and fight it.
            if (lamp is null || lamp.HardwareVendorId == LogitechVendorId)
                return;
            await Dispatcher.InvokeAsync(() => AddDevice(new LampDevice(info.Id, info.Name, new LampArrayBackend(lamp))));
        };
        _watcher.Removed += (_, update) => Dispatcher.InvokeAsync(() =>
        {
            var device = _devices.FirstOrDefault(d => d.Id == update.Id);
            if (device is not null)
            {
                device.PropertyChanged -= Device_PropertyChanged;
                _devices.Remove(device);
            }
        });
        _watcher.EnumerationCompleted += (_, _) => Dispatcher.InvokeAsync(UpdateEmptyState);
        _watcher.Start();
    }

    private void AddDevice(LampDevice device)
    {
        if (_devices.Any(d => d.Id == device.Id))
            return;
        device.PropertyChanged += Device_PropertyChanged;
        _devices.Add(device);
    }

    private void RenderFrame()
    {
        double t = _clock.Elapsed.TotalSeconds;
        foreach (var device in _devices)
        {
            try
            {
                device.Render(t);
            }
            catch (Exception ex)
            {
                // A device unplugged mid-frame throws; the watcher removes it shortly after.
                Debug.WriteLine($"Render failed for {device.Name}: {ex.Message}");
            }
        }
    }

    private void Device_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || SyncBox.IsChecked != true || sender is not LampDevice source)
            return;
        // Only lighting settings are synced; status, battery, name and DPI stay per device.
        if (e.PropertyName is not (nameof(LampDevice.R) or nameof(LampDevice.G) or nameof(LampDevice.B)
            or nameof(LampDevice.Brightness) or nameof(LampDevice.Speed) or nameof(LampDevice.Effect)))
            return;

        _syncing = true;
        try
        {
            foreach (var other in _devices.Where(d => d != source))
                other.CopyFrom(source);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>
    /// The pane's template is reused across devices, so a tab the new device doesn't have (DPI on the headset)
    /// could stay selected while hidden; fall back to the first visible tab.
    /// </summary>
    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => EnsureVisibleTab();

    /// <summary>Devices whose LED capability has been probed (once per device).</summary>
    private readonly HashSet<string> _lightingChecked = new();

    private void EnsureVisibleTab()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (FindChild<TabControl>(DevicePane) is { SelectedItem: TabItem { Visibility: not Visibility.Visible } } tabs)
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Visibility == Visibility.Visible);
        }, DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match || FindChild<T>(child) is { } nested && (match = nested) is not null)
                return match;
        }
        return null;
    }

    private void StartupBox_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupBox.IsChecked == true;
        try
        {
            StartupRegistration.Set(enabled);
            StatusLine.Text = enabled
                ? "O app vai abrir minimizado quando você entrar no Windows."
                : "O app não abre mais com o Windows.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            StartupBox.IsChecked = !enabled;
            StatusLine.Text = $"Não foi possível alterar a inicialização: {ex.Message}";
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LampDevice device, Tag: string hex })
            device.Hex = hex;
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_devices.Count == 0 && _watcher?.Status == DeviceWatcherStatus.EnumerationCompleted)
            EmptyText.Text = "Nenhum dispositivo Dynamic Lighting encontrado.";

        StatusLine.Text = _sdkActive
            ? $"{_devices.Count} dispositivo(s). A luz passa pelo G HUB (LED SDK); " +
              "ao fechar o app, o G HUB volta para o perfil de iluminação dele."
            : $"{_devices.Count} dispositivo(s), controlados direto pelo app (sem G HUB). " +
              "Ao fechar, a luz atual fica gravada no headset.";
    }

    private void Shutdown()
    {
        _shutdown.Cancel();
        _tray.Dispose();  // otherwise a dead icon lingers in the tray until hovered
        SaveLightingOnExit();
        _headsetButtons?.Dispose();
        _frameTimer.Stop();
        _watcher?.Stop();
        if (_sdkActive)
            LogiLedSdk.LogiLedShutdown();
    }
}

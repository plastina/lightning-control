namespace LightingControl;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        Diag.Log($"app: OnStartup args=[{string.Join(' ', e.Args)}]");
        Updater.CleanUp();
        var window = new MainWindow();
        MainWindow = window;
        // Started with Windows: stay in the tray, the window opens from the icon.
        if (!e.Args.Contains(StartupRegistration.MinimizedArg))
            window.Show();
        // Every launch looks for a newer release; runs in the background so startup is not delayed.
        _ = Updater.CheckAndPromptAsync();
    }
}

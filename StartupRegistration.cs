using Microsoft.Win32;

namespace LightingControl;

/// <summary>
/// "Start with Windows" through the per-user Run key: no admin rights, and it shows up in Task Manager's
/// Startup apps like any other entry.
/// </summary>
internal static class StartupRegistration
{
    public const string MinimizedArg = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager disables an entry here instead of deleting it from Run: first byte 0x02 is on, 0x03 is off.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "LightingControl";

    private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArg}";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is null)
                return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            run.SetValue(ValueName, Command);
        else
            run.DeleteValue(ValueName, throwOnMissingValue: false);

        // Re-enabling from the app also clears a Task Manager "disabled" mark, or the checkbox would lie.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the entry pointing at this exe after the app folder is moved or rebuilt elsewhere.</summary>
    public static void RefreshPath()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(ValueName) is string current && current != Command)
            run.SetValue(ValueName, Command);
    }
}

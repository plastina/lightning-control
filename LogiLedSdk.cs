using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LightingControl;

/// <summary>
/// P/Invoke bindings for the Logitech G Legacy LED SDK that G HUB installs (sdks\sdk_legacy_led_x64.dll).
/// The DLL forwards commands to the running lghub_agent, which drives the hardware.
/// </summary>
internal static class LogiLedSdk
{
    private const string Library = "sdk_legacy_led";

    // CLSID that G HUB registers for the LED SDK; its ServerBinary value points at the DLL.
    private const string ClsidKey = @"SOFTWARE\Classes\CLSID\{a6519e67-7632-4375-afdf-caa889744403}\ServerBinary";
    private const string DefaultPath = @"C:\Program Files\LGHUB\sdks\sdk_legacy_led_x64.dll";

    public const int TargetRgb = 0x0002;

    public enum DeviceType
    {
        Keyboard = 0x0,
        Mouse = 0x3,
        Mousemat = 0x4,
        Headset = 0x8,
        Speaker = 0xE,
    }

    static LogiLedSdk()
    {
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
            name == Library && ResolvePath() is { } path ? NativeLibrary.Load(path) : IntPtr.Zero);
    }

    public static string? ResolvePath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ClsidKey);
        var path = key?.GetValue(null) as string ?? DefaultPath;
        return File.Exists(path) ? path : null;
    }

    /// <summary>Connects to G HUB; false when G HUB or its SDK is missing.</summary>
    public static bool TryInit(string appName)
    {
        if (ResolvePath() is null)
            return false;
        try
        {
            return LogiLedInitWithName(appName) && LogiLedSetTargetDevice(TargetRgb);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport(Library, CharSet = CharSet.Unicode)]
    private static extern bool LogiLedInitWithName(string name);

    [DllImport(Library)]
    private static extern bool LogiLedSetTargetDevice(int targetDevice);

    /// <summary>Color channels are percentages, 0..100.</summary>
    [DllImport(Library)]
    public static extern bool LogiLedSetLightingForTargetZone(DeviceType deviceType, int zone, int redPercentage, int greenPercentage, int bluePercentage);

    /// <summary>Sets every RGB device G HUB manages at once. Color channels are percentages, 0..100.</summary>
    [DllImport(Library)]
    public static extern bool LogiLedSetLighting(int redPercentage, int greenPercentage, int bluePercentage);

    [DllImport(Library)]
    public static extern void LogiLedShutdown();
}

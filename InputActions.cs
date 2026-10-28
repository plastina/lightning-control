using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LightingControl;

/// <summary>System actions a remapped button can trigger.</summary>
internal static class InputActions
{
    public const ushort VkMediaNext = 0xB0, VkMediaPrev = 0xB1, VkMediaPlayPause = 0xB3, VkVolumeMute = 0xAD;

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["space"] = 0x20, ["enter"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["backspace"] = 0x08,
        ["delete"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["pause"] = 0x13,
    };

    public static void PressKey(ushort vk) => SendKeys(new[] { vk });

    /// <summary>Sends a shortcut such as "Ctrl+Shift+M" or "Alt+F4". Returns false if it cannot be parsed.</summary>
    public static bool SendShortcut(string shortcut)
    {
        var keys = new List<ushort>();
        foreach (var part in shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (NamedKeys.TryGetValue(part, out var vk))
                keys.Add(vk);
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                keys.Add(char.ToUpperInvariant(part[0]));
            else if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
                keys.Add((ushort)(0x70 + f - 1));
            else
                return false;
        }
        if (keys.Count == 0)
            return false;
        SendKeys(keys);
        return true;
    }

    public static void Launch(string target)
    {
        if (!string.IsNullOrWhiteSpace(target))
            Process.Start(new ProcessStartInfo(target.Trim().Trim('"')) { UseShellExecute = true });
    }

    /// <summary>Toggles mute on the default recording device; returns the new muted state.</summary>
    public static bool ToggleMicrophoneMute()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleCommunications, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj));
        var volume = (IAudioEndpointVolume)obj;
        Marshal.ThrowExceptionForHR(volume.GetMute(out var muted));
        Marshal.ThrowExceptionForHR(volume.SetMute(!muted, Guid.Empty));
        return !muted;
    }

    private static void SendKeys(IReadOnlyList<ushort> keys)
    {
        // Press in order, release in reverse, so modifiers wrap the final key.
        var inputs = new INPUT[keys.Count * 2];
        for (int i = 0; i < keys.Count; i++)
        {
            inputs[i] = KeyInput(keys[i], up: false);
            inputs[keys.Count * 2 - 1 - i] = KeyInput(keys[i], up: true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = InputKeyboard,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KeyEventKeyUp : 0 } },
    };

    private const uint InputKeyboard = 1, KeyEventKeyUp = 0x0002;
    private const int DataFlowCapture = 1, RoleCommunications = 2, ClsctxAll = 0x17;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, Guid context);
        int SetMasterVolumeLevelScalar(float level, Guid context);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

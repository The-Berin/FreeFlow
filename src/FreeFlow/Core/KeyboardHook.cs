using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FreeFlow.Core;

/// <summary>
/// Global low-level keyboard hook. Watches for the dictation hotkey (and optional
/// command-mode hotkey) system-wide, and reports any other keystrokes so smart
/// spacing knows whether the user typed since the last injection.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public event Action? HotkeyDown;
    public event Action? HotkeyUp;
    public event Action? CommandKeyDown;
    public event Action? CommandKeyUp;
    public event Action? OtherKeyPressed;

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc; // kept referenced so the GC never collects it
    private int _hotkeyVk;
    private int _commandVk;
    private bool _swallow;
    private bool _hotkeyIsDown;
    private bool _commandIsDown;

    public void Install(AppConfig cfg)
    {
        Uninstall();
        _hotkeyVk = Vk.FromName(cfg.HotkeyName);
        _commandVk = Vk.FromName(cfg.CommandHotkeyName);
        _swallow = cfg.SwallowHotkey;
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"SetWindowsHookEx failed (error {Marshal.GetLastWin32Error()})");
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _hotkeyIsDown = false;
        _commandIsDown = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                int vk = (int)info.vkCode;

                if (vk == _hotkeyVk && _hotkeyVk != 0)
                {
                    if (isDown && !_hotkeyIsDown)
                    {
                        _hotkeyIsDown = true;
                        SafeRaise(HotkeyDown);
                    }
                    else if (isUp && _hotkeyIsDown)
                    {
                        _hotkeyIsDown = false;
                        SafeRaise(HotkeyUp);
                    }
                    if (_swallow && (isDown || isUp))
                        return (IntPtr)1;
                }
                else if (vk == _commandVk && _commandVk != 0)
                {
                    if (isDown && !_commandIsDown)
                    {
                        _commandIsDown = true;
                        SafeRaise(CommandKeyDown);
                    }
                    else if (isUp && _commandIsDown)
                    {
                        _commandIsDown = false;
                        SafeRaise(CommandKeyUp);
                    }
                    if (_swallow && (isDown || isUp))
                        return (IntPtr)1;
                }
                else if (isDown)
                {
                    SafeRaise(OtherKeyPressed);
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void SafeRaise(Action? evt)
    {
        try { evt?.Invoke(); }
        catch (Exception ex) { Logger.Log(ex, "keyboard hook handler"); }
    }

    public void Dispose() => Uninstall();

    #region interop

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    #endregion
}

/// <summary>Virtual-key names offered in settings, mapped to VK codes.</summary>
public static class Vk
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0,
        ["RightCtrl"] = 0xA3,
        ["LeftCtrl"] = 0xA2,
        ["RightAlt"] = 0xA5,
        ["RightShift"] = 0xA1,
        ["CapsLock"] = 0x14,
        ["ScrollLock"] = 0x91,
        ["Pause"] = 0x13,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
    };

    public static int FromName(string name) => Map.TryGetValue(name, out var vk) ? vk : 0;

    public static string[] Names => Map.Keys.ToArray();

    public static string[] HotkeyChoices => Map.Keys.Where(k => k != "None").ToArray();
}

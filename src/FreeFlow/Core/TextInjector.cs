using System.Runtime.InteropServices;

namespace FreeFlow.Core;

/// <summary>
/// Puts text into whatever app has focus. Default strategy: set the clipboard,
/// send Ctrl+V, then restore the old clipboard text. Fallback: type each
/// character via SendInput unicode events. Must be called on the UI (STA) thread.
/// </summary>
public static class TextInjector
{
    public static void Inject(string text, string mode)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (mode == "Type")
            TypeText(text);
        else
            PasteText(text);
    }

    public static void PasteText(string text)
    {
        string? oldClip = null;
        try
        {
            if (Clipboard.ContainsText())
                oldClip = Clipboard.GetText();
        }
        catch { /* clipboard busy — proceed without restore */ }

        SetClipboardText(text);
        ReleaseStuckModifiers();
        SendCtrlV();

        // give the target app a beat to read the clipboard before restoring it
        var restore = oldClip;
        Task.Delay(400).ContinueWith(_ =>
        {
            try
            {
                if (restore != null)
                {
                    var t = new Thread(() => SetClipboardText(restore));
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                }
            }
            catch (Exception ex) { Logger.Log(ex, "restoring clipboard"); }
        });
    }

    private static void SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: false);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(50);
            }
        }
        Logger.Log("clipboard set failed after retries");
    }

    /// <summary>Reads the current selection by sending Ctrl+C. Restores clipboard afterwards is the caller's job.</summary>
    public static string? CopySelection()
    {
        string? oldClip = null;
        try
        {
            if (Clipboard.ContainsText())
                oldClip = Clipboard.GetText();
            Clipboard.Clear();
        }
        catch { }

        ReleaseStuckModifiers();
        SendKeyCombo(VK_CONTROL, (ushort)'C');
        Thread.Sleep(180);
        Application.DoEvents(); // let the WM_CLIPBOARDUPDATE round-trip complete

        string? selection = null;
        try
        {
            if (Clipboard.ContainsText())
                selection = Clipboard.GetText();
        }
        catch { }

        if (string.IsNullOrEmpty(selection) && oldClip != null)
            SetClipboardText(oldClip); // nothing was selected — put the old clipboard back

        return selection;
    }

    public static void SendBackspaces(int count)
    {
        if (count <= 0) return;
        count = Math.Min(count, 4000);
        var inputs = new List<INPUT>(count * 2);
        for (int i = 0; i < count; i++)
        {
            inputs.Add(KeyInput(VK_BACK, false));
            inputs.Add(KeyInput(VK_BACK, true));
        }
        SendBatched(inputs);
    }

    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(KeyInput(VK_RETURN, false));
                inputs.Add(KeyInput(VK_RETURN, true));
                continue;
            }
            inputs.Add(UnicodeInput(ch, false));
            inputs.Add(UnicodeInput(ch, true));
        }
        SendBatched(inputs);
    }

    private static void SendBatched(List<INPUT> inputs)
    {
        const int chunk = 64;
        for (int i = 0; i < inputs.Count; i += chunk)
        {
            var slice = inputs.Skip(i).Take(chunk).ToArray();
            SendInput((uint)slice.Length, slice, Marshal.SizeOf<INPUT>());
            Thread.Sleep(5);
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, false),
            KeyInput((ushort)'V', false),
            KeyInput((ushort)'V', true),
            KeyInput(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendKeyCombo(ushort modifier, ushort key)
    {
        var inputs = new[]
        {
            KeyInput(modifier, false),
            KeyInput(key, false),
            KeyInput(key, true),
            KeyInput(modifier, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// If the user's dictation hotkey is a modifier (e.g. Right Alt) and still held,
    /// sending Ctrl+V would become Ctrl+Alt+V. Lift any physically-down modifiers first.
    /// </summary>
    private static void ReleaseStuckModifiers()
    {
        var ups = new List<INPUT>();
        foreach (ushort vk in new ushort[] { VK_MENU, VK_LMENU, VK_RMENU, VK_SHIFT, VK_LSHIFT, VK_RSHIFT, VK_CONTROL, VK_LCONTROL, VK_RCONTROL })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                ups.Add(KeyInput(vk, true));
        }
        if (ups.Count > 0)
        {
            SendInput((uint)ups.Count, ups.ToArray(), Marshal.SizeOf<INPUT>());
            Thread.Sleep(15);
        }
    }

    #region interop

    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

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
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static INPUT UnicodeInput(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    #endregion
}

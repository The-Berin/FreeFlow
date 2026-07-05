using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeFlow.Core;

public static class ForegroundApp
{
    public static (string ProcessName, string WindowTitle) Get()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return ("", "");

            GetWindowThreadProcessId(hwnd, out uint pid);
            string name = "";
            if (pid != 0)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    name = p.ProcessName;
                }
                catch { }
            }

            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            return (name, sb.ToString());
        }
        catch
        {
            return ("", "");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}

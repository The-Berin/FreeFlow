namespace FreeFlow.Core;

public static class Logger
{
    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(Paths.LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }

    public static void Log(Exception ex, string context = "")
        => Log($"ERROR {context}: {ex}");
}

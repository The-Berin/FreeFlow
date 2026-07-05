namespace FreeFlow.Core;

public static class Paths
{
    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeFlow");

    public static string ModelsDir => Path.Combine(AppDataDir, "models");
    public static string ConfigPath => Path.Combine(AppDataDir, "config.json");
    public static string HistoryPath => Path.Combine(AppDataDir, "history.jsonl");
    public static string LogPath => Path.Combine(AppDataDir, "log.txt");
    public static string SelfTestReportPath => Path.Combine(AppDataDir, "selftest.txt");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(ModelsDir);
    }
}

using System.Text.Json;

namespace FreeFlow.Core;

public class HistoryEntry
{
    public DateTime Ts { get; set; }
    public string App { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Text { get; set; } = "";
}

public static class HistoryStore
{
    private static readonly object Lock = new();

    public static void Append(AppConfig cfg, HistoryEntry entry)
    {
        if (!cfg.HistoryEnabled) return;
        try
        {
            lock (Lock)
            {
                File.AppendAllText(Paths.HistoryPath,
                    JsonSerializer.Serialize(entry) + Environment.NewLine);
                TrimIfNeeded(cfg.HistoryMax);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "history append");
        }
    }

    public static List<HistoryEntry> ReadAll()
    {
        var list = new List<HistoryEntry>();
        try
        {
            lock (Lock)
            {
                if (!File.Exists(Paths.HistoryPath)) return list;
                foreach (var line in File.ReadAllLines(Paths.HistoryPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonSerializer.Deserialize<HistoryEntry>(line);
                        if (e != null) list.Add(e);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "history read");
        }
        return list;
    }

    public static void Clear()
    {
        try
        {
            lock (Lock)
            {
                if (File.Exists(Paths.HistoryPath))
                    File.Delete(Paths.HistoryPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "history clear");
        }
    }

    private static void TrimIfNeeded(int max)
    {
        var lines = File.ReadAllLines(Paths.HistoryPath);
        if (lines.Length > max * 2)
            File.WriteAllLines(Paths.HistoryPath, lines.Skip(lines.Length - max));
    }
}

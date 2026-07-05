using System.Text.Json;

namespace FreeFlow.Core;

public class Stats
{
    public long TotalWords { get; set; }
    public long TotalDictations { get; set; }
    public double TotalAudioSeconds { get; set; }
    public double TotalDecodeMs { get; set; }
    public Dictionary<string, long> WordsByDay { get; set; } = new();

    public long WordsToday
        => WordsByDay.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out var w) ? w : 0;

    /// <summary>Average speaking rate across all dictations, words per minute.</summary>
    public double AvgWpm
        => TotalAudioSeconds > 1 ? TotalWords / (TotalAudioSeconds / 60.0) : 0;
}

public static class StatsStore
{
    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(Paths.AppDataDir, "stats.json");
    private static Stats? _cached;

    public static Stats Get()
    {
        lock (Lock)
        {
            if (_cached != null) return _cached;
            try
            {
                if (File.Exists(FilePath))
                    _cached = JsonSerializer.Deserialize<Stats>(File.ReadAllText(FilePath));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "stats load");
            }
            return _cached ??= new Stats();
        }
    }

    public static void RecordDictation(int words, double audioSeconds, double decodeMs)
    {
        lock (Lock)
        {
            var s = Get();
            s.TotalWords += words;
            s.TotalDictations++;
            s.TotalAudioSeconds += audioSeconds;
            s.TotalDecodeMs += decodeMs;
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            s.WordsByDay[day] = s.WordsByDay.TryGetValue(day, out var w) ? w + words : words;

            // keep the per-day map from growing forever
            if (s.WordsByDay.Count > 400)
            {
                foreach (var key in s.WordsByDay.Keys.OrderBy(k => k).Take(s.WordsByDay.Count - 366).ToList())
                    s.WordsByDay.Remove(key);
            }

            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "stats save");
            }
        }
    }
}

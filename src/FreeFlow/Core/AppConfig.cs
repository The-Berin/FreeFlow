using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeFlow.Core;

public class DictionaryEntry
{
    public string Spoken { get; set; } = "";
    public string Written { get; set; } = "";
}

public class Snippet
{
    public string Trigger { get; set; } = "";
    public string Expansion { get; set; } = "";
}

public class AppProfile
{
    public string ProcessName { get; set; } = "";
    /// <summary>Default | Casual | Professional | Verbatim</summary>
    public string Tone { get; set; } = "Default";
    /// <summary>Paste | Type</summary>
    public string InjectMode { get; set; } = "Paste";
}

public class AppConfig
{
    // Hotkeys
    public string HotkeyName { get; set; } = "RightCtrl";
    public string CommandHotkeyName { get; set; } = "None";
    public bool TapToLatch { get; set; } = true;
    public int TapThresholdMs { get; set; } = 350;
    public bool SwallowHotkey { get; set; } = true;

    // Audio
    public int MicDevice { get; set; } = -1;
    public bool KeepMicWarm { get; set; } = true;
    public double MicGain { get; set; } = 1.0;
    public bool PlaySounds { get; set; } = true;

    // Model
    public string ModelId { get; set; } = "parakeet-tdt-0.6b-v2-int8";
    public int NumThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    /// <summary>Language hint for multilingual (Whisper) models. Empty = auto-detect.</summary>
    public string Language { get; set; } = "en";

    // Formatting
    public bool RemoveFillers { get; set; } = true;
    public List<string> FillerWords { get; set; } =
        new() { "um", "umm", "uh", "uhh", "er", "erm", "hmm", "mhm" };
    public bool SpokenCommands { get; set; } = true;
    public bool PunctuationWords { get; set; } = false;
    public bool SmartSpacing { get; set; } = true;
    public string DefaultTone { get; set; } = "Default";

    public List<AppProfile> AppProfiles { get; set; } = new()
    {
        new AppProfile { ProcessName = "Discord", Tone = "Casual" },
        new AppProfile { ProcessName = "slack", Tone = "Casual" },
        new AppProfile { ProcessName = "WindowsTerminal", Tone = "Verbatim", InjectMode = "Paste" },
        new AppProfile { ProcessName = "OUTLOOK", Tone = "Professional" },
    };

    public List<DictionaryEntry> Dictionary { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = new();

    // History
    public bool HistoryEnabled { get; set; } = true;
    public int HistoryMax { get; set; } = 500;

    // AI (command mode / polish) — optional, points at any OpenAI-compatible endpoint
    public bool AiEnabled { get; set; } = false;
    public string AiBaseUrl { get; set; } = "http://localhost:11434/v1";
    public string AiModel { get; set; } = "llama3.2";
    public string AiApiKey { get; set; } = "";
    public bool AiPolish { get; set; } = false;

    public bool FirstRunDone { get; set; } = false;

    [JsonIgnore]
    public static readonly string[] ToneNames = { "Default", "Casual", "Professional", "Verbatim" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigPath), JsonOpts);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "loading config, using defaults");
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Paths.EnsureDirs();
            File.WriteAllText(Paths.ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "saving config");
        }
    }

    public AppConfig Clone()
        => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;

    public AppProfile? ProfileFor(string processName)
        => AppProfiles.FirstOrDefault(p =>
            p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase));
}

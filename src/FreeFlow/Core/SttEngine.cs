using SherpaOnnx;

namespace FreeFlow.Core;

public enum ModelKind { NemoTransducer, Whisper }

public class ModelFile
{
    public string Name { get; }
    public string Url { get; }
    public long ApproxBytes { get; }

    public ModelFile(string name, string url, long approxBytes)
    {
        Name = name; Url = url; ApproxBytes = approxBytes;
    }
}

public class ModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required ModelKind Kind { get; init; }
    public required string LanguageNote { get; init; }
    public required ModelFile[] Files { get; init; }

    public string Dir => Path.Combine(Paths.ModelsDir, Id);
    public string PathOf(string fileName) => Path.Combine(Dir, fileName);
    public long TotalBytes => Files.Sum(f => f.ApproxBytes);

    public bool IsDownloaded()
        => Files.All(f => File.Exists(PathOf(f.Name)) && new FileInfo(PathOf(f.Name)).Length > f.ApproxBytes / 2);
}

public static class ModelRegistry
{
    private const string ParakeetBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/main";
    private const string WhisperSmallBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small/resolve/main";
    private const string WhisperBaseBase =
        "https://huggingface.co/csukuangfj/sherpa-onnx-whisper-base/resolve/main";

    public static readonly ModelInfo[] All =
    {
        new()
        {
            Id = "parakeet-tdt-0.6b-v2-int8",
            DisplayName = "Parakeet TDT 0.6B v2 (English — best accuracy & speed)",
            Kind = ModelKind.NemoTransducer,
            LanguageNote = "English only. Punctuation + capitalization built in. Recommended.",
            Files = new[]
            {
                new ModelFile("encoder.int8.onnx", $"{ParakeetBase}/encoder.int8.onnx", 652_184_296),
                new ModelFile("decoder.int8.onnx", $"{ParakeetBase}/decoder.int8.onnx", 7_257_753),
                new ModelFile("joiner.int8.onnx", $"{ParakeetBase}/joiner.int8.onnx", 1_739_080),
                new ModelFile("tokens.txt", $"{ParakeetBase}/tokens.txt", 9_384),
            },
        },
        new()
        {
            Id = "whisper-small-int8",
            DisplayName = "Whisper Small (multilingual — 100+ languages)",
            Kind = ModelKind.Whisper,
            LanguageNote = "~100 languages. Slower than Parakeet on CPU.",
            Files = new[]
            {
                new ModelFile("small-encoder.int8.onnx", $"{WhisperSmallBase}/small-encoder.int8.onnx", 112_000_000),
                new ModelFile("small-decoder.int8.onnx", $"{WhisperSmallBase}/small-decoder.int8.onnx", 262_000_000),
                new ModelFile("small-tokens.txt", $"{WhisperSmallBase}/small-tokens.txt", 800_000),
            },
        },
        new()
        {
            Id = "whisper-base-int8",
            DisplayName = "Whisper Base (multilingual — fastest, lower accuracy)",
            Kind = ModelKind.Whisper,
            LanguageNote = "~100 languages, small download. Use if Small feels slow.",
            Files = new[]
            {
                new ModelFile("base-encoder.int8.onnx", $"{WhisperBaseBase}/base-encoder.int8.onnx", 20_000_000),
                new ModelFile("base-decoder.int8.onnx", $"{WhisperBaseBase}/base-decoder.int8.onnx", 50_000_000),
                new ModelFile("base-tokens.txt", $"{WhisperBaseBase}/base-tokens.txt", 800_000),
            },
        },
    };

    public static ModelInfo Get(string id)
        => All.FirstOrDefault(m => m.Id == id) ?? All[0];
}

public static class ModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public static async Task DownloadAsync(ModelInfo model,
        IProgress<(string File, double Percent, long Done, long Total)>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(model.Dir);
        foreach (var f in model.Files)
        {
            string finalPath = model.PathOf(f.Name);
            if (File.Exists(finalPath) && new FileInfo(finalPath).Length > f.ApproxBytes / 2)
                continue;

            string tmpPath = finalPath + ".part";
            using var resp = await Http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? f.ApproxBytes;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmpPath))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((f.Name, total > 0 ? 100.0 * done / total : 0, done, total));
                }
            }
            File.Move(tmpPath, finalPath, overwrite: true);
        }
    }
}

/// <summary>Wraps a sherpa-onnx offline recognizer. Thread-safe for one-at-a-time decodes.</summary>
public sealed class SttEngine : IDisposable
{
    private readonly object _lock = new();
    private OfflineRecognizer? _rec;

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }
    public string LoadedModelId { get; private set; } = "";

    public void Load(AppConfig cfg)
    {
        lock (_lock)
        {
            DisposeRecognizer();
            LoadError = null;
            IsLoaded = false;

            var model = ModelRegistry.Get(cfg.ModelId);
            if (!model.IsDownloaded())
            {
                LoadError = $"Model \"{model.DisplayName}\" is not downloaded yet. Open Settings → Model.";
                return;
            }

            try
            {
                var c = new OfflineRecognizerConfig();
                c.FeatConfig.SampleRate = 16000;
                c.FeatConfig.FeatureDim = 80;
                c.ModelConfig.Tokens = model.PathOf(model.Files.First(f => f.Name.Contains("tokens")).Name);
                c.ModelConfig.NumThreads = Math.Max(1, cfg.NumThreads);
                c.ModelConfig.Provider = "cpu";
                c.ModelConfig.Debug = 0;
                c.DecodingMethod = "greedy_search";

                switch (model.Kind)
                {
                    case ModelKind.NemoTransducer:
                        c.ModelConfig.Transducer.Encoder = model.PathOf("encoder.int8.onnx");
                        c.ModelConfig.Transducer.Decoder = model.PathOf("decoder.int8.onnx");
                        c.ModelConfig.Transducer.Joiner = model.PathOf("joiner.int8.onnx");
                        c.ModelConfig.ModelType = "nemo_transducer";
                        break;
                    case ModelKind.Whisper:
                        c.ModelConfig.Whisper.Encoder = model.PathOf(model.Files.First(f => f.Name.Contains("encoder")).Name);
                        c.ModelConfig.Whisper.Decoder = model.PathOf(model.Files.First(f => f.Name.Contains("decoder")).Name);
                        c.ModelConfig.Whisper.Language = cfg.Language;
                        c.ModelConfig.Whisper.Task = "transcribe";
                        break;
                }

                _rec = new OfflineRecognizer(c);
                IsLoaded = true;
                LoadedModelId = model.Id;
                Logger.Log($"model loaded: {model.Id}, threads={cfg.NumThreads}");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"loading model {model.Id}");
                LoadError = $"Failed to load model: {ex.Message}";
            }
        }
    }

    public string Transcribe(float[] samples16k)
    {
        lock (_lock)
        {
            if (_rec == null)
                throw new InvalidOperationException(LoadError ?? "Recognizer not loaded.");
            using var stream = _rec.CreateStream();
            stream.AcceptWaveform(16000, samples16k);
            _rec.Decode(stream);
            return (stream.Result.Text ?? "").Trim();
        }
    }

    private void DisposeRecognizer()
    {
        _rec?.Dispose();
        _rec = null;
        IsLoaded = false;
    }

    public void Dispose()
    {
        lock (_lock) { DisposeRecognizer(); }
    }
}

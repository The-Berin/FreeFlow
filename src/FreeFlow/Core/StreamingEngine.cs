using System.Collections.Concurrent;
using SherpaOnnx;

namespace FreeFlow.Core;

/// <summary>
/// Real-time word-by-word recognition: a streaming Zipformer transducer fed from
/// the mic on a dedicated decode thread, emitting partial transcripts as you speak,
/// plus an online punctuation model to restore casing/punctuation on the raw stream.
/// The model outputs ALL-UPPERCASE unpunctuated text; we normalize to lowercase.
/// </summary>
public sealed class StreamingEngine : IDisposable
{
    /// <summary>Raw lowercase partial transcript, fired from the decode thread whenever it grows/changes.</summary>
    public event Action<string>? Partial;

    private readonly object _lock = new();
    private OnlineRecognizer? _rec;
    private OnlinePunctuation? _punct;

    private OnlineStream? _stream;
    private BlockingCollection<float[]>? _queue;
    private Thread? _decodeThread;
    private string _lastPartial = "";
    private volatile bool _sessionActive;

    public bool IsLoaded { get; private set; }
    public bool PunctLoaded { get; private set; }
    public string? LoadError { get; private set; }

    public void Load(AppConfig cfg)
    {
        lock (_lock)
        {
            AbortSessionLocked();
            _rec?.Dispose();
            _rec = null;
            _punct?.Dispose();
            _punct = null;
            IsLoaded = false;
            PunctLoaded = false;
            LoadError = null;

            var model = ModelRegistry.Get(ModelRegistry.StreamingModelId);
            if (!model.IsDownloaded())
            {
                LoadError = "Streaming model not downloaded yet.";
                return;
            }

            try
            {
                var c = new OnlineRecognizerConfig();
                c.FeatConfig.SampleRate = 16000;
                c.FeatConfig.FeatureDim = 80;
                c.ModelConfig.Transducer.Encoder = model.PathOf("encoder.int8.onnx");
                c.ModelConfig.Transducer.Decoder = model.PathOf("decoder.int8.onnx");
                c.ModelConfig.Transducer.Joiner = model.PathOf("joiner.int8.onnx");
                c.ModelConfig.Tokens = model.PathOf("tokens.txt");
                c.ModelConfig.NumThreads = Math.Clamp(cfg.NumThreads / 2, 1, 2); // leave headroom for the app + final pass
                c.ModelConfig.Provider = "cpu";
                c.ModelConfig.Debug = 0;
                c.DecodingMethod = "greedy_search";
                c.EnableEndpoint = 0; // push-to-talk defines utterance boundaries, not silence
                _rec = new OnlineRecognizer(c);
                IsLoaded = true;
                Logger.Log("streaming model loaded");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "loading streaming model");
                LoadError = $"Failed to load streaming model: {ex.Message}";
                return;
            }

            var punctModel = ModelRegistry.Get(ModelRegistry.PunctModelId);
            if (punctModel.IsDownloaded())
            {
                try
                {
                    var pc = new OnlinePunctuationConfig();
                    pc.Model.CnnBiLstm = punctModel.PathOf("model.int8.onnx");
                    pc.Model.BpeVocab = punctModel.PathOf("bpe.vocab");
                    pc.Model.NumThreads = 1;
                    pc.Model.Provider = "cpu";
                    pc.Model.Debug = 0;
                    _punct = new OnlinePunctuation(pc);
                    PunctLoaded = true;
                    Logger.Log("punctuation model loaded");
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "loading punctuation model (continuing without)");
                }
            }
        }
    }

    public void StartSession()
    {
        lock (_lock)
        {
            if (_rec == null || _sessionActive) return;
            _stream = _rec.CreateStream();
            _queue = new BlockingCollection<float[]>(boundedCapacity: 256);
            _lastPartial = "";
            _sessionActive = true;
            _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "FreeFlowStreamDecode" };
            _decodeThread.Start();
        }
    }

    /// <summary>Feed 16 kHz mono samples from the mic callback. Non-blocking; drops if the decoder is drowning.</summary>
    public void Feed(float[] samples16k)
    {
        var q = _queue;
        if (q is { IsAddingCompleted: false } && _sessionActive)
            q.TryAdd(samples16k);
    }

    /// <summary>Stop feeding, drain the decoder, return the final raw lowercase transcript.</summary>
    public string FinishSession()
    {
        Thread? worker;
        lock (_lock)
        {
            if (!_sessionActive || _queue == null) return "";
            _queue.CompleteAdding();
            worker = _decodeThread;
        }
        worker?.Join(10_000);

        lock (_lock)
        {
            string final = "";
            if (_rec != null && _stream != null)
            {
                try
                {
                    _stream.InputFinished();
                    while (_rec.IsReady(_stream))
                        _rec.Decode(_stream);
                    final = Normalize(_rec.GetResult(_stream).Text);
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "finishing stream session");
                    final = _lastPartial;
                }
            }
            CleanupSessionLocked();
            return final;
        }
    }

    public void AbortSession()
    {
        Thread? worker;
        lock (_lock)
        {
            if (!_sessionActive) return;
            _queue?.CompleteAdding();
            worker = _decodeThread;
        }
        worker?.Join(3000);
        lock (_lock) { CleanupSessionLocked(); }
    }

    private void AbortSessionLocked()
    {
        _queue?.CompleteAdding();
        CleanupSessionLocked();
    }

    private void CleanupSessionLocked()
    {
        _sessionActive = false;
        _stream?.Dispose();
        _stream = null;
        _queue?.Dispose();
        _queue = null;
        _decodeThread = null;
    }

    private void DecodeLoop()
    {
        var queue = _queue;
        var stream = _stream;
        var rec = _rec;
        if (queue == null || stream == null || rec == null) return;

        try
        {
            foreach (var chunk in queue.GetConsumingEnumerable())
            {
                stream.AcceptWaveform(16000, chunk);
                while (rec.IsReady(stream))
                    rec.Decode(stream);

                string text = Normalize(rec.GetResult(stream).Text);
                if (text.Length > 0 && text != _lastPartial)
                {
                    _lastPartial = text;
                    try { Partial?.Invoke(text); }
                    catch (Exception ex) { Logger.Log(ex, "partial handler"); }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // session torn down while decoding — fine
        }
        catch (InvalidOperationException)
        {
            // queue disposed between checks — fine
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "stream decode loop");
        }
    }

    /// <summary>Restore punctuation + capitalization on raw streaming output ("how are you" → "How are you?").</summary>
    public string Punctuate(string rawLower)
    {
        lock (_lock)
        {
            if (_punct == null || string.IsNullOrWhiteSpace(rawLower)) return rawLower;
            try { return _punct.AddPunct(rawLower); }
            catch (Exception ex)
            {
                Logger.Log(ex, "punctuation");
                return rawLower;
            }
        }
    }

    private static string Normalize(string? text)
        => (text ?? "").Trim().ToLowerInvariant();

    public void Dispose()
    {
        Thread? worker;
        lock (_lock)
        {
            _queue?.CompleteAdding();
            worker = _decodeThread;
        }
        worker?.Join(3000);
        lock (_lock)
        {
            CleanupSessionLocked();
            _rec?.Dispose();
            _rec = null;
            _punct?.Dispose();
            _punct = null;
            IsLoaded = false;
        }
    }
}

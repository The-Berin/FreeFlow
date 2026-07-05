using NAudio.Wave;

namespace FreeFlow.Core;

/// <summary>
/// Microphone capture. In "warm" mode the device stays open, feeding a small ring
/// buffer so the first syllable after the hotkey press is never clipped. Captured
/// audio is returned as 16 kHz mono floats ready for the recognizer.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public event Action<float>? Level;      // RMS 0..1 per buffer while capturing
    public event Action<string>? Error;

    private readonly object _lock = new();
    private WaveInEvent? _waveIn;
    private int _actualRate = 16000;
    private bool _warm;
    private bool _deviceRunning;
    private bool _capturing;
    private double _gain = 1.0;
    private int _deviceNumber = -1;

    private float[] _ring = Array.Empty<float>();
    private int _ringPos;
    private int _ringFilled;
    private List<float> _current = new();

    private const int PreRollMs = 250;

    public bool IsWarm => _warm && _deviceRunning;

    public static List<(int Index, string Name)> ListDevices()
    {
        var list = new List<(int, string)> { (-1, "Default microphone") };
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { list.Add((i, WaveInEvent.GetCapabilities(i).ProductName)); }
            catch { /* skip unreadable device */ }
        }
        return list;
    }

    public void Configure(AppConfig cfg)
    {
        lock (_lock)
        {
            CloseDevice();
            _warm = cfg.KeepMicWarm;
            _gain = cfg.MicGain;
            _deviceNumber = cfg.MicDevice;
            if (_warm)
                TryOpenAndStart();
        }
    }

    public void StartCapture()
    {
        lock (_lock)
        {
            _current = new List<float>(_actualRate * 15);
            // seed with the pre-roll from the ring buffer so we don't clip speech onset
            if (_ringFilled > 0)
            {
                int want = Math.Min(_actualRate * PreRollMs / 1000, _ringFilled);
                int start = (_ringPos - want + _ring.Length) % _ring.Length;
                for (int i = 0; i < want; i++)
                    _current.Add(_ring[(start + i) % _ring.Length]);
            }
            _capturing = true;
            if (!_deviceRunning)
                TryOpenAndStart();
        }
    }

    /// <summary>Stops capturing and returns audio resampled to 16 kHz mono.</summary>
    public float[] StopCapture()
    {
        float[] samples;
        lock (_lock)
        {
            _capturing = false;
            samples = _current.ToArray();
            _current = new List<float>();
            if (!_warm)
                CloseDevice();
        }
        return _actualRate == 16000 ? samples : Resample(samples, _actualRate, 16000);
    }

    public void CancelCapture()
    {
        lock (_lock)
        {
            _capturing = false;
            _current = new List<float>();
            if (!_warm)
                CloseDevice();
        }
    }

    private void TryOpenAndStart()
    {
        foreach (int rate in new[] { 16000, 48000, 44100 })
        {
            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _deviceNumber,
                    WaveFormat = new WaveFormat(rate, 16, 1),
                    BufferMilliseconds = 30,
                    NumberOfBuffers = 4,
                };
                _waveIn.DataAvailable += OnData;
                _waveIn.RecordingStopped += OnStopped;
                _waveIn.StartRecording();
                _actualRate = rate;
                _ring = new float[rate]; // 1 second of pre-roll
                _ringPos = 0;
                _ringFilled = 0;
                _deviceRunning = true;
                return;
            }
            catch (Exception ex)
            {
                _waveIn?.Dispose();
                _waveIn = null;
                Logger.Log($"mic open at {rate} Hz failed: {ex.Message}");
            }
        }
        _deviceRunning = false;
        Error?.Invoke("Could not open the microphone. Check that one is connected and not blocked by privacy settings.");
    }

    private void CloseDevice()
    {
        if (_waveIn != null)
        {
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.DataAvailable -= OnData;
            _waveIn.RecordingStopped -= OnStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
        _deviceRunning = false;
        _ringFilled = 0;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int n = e.BytesRecorded / 2;
        if (n == 0) return;

        var floats = new float[n];
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(e.Buffer[2 * i] | (e.Buffer[2 * i + 1] << 8));
            float f = (float)Math.Clamp(s / 32768.0 * _gain, -1.0, 1.0);
            floats[i] = f;
            sumSq += f * f;
        }

        bool capturing;
        lock (_lock)
        {
            capturing = _capturing;
            if (capturing)
                _current.AddRange(floats);
            if (_ring.Length > 0)
            {
                foreach (var f in floats)
                {
                    _ring[_ringPos] = f;
                    _ringPos = (_ringPos + 1) % _ring.Length;
                }
                _ringFilled = Math.Min(_ringFilled + n, _ring.Length);
            }
        }

        if (capturing)
            Level?.Invoke((float)Math.Sqrt(sumSq / n));
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Logger.Log(e.Exception, "recording stopped unexpectedly");
            lock (_lock) { _deviceRunning = false; }
            Error?.Invoke($"Microphone error: {e.Exception.Message}");
        }
    }

    public static float[] Resample(float[] src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || src.Length == 0) return src;
        int dstLen = (int)((long)src.Length * dstRate / srcRate);
        var dst = new float[dstLen];
        double step = (double)srcRate / dstRate;
        for (int i = 0; i < dstLen; i++)
        {
            double pos = i * step;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double frac = pos - i0;
            dst[i] = (float)(src[i0] * (1 - frac) + src[i1] * frac);
        }
        return dst;
    }

    public void Dispose()
    {
        lock (_lock) { CloseDevice(); }
    }
}

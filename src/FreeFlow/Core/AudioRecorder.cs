using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FreeFlow.Core;

/// <summary>
/// Microphone capture, tuned for both wired and Bluetooth headsets.
///
/// Wired mics in "warm" mode stay open with a ring buffer so the first syllable
/// is never clipped. Bluetooth hands-free mics (AirPods etc.) are auto-detected
/// and handled differently: opening the HFP link hijacks the headset's audio
/// quality, so the mic opens on demand and lingers ~20s after the last dictation —
/// consecutive dictations are instant, and music quality comes back shortly after.
/// A soft auto-gain lifts quiet Bluetooth mics to a usable level.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public event Action<float>? Level;      // RMS 0..1 per buffer while capturing
    public event Action<float[]>? Spectrum; // ~16 log-spaced band magnitudes 0..1 while capturing
    public event Action<float[]>? Samples16k; // live 16 kHz mono chunks while capturing (for streaming STT)
    /// <summary>First audio buffer of a capture actually arrived — safe to tell the user "talk now".</summary>
    public event Action? FirstAudio;
    public event Action<string>? Error;

    private readonly object _lock = new();
    private WaveInEvent? _waveIn;
    private int _actualRate = 16000;
    private bool _warm;
    private bool _deviceRunning;
    private bool _capturing;
    private double _gain = 1.0;
    private double _agcGain = 1.0;
    private string _deviceName = "";
    private bool _isBluetooth;
    private int _lingerSeconds = 20;
    private System.Threading.Timer? _lingerTimer;
    private bool _firstAudioPending;

    private float[] _ring = Array.Empty<float>();
    private int _ringPos;
    private int _ringFilled;
    private List<float> _current = new();
    private ChunkResampler? _liveResampler;

    // Bluetooth hands-free links sometimes come up half-open: Windows opens the mic
    // endpoint fine but the voice channel never truly establishes — the device
    // delivers a faint dither floor (NOT exact zeros) and none of the real audio.
    // Playing (silence) to the headset's hands-free RENDER endpoint forces the
    // two-way link up; a watchdog cycles the device if no real signal arrives,
    // and after two dead cycles we fall back to the default wired/USB mic.
    private MMDevice? _linkDevice;
    private WasapiOut? _linkOut;
    private long _quietRunSamples;
    private int _deadKicks;
    private bool _sawRealAudio;
    private int _forceDeviceIndex = -2; // -2 = none; session-sticky fallback device

    // spectral bookkeeping to spot a narrowband (phone-quality) Bluetooth link
    private double _midBandSum, _hiBandSum;
    private int _bandFrames;

    /// <summary>True when the last capture had speech energy but nothing above ~4 kHz —
    /// the signature of an 8 kHz Bluetooth hands-free link.</summary>
    public bool LastCaptureNarrowband { get; private set; }

    private const int PreRollMs = 250;
    private static readonly string[] BluetoothMarkers = { "hands-free", "airpod", "headset", "bluetooth" };

    public bool IsWarm => _warm && _deviceRunning;

    /// <summary>Live gain update from the settings slider — no device reopen needed.</summary>
    public void SetGain(double gain)
    {
        lock (_lock) { _gain = gain; }
    }

    /// <summary>
    /// Close the mic link now instead of waiting out the linger. Needed before audio
    /// playback on Bluetooth headsets: while the hands-free link is open, Windows
    /// mutes the headset's normal (A2DP) output entirely.
    /// </summary>
    public void ReleaseDevice()
    {
        lock (_lock)
        {
            if (!_capturing)
                CloseDeviceLocked();
        }
    }
    public bool IsBluetoothDevice { get { lock (_lock) return _isBluetooth; } }

    public static bool AnyDevicePresent => WaveInEvent.DeviceCount > 0;

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
            CloseDeviceLocked();
            _warm = cfg.KeepMicWarm;
            _gain = cfg.MicGain;
            _deviceName = cfg.MicDeviceName;
            _forceDeviceIndex = -2; // device settings changed — drop any fallback
            _lingerSeconds = Math.Clamp(cfg.MicLingerSeconds, 3, 120);
            _agcGain = 1.0;

            // warm mode only makes sense for wired mics — holding a Bluetooth
            // hands-free link open ruins the headset's playback quality
            var (_, _, isBt) = ResolveDevice();
            _isBluetooth = isBt;
            if (_warm && !isBt)
                TryOpenAndStartLocked();
        }
    }

    public void StartCapture()
    {
        bool signalReadyNow = false;
        lock (_lock)
        {
            CancelLingerLocked();
            _liveResampler = new ChunkResampler(_actualRate, 16000);
            _current = new List<float>(_actualRate * 15);
            if (_ringFilled > 0)
            {
                int want = Math.Min(_actualRate * PreRollMs / 1000, _ringFilled);
                int start = (_ringPos - want + _ring.Length) % _ring.Length;
                for (int i = 0; i < want; i++)
                    _current.Add(_ring[(start + i) % _ring.Length]);
            }
            _capturing = true;
            _midBandSum = _hiBandSum = 0;
            _bandFrames = 0;
            _quietRunSamples = 0;
            _deadKicks = 0;
            _sawRealAudio = false;
            if (_deviceRunning)
            {
                _firstAudioPending = false; // already flowing — no wake-up delay to wait out
                signalReadyNow = true;
            }
            else
            {
                _firstAudioPending = true;  // signalled from OnData when the first buffer lands
                TryOpenAndStartLocked();
            }
        }
        if (signalReadyNow)
            FirstAudio?.Invoke();
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
            double mid = _bandFrames > 0 ? _midBandSum / _bandFrames : 0;
            double hi = _bandFrames > 0 ? _hiBandSum / _bandFrames : 0;
            LastCaptureNarrowband = _bandFrames > 15 && mid > 0.02 && hi < mid * 0.08;
            ScheduleCloseLocked();
        }
        return _actualRate == 16000 ? samples : Resample(samples, _actualRate, 16000);
    }

    public void CancelCapture()
    {
        lock (_lock)
        {
            _capturing = false;
            _current = new List<float>();
            ScheduleCloseLocked();
        }
    }

    /// <summary>Keep the device open briefly after a dictation so the next one starts instantly.</summary>
    private void ScheduleCloseLocked()
    {
        if (_warm && !_isBluetooth)
            return; // wired warm mode: stays open for the ring buffer

        CancelLingerLocked();
        _lingerTimer = new System.Threading.Timer(_ =>
        {
            lock (_lock)
            {
                if (!_capturing)
                    CloseDeviceLocked();
            }
        }, null, TimeSpan.FromSeconds(_lingerSeconds), Timeout.InfiniteTimeSpan);
    }

    private void CancelLingerLocked()
    {
        _lingerTimer?.Dispose();
        _lingerTimer = null;
    }

    private (int Index, string Name, bool IsBluetooth) ResolveDevice()
    {
        if (_forceDeviceIndex != -2)
        {
            string fallbackName = "";
            try { fallbackName = WaveInEvent.GetCapabilities(_forceDeviceIndex).ProductName; } catch { }
            return (_forceDeviceIndex, fallbackName, false);
        }

        string name = _deviceName;
        int index = -1;

        if (!string.IsNullOrWhiteSpace(name))
        {
            // match by name so Bluetooth reconnects (which shuffle device numbers) still find it
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                try
                {
                    var product = WaveInEvent.GetCapabilities(i).ProductName;
                    if (product.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(product, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        name = product;
                        break;
                    }
                }
                catch { }
            }
        }
        else
        {
            // default device — ask CoreAudio what it actually is, for Bluetooth detection
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                name = dev.FriendlyName;
            }
            catch { name = ""; }
        }

        bool isBt = BluetoothMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
        return (index, name, isBt);
    }

    private void TryOpenAndStartLocked()
    {
        var (index, name, isBt) = ResolveDevice();
        _isBluetooth = isBt;
        int previousRate = _actualRate;

        if (isBt)
        {
            OpenLinkHolderLocked(name);
            EnsureMicVolume(name); // Windows loves parking HFP mic levels at ~37%
        }

        // Bluetooth links can refuse the first open right after waking — try twice
        for (int attempt = 0; attempt < 2; attempt++)
        {
            foreach (int rate in new[] { 16000, 48000, 44100 })
            {
                try
                {
                    _waveIn = new WaveInEvent
                    {
                        DeviceNumber = index,
                        WaveFormat = new WaveFormat(rate, 16, 1),
                        BufferMilliseconds = 40,
                        NumberOfBuffers = 8, // extra slack so BT stutters don't starve the driver
                    };
                    _waveIn.DataAvailable += OnData;
                    _waveIn.RecordingStopped += OnStopped;
                    _waveIn.StartRecording();
                    // rate changed mid-capture (device reopened differently): convert what
                    // we already have so the final buffer is a single consistent rate
                    if (_capturing && rate != previousRate && _current.Count > 0)
                        _current = new List<float>(Resample(_current.ToArray(), previousRate, rate));
                    _actualRate = rate;
                    _ring = new float[rate];
                    _ringPos = 0;
                    _ringFilled = 0;
                    _deviceRunning = true;
                    if (_capturing)
                        _liveResampler = new ChunkResampler(rate, 16000);
                    Logger.Log($"mic open: \"{name}\" at {rate} Hz{(isBt ? " (bluetooth mode)" : "")}");
                    return;
                }
                catch (Exception ex)
                {
                    _waveIn?.Dispose();
                    _waveIn = null;
                    Logger.Log($"mic open \"{name}\" at {rate} Hz failed: {ex.Message}");
                }
            }
            if (attempt == 0)
                Thread.Sleep(350);
        }
        _deviceRunning = false;
        Error?.Invoke("Couldn't open the microphone. Check it's connected (Bluetooth: make sure the AirPods are paired and awake).");
    }

    private void CloseDeviceLocked()
    {
        CancelLingerLocked();
        if (_waveIn != null)
        {
            try { _waveIn.StopRecording(); } catch { }
            _waveIn.DataAvailable -= OnData;
            _waveIn.RecordingStopped -= OnStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
        CloseLinkHolderLocked();
        _deviceRunning = false;
        _ringFilled = 0;
    }

    /// <summary>
    /// Force the Bluetooth voice (SCO) link up by streaming silence to the headset's
    /// hands-free render endpoint, and keep it up for the life of the capture device.
    /// Without this, Windows sometimes opens the mic endpoint successfully while the
    /// voice channel stays down — the mic delivers pure zeros and dictation "hears"
    /// nothing. A2DP is muted while this is open, but the mic being open does that anyway.
    /// </summary>
    private void OpenLinkHolderLocked(string micName)
    {
        if (_linkOut != null) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? pick = null;
            int bestScore = -1;
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string fn;
                try { fn = dev.FriendlyName; } catch { dev.Dispose(); continue; }
                if (!fn.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase))
                {
                    dev.Dispose();
                    continue;
                }
                // prefer the render endpoint belonging to the same headset as the mic
                int score = fn.Split(' ', '(', ')', '-')
                              .Count(w => w.Length >= 4 && micName.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (score > bestScore)
                {
                    pick?.Dispose();
                    pick = dev;
                    bestScore = score;
                }
                else dev.Dispose();
            }
            if (pick == null) return;

            _linkDevice = pick;
            _linkOut = new WasapiOut(pick, AudioClientShareMode.Shared, true, 200);
            _linkOut.Init(new SilenceProvider(new WaveFormat(16000, 16, 1)));
            _linkOut.Play();
            Logger.Log($"voice link held open via \"{pick.FriendlyName}\"");
            Thread.Sleep(250); // give the SCO link a beat to come up before opening the mic
        }
        catch (Exception ex)
        {
            Logger.Log($"voice link holder unavailable (non-fatal): {ex.Message}");
            CloseLinkHolderLocked();
        }
    }

    private void CloseLinkHolderLocked()
    {
        try { _linkOut?.Stop(); } catch { }
        try { _linkOut?.Dispose(); } catch { }
        _linkOut = null;
        try { _linkDevice?.Dispose(); } catch { }
        _linkDevice = null;
    }

    /// <summary>Mic is open but hearing nothing real — cycle it; on the second strike,
    /// abandon the Bluetooth mic for the rest of the session and use the default mic.</summary>
    private void KickDeadLink(bool fallbackToDefault)
    {
        lock (_lock)
        {
            if (!_capturing) return;
            if (fallbackToDefault)
            {
                int idx = FindFallbackIndexLocked();
                if (idx != -2)
                {
                    _forceDeviceIndex = idx;
                    Logger.Log("bluetooth mic is up but hearing nothing — switching to the default mic for this session");
                }
                else Logger.Log("bluetooth mic hearing nothing and no other mic exists — cycling again");
            }
            else Logger.Log("bluetooth mic hearing nothing — cycling the voice link");
            CloseDeviceLocked();
        }
        Thread.Sleep(300);
        lock (_lock)
        {
            if (!_capturing) return;
            TryOpenAndStartLocked();
        }
    }

    /// <summary>Best non-Bluetooth waveIn device: the system default capture device if
    /// it has one, otherwise the first wired/USB mic.</summary>
    private static int FindFallbackIndexLocked()
    {
        string def = "";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            def = d.FriendlyName;
        }
        catch { }

        int firstNonBt = -2;
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            string product;
            try { product = WaveInEvent.GetCapabilities(i).ProductName; } catch { continue; }
            if (BluetoothMarkers.Any(m => product.Contains(m, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (firstNonBt == -2) firstNonBt = i;
            // waveIn names are truncated to 31 chars, so match by prefix either way
            if (def.Contains(product, StringComparison.OrdinalIgnoreCase) ||
                product.Contains(def, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return firstNonBt;
    }

    /// <summary>Windows quietly parks Bluetooth hands-free mic levels low (37% observed),
    /// which buries quiet speech. Pin the endpoint at 100% whenever we open it.</summary>
    private static void EnsureMicVolume(string micName)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string fn;
                try { fn = dev.FriendlyName; } catch { dev.Dispose(); continue; }
                bool match = fn.Contains(micName, StringComparison.OrdinalIgnoreCase) ||
                             micName.Contains(fn, StringComparison.OrdinalIgnoreCase);
                if (match)
                {
                    var vol = dev.AudioEndpointVolume;
                    if (vol.Mute) vol.Mute = false;
                    if (vol.MasterVolumeLevelScalar < 0.99f)
                    {
                        Logger.Log($"mic endpoint volume was {vol.MasterVolumeLevelScalar:P0} — raising to 100%");
                        vol.MasterVolumeLevelScalar = 1f;
                    }
                }
                dev.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"mic volume check failed (non-fatal): {ex.Message}");
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int n = e.BytesRecorded / 2;
        if (n == 0) return;

        // raw RMS first, so auto-gain can adapt before we scale
        double sumSqRaw = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(e.Buffer[2 * i] | (e.Buffer[2 * i + 1] << 8));
            double f = s / 32768.0;
            sumSqRaw += f * f;
        }
        double rmsRaw = Math.Sqrt(sumSqRaw / n);

        // dead-link watchdog: a half-open Bluetooth voice link delivers a faint
        // dither floor (rms ~0.0003) instead of real audio — indistinguishable from
        // silence but never exactly zero. If a capture starts and no real signal
        // shows up, cycle the device; if it's still deaf after that, switch to the
        // default wired/USB mic so dictation keeps working. Once real audio has
        // been seen the link is proven live and the watchdog disarms (so pauses
        // mid-sentence never trigger it).
        if (rmsRaw > 0.0015)
        {
            lock (_lock) { _sawRealAudio = true; _quietRunSamples = 0; }
        }
        else
        {
            bool kick = false, fallback = false;
            lock (_lock)
            {
                if (_capturing && _isBluetooth && !_sawRealAudio)
                {
                    _quietRunSamples += n;
                    if (_quietRunSamples > _actualRate * 3 / 2 && _deadKicks < 2)
                    {
                        _quietRunSamples = 0;
                        _deadKicks++;
                        kick = true;
                        fallback = _deadKicks >= 2;
                    }
                }
            }
            if (kick)
            {
                ThreadPool.QueueUserWorkItem(_ => KickDeadLink(fallback));
                return;
            }
        }

        // soft auto-gain: only ever boosts (1x..8x), adapts slowly, ignores silence.
        // Bluetooth hands-free mics often arrive very quiet.
        if (rmsRaw > 0.004)
        {
            double desired = Math.Clamp(0.08 / rmsRaw, 1.0, 10.0);
            _agcGain += (desired - _agcGain) * 0.08;
        }
        double factor = _gain * _agcGain;

        var floats = new float[n];
        double sumSq = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(e.Buffer[2 * i] | (e.Buffer[2 * i + 1] << 8));
            float f = (float)Math.Clamp(s / 32768.0 * factor, -1.0, 1.0);
            floats[i] = f;
            sumSq += f * f;
        }

        bool capturing;
        bool firstAudio = false;
        ChunkResampler? liveResampler;
        lock (_lock)
        {
            capturing = _capturing;
            liveResampler = _liveResampler;
            if (capturing)
            {
                _current.AddRange(floats);
                // the ready beep waits for real signal — an all-zeros buffer means the
                // BT voice link isn't actually delivering audio yet
                if (_firstAudioPending && rmsRaw > 0)
                {
                    _firstAudioPending = false;
                    firstAudio = true;
                }
            }
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

        if (firstAudio)
            FirstAudio?.Invoke();

        if (capturing)
        {
            Level?.Invoke((float)Math.Sqrt(sumSq / n));
            var mags = ComputeSpectrum(floats, _actualRate);
            lock (_lock)
            {
                // bands are log-spaced 90 Hz → 6.5 kHz; 5..9 ≈ speech mids, 13..15 ≥ ~4 kHz
                _midBandSum += (mags[5] + mags[6] + mags[7] + mags[8] + mags[9]) / 5.0;
                _hiBandSum += (mags[13] + mags[14] + mags[15]) / 3.0;
                _bandFrames++;
            }
            Spectrum?.Invoke(mags);
            if (Samples16k != null && liveResampler != null)
            {
                var live = liveResampler.Process(floats);
                if (live.Length > 0)
                    Samples16k.Invoke(live);
            }
        }
    }

    /// <summary>Cheap Goertzel magnitudes at log-spaced speech frequencies for the equalizer.</summary>
    private static float[] ComputeSpectrum(float[] frame, int rate)
    {
        const int bands = 16;
        var mags = new float[bands];
        double fMin = 90, fMax = Math.Min(6500, rate / 2.0 - 200);
        for (int b = 0; b < bands; b++)
        {
            double freq = fMin * Math.Pow(fMax / fMin, b / (double)(bands - 1));
            double w = 2 * Math.PI * freq / rate;
            double coeff = 2 * Math.Cos(w);
            double s0, s1 = 0, s2 = 0;
            for (int i = 0; i < frame.Length; i++)
            {
                s0 = frame[i] + coeff * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            mags[b] = (float)Math.Min(1.0, Math.Sqrt(Math.Max(0, power)) / frame.Length * 40);
        }
        return mags;
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null) return;
        Logger.Log(e.Exception, "recording stopped unexpectedly");

        // Bluetooth links hiccup — if it dies mid-dictation, re-open and keep the take
        // alive. AirPods take 1-2s to re-establish the hands-free link, so retry hard.
        bool wasCapturing;
        lock (_lock)
        {
            _deviceRunning = false;
            wasCapturing = _capturing;
            if (wasCapturing)
                CloseDeviceLocked();
        }
        if (!wasCapturing) return;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 300 : 700);
            lock (_lock)
            {
                if (!_capturing) return; // user released the key meanwhile
                TryOpenAndStartLocked();
                if (_deviceRunning)
                {
                    Logger.Log($"mic recovered mid-dictation after dropout (attempt {attempt + 1})");
                    return;
                }
            }
        }

        Logger.Log("mic recovery failed after 4 attempts — handing back what was captured");
        Error?.Invoke("Bluetooth dropped. Inserted what was caught before the cutout.");
        RecoveryFailed?.Invoke();
    }

    /// <summary>Mid-dictation recovery gave up — the controller should stop and use what was captured.</summary>
    public event Action? RecoveryFailed;

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
        lock (_lock) { CloseDeviceLocked(); }
    }
}

/// <summary>
/// Linear resampler that carries its fractional position and last sample across
/// chunks, so a continuous stream can be resampled chunk-by-chunk without seams.
/// </summary>
public sealed class ChunkResampler
{
    private readonly double _step;  // source samples advanced per output sample
    private double _frac;           // fractional position inside the current source interval
    private float _prev;
    private bool _hasPrev;

    public ChunkResampler(int srcRate, int dstRate)
    {
        _step = (double)srcRate / dstRate;
    }

    public float[] Process(float[] chunk)
    {
        if (chunk.Length == 0) return Array.Empty<float>();
        if (Math.Abs(_step - 1.0) < 1e-9) return chunk;

        var output = new List<float>((int)(chunk.Length / _step) + 2);
        foreach (var s in chunk)
        {
            if (!_hasPrev)
            {
                _prev = s;
                _hasPrev = true;
                continue;
            }
            while (_frac < 1.0)
            {
                output.Add((float)(_prev * (1 - _frac) + s * _frac));
                _frac += _step;
            }
            _frac -= 1.0;
            _prev = s;
        }
        return output.ToArray();
    }
}

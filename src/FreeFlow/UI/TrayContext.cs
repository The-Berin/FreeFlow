using System.Diagnostics;
using FreeFlow.Core;

namespace FreeFlow.UI;

/// <summary>
/// The conductor: owns the tray icon and both recognition engines, and wires
/// hotkey → recorder → streaming words → live typing → final polish → injection.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private enum FlowState { Idle, Recording, RecordingLatched, Processing }

    private AppConfig _cfg;
    private readonly SttEngine _engine = new();          // accurate batch model (Parakeet/Whisper)
    private readonly StreamingEngine _streaming = new(); // live word-by-word model
    private readonly AudioRecorder _recorder = new();
    private readonly KeyboardHook _hook = new();
    private readonly OverlayForm _overlay = new();
    private readonly LiveTyper _liveTyper = new();
    private readonly NotifyIcon _tray;

    private FlowState _state = FlowState.Idle;
    private bool _enabled = true;
    private bool _commandCapture;
    private DateTime _keyDownAt;
    private (string ProcessName, string Title) _targetApp;
    private string _livePrefix = "";

    // partial-result coalescing onto the UI thread
    private volatile string _pendingPartial = "";
    private int _partialScheduled;

    // smart spacing state
    private int _lastInjectedLength;
    private string _lastInjectedApp = "";
    private DateTime _lastInjectedAt = DateTime.MinValue;
    private volatile bool _typedSinceInjection = true;

    private MainForm? _mainForm;

    public AppConfig Config => _cfg;
    public SttEngine Engine => _engine;
    public StreamingEngine Streaming => _streaming;
    public AudioRecorder Recorder => _recorder;
    public KeyboardHook Hook => _hook;
    public bool Enabled { get => _enabled; set => SetEnabled(value); }

    public event Action? EngineStateChanged;

    public TrayContext(AppConfig cfg, bool showMainWindow)
    {
        _cfg = cfg;

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Loading,
            Text = "FreeFlow — loading models…",
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildMenu();
        _tray.DoubleClick += (_, _) => OpenMain();

        _ = _overlay.Handle; // create the handle so BeginInvoke works from any thread
        _overlay.StopRequested += () => _overlay.BeginInvoke(OnPillClicked);

        _recorder.Level += rms => { }; // reserved for the app window mic meter
        _recorder.Spectrum += bands => _overlay.PushSpectrum(bands);
        _recorder.Samples16k += chunk => _streaming.Feed(chunk);
        _recorder.Error += msg => _overlay.SetState(OverlayState.Error, msg);
        _recorder.Configure(_cfg);

        _streaming.Partial += OnPartialFromDecoder;

        _hook.HotkeyDown += OnHotkeyDown;
        _hook.HotkeyUp += OnHotkeyUp;
        _hook.CommandKeyDown += OnCommandKeyDown;
        _hook.CommandKeyUp += OnCommandKeyUp;
        _hook.OtherKeyPressed += () => _typedSinceInjection = true;
        try
        {
            _hook.Install(_cfg);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "installing keyboard hook");
            MessageBox.Show($"Could not install the global hotkey hook:\n{ex.Message}",
                "FreeFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        LoadEnginesInBackground(firstLoad: true);

        if (showMainWindow || !_cfg.FirstRunDone)
        {
            _cfg.FirstRunDone = true;
            _cfg.Save();
            OpenMain();
        }
    }

    private void LoadEnginesInBackground(bool firstLoad)
    {
        SetTray(TrayIcons.Loading, "FreeFlow — loading models…");
        Task.Run(async () =>
        {
            // make sure the small live-mode models exist (streaming 72 MB + punct 7 MB)
            foreach (var id in new[] { ModelRegistry.StreamingModelId, ModelRegistry.PunctModelId })
            {
                var m = ModelRegistry.Get(id);
                if (!m.IsDownloaded())
                {
                    try
                    {
                        SetTraySafe(TrayIcons.Loading, $"FreeFlow — downloading {m.Id}…");
                        await ModelDownloader.DownloadAsync(m, null, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, $"auto-downloading {m.Id}");
                    }
                }
            }

            _streaming.Load(_cfg);
            _engine.Load(_cfg);

            _overlay.BeginInvoke(() =>
            {
                EngineStateChanged?.Invoke();
                if (_engine.IsLoaded || _streaming.IsLoaded)
                {
                    SetTray(_enabled ? TrayIcons.Idle : TrayIcons.Disabled,
                        $"FreeFlow — hold {_cfg.HotkeyName} to dictate");
                }
                else
                {
                    SetTray(TrayIcons.Disabled, "FreeFlow — no model loaded");
                    if (firstLoad)
                        OpenMain();
                }
            });
        });
    }

    #region hotkey handling

    private void OnHotkeyDown()
    {
        _overlay.BeginInvoke(() =>
        {
            if (!_enabled || (!_engine.IsLoaded && !_streaming.IsLoaded)) return;

            switch (_state)
            {
                case FlowState.RecordingLatched:
                    StopAndProcess();
                    break;
                case FlowState.Idle:
                    _keyDownAt = DateTime.UtcNow;
                    StartRecording(command: false);
                    break;
            }
        });
    }

    private void OnHotkeyUp()
    {
        _overlay.BeginInvoke(() =>
        {
            if (_state != FlowState.Recording || _commandCapture) return;

            var heldMs = (DateTime.UtcNow - _keyDownAt).TotalMilliseconds;
            if (_cfg.TapToLatch && heldMs < _cfg.TapThresholdMs)
            {
                _state = FlowState.RecordingLatched;
                _overlay.SetState(OverlayState.Latched);
            }
            else
            {
                StopAndProcess();
            }
        });
    }

    private void OnCommandKeyDown()
    {
        _overlay.BeginInvoke(() =>
        {
            if (!_enabled || !_engine.IsLoaded || _state != FlowState.Idle) return;
            if (!_cfg.AiEnabled)
            {
                _overlay.SetState(OverlayState.Error, "Command mode needs an AI endpoint (Settings → AI)");
                return;
            }
            StartRecording(command: true);
        });
    }

    private void OnCommandKeyUp()
    {
        _overlay.BeginInvoke(() =>
        {
            if (_state == FlowState.Recording && _commandCapture)
                StopAndProcess();
        });
    }

    private void OnPillClicked()
    {
        if (_state is FlowState.Recording or FlowState.RecordingLatched)
            StopAndProcess();
    }

    #endregion

    #region recording pipeline

    private void StartRecording(bool command)
    {
        _commandCapture = command;
        _targetApp = ForegroundApp.Get();

        _livePrefix = _cfg.SmartSpacing &&
                      !_typedSinceInjection &&
                      _lastInjectedApp == _targetApp.ProcessName &&
                      (DateTime.UtcNow - _lastInjectedAt) < TimeSpan.FromSeconds(90) &&
                      _lastInjectedLength > 0
            ? " " : "";

        if (_streaming.IsLoaded)
            _streaming.StartSession();
        if (!command && _cfg.LiveTyping && _streaming.IsLoaded)
            _liveTyper.Begin();

        _recorder.StartCapture();
        _state = FlowState.Recording;
        if (_cfg.ShowPill)
            _overlay.SetState(OverlayState.Listening);
        SetTray(TrayIcons.Recording, "FreeFlow — listening");
        SoundFx.RecordStart(_cfg);
    }

    private void OnPartialFromDecoder(string text)
    {
        if (!_liveTyper.Active) return;

        _pendingPartial = text;
        if (Interlocked.CompareExchange(ref _partialScheduled, 1, 0) == 0)
        {
            _overlay.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _partialScheduled, 0);
                if (_liveTyper.Active)
                    _liveTyper.OnPartial(_pendingPartial, _cfg, _livePrefix);
            });
        }
    }

    private void StopAndProcess()
    {
        var samples = _recorder.StopCapture();
        bool isCommand = _commandCapture;
        _commandCapture = false;
        bool wasLive = _liveTyper.Active;
        string livePrefix = _livePrefix;
        _state = FlowState.Processing;
        SoundFx.RecordStop(_cfg);
        _overlay.SetState(OverlayState.Processing);
        SetTray(TrayIcons.Processing, "FreeFlow — processing");

        Task.Run(async () =>
        {
            // drain the live decoder first — its final text is our fallback
            string rawStream = _streaming.FinishSession();
            double audioSec = samples.Length / 16000.0;

            if (audioSec < 0.35)
            {
                Finish(() =>
                {
                    if (wasLive) _liveTyper.Erase();
                    _overlay.SetState(OverlayState.Hidden);
                });
                return;
            }

            if (isCommand)
            {
                await RunCommandMode(samples, rawStream);
                return;
            }

            var profile = _cfg.ProfileFor(_targetApp.ProcessName);
            string tone = profile?.Tone is { Length: > 0 } and not "Default" ? profile!.Tone : _cfg.DefaultTone;
            string injectMode = profile?.InjectMode ?? "Paste";

            // produce the best final text we can
            var sw = Stopwatch.StartNew();
            string finalRaw = "";
            if (_cfg.FinalPass == "parakeet" || !wasLive)
            {
                try
                {
                    if (_engine.IsLoaded)
                        finalRaw = _engine.Transcribe(samples);
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "final-pass transcription");
                }
            }
            if (string.IsNullOrWhiteSpace(finalRaw))
                finalRaw = _cfg.FinalPass == "off" ? rawStream : _streaming.Punctuate(rawStream);
            sw.Stop();
            Logger.Log($"utterance {audioSec:0.0}s → final in {sw.ElapsedMilliseconds}ms: \"{Truncate(finalRaw, 80)}\"");

            if (string.IsNullOrWhiteSpace(finalRaw))
            {
                Finish(() =>
                {
                    if (wasLive) _liveTyper.Erase();
                    SoundFx.ErrorTone(_cfg);
                    _overlay.SetState(OverlayState.Error, "Didn't catch that");
                });
                return;
            }

            var result = TextFormatter.Format(finalRaw, _cfg, tone);
            string text = result.Text;

            if (_cfg.AiPolish && _cfg.AiEnabled && !result.DeleteLast && text.Length > 0 && tone != "Verbatim")
            {
                try
                {
                    var polished = await AiProvider.PolishAsync(_cfg, text);
                    if (!string.IsNullOrWhiteSpace(polished))
                        text = polished;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "AI polish (using local formatting)");
                }
            }

            Finish(() =>
            {
                if (result.DeleteLast)
                {
                    if (wasLive) _liveTyper.Erase();
                    if (_lastInjectedLength > 0)
                    {
                        TextInjector.SendBackspaces(_lastInjectedLength);
                        _lastInjectedLength = 0;
                        _overlay.SetState(OverlayState.Success, "deleted");
                    }
                    else
                    {
                        _overlay.SetState(OverlayState.Error, "Nothing to scratch");
                    }
                    return;
                }

                if (text.Length == 0)
                {
                    if (wasLive) _liveTyper.Erase();
                    _overlay.SetState(OverlayState.Hidden);
                    return;
                }

                string finalWithPrefix = livePrefix.Length > 0 && text[0] != '\n' && !char.IsPunctuation(text[0])
                    ? livePrefix + text
                    : text;

                if (wasLive)
                {
                    _liveTyper.Finalize(finalWithPrefix, injectMode);
                }
                else
                {
                    TextInjector.Inject(finalWithPrefix, injectMode);
                }

                _lastInjectedLength = finalWithPrefix.Length;
                _lastInjectedApp = _targetApp.ProcessName;
                _lastInjectedAt = DateTime.UtcNow;
                _typedSinceInjection = false;

                int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                StatsStore.RecordDictation(words, audioSec, sw.ElapsedMilliseconds);
                HistoryStore.Append(_cfg, new HistoryEntry
                {
                    Ts = DateTime.Now,
                    App = _targetApp.ProcessName,
                    Raw = finalRaw,
                    Text = text,
                });

                _overlay.SetState(OverlayState.Success);
            });
        });
    }

    private async Task RunCommandMode(float[] samples, string rawStream)
    {
        string instruction = rawStream;
        try
        {
            if (_engine.IsLoaded)
                instruction = _engine.Transcribe(samples);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "command instruction transcription");
        }
        if (string.IsNullOrWhiteSpace(instruction))
        {
            Finish(() => _overlay.SetState(OverlayState.Error, "Didn't catch the command"));
            return;
        }

        string? selection = null;
        var done = new SemaphoreSlim(0);
        _overlay.BeginInvoke(() =>
        {
            selection = TextInjector.CopySelection();
            done.Release();
        });
        await done.WaitAsync();

        if (string.IsNullOrWhiteSpace(selection))
        {
            Finish(() =>
            {
                SoundFx.ErrorTone(_cfg);
                _overlay.SetState(OverlayState.Error, "Select some text first");
            });
            return;
        }

        string? rewritten = null;
        try
        {
            rewritten = await AiProvider.RewriteAsync(_cfg, instruction, selection!);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "command mode rewrite");
        }

        Finish(() =>
        {
            if (string.IsNullOrWhiteSpace(rewritten))
            {
                SoundFx.ErrorTone(_cfg);
                _overlay.SetState(OverlayState.Error, "AI rewrite failed — see log");
                return;
            }
            TextInjector.PasteText(rewritten);
            HistoryStore.Append(_cfg, new HistoryEntry
            {
                Ts = DateTime.Now,
                App = _targetApp.ProcessName,
                Raw = $"[command] {instruction}",
                Text = rewritten,
            });
            _overlay.SetState(OverlayState.Success, "rewritten");
        });
    }

    private void Finish(Action uiAction)
    {
        _overlay.BeginInvoke(() =>
        {
            uiAction();
            _state = FlowState.Idle;
            SetTray(_enabled ? TrayIcons.Idle : TrayIcons.Disabled, "FreeFlow — ready");
        });
    }

    #endregion

    #region tray, windows, config

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open FreeFlow") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        openItem.Click += (_, _) => OpenMain();

        var enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) => SetEnabled(enabledItem.Checked);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(openItem);
        menu.Items.Add(enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void SetEnabled(bool value)
    {
        _enabled = value;
        SetTray(_enabled ? TrayIcons.Idle : TrayIcons.Disabled,
            _enabled ? "FreeFlow — ready" : "FreeFlow — disabled");
        EngineStateChanged?.Invoke();
    }

    public void OpenMain()
    {
        if (_mainForm is { IsDisposed: false })
        {
            _mainForm.Show();
            _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            return;
        }
        _mainForm = new MainForm(this);
        _mainForm.Show();
    }

    public void ApplyNewConfig(AppConfig fresh)
    {
        bool modelChanged = fresh.ModelId != _cfg.ModelId
                            || fresh.NumThreads != _cfg.NumThreads
                            || fresh.Language != _cfg.Language;
        bool hookChanged = fresh.HotkeyName != _cfg.HotkeyName
                           || fresh.CommandHotkeyName != _cfg.CommandHotkeyName
                           || fresh.SwallowHotkey != _cfg.SwallowHotkey;
        bool audioChanged = fresh.MicDevice != _cfg.MicDevice
                            || fresh.KeepMicWarm != _cfg.KeepMicWarm
                            || Math.Abs(fresh.MicGain - _cfg.MicGain) > 0.001;

        _cfg = fresh;
        _cfg.Save();

        if (hookChanged)
        {
            try { _hook.Install(_cfg); }
            catch (Exception ex) { Logger.Log(ex, "reinstalling hook"); }
        }
        if (audioChanged)
            _recorder.Configure(_cfg);
        if (modelChanged || !_engine.IsLoaded || !_streaming.IsLoaded)
            LoadEnginesInBackground(firstLoad: false);

        EngineStateChanged?.Invoke();
    }

    private void SetTray(Icon icon, string text)
    {
        _tray.Icon = icon;
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void SetTraySafe(Icon icon, string text)
    {
        if (_overlay.InvokeRequired)
            _overlay.BeginInvoke(() => SetTray(icon, text));
        else
            SetTray(icon, text);
    }

    public void ExitApp()
    {
        _tray.Visible = false;
        _hook.Dispose();
        _recorder.Dispose();
        _streaming.Dispose();
        _engine.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _hook.Dispose();
            _recorder.Dispose();
            _streaming.Dispose();
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Tray icons drawn from the brand tile — violet wave, tinted per state.</summary>
public static class TrayIcons
{
    public static readonly Icon Idle = Make(Color.FromArgb(109, 74, 255), Color.FromArgb(175, 74, 255));
    public static readonly Icon Recording = Make(Color.FromArgb(235, 60, 80), Color.FromArgb(255, 110, 70));
    public static readonly Icon Processing = Make(Color.FromArgb(60, 110, 235), Color.FromArgb(110, 74, 255));
    public static readonly Icon Loading = Make(Color.FromArgb(230, 150, 50), Color.FromArgb(255, 190, 80));
    public static readonly Icon Disabled = Make(Color.FromArgb(95, 95, 105), Color.FromArgb(120, 120, 132));

    private static Icon Make(Color top, Color bottom)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, 32, 32);
            using var path = Draw.RoundedRect(rect, 8);
            using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(rect, top, bottom, 55f);
            g.FillPath(grad, path);
            Draw.Wave(g, rect, 2.6f, Color.White);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

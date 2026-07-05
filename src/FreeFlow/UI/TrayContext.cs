using System.Diagnostics;
using FreeFlow.Core;
using Microsoft.Win32;

namespace FreeFlow.UI;

/// <summary>
/// The conductor: owns the tray icon, wires hotkey → recorder → recognizer →
/// formatter → injector, and hosts the settings/history windows.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private enum FlowState { Idle, Recording, RecordingLatched, Processing }

    private AppConfig _cfg;
    private readonly SttEngine _engine = new();
    private readonly AudioRecorder _recorder = new();
    private readonly KeyboardHook _hook = new();
    private readonly OverlayForm _overlay = new();
    private readonly NotifyIcon _tray;

    private FlowState _state = FlowState.Idle;
    private bool _enabled = true;
    private bool _commandCapture;         // current recording is a command-mode instruction
    private DateTime _keyDownAt;
    private (string ProcessName, string Title) _targetApp;

    // smart spacing state
    private int _lastInjectedLength;
    private string _lastInjectedApp = "";
    private DateTime _lastInjectedAt = DateTime.MinValue;
    private volatile bool _typedSinceInjection = true;

    private SettingsForm? _settingsForm;
    private HistoryForm? _historyForm;

    public TrayContext(AppConfig cfg)
    {
        _cfg = cfg;

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "FreeFlow — loading model…",
            Visible = true,
        };
        _tray.ContextMenuStrip = BuildMenu();
        _tray.DoubleClick += (_, _) => OpenSettings();

        // force handle creation so BeginInvoke works from any thread
        _ = _overlay.Handle;

        _recorder.Level += rms => _overlay.PushLevel(rms);
        _recorder.Error += msg => _overlay.SetState(OverlayState.Error, msg);
        _recorder.Configure(_cfg);

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

        LoadEngineInBackground(firstLoad: true);

        if (!_cfg.FirstRunDone)
        {
            _cfg.FirstRunDone = true;
            _cfg.Save();
            _tray.ShowBalloonTip(6000, "FreeFlow is running",
                $"Hold {_cfg.HotkeyName} and speak — release to insert text. Double-click the tray icon for settings.",
                ToolTipIcon.Info);
        }
    }

    private void LoadEngineInBackground(bool firstLoad)
    {
        SetTray(TrayIcons.Loading, "FreeFlow — loading model…");
        Task.Run(() =>
        {
            _engine.Load(_cfg);
            _overlay.BeginInvoke(() =>
            {
                if (_engine.IsLoaded)
                {
                    SetTray(_enabled ? TrayIcons.Idle : TrayIcons.Disabled,
                        $"FreeFlow — ready (hold {_cfg.HotkeyName} to dictate)");
                }
                else
                {
                    SetTray(TrayIcons.Disabled, "FreeFlow — model not loaded");
                    if (firstLoad)
                    {
                        _tray.ShowBalloonTip(8000, "FreeFlow needs a model",
                            _engine.LoadError ?? "Open Settings → Model to download one.", ToolTipIcon.Warning);
                        OpenSettings(selectModelPage: true);
                    }
                    else
                    {
                        MessageBox.Show(_engine.LoadError, "FreeFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            });
        });
    }

    #region hotkey handling

    private void OnHotkeyDown()
    {
        _overlay.BeginInvoke(() =>
        {
            if (!_enabled || !_engine.IsLoaded) return;

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

    #endregion

    private void StartRecording(bool command)
    {
        _commandCapture = command;
        _targetApp = ForegroundApp.Get();
        _recorder.StartCapture();
        _state = FlowState.Recording;
        _overlay.SetState(OverlayState.Listening);
        SetTray(TrayIcons.Recording, "FreeFlow — listening");
        SoundFx.RecordStart(_cfg);
    }

    private void StopAndProcess()
    {
        var samples = _recorder.StopCapture();
        bool isCommand = _commandCapture;
        _commandCapture = false;
        _state = FlowState.Processing;
        SoundFx.RecordStop(_cfg);

        // too short to be intentional speech
        if (samples.Length < 16000 * 0.35)
        {
            _state = FlowState.Idle;
            _overlay.SetState(OverlayState.Hidden);
            SetTray(TrayIcons.Idle, "FreeFlow — ready");
            return;
        }

        _overlay.SetState(OverlayState.Processing);
        SetTray(TrayIcons.Processing, "FreeFlow — processing");

        Task.Run(async () =>
        {
            string raw;
            var sw = Stopwatch.StartNew();
            try
            {
                raw = _engine.Transcribe(samples);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "transcription");
                Finish(() => _overlay.SetState(OverlayState.Error, "Transcription failed — see log"));
                return;
            }
            Logger.Log($"transcribed {samples.Length / 16000.0:0.0}s audio in {sw.ElapsedMilliseconds}ms: \"{Truncate(raw, 80)}\"");

            if (string.IsNullOrWhiteSpace(raw))
            {
                Finish(() =>
                {
                    SoundFx.ErrorTone(_cfg);
                    _overlay.SetState(OverlayState.Error, "Didn't catch that");
                });
                return;
            }

            if (isCommand)
                await RunCommandMode(raw);
            else
                await RunDictation(raw);
        });
    }

    private async Task RunDictation(string raw)
    {
        var profile = _cfg.ProfileFor(_targetApp.ProcessName);
        string tone = profile?.Tone is { Length: > 0 } and not "Default" ? profile!.Tone : _cfg.DefaultTone;
        string injectMode = profile?.InjectMode ?? "Paste";

        var result = TextFormatter.Format(raw, _cfg, tone);
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
                Logger.Log(ex, "AI polish (falling back to local formatting)");
            }
        }

        Finish(() =>
        {
            if (result.DeleteLast)
            {
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
                _overlay.SetState(OverlayState.Hidden);
                return;
            }

            // smart spacing: dictating again into the same app without typing → add a space
            if (_cfg.SmartSpacing &&
                !_typedSinceInjection &&
                _lastInjectedApp == _targetApp.ProcessName &&
                (DateTime.UtcNow - _lastInjectedAt) < TimeSpan.FromSeconds(90) &&
                _lastInjectedLength > 0 &&
                text[0] != '\n' && !char.IsPunctuation(text[0]))
            {
                text = " " + text;
            }

            TextInjector.Inject(text, injectMode);
            _lastInjectedLength = text.Length;
            _lastInjectedApp = _targetApp.ProcessName;
            _lastInjectedAt = DateTime.UtcNow;
            _typedSinceInjection = false;

            HistoryStore.Append(_cfg, new HistoryEntry
            {
                Ts = DateTime.Now,
                App = _targetApp.ProcessName,
                Raw = raw,
                Text = text,
            });

            _overlay.SetState(OverlayState.Success);
        });
    }

    private async Task RunCommandMode(string instruction)
    {
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
        string? error = null;
        try
        {
            rewritten = await AiProvider.RewriteAsync(_cfg, instruction, selection!);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "command mode rewrite");
            error = ex.Message;
        }

        Finish(() =>
        {
            if (string.IsNullOrWhiteSpace(rewritten))
            {
                SoundFx.ErrorTone(_cfg);
                _overlay.SetState(OverlayState.Error, error != null ? "AI error — see log" : "AI returned nothing");
                return;
            }
            TextInjector.PasteText(rewritten); // replaces the still-highlighted selection
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

    /// <summary>Marshal a completion action onto the UI thread and reset state.</summary>
    private void Finish(Action uiAction)
    {
        _overlay.BeginInvoke(() =>
        {
            uiAction();
            _state = FlowState.Idle;
            SetTray(_enabled ? TrayIcons.Idle : TrayIcons.Disabled, "FreeFlow — ready");
        });
    }

    #region tray & windows

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var enabledItem = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabledItem.CheckedChanged += (_, _) =>
        {
            _enabled = enabledItem.Checked;
            SetTray(_enabled ? TrayIcons.Idle : TrayIcons.Disabled,
                _enabled ? "FreeFlow — ready" : "FreeFlow — disabled");
        };

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => OpenSettings();

        var historyItem = new ToolStripMenuItem("History…");
        historyItem.Click += (_, _) => OpenHistory();

        var logItem = new ToolStripMenuItem("Open log");
        logItem.Click += (_, _) =>
        {
            if (File.Exists(Paths.LogPath))
                Process.Start(new ProcessStartInfo(Paths.LogPath) { UseShellExecute = true });
        };

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void OpenSettings(bool selectModelPage = false)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_cfg.Clone(), ApplyNewConfig);
        if (selectModelPage) _settingsForm.SelectModelPage();
        _settingsForm.Show();
    }

    private void OpenHistory()
    {
        if (_historyForm is { IsDisposed: false })
        {
            _historyForm.Activate();
            return;
        }
        _historyForm = new HistoryForm();
        _historyForm.Show();
    }

    private void ApplyNewConfig(AppConfig fresh)
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
        if (modelChanged || !_engine.IsLoaded)
            LoadEngineInBackground(firstLoad: false);
    }

    private void SetTray(Icon icon, string text)
    {
        _tray.Icon = icon;
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ExitApp()
    {
        _tray.Visible = false;
        _hook.Dispose();
        _recorder.Dispose();
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
            _engine.Dispose();
        }
        base.Dispose(disposing);
    }

    #endregion

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Programmatically drawn tray icons (a small mic capsule), one per state.</summary>
public static class TrayIcons
{
    public static readonly Icon Idle = Make(Color.FromArgb(230, 230, 238));
    public static readonly Icon Recording = Make(Color.FromArgb(235, 70, 70));
    public static readonly Icon Processing = Make(Color.FromArgb(124, 92, 255));
    public static readonly Icon Loading = Make(Color.FromArgb(235, 170, 60));
    public static readonly Icon Disabled = Make(Color.FromArgb(110, 110, 120));

    private static Icon Make(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 3);
            g.FillRectangle(brush, 12, 3, 8, 15);
            g.FillEllipse(brush, 12, 0, 8, 8);
            g.FillEllipse(brush, 12, 14, 8, 8);
            g.DrawArc(pen, 7, 8, 18, 16, 0, 180);
            g.DrawLine(pen, 16, 24, 16, 29);
            g.DrawLine(pen, 10, 29, 22, 29);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

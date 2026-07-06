using System.Runtime.InteropServices;
using FreeFlow.Core;
using Microsoft.Win32;

namespace FreeFlow.UI;

/// <summary>
/// The FreeFlow app window: sidebar navigation, dashboard with live stats,
/// dictation settings, dictionary/shortcuts/profiles editors, history, model
/// manager, and press-any-key hotkey capture. Closing hides to the tray.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TrayContext _ctx;
    private AppConfig Cfg => _ctx.Config;

    private readonly Panel _sidebar = new();
    private readonly Panel _content = new();
    private readonly Dictionary<string, Panel> _pages = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private string _currentPage = "";

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _applyDebounce;
    private bool _loadingUi;

    // Home
    private Label _statusBig = null!, _statusSub = null!, _statWords = null!, _statToday = null!, _statWpm = null!, _statCount = null!;
    private Button _hotkeyPill = null!;
    private Label _sideStatus = null!;

    // Dictation
    private CheckBox _liveTyping = null!, _fillers = null!, _spokenCmds = null!, _punctWords = null!, _smartSpace = null!, _sounds = null!;
    private ComboBox _finalPass = null!, _defaultTone = null!;

    // Settings
    private Button _hotkeyBtn = null!, _cmdHotkeyBtn = null!;
    private CheckBox _tapToLatch = null!, _swallow = null!, _warm = null!, _autostart = null!;
    private NumericUpDown _tapMs = null!, _threads = null!;
    private TrackBar _gain = null!;
    private Label _gainLabel = null!;
    private ComboBox _mic = null!;

    // mic test zone
    private ProgressBar _testLevel = null!;
    private Button _testRecord = null!, _testPlay = null!;
    private Label _testVerdict = null!;
    private float[]? _testClip;
    private bool _testing;
    private TextBox _language = null!;
    private CheckBox _aiEnabled = null!, _aiPolish = null!;
    private TextBox _aiUrl = null!, _aiModel = null!, _aiKey = null!;
    private Label _aiTestResult = null!;

    // Grids
    private DataGridView _dictGrid = null!, _snipGrid = null!, _profGrid = null!;

    // Model page
    private ComboBox _modelPick = null!;
    private Label _modelStatus = null!;
    private ProgressBar _dlBar = null!;
    private Button _dlButton = null!;

    // History
    private ListView _histList = null!;
    private TextBox _histPreview = null!;

    public MainForm(TrayContext ctx)
    {
        _ctx = ctx;

        Text = "FreeFlow";
        Size = new Size(1040, 690);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Back;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")); }
        catch { Icon = TrayIcons.Idle; }

        BuildSidebar();
        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(26, 22, 26, 22);
        _content.BackColor = Theme.Back;
        Controls.Add(_content);
        Controls.Add(_sidebar);

        AddPage("Home", BuildHome());
        AddPage("Dictation", BuildDictation());
        AddPage("Dictionary", BuildDictionary());
        AddPage("Shortcuts", BuildShortcuts());
        AddPage("App Profiles", BuildProfiles());
        AddPage("History", BuildHistory());
        AddPage("Settings", BuildSettings());
        AddPage("Model", BuildModel());
        AddPage("About", BuildAbout());

        Theme.Apply(this);
        LoadUiFromConfig();
        ShowPage("Home");

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();

        _applyDebounce = new System.Windows.Forms.Timer { Interval = 450 };
        _applyDebounce.Tick += (_, _) => { _applyDebounce.Stop(); CommitConfig(); };

        _ctx.EngineStateChanged += OnEngineStateChanged;
        _ctx.Recorder.Level += OnMicLevel;
        FormClosing += (s, e) =>
        {
            // flush any un-debounced or mid-edit changes before the window goes away
            _applyDebounce.Stop();
            foreach (var grid in new[] { _dictGrid, _snipGrid, _profGrid })
                if (grid.IsCurrentCellInEditMode)
                    grid.EndEdit();
            CommitConfig();

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; // the app lives in the tray
                Hide();
            }
        };
        FormClosed += (_, _) =>
        {
            _ctx.EngineStateChanged -= OnEngineStateChanged;
            _ctx.Recorder.Level -= OnMicLevel;
        };

        UseDarkTitleBar();
    }

    private void OnEngineStateChanged()
    {
        if (IsDisposed) return;
        try { BeginInvoke(RefreshStatus); } catch { }
    }

    private void UseDarkTitleBar()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    #region navigation

    private void BuildSidebar()
    {
        _sidebar.Dock = DockStyle.Left;
        _sidebar.Width = 208;
        _sidebar.BackColor = Theme.Panel;

        var logo = new PictureBox
        {
            Size = new Size(44, 44),
            Location = new Point(20, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try { logo.Image = Image.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "logo64.png")); }
        catch { /* assets ship with the app; sidebar just shows text if missing */ }

        var name = new Label
        {
            Text = "FreeFlow",
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Theme.Text,
            Location = new Point(72, 24),
            AutoSize = true,
        };
        var tag = new Label
        {
            Text = "local dictation",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Theme.SubText,
            Location = new Point(74, 50),
            AutoSize = true,
        };
        _sidebar.Controls.Add(logo);
        _sidebar.Controls.Add(name);
        _sidebar.Controls.Add(tag);

        _sideStatus = new Label
        {
            Location = new Point(20, _sidebar.Height - 64),
            AutoSize = false,
            Size = new Size(170, 44),
            ForeColor = Theme.SubText,
            Font = new Font("Segoe UI", 8.5f),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        _sidebar.Controls.Add(_sideStatus);
    }

    private void AddPage(string title, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        page.AutoScroll = true;
        _pages[title] = page;
        _content.Controls.Add(page);

        var btn = new Button
        {
            Text = "    " + title,
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(184, 38),
            Location = new Point(12, 92 + _navButtons.Count * 42),
            Font = new Font("Segoe UI", 10f),
            ForeColor = Theme.Text,
            BackColor = Theme.Panel,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Theme.Field;
        btn.Click += (_, _) => ShowPage(title);
        _navButtons[title] = btn;
        _sidebar.Controls.Add(btn);
    }

    private void ShowPage(string title)
    {
        _currentPage = title;
        foreach (var (k, p) in _pages)
            p.Visible = k == title;
        foreach (var (k, b) in _navButtons)
        {
            b.BackColor = k == title ? Theme.AccentSoft : Theme.Panel;
            b.ForeColor = k == title ? Color.White : Theme.Text;
        }
        if (title == "History") ReloadHistory();
        if (title == "Home") RefreshStatus();
    }

    #endregion

    #region page builders

    private static Label Header(string text, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = Theme.Text,
        Location = new Point(0, y),
        AutoSize = true,
    };

    private static Label Note(string text, int y, int width = 640) => new()
    {
        Text = text,
        ForeColor = Theme.SubText,
        Location = new Point(0, y),
        MaximumSize = new Size(width, 0),
        AutoSize = true,
    };

    private static Label FieldLabel(string text, int x, int y) => new()
    {
        Text = text,
        ForeColor = Theme.Text,
        Location = new Point(x, y + 3),
        AutoSize = true,
    };

    private Panel Card(int x, int y, int w, int h)
    {
        var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = Theme.Panel };
        p.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    private Panel BuildHome()
    {
        var page = new Panel();

        var hero = Card(0, 0, 720, 148);
        _statusBig = new Label
        {
            Text = "Loading…",
            Font = new Font("Segoe UI Semibold", 17f),
            ForeColor = Theme.Text,
            Location = new Point(22, 20),
            AutoSize = true,
        };
        _statusSub = new Label
        {
            Text = "",
            ForeColor = Theme.SubText,
            Location = new Point(24, 58),
            AutoSize = true,
            MaximumSize = new Size(660, 0),
        };
        _hotkeyPill = new Button
        {
            Text = "…",
            Font = new Font("Consolas", 11f, FontStyle.Bold),
            Size = new Size(150, 34),
            Location = new Point(22, 102),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Field,
            ForeColor = Theme.Accent,
            Cursor = Cursors.Hand,
        };
        _hotkeyPill.FlatAppearance.BorderColor = Theme.Accent;
        _hotkeyPill.Click += (_, _) => CaptureHotkey(forCommand: false);
        var pillHint = new Label
        {
            Text = "← hold to dictate, tap to latch. Click to change.",
            ForeColor = Theme.SubText,
            Location = new Point(184, 110),
            AutoSize = true,
        };
        hero.Controls.Add(_statusBig);
        hero.Controls.Add(_statusSub);
        hero.Controls.Add(_hotkeyPill);
        hero.Controls.Add(pillHint);
        page.Controls.Add(hero);

        // stats row
        string[] labels = { "words dictated", "words today", "speaking speed", "dictations" };
        var statValues = new Label[4];
        for (int i = 0; i < 4; i++)
        {
            var card = Card(i * 184, 164, 168, 84);
            statValues[i] = new Label
            {
                Text = "—",
                Font = new Font("Segoe UI Semibold", 16f),
                ForeColor = Theme.Accent,
                Location = new Point(16, 14),
                AutoSize = true,
            };
            card.Controls.Add(statValues[i]);
            card.Controls.Add(new Label
            {
                Text = labels[i],
                ForeColor = Theme.SubText,
                Location = new Point(17, 50),
                AutoSize = true,
            });
            page.Controls.Add(card);
        }
        (_statWords, _statToday, _statWpm, _statCount) = (statValues[0], statValues[1], statValues[2], statValues[3]);

        page.Controls.Add(new Label
        {
            Text = "Try it — click below, hold the hotkey, and talk:",
            ForeColor = Theme.SubText,
            Location = new Point(2, 266),
            AutoSize = true,
        });
        var tryBox = new TextBox
        {
            Multiline = true,
            Location = new Point(0, 290),
            Size = new Size(720, 150),
            Font = new Font("Segoe UI", 11f),
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        page.Controls.Add(tryBox);

        return page;
    }

    private Panel BuildDictation()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("Live dictation", y)); y += 36;
        p.Controls.Add(Note("Words appear while you're still speaking. When you release the hotkey, FreeFlow re-checks the whole utterance with the accurate model and cleans it up in place.", y)); y += 46;

        _liveTyping = new CheckBox { Text = "Live typing — words land in the app as you speak", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_liveTyping); y += 30;

        p.Controls.Add(FieldLabel("When you release the key", 0, y));
        _finalPass = new ComboBox { Location = new Point(200, y), Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        _finalPass.Items.AddRange(new object[]
        {
            "Polish with accurate model (recommended)",
            "Just add punctuation (instant)",
            "Leave live text as-is",
        });
        p.Controls.Add(_finalPass); y += 44;

        p.Controls.Add(Header("Auto-edits", y)); y += 36;
        _fillers = new CheckBox { Text = "Remove filler words (um, uh, hmm…)", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_fillers); y += 28;
        _spokenCmds = new CheckBox { Text = "Spoken commands: \"new line\", \"new paragraph\", \"scratch that\"", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_spokenCmds); y += 28;
        _punctWords = new CheckBox { Text = "Spoken punctuation: \"comma\", \"period\" (off = automatic punctuation)", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_punctWords); y += 28;
        _smartSpace = new CheckBox { Text = "Smart spacing between consecutive dictations", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_smartSpace); y += 28;
        _sounds = new CheckBox { Text = "Start/stop sounds", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_sounds); y += 40;

        p.Controls.Add(Header("Tone", y)); y += 34;
        p.Controls.Add(Note("Casual drops the trailing period on short messages; Professional guarantees caps + end punctuation; Verbatim turns all edits off. Per-app overrides: App Profiles.", y)); y += 44;
        p.Controls.Add(FieldLabel("Default tone", 0, y));
        _defaultTone = new ComboBox { Location = new Point(120, y), Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
        _defaultTone.Items.AddRange(AppConfig.ToneNames.Cast<object>().ToArray());
        p.Controls.Add(_defaultTone);

        return p;
    }

    private Panel BuildDictionary()
    {
        var p = new Panel();
        p.Controls.Add(Header("Custom dictionary", 0));
        p.Controls.Add(Note("When you say the spoken form, FreeFlow writes yours instead — names, jargon, brands. e.g. \"hey gen\" → \"HeyGen\".", 34));
        _dictGrid = new DataGridView
        {
            Location = new Point(0, 78),
            Size = new Size(620, 400),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _dictGrid.Columns.Add("spoken", "Spoken (what you say)");
        _dictGrid.Columns.Add("written", "Written (what gets typed)");
        _dictGrid.CellValueChanged += (_, _) => ScheduleApply();
        _dictGrid.RowsRemoved += (_, _) => ScheduleApply();
        p.Controls.Add(_dictGrid);
        return p;
    }

    private Panel BuildShortcuts()
    {
        var p = new Panel();
        p.Controls.Add(Header("Voice shortcuts", 0));
        p.Controls.Add(Note("Say the trigger phrase on its own and the full expansion gets typed — email address, scheduling link, canned intro.", 34));
        _snipGrid = new DataGridView
        {
            Location = new Point(0, 78),
            Size = new Size(620, 400),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _snipGrid.Columns.Add("trigger", "Trigger phrase");
        _snipGrid.Columns.Add("expansion", "Expands to");
        _snipGrid.CellValueChanged += (_, _) => ScheduleApply();
        _snipGrid.RowsRemoved += (_, _) => ScheduleApply();
        p.Controls.Add(_snipGrid);
        return p;
    }

    private Panel BuildProfiles()
    {
        var p = new Panel();
        p.Controls.Add(Header("Per-app profiles", 0));
        p.Controls.Add(Note("Match by process name (Task Manager → Details, no .exe). Tone matching per app: casual in chat, professional in email, verbatim in terminals.", 34));
        _profGrid = new DataGridView
        {
            Location = new Point(0, 78),
            Size = new Size(660, 400),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _profGrid.Columns.Add("process", "Process name");
        var toneCol = new DataGridViewComboBoxColumn { Name = "tone", HeaderText = "Tone", FlatStyle = FlatStyle.Flat };
        toneCol.Items.AddRange(AppConfig.ToneNames.Cast<object>().ToArray());
        _profGrid.Columns.Add(toneCol);
        var injectCol = new DataGridViewComboBoxColumn { Name = "inject", HeaderText = "Insert via", FlatStyle = FlatStyle.Flat };
        injectCol.Items.AddRange("Paste", "Type");
        _profGrid.Columns.Add(injectCol);
        _profGrid.CellValueChanged += (_, _) => ScheduleApply();
        _profGrid.RowsRemoved += (_, _) => ScheduleApply();
        p.Controls.Add(_profGrid);
        return p;
    }

    private Panel BuildHistory()
    {
        var p = new Panel();
        p.Controls.Add(Header("History", 0));

        _histList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            Location = new Point(0, 42),
            Size = new Size(740, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _histList.Columns.Add("Time", 138);
        _histList.Columns.Add("App", 105);
        _histList.Columns.Add("Text", 470);
        _histList.SelectedIndexChanged += (_, _) =>
        {
            _histPreview.Text = _histList.SelectedItems.Count > 0
                ? ((HistoryEntry)_histList.SelectedItems[0].Tag!).Text
                : "";
        };
        p.Controls.Add(_histList);

        _histPreview = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(0, 350),
            Size = new Size(740, 90),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        p.Controls.Add(_histPreview);

        var copy = Theme.PrimaryButton(new Button
        {
            Text = "Copy",
            Size = new Size(88, 30),
            Location = new Point(0, 448),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        });
        copy.Click += (_, _) =>
        {
            if (_histList.SelectedItems.Count > 0)
                Clipboard.SetText(((HistoryEntry)_histList.SelectedItems[0].Tag!).Text);
        };
        var clear = new Button
        {
            Text = "Clear history",
            Size = new Size(108, 30),
            Location = new Point(96, 448),
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show("Delete all dictation history?", "FreeFlow",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                HistoryStore.Clear();
                ReloadHistory();
            }
        };
        p.Controls.Add(copy);
        p.Controls.Add(clear);
        return p;
    }

    private void ReloadHistory()
    {
        _histList.Items.Clear();
        var entries = HistoryStore.ReadAll();
        entries.Reverse();
        foreach (var e in entries.Take(500))
        {
            var item = new ListViewItem(e.Ts.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = e };
            item.SubItems.Add(e.App);
            item.SubItems.Add(e.Text.Replace('\n', ' '));
            _histList.Items.Add(item);
        }
    }

    private Panel BuildSettings()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("Hotkeys", y)); y += 38;

        p.Controls.Add(FieldLabel("Dictation hotkey", 0, y));
        _hotkeyBtn = new Button { Location = new Point(180, y), Size = new Size(170, 30), Font = new Font("Consolas", 10f, FontStyle.Bold), ForeColor = Theme.Accent };
        _hotkeyBtn.Click += (_, _) => CaptureHotkey(forCommand: false);
        p.Controls.Add(_hotkeyBtn); y += 38;

        p.Controls.Add(FieldLabel("Command-mode hotkey", 0, y));
        _cmdHotkeyBtn = new Button { Location = new Point(180, y), Size = new Size(170, 30), Font = new Font("Consolas", 10f, FontStyle.Bold), ForeColor = Theme.Accent };
        _cmdHotkeyBtn.Click += (_, _) => CaptureHotkey(forCommand: true);
        var clearCmd = new Button { Text = "clear", Location = new Point(356, y), Size = new Size(56, 30) };
        clearCmd.Click += (_, _) => { var c = Cfg.Clone(); c.CommandHotkeyName = "None"; _ctx.ApplyNewConfig(c); LoadUiFromConfig(); };
        p.Controls.Add(_cmdHotkeyBtn);
        p.Controls.Add(clearCmd); y += 38;

        _tapToLatch = new CheckBox { Text = "Quick tap latches hands-free recording", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_tapToLatch); y += 28;
        p.Controls.Add(FieldLabel("Tap threshold (ms)", 0, y));
        _tapMs = new NumericUpDown { Location = new Point(180, y), Width = 80, Minimum = 100, Maximum = 1000 };
        p.Controls.Add(_tapMs); y += 34;
        _swallow = new CheckBox { Text = "Swallow the hotkey so other apps never see it", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_swallow); y += 40;

        p.Controls.Add(Header("Microphone", y)); y += 38;
        p.Controls.Add(FieldLabel("Device", 0, y));
        _mic = new ComboBox { Location = new Point(180, y), Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        p.Controls.Add(_mic); y += 36;
        _warm = new CheckBox { Text = "Keep microphone warm (wired mics — Bluetooth is managed automatically)", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_warm); y += 32;

        p.Controls.Add(FieldLabel("Input gain — boost for quiet or mumbled speech", 0, y)); y += 26;
        _gain = new TrackBar
        {
            Location = new Point(0, y),
            Width = 320,
            Minimum = 2,    // 0.5×
            Maximum = 24,   // 6.0×
            TickFrequency = 4,
            SmallChange = 1,
            LargeChange = 4,
        };
        _gainLabel = new Label
        {
            Location = new Point(330, y + 8),
            AutoSize = true,
            ForeColor = Theme.Accent,
            Font = new Font("Segoe UI Semibold", 10.5f),
        };
        _gain.ValueChanged += (_, _) =>
        {
            _gainLabel.Text = $"{_gain.Value / 4.0:0.00}×";
            if (!_loadingUi)
                _ctx.Recorder.SetGain(_gain.Value / 4.0); // live, so the test zone hears it instantly
        };
        p.Controls.Add(_gain);
        p.Controls.Add(_gainLabel); y += 56;

        p.Controls.Add(Header("Mic test", y)); y += 36;
        p.Controls.Add(Note("Record a few seconds at the current gain, hear exactly what the recognizer hears, adjust, repeat.", y)); y += 30;
        _testLevel = new ProgressBar { Location = new Point(0, y), Size = new Size(320, 14), Maximum = 100 };
        p.Controls.Add(_testLevel); y += 24;
        _testRecord = new Button { Text = "●  Record 3s", Size = new Size(112, 30), Location = new Point(0, y) };
        _testPlay = new Button { Text = "▶  Play back", Size = new Size(112, 30), Location = new Point(120, y), Enabled = false };
        _testVerdict = new Label { Location = new Point(244, y + 6), AutoSize = true, ForeColor = Theme.SubText, MaximumSize = new Size(380, 0) };
        _testRecord.Click += (_, _) => RunMicTest();
        _testPlay.Click += (_, _) => PlayMicTest();
        p.Controls.Add(_testRecord);
        p.Controls.Add(_testPlay);
        p.Controls.Add(_testVerdict); y += 48;

        p.Controls.Add(Header("System", y)); y += 38;
        _autostart = new CheckBox { Text = "Start FreeFlow when Windows starts", Location = new Point(0, y), AutoSize = true };
        _autostart.CheckedChanged += (_, _) => { if (!_loadingUi) Autostart.Set(_autostart.Checked); };
        p.Controls.Add(_autostart); y += 28;
        p.Controls.Add(FieldLabel("CPU threads", 0, y));
        _threads = new NumericUpDown { Location = new Point(180, y), Width = 70, Minimum = 1, Maximum = Environment.ProcessorCount };
        p.Controls.Add(_threads); y += 34;
        p.Controls.Add(FieldLabel("Language (Whisper models: en, de… blank = auto)", 0, y));
        _language = new TextBox { Location = new Point(360, y), Width = 70 };
        p.Controls.Add(_language); y += 44;

        p.Controls.Add(Header("AI (optional — command mode & polish)", y)); y += 38;
        p.Controls.Add(Note("Any OpenAI-compatible endpoint. For free + local install Ollama (ollama.com); the defaults below then just work. Dictation never needs this.", y)); y += 44;
        _aiEnabled = new CheckBox { Text = "Enable AI features", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_aiEnabled); y += 30;
        p.Controls.Add(FieldLabel("Endpoint", 0, y));
        _aiUrl = new TextBox { Location = new Point(180, y), Width = 320 };
        p.Controls.Add(_aiUrl); y += 34;
        p.Controls.Add(FieldLabel("Model", 0, y));
        _aiModel = new TextBox { Location = new Point(180, y), Width = 200 };
        p.Controls.Add(_aiModel); y += 34;
        p.Controls.Add(FieldLabel("API key (if needed)", 0, y));
        _aiKey = new TextBox { Location = new Point(180, y), Width = 320, UseSystemPasswordChar = true };
        p.Controls.Add(_aiKey); y += 34;
        _aiPolish = new CheckBox { Text = "AI polish: run every dictation through the AI (slower)", Location = new Point(0, y), AutoSize = true };
        p.Controls.Add(_aiPolish); y += 36;
        var test = new Button { Text = "Test connection", Size = new Size(130, 30), Location = new Point(0, y) };
        _aiTestResult = new Label { Location = new Point(140, y + 6), AutoSize = true, ForeColor = Theme.SubText, MaximumSize = new Size(440, 0) };
        test.Click += async (_, _) =>
        {
            _aiTestResult.Text = "Testing…";
            CommitConfig();
            var (ok, msg) = await AiProvider.TestAsync(Cfg);
            _aiTestResult.ForeColor = ok ? Theme.Ok : Theme.Danger;
            _aiTestResult.Text = msg;
        };
        p.Controls.Add(test);
        p.Controls.Add(_aiTestResult);

        return p;
    }

    private Panel BuildModel()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("Speech models", y)); y += 36;
        p.Controls.Add(Note("Everything runs locally on your CPU. The live streaming + punctuation models download automatically; this picks the accurate model used for the final pass (and accurate mode).", y)); y += 52;

        _modelPick = new ComboBox { Location = new Point(0, y), Width = 500, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var m in ModelRegistry.Selectable)
            _modelPick.Items.Add(new ModelChoice(m));
        _modelPick.SelectedIndexChanged += (_, _) => { RefreshModelStatus(); ScheduleApply(); };
        p.Controls.Add(_modelPick); y += 38;

        _modelStatus = new Label { Location = new Point(0, y), AutoSize = true, ForeColor = Theme.SubText, MaximumSize = new Size(640, 0) };
        p.Controls.Add(_modelStatus); y += 48;

        _dlButton = new Button { Text = "Download model", Location = new Point(0, y), Size = new Size(150, 30) };
        _dlButton.Click += async (_, _) => await DownloadSelectedModel();
        p.Controls.Add(_dlButton);
        _dlBar = new ProgressBar { Location = new Point(160, y + 2), Width = 340, Height = 24, Visible = false };
        p.Controls.Add(_dlBar); y += 52;

        var helper = new Label
        {
            Location = new Point(0, y),
            AutoSize = true,
            ForeColor = Theme.SubText,
            MaximumSize = new Size(640, 0),
            Text = "Helper models (auto-managed):",
        };
        p.Controls.Add(helper); y += 26;
        foreach (var id in new[] { ModelRegistry.StreamingModelId, ModelRegistry.PunctModelId })
        {
            var m = ModelRegistry.Get(id);
            p.Controls.Add(new Label
            {
                Location = new Point(12, y),
                AutoSize = true,
                ForeColor = Theme.SubText,
                Text = $"• {m.DisplayName} — {(m.IsDownloaded() ? "downloaded ✓" : "will download on next start")}",
            });
            y += 24;
        }

        return p;
    }

    private void RefreshModelStatus()
    {
        if (_modelPick.SelectedItem is not ModelChoice mc) return;
        bool have = mc.Model.IsDownloaded();
        _modelStatus.Text = $"{mc.Model.LanguageNote}\nDownload size: {mc.Model.TotalBytes / 1024.0 / 1024.0:0} MB — " +
                            (have ? "downloaded ✓" : "not downloaded yet");
        _dlButton.Enabled = !have;
        _dlButton.Text = have ? "Downloaded ✓" : "Download model";
    }

    private async Task DownloadSelectedModel()
    {
        if (_modelPick.SelectedItem is not ModelChoice mc) return;
        _dlButton.Enabled = false;
        _dlBar.Visible = true;
        var progress = new Progress<(string File, double Percent, long Done, long Total)>(pr =>
        {
            _dlBar.Value = (int)Math.Clamp(pr.Percent, 0, 100);
            _modelStatus.Text = $"Downloading {pr.File}: {pr.Done / 1024.0 / 1024.0:0} / {pr.Total / 1024.0 / 1024.0:0} MB";
        });
        try
        {
            await ModelDownloader.DownloadAsync(mc.Model, progress, CancellationToken.None);
            _modelStatus.Text = "Download complete ✓";
            CommitConfig(); // reloads engines if this is the selected model
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "model download");
            _modelStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _dlBar.Visible = false;
            RefreshModelStatus();
        }
    }

    private Panel BuildAbout()
    {
        var p = new Panel();
        var banner = new PictureBox
        {
            Size = new Size(640, 192),
            Location = new Point(0, 0),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try { banner.Image = Image.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "banner.png")); }
        catch { }
        p.Controls.Add(banner);
        p.Controls.Add(Note(
            "FreeFlow — a Wispr Flow replacement that costs nothing, forever.\n\n" +
            "• Live words: streaming Zipformer transducer (sherpa-onnx), on your CPU\n" +
            "• Final polish: NVIDIA Parakeet TDT 0.6B v2 — beats Whisper large-v3 on English\n" +
            "• Punctuation: CT-Transformer online punctuation model\n" +
            "• Nothing leaves this machine. No account. No subscription. No word limits.\n\n" +
            $"Version {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}   •   Config: {Paths.AppDataDir}\n\n" +
            "Say \"scratch that\" to undo, \"new line\" / \"new paragraph\" to break lines.\n" +
            "Select text + hold the command key to rewrite it with AI (needs Ollama or any OpenAI-compatible endpoint).",
            210, 640));
        return p;
    }

    #endregion

    #region mic test

    private async void RunMicTest()
    {
        if (_testing) return;
        _testing = true;
        _testRecord.Enabled = false;
        _testVerdict.ForeColor = Theme.SubText;
        _testVerdict.Text = "Recording — talk like you normally would (mumble away).";
        try
        {
            _ctx.Recorder.StartCapture();
            for (int tenths = 30; tenths >= 1; tenths--)
            {
                _testRecord.Text = $"●  {tenths / 10.0:0.0}s";
                await Task.Delay(100);
            }
            var clip = _ctx.Recorder.StopCapture();
            _testClip = clip;
            _testPlay.Enabled = clip.Length > 0;

            double sumSq = 0;
            int clipped = 0;
            foreach (var s in clip)
            {
                sumSq += s * s;
                if (Math.Abs(s) > 0.985) clipped++;
            }
            double rms = clip.Length > 0 ? Math.Sqrt(sumSq / clip.Length) : 0;
            double clipPct = clip.Length > 0 ? 100.0 * clipped / clip.Length : 0;

            if (clip.Length < 16000)
            {
                _testVerdict.ForeColor = Theme.Danger;
                _testVerdict.Text = "No audio captured. Is the mic connected and selected above?";
            }
            else if (clipPct > 0.5)
            {
                _testVerdict.ForeColor = Theme.Danger;
                _testVerdict.Text = $"Clipping ({clipPct:0.0}% of samples) — that DOES hurt accuracy. Lower the gain a notch.";
            }
            else if (rms < 0.015)
            {
                _testVerdict.ForeColor = Theme.Danger;
                _testVerdict.Text = "Still very quiet. Raise the gain or get closer to the mic.";
            }
            else
            {
                _testVerdict.ForeColor = Theme.Ok;
                _testVerdict.Text = "Good level. Play it back — if you can make out the words, so can the recognizer.";
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "mic test");
            _testVerdict.ForeColor = Theme.Danger;
            _testVerdict.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            _testing = false;
            _testRecord.Enabled = true;
            _testRecord.Text = "●  Record 3s";
            _testLevel.Value = 0;
        }
    }

    private async void PlayMicTest()
    {
        var clip = _testClip;
        if (clip == null || clip.Length == 0) return;
        try
        {
            // Bluetooth: the lingering mic link mutes the headset's normal output —
            // release it and give Windows a beat to switch profiles before playing
            if (_ctx.Recorder.IsBluetoothDevice)
            {
                _testPlay.Enabled = false;
                _testVerdict.ForeColor = Theme.SubText;
                _testVerdict.Text = "Switching your headset back to playback mode…";
                _ctx.Recorder.ReleaseDevice();
                await Task.Delay(1200);
                _testVerdict.Text = "Playing…";
                _testPlay.Enabled = true;
            }

            var pcm = new byte[clip.Length * 2];
            for (int i = 0; i < clip.Length; i++)
            {
                short s = (short)Math.Clamp(clip[i] * 32767f, short.MinValue, short.MaxValue);
                pcm[2 * i] = (byte)(s & 0xFF);
                pcm[2 * i + 1] = (byte)(s >> 8);
            }
            var raw = new NAudio.Wave.RawSourceWaveStream(new MemoryStream(pcm), new NAudio.Wave.WaveFormat(16000, 16, 1));
            var outDev = new NAudio.Wave.WaveOutEvent();
            outDev.Init(raw);
            outDev.PlaybackStopped += (_, _) => { outDev.Dispose(); raw.Dispose(); };
            outDev.Play();
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "mic test playback");
        }
    }

    private void OnMicLevel(float rms)
    {
        if (IsDisposed || !Visible || !_testing) return;
        try
        {
            BeginInvoke(() => _testLevel.Value = Math.Min(100, (int)(rms * 300)));
        }
        catch { }
    }

    #endregion

    #region hotkey capture

    private void CaptureHotkey(bool forCommand)
    {
        using var dlg = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            Size = new Size(420, 130),
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Panel,
            ShowInTaskbar = false,
            TopMost = true,
        };
        dlg.Controls.Add(new Label
        {
            Text = forCommand ? "Press any key for COMMAND mode…" : "Press any key to DICTATE with…",
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Theme.Text,
            Dock = DockStyle.Top,
            Height = 60,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        dlg.Controls.Add(new Label
        {
            Text = "Esc cancels. Good picks: Right Ctrl, F9, CapsLock.",
            ForeColor = Theme.SubText,
            Dock = DockStyle.Bottom,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        dlg.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Accent, 2);
            e.Graphics.DrawRectangle(pen, 1, 1, dlg.Width - 3, dlg.Height - 3);
        };

        void OnCaptured(int vk)
        {
            try
            {
                dlg.BeginInvoke(() =>
                {
                    if (vk != 0x1B) // Esc = cancel
                    {
                        var c = Cfg.Clone();
                        string name = Vk.NameOf(vk);
                        if (forCommand)
                        {
                            if (name == c.HotkeyName) { MessageBox.Show("That's already the dictation hotkey."); }
                            else c.CommandHotkeyName = name;
                        }
                        else
                        {
                            if (name == c.CommandHotkeyName) c.CommandHotkeyName = "None";
                            c.HotkeyName = name;
                        }
                        _ctx.ApplyNewConfig(c);
                        LoadUiFromConfig();
                    }
                    dlg.Close();
                });
            }
            catch { }
        }

        _ctx.Hook.KeyCaptured += OnCaptured;
        _ctx.Hook.CaptureNextKey();
        dlg.FormClosed += (_, _) =>
        {
            _ctx.Hook.KeyCaptured -= OnCaptured;
            _ctx.Hook.CancelKeyCapture();
        };
        dlg.ShowDialog(this);
    }

    #endregion

    #region config load / apply

    private void LoadUiFromConfig()
    {
        _loadingUi = true;
        var c = Cfg;

        _hotkeyPill.Text = Vk.DisplayName(c.HotkeyName);
        _hotkeyBtn.Text = Vk.DisplayName(c.HotkeyName);
        _cmdHotkeyBtn.Text = Vk.DisplayName(c.CommandHotkeyName);
        _tapToLatch.Checked = c.TapToLatch;
        _tapMs.Value = Math.Clamp(c.TapThresholdMs, 100, 1000);
        _swallow.Checked = c.SwallowHotkey;
        _warm.Checked = c.KeepMicWarm;
        _gain.Value = Math.Clamp((int)Math.Round(c.MicGain * 4), _gain.Minimum, _gain.Maximum);
        _gainLabel.Text = $"{_gain.Value / 4.0:0.00}×";
        _threads.Value = Math.Clamp(c.NumThreads, 1, Environment.ProcessorCount);
        _language.Text = c.Language;
        _autostart.Checked = Autostart.IsEnabled();

        _mic.Items.Clear();
        foreach (var (idx, name) in AudioRecorder.ListDevices())
            _mic.Items.Add(new MicChoice(idx, name));
        int micIdx = string.IsNullOrEmpty(c.MicDeviceName)
            ? 0
            : _mic.Items.Cast<MicChoice>().ToList().FindIndex(m =>
                m.Name.Contains(c.MicDeviceName, StringComparison.OrdinalIgnoreCase));
        _mic.SelectedIndex = Math.Max(0, micIdx);

        _liveTyping.Checked = c.LiveTyping;
        _finalPass.SelectedIndex = c.FinalPass switch { "punct" => 1, "off" => 2, _ => 0 };
        _fillers.Checked = c.RemoveFillers;
        _spokenCmds.Checked = c.SpokenCommands;
        _punctWords.Checked = c.PunctuationWords;
        _smartSpace.Checked = c.SmartSpacing;
        _sounds.Checked = c.PlaySounds;
        _defaultTone.SelectedItem = c.DefaultTone;
        if (_defaultTone.SelectedIndex < 0) _defaultTone.SelectedIndex = 0;

        _aiEnabled.Checked = c.AiEnabled;
        _aiUrl.Text = c.AiBaseUrl;
        _aiModel.Text = c.AiModel;
        _aiKey.Text = c.AiApiKey;
        _aiPolish.Checked = c.AiPolish;

        _dictGrid.Rows.Clear();
        foreach (var d in c.Dictionary) _dictGrid.Rows.Add(d.Spoken, d.Written);
        _snipGrid.Rows.Clear();
        foreach (var s in c.Snippets) _snipGrid.Rows.Add(s.Trigger, s.Expansion);
        _profGrid.Rows.Clear();
        foreach (var pr in c.AppProfiles) _profGrid.Rows.Add(pr.ProcessName, pr.Tone, pr.InjectMode);

        int mi = ModelRegistry.Selectable.ToList().FindIndex(m => m.Id == c.ModelId);
        _modelPick.SelectedIndex = Math.Max(0, mi);
        RefreshModelStatus();

        // wire change-tracking once everything is populated
        if (!_changeHandlersWired)
        {
            _changeHandlersWired = true;
            foreach (var control in new Control[]
                     {
                         _tapToLatch, _tapMs, _swallow, _warm, _gain, _threads, _language, _mic,
                         _liveTyping, _finalPass, _fillers, _spokenCmds,
                         _punctWords, _smartSpace, _sounds, _defaultTone,
                         _aiEnabled, _aiUrl, _aiModel, _aiKey, _aiPolish,
                     })
            {
                switch (control)
                {
                    case CheckBox cb: cb.CheckedChanged += (_, _) => ScheduleApply(); break;
                    case ComboBox combo: combo.SelectedIndexChanged += (_, _) => ScheduleApply(); break;
                    case NumericUpDown num: num.ValueChanged += (_, _) => ScheduleApply(); break;
                    case TrackBar track: track.ValueChanged += (_, _) => ScheduleApply(); break;
                    case TextBox tb: tb.TextChanged += (_, _) => ScheduleApply(); break;
                }
            }
        }

        _loadingUi = false;
        RefreshStatus();
    }

    private bool _changeHandlersWired;

    private void ScheduleApply()
    {
        if (_loadingUi) return;
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private void CommitConfig()
    {
        if (_loadingUi) return;
        var c = Cfg.Clone();

        c.TapToLatch = _tapToLatch.Checked;
        c.TapThresholdMs = (int)_tapMs.Value;
        c.SwallowHotkey = _swallow.Checked;
        c.KeepMicWarm = _warm.Checked;
        c.MicGain = _gain.Value / 4.0;
        c.NumThreads = (int)_threads.Value;
        c.Language = _language.Text.Trim();
        if (_mic.SelectedItem is MicChoice mc) c.MicDeviceName = mc.Index == -1 ? "" : mc.Name;

        c.LiveTyping = _liveTyping.Checked;
        c.FinalPass = _finalPass.SelectedIndex switch { 1 => "punct", 2 => "off", _ => "parakeet" };
        c.RemoveFillers = _fillers.Checked;
        c.SpokenCommands = _spokenCmds.Checked;
        c.PunctuationWords = _punctWords.Checked;
        c.SmartSpacing = _smartSpace.Checked;
        c.PlaySounds = _sounds.Checked;
        c.DefaultTone = _defaultTone.SelectedItem as string ?? "Default";

        c.AiEnabled = _aiEnabled.Checked;
        c.AiBaseUrl = _aiUrl.Text.Trim();
        c.AiModel = _aiModel.Text.Trim();
        c.AiApiKey = _aiKey.Text.Trim();
        c.AiPolish = _aiPolish.Checked;

        c.Dictionary = _dictGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Cells[0].Value is string s && s.Trim().Length > 0)
            .Select(r => new DictionaryEntry
            {
                Spoken = (r.Cells[0].Value as string ?? "").Trim(),
                Written = (r.Cells[1].Value as string ?? "").Trim(),
            }).ToList();
        c.Snippets = _snipGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Cells[0].Value is string s && s.Trim().Length > 0)
            .Select(r => new Snippet
            {
                Trigger = (r.Cells[0].Value as string ?? "").Trim(),
                Expansion = r.Cells[1].Value as string ?? "",
            }).ToList();
        c.AppProfiles = _profGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow && r.Cells[0].Value is string s && s.Trim().Length > 0)
            .Select(r => new AppProfile
            {
                ProcessName = (r.Cells[0].Value as string ?? "").Trim(),
                Tone = r.Cells[1].Value as string ?? "Default",
                InjectMode = r.Cells[2].Value as string ?? "Paste",
            }).ToList();

        if (_modelPick.SelectedItem is ModelChoice choice)
            c.ModelId = choice.Model.Id;

        _ctx.ApplyNewConfig(c);
    }

    #endregion

    private void RefreshStatus()
    {
        if (IsDisposed || !Visible) return;

        var s = StatsStore.Get();
        _statWords.Text = s.TotalWords.ToString("N0");
        _statToday.Text = s.WordsToday.ToString("N0");
        _statWpm.Text = s.AvgWpm > 0 ? $"{s.AvgWpm:0} wpm" : "—";
        _statCount.Text = s.TotalDictations.ToString("N0");

        bool micOk = AudioRecorder.AnyDevicePresent;
        string status, sub;
        if (!_ctx.Enabled)
        {
            status = "Paused";
            sub = "Enable FreeFlow from the tray icon.";
        }
        else if (_ctx.Streaming.IsLoaded || _ctx.Engine.IsLoaded)
        {
            status = "Ready to dictate";
            sub = (Cfg.LiveTyping && _ctx.Streaming.IsLoaded
                      ? "Live word-by-word typing is ON. "
                      : "Accurate mode: text lands when you release the key. ")
                  + (micOk
                      ? (_ctx.Recorder.IsBluetoothDevice ? "Bluetooth mic mode: speak after the beep." : "")
                      : "⚠ No microphone detected — connect one (AirPods count) and it'll be picked up automatically.");
        }
        else
        {
            status = "Loading models…";
            sub = _ctx.Engine.LoadError ?? _ctx.Streaming.LoadError ?? "";
        }
        _statusBig.Text = status;
        _statusSub.Text = sub;

        _sideStatus.Text =
            $"{(_ctx.Streaming.IsLoaded ? "●" : "○")} live engine\n" +
            $"{(_ctx.Engine.IsLoaded ? "●" : "○")} accurate engine ({ModelRegistry.Get(Cfg.ModelId).Id.Split('-')[0]})";
    }

    private sealed record MicChoice(int Index, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ModelChoice(ModelInfo Model)
    {
        public override string ToString() => Model.DisplayName;
    }
}

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FreeFlow";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\" --minimized");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "autostart registry");
        }
    }
}

using FreeFlow.Core;
using Microsoft.Win32;

namespace FreeFlow.UI;

public sealed class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly Action<AppConfig> _onSave;

    private readonly ListBox _nav = new();
    private readonly Panel _content = new();
    private readonly Dictionary<string, Panel> _pages = new();

    // General
    private ComboBox _hotkey = null!, _mic = null!;
    private CheckBox _tapToLatch = null!, _swallow = null!, _warm = null!, _sounds = null!, _smartSpace = null!, _autostart = null!;
    private NumericUpDown _tapMs = null!, _gain = null!;

    // Formatting
    private CheckBox _fillers = null!, _spokenCmds = null!, _punctWords = null!;
    private TextBox _fillerList = null!;
    private ComboBox _defaultTone = null!;

    // Grids
    private DataGridView _dictGrid = null!, _snipGrid = null!, _profGrid = null!;

    // Model
    private ComboBox _modelPick = null!;
    private Label _modelStatus = null!;
    private ProgressBar _dlBar = null!;
    private Button _dlButton = null!;
    private NumericUpDown _threads = null!;
    private TextBox _language = null!;

    // AI
    private CheckBox _aiEnabled = null!, _aiPolish = null!;
    private TextBox _aiUrl = null!, _aiModel = null!, _aiKey = null!;
    private ComboBox _cmdHotkey = null!;
    private Label _aiTestResult = null!;

    public SettingsForm(AppConfig cfg, Action<AppConfig> onSave)
    {
        _cfg = cfg;
        _onSave = onSave;

        Text = "FreeFlow Settings";
        Size = new Size(880, 640);
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.Idle;

        _nav.Dock = DockStyle.Left;
        _nav.Width = 150;
        _nav.Font = new Font("Segoe UI", 10.5f);
        _nav.ItemHeight = 34;
        _nav.DrawMode = DrawMode.OwnerDrawFixed;
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => ShowPage((string)_nav.SelectedItem!);

        _content.Dock = DockStyle.Fill;
        _content.Padding = new Padding(18);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54 };
        var save = Theme.PrimaryButton(new Button { Text = "Save && Apply", Size = new Size(120, 32) });
        var cancel = new Button { Text = "Cancel", Size = new Size(90, 32) };
        save.Click += (_, _) => SaveAndClose();
        cancel.Click += (_, _) => Close();
        bottom.Controls.Add(save);
        bottom.Controls.Add(cancel);
        bottom.Resize += (_, _) =>
        {
            save.Location = new Point(bottom.Width - 240, 11);
            cancel.Location = new Point(bottom.Width - 110, 11);
        };

        Controls.Add(_content);
        Controls.Add(bottom);
        Controls.Add(_nav);

        AddPage("General", BuildGeneral());
        AddPage("Formatting", BuildFormatting());
        AddPage("Dictionary", BuildDictionary());
        AddPage("Snippets", BuildSnippets());
        AddPage("App Profiles", BuildProfiles());
        AddPage("Model", BuildModel());
        AddPage("AI / Commands", BuildAi());
        AddPage("About", BuildAbout());

        Theme.Apply(this);
        _nav.BackColor = Theme.Panel;
        _nav.SelectedIndex = 0;
    }

    public void SelectModelPage() => _nav.SelectedItem = "Model";

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Theme.AccentSoft : Theme.Panel);
        e.Graphics.FillRectangle(back, e.Bounds);
        using var textBrush = new SolidBrush(selected ? Color.White : Theme.Text);
        var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString((string)_nav.Items[e.Index]!, _nav.Font, textBrush,
            new RectangleF(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), sf);
    }

    private void AddPage(string name, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        page.AutoScroll = true;
        _pages[name] = page;
        _content.Controls.Add(page);
        _nav.Items.Add(name);
    }

    private void ShowPage(string name)
    {
        foreach (var (k, p) in _pages)
            p.Visible = k == name;
    }

    #region page builders

    private static Label Header(string text, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Semibold", 13f),
        Location = new Point(0, y),
        AutoSize = true,
    };

    private static Label Note(string text, int y, int width = 620) => new()
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
        Location = new Point(x, y + 3),
        AutoSize = true,
    };

    private Panel BuildGeneral()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("Dictation hotkey", y)); y += 34;
        p.Controls.Add(Note("Hold the key and speak; release to insert. A quick tap latches hands-free mode — tap again to stop.", y)); y += 40;

        p.Controls.Add(FieldLabel("Hotkey", 0, y));
        _hotkey = new ComboBox { Location = new Point(120, y), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _hotkey.Items.AddRange(Vk.HotkeyChoices.Cast<object>().ToArray());
        _hotkey.SelectedItem = _cfg.HotkeyName;
        if (_hotkey.SelectedIndex < 0) _hotkey.SelectedIndex = 0;
        p.Controls.Add(_hotkey); y += 36;

        _tapToLatch = new CheckBox { Text = "Quick tap latches hands-free recording", Location = new Point(0, y), AutoSize = true, Checked = _cfg.TapToLatch };
        p.Controls.Add(_tapToLatch); y += 30;

        p.Controls.Add(FieldLabel("Tap threshold (ms)", 0, y));
        _tapMs = new NumericUpDown { Location = new Point(160, y), Width = 80, Minimum = 100, Maximum = 1000, Value = Math.Clamp(_cfg.TapThresholdMs, 100, 1000) };
        p.Controls.Add(_tapMs); y += 36;

        _swallow = new CheckBox { Text = "Swallow the hotkey so other apps never see it (recommended)", Location = new Point(0, y), AutoSize = true, Checked = _cfg.SwallowHotkey };
        p.Controls.Add(_swallow); y += 42;

        p.Controls.Add(Header("Microphone", y)); y += 36;
        p.Controls.Add(FieldLabel("Device", 0, y));
        _mic = new ComboBox { Location = new Point(120, y), Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (idx, name) in AudioRecorder.ListDevices())
            _mic.Items.Add(new MicChoice(idx, name));
        _mic.SelectedIndex = Math.Max(0, ((IEnumerable<object>)_mic.Items.Cast<object>()).ToList()
            .FindIndex(o => ((MicChoice)o).Index == _cfg.MicDevice));
        p.Controls.Add(_mic); y += 36;

        _warm = new CheckBox { Text = "Keep microphone warm (instant start, catches the first syllable)", Location = new Point(0, y), AutoSize = true, Checked = _cfg.KeepMicWarm };
        p.Controls.Add(_warm); y += 30;

        p.Controls.Add(FieldLabel("Input gain (whisper mode boost)", 0, y));
        _gain = new NumericUpDown { Location = new Point(240, y), Width = 80, Minimum = 0.5m, Maximum = 6m, Increment = 0.25m, DecimalPlaces = 2, Value = (decimal)Math.Clamp(_cfg.MicGain, 0.5, 6) };
        p.Controls.Add(_gain); y += 42;

        p.Controls.Add(Header("Behavior", y)); y += 36;
        _sounds = new CheckBox { Text = "Play start/stop sounds", Location = new Point(0, y), AutoSize = true, Checked = _cfg.PlaySounds };
        p.Controls.Add(_sounds); y += 30;
        _smartSpace = new CheckBox { Text = "Smart spacing between consecutive dictations", Location = new Point(0, y), AutoSize = true, Checked = _cfg.SmartSpacing };
        p.Controls.Add(_smartSpace); y += 30;
        _autostart = new CheckBox { Text = "Start FreeFlow when Windows starts", Location = new Point(0, y), AutoSize = true, Checked = Autostart.IsEnabled() };
        p.Controls.Add(_autostart);

        return p;
    }

    private Panel BuildFormatting()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("Auto-edits", y)); y += 36;

        _fillers = new CheckBox { Text = "Remove filler words", Location = new Point(0, y), AutoSize = true, Checked = _cfg.RemoveFillers };
        p.Controls.Add(_fillers); y += 30;

        p.Controls.Add(FieldLabel("Filler words (comma separated)", 0, y)); y += 26;
        _fillerList = new TextBox { Location = new Point(0, y), Width = 520, Text = string.Join(", ", _cfg.FillerWords) };
        p.Controls.Add(_fillerList); y += 40;

        _spokenCmds = new CheckBox { Text = "Spoken commands: \"new line\", \"new paragraph\", \"scratch that\"", Location = new Point(0, y), AutoSize = true, Checked = _cfg.SpokenCommands };
        p.Controls.Add(_spokenCmds); y += 30;

        _punctWords = new CheckBox { Text = "Spoken punctuation: \"comma\", \"period\", \"question mark\" (off = the model punctuates for you)", Location = new Point(0, y), AutoSize = true, Checked = _cfg.PunctuationWords };
        p.Controls.Add(_punctWords); y += 42;

        p.Controls.Add(Header("Tone", y)); y += 36;
        p.Controls.Add(Note("Default keeps the model's own punctuation. Casual drops the trailing period on short messages. Professional guarantees capitalization and end punctuation. Verbatim disables all edits. Per-app overrides live in App Profiles.", y)); y += 60;

        p.Controls.Add(FieldLabel("Default tone", 0, y));
        _defaultTone = new ComboBox { Location = new Point(120, y), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _defaultTone.Items.AddRange(AppConfig.ToneNames.Cast<object>().ToArray());
        _defaultTone.SelectedItem = _cfg.DefaultTone;
        if (_defaultTone.SelectedIndex < 0) _defaultTone.SelectedIndex = 0;
        p.Controls.Add(_defaultTone);

        return p;
    }

    private Panel BuildDictionary()
    {
        var p = new Panel();
        p.Controls.Add(Header("Custom dictionary", 0));
        p.Controls.Add(Note("When the recognizer hears the spoken form, FreeFlow writes your form instead. Use it for names, jargon, and brands — e.g. \"hey gen\" → \"HeyGen\".", 34));

        _dictGrid = new DataGridView
        {
            Location = new Point(0, 84),
            Size = new Size(600, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _dictGrid.Columns.Add("spoken", "Spoken (what you say)");
        _dictGrid.Columns.Add("written", "Written (what gets typed)");
        foreach (var d in _cfg.Dictionary)
            _dictGrid.Rows.Add(d.Spoken, d.Written);
        p.Controls.Add(_dictGrid);
        return p;
    }

    private Panel BuildSnippets()
    {
        var p = new Panel();
        p.Controls.Add(Header("Voice shortcuts", 0));
        p.Controls.Add(Note("Say the trigger phrase alone and FreeFlow inserts the full expansion. Great for your email address, scheduling links, or canned intros.", 34));

        _snipGrid = new DataGridView
        {
            Location = new Point(0, 84),
            Size = new Size(600, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _snipGrid.Columns.Add("trigger", "Trigger phrase");
        _snipGrid.Columns.Add("expansion", "Expands to");
        foreach (var s in _cfg.Snippets)
            _snipGrid.Rows.Add(s.Trigger, s.Expansion);
        p.Controls.Add(_snipGrid);
        return p;
    }

    private Panel BuildProfiles()
    {
        var p = new Panel();
        p.Controls.Add(Header("Per-app profiles", 0));
        p.Controls.Add(Note("Match by process name (Task Manager → Details, without .exe). Tone matching: casual in chat apps, professional in email, verbatim in terminals.", 34));

        _profGrid = new DataGridView
        {
            Location = new Point(0, 84),
            Size = new Size(640, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
        };
        _profGrid.Columns.Add("process", "Process name");
        var toneCol = new DataGridViewComboBoxColumn { Name = "tone", HeaderText = "Tone", FlatStyle = FlatStyle.Flat };
        toneCol.Items.AddRange(AppConfig.ToneNames.Cast<object>().ToArray());
        _profGrid.Columns.Add(toneCol);
        var injectCol = new DataGridViewComboBoxColumn { Name = "inject", HeaderText = "Insert via", FlatStyle = FlatStyle.Flat };
        injectCol.Items.AddRange("Paste", "Type");
        _profGrid.Columns.Add(injectCol);
        foreach (var pr in _cfg.AppProfiles)
            _profGrid.Rows.Add(pr.ProcessName, pr.Tone, pr.InjectMode);
        p.Controls.Add(_profGrid);
        return p;
    }

    private Panel BuildModel()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("Speech model", y)); y += 36;
        p.Controls.Add(Note("Everything runs locally on your CPU. Parakeet is the recommended English model; Whisper models add multilingual support.", y)); y += 44;

        _modelPick = new ComboBox { Location = new Point(0, y), Width = 480, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var m in ModelRegistry.All)
            _modelPick.Items.Add(new ModelChoice(m));
        _modelPick.SelectedIndex = Array.FindIndex(ModelRegistry.All, m => m.Id == _cfg.ModelId);
        if (_modelPick.SelectedIndex < 0) _modelPick.SelectedIndex = 0;
        _modelPick.SelectedIndexChanged += (_, _) => RefreshModelStatus();
        p.Controls.Add(_modelPick); y += 36;

        _modelStatus = new Label { Location = new Point(0, y), AutoSize = true, ForeColor = Theme.SubText, MaximumSize = new Size(620, 0) };
        p.Controls.Add(_modelStatus); y += 44;

        _dlButton = new Button { Text = "Download model", Location = new Point(0, y), Size = new Size(150, 30) };
        _dlButton.Click += async (_, _) => await DownloadSelectedModel();
        p.Controls.Add(_dlButton);

        _dlBar = new ProgressBar { Location = new Point(160, y + 2), Width = 320, Height = 24, Visible = false };
        p.Controls.Add(_dlBar); y += 48;

        p.Controls.Add(FieldLabel("CPU threads", 0, y));
        _threads = new NumericUpDown { Location = new Point(120, y), Width = 70, Minimum = 1, Maximum = Environment.ProcessorCount, Value = Math.Clamp(_cfg.NumThreads, 1, Environment.ProcessorCount) };
        p.Controls.Add(_threads); y += 36;

        p.Controls.Add(FieldLabel("Language (Whisper only, e.g. en, de, fr — blank = auto)", 0, y));
        _language = new TextBox { Location = new Point(380, y), Width = 70, Text = _cfg.Language };
        p.Controls.Add(_language);

        RefreshModelStatus();
        return p;
    }

    private void RefreshModelStatus()
    {
        var m = ((ModelChoice)_modelPick.SelectedItem!).Model;
        bool have = m.IsDownloaded();
        _modelStatus.Text = $"{m.LanguageNote}\nDownload size: {m.TotalBytes / 1024.0 / 1024.0:0} MB — " +
                            (have ? "already downloaded ✓" : "not downloaded yet");
        _dlButton.Enabled = !have;
        _dlButton.Text = have ? "Downloaded ✓" : "Download model";
    }

    private async Task DownloadSelectedModel()
    {
        var m = ((ModelChoice)_modelPick.SelectedItem!).Model;
        _dlButton.Enabled = false;
        _dlBar.Visible = true;
        var progress = new Progress<(string File, double Percent, long Done, long Total)>(p =>
        {
            _dlBar.Value = (int)Math.Clamp(p.Percent, 0, 100);
            _modelStatus.Text = $"Downloading {p.File}: {p.Done / 1024.0 / 1024.0:0} / {p.Total / 1024.0 / 1024.0:0} MB";
        });
        try
        {
            await ModelDownloader.DownloadAsync(m, progress, CancellationToken.None);
            _modelStatus.Text = "Download complete ✓ — click Save & Apply to load it.";
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "model download");
            _modelStatus.Text = $"Download failed: {ex.Message}";
            _dlButton.Enabled = true;
        }
        finally
        {
            _dlBar.Visible = false;
            RefreshModelStatus();
        }
    }

    private Panel BuildAi()
    {
        var p = new Panel();
        int y = 0;
        p.Controls.Add(Header("AI features (optional)", y)); y += 36;
        p.Controls.Add(Note("Command mode lets you select text, hold the command key, and say things like \"make this more concise\". It needs any OpenAI-compatible endpoint. For 100% free + local, install Ollama (ollama.com) and it works out of the box with the defaults below. Dictation itself never needs this.", y)); y += 76;

        _aiEnabled = new CheckBox { Text = "Enable AI features", Location = new Point(0, y), AutoSize = true, Checked = _cfg.AiEnabled };
        p.Controls.Add(_aiEnabled); y += 34;

        p.Controls.Add(FieldLabel("Endpoint base URL", 0, y));
        _aiUrl = new TextBox { Location = new Point(160, y), Width = 340, Text = _cfg.AiBaseUrl };
        p.Controls.Add(_aiUrl); y += 34;

        p.Controls.Add(FieldLabel("Model", 0, y));
        _aiModel = new TextBox { Location = new Point(160, y), Width = 200, Text = _cfg.AiModel };
        p.Controls.Add(_aiModel); y += 34;

        p.Controls.Add(FieldLabel("API key (if needed)", 0, y));
        _aiKey = new TextBox { Location = new Point(160, y), Width = 340, Text = _cfg.AiApiKey, UseSystemPasswordChar = true };
        p.Controls.Add(_aiKey); y += 34;

        p.Controls.Add(FieldLabel("Command-mode hotkey", 0, y));
        _cmdHotkey = new ComboBox { Location = new Point(160, y), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        _cmdHotkey.Items.AddRange(Vk.Names.Cast<object>().ToArray());
        _cmdHotkey.SelectedItem = _cfg.CommandHotkeyName;
        if (_cmdHotkey.SelectedIndex < 0) _cmdHotkey.SelectedIndex = 0;
        p.Controls.Add(_cmdHotkey); y += 34;

        _aiPolish = new CheckBox { Text = "AI polish: run every dictation through the AI for cleanup (slower)", Location = new Point(0, y), AutoSize = true, Checked = _cfg.AiPolish };
        p.Controls.Add(_aiPolish); y += 40;

        var test = new Button { Text = "Test connection", Size = new Size(130, 30), Location = new Point(0, y) };
        _aiTestResult = new Label { Location = new Point(140, y + 6), AutoSize = true, ForeColor = Theme.SubText, MaximumSize = new Size(460, 0) };
        test.Click += async (_, _) =>
        {
            _aiTestResult.Text = "Testing…";
            var probe = Harvest(validateOnly: true);
            var (ok, msg) = await AiProvider.TestAsync(probe);
            _aiTestResult.ForeColor = ok ? Theme.Ok : Theme.Danger;
            _aiTestResult.Text = msg;
        };
        p.Controls.Add(test);
        p.Controls.Add(_aiTestResult);

        return p;
    }

    private Panel BuildAbout()
    {
        var p = new Panel();
        p.Controls.Add(Header("FreeFlow", 0));
        p.Controls.Add(Note(
            "Free, local, private voice dictation — a Wispr Flow replacement that costs nothing, forever.\n\n" +
            "• Speech recognition: sherpa-onnx running NVIDIA Parakeet / OpenAI Whisper models on your CPU\n" +
            "• Nothing ever leaves this machine (unless you wire up an AI endpoint yourself)\n" +
            "• No account, no subscription, no word limits\n\n" +
            $"Config folder: {Paths.AppDataDir}\n\n" +
            "Hold the hotkey → speak → release. Tap to latch hands-free. Say \"scratch that\" to undo, " +
            "\"new line\" / \"new paragraph\" to break lines. Select text and hold the command key to rewrite it with AI.",
            40, 640));
        return p;
    }

    #endregion

    private AppConfig Harvest(bool validateOnly = false)
    {
        var c = validateOnly ? _cfg.Clone() : _cfg;

        c.HotkeyName = (string)_hotkey.SelectedItem!;
        c.TapToLatch = _tapToLatch.Checked;
        c.TapThresholdMs = (int)_tapMs.Value;
        c.SwallowHotkey = _swallow.Checked;
        c.MicDevice = ((MicChoice)_mic.SelectedItem!).Index;
        c.KeepMicWarm = _warm.Checked;
        c.MicGain = (double)_gain.Value;
        c.PlaySounds = _sounds.Checked;
        c.SmartSpacing = _smartSpace.Checked;

        c.RemoveFillers = _fillers.Checked;
        c.FillerWords = _fillerList.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        c.SpokenCommands = _spokenCmds.Checked;
        c.PunctuationWords = _punctWords.Checked;
        c.DefaultTone = (string)_defaultTone.SelectedItem!;

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

        c.ModelId = ((ModelChoice)_modelPick.SelectedItem!).Model.Id;
        c.NumThreads = (int)_threads.Value;
        c.Language = _language.Text.Trim();

        c.AiEnabled = _aiEnabled.Checked;
        c.AiBaseUrl = _aiUrl.Text.Trim();
        c.AiModel = _aiModel.Text.Trim();
        c.AiApiKey = _aiKey.Text.Trim();
        c.AiPolish = _aiPolish.Checked;
        c.CommandHotkeyName = (string)_cmdHotkey.SelectedItem!;

        return c;
    }

    private void SaveAndClose()
    {
        var c = Harvest();

        if (c.CommandHotkeyName == c.HotkeyName)
        {
            MessageBox.Show("The command-mode hotkey must be different from the dictation hotkey.",
                "FreeFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Autostart.Set(_autostart.Checked);
        _onSave(c);
        Close();
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
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "autostart registry");
        }
    }
}

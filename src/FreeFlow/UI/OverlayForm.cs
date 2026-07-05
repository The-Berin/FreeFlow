using System.Drawing.Drawing2D;
using FreeFlow.Core;

namespace FreeFlow.UI;

public enum OverlayState { Hidden, Listening, Latched, Processing, Success, Error }

/// <summary>
/// The floating pill: a live frequency equalizer pulsing behind the words as they're
/// recognized, a state indicator on the left, and click-to-stop. Never steals focus.
/// </summary>
public sealed class OverlayForm : Form
{
    /// <summary>User clicked the pill while recording — treat as "stop".</summary>
    public event Action? StopRequested;

    private OverlayState _state = OverlayState.Hidden;
    private string _message = "";
    private string _liveText = "";
    private float _spinAngle;
    private DateTime _stateSince;
    private DateTime _recordingStarted;
    private readonly System.Windows.Forms.Timer _timer;

    // equalizer: target set from the audio thread, drawn values eased every frame
    private volatile float[] _bandsTarget = new float[16];
    private readonly float[] _bands = new float[16];

    private const int W = 500;
    private const int H = 64;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        BackColor = Color.FromArgb(14, 14, 20);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - W) / 2, wa.Bottom - H - 26);

        using var path = AssetGenerator.RoundedRect(new Rectangle(0, 0, W, H), H / 2);
        Region = new Region(path);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => { _spinAngle += 10; AutoHideCheck(); Invalidate(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x80 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_state is OverlayState.Listening or OverlayState.Latched)
            StopRequested?.Invoke();
    }

    public void SetState(OverlayState state, string message = "")
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetState(state, message));
            return;
        }

        _state = state;
        _message = message;
        _stateSince = DateTime.UtcNow;

        if (state is OverlayState.Listening or OverlayState.Latched)
        {
            if (_recordingStarted == default)
            {
                _recordingStarted = DateTime.UtcNow;
                _liveText = "";
                Array.Clear(_bands);
                _bandsTarget = new float[16];
            }
        }
        else if (state != OverlayState.Processing)
        {
            _recordingStarted = default;
        }

        if (state == OverlayState.Hidden)
        {
            _timer.Stop();
            _liveText = "";
            Hide();
        }
        else
        {
            if (!Visible)
                Show();
            _timer.Start();
            Invalidate();
        }
    }

    public void PushSpectrum(float[] bands) => _bandsTarget = bands;

    public void SetLiveText(string text)
    {
        _liveText = text;
        // repaint happens on the next timer tick; no cross-thread Invalidate needed
    }

    private void AutoHideCheck()
    {
        var age = DateTime.UtcNow - _stateSince;
        if (_state == OverlayState.Success && age > TimeSpan.FromMilliseconds(900))
            SetState(OverlayState.Hidden);
        else if (_state == OverlayState.Error && age > TimeSpan.FromMilliseconds(2400))
            SetState(OverlayState.Hidden);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        DrawEqualizer(g);

        using (var border = new Pen(Color.FromArgb(64, 124, 92, 255), 1.5f))
            g.DrawPath(border, AssetGenerator.RoundedRect(new Rectangle(1, 1, W - 3, H - 3), (H - 3) / 2));

        switch (_state)
        {
            case OverlayState.Listening:
                DrawDot(g, Color.FromArgb(240, 76, 76), pulse: true);
                DrawTranscript(g, _liveText, fallback: "listening…");
                DrawTimer(g);
                break;
            case OverlayState.Latched:
                DrawDot(g, Color.FromArgb(240, 76, 76), pulse: true, ring: true);
                DrawTranscript(g, _liveText, fallback: "listening — tap or click to stop");
                DrawTimer(g);
                break;
            case OverlayState.Processing:
                DrawSpinner(g);
                DrawTranscript(g, _liveText, fallback: "processing…", dim: true);
                break;
            case OverlayState.Success:
                DrawCheck(g);
                DrawTranscript(g, string.IsNullOrEmpty(_message) ? _liveText : _message, fallback: "done");
                break;
            case OverlayState.Error:
                DrawDot(g, Color.FromArgb(240, 165, 70), pulse: false);
                DrawTranscript(g, _message, fallback: "error");
                break;
        }
    }

    private void DrawEqualizer(Graphics g)
    {
        var target = _bandsTarget;
        bool live = _state is OverlayState.Listening or OverlayState.Latched;

        for (int i = 0; i < _bands.Length; i++)
        {
            float t = live && i < target.Length ? target[i] : 0f;
            // fast attack, slow decay — the classic equalizer feel
            _bands[i] = t > _bands[i]
                ? _bands[i] + (t - _bands[i]) * 0.55f
                : _bands[i] + (t - _bands[i]) * 0.16f;
        }

        const float left = 52, right = W - 64;
        float span = right - left;
        float bw = span / _bands.Length;

        for (int i = 0; i < _bands.Length; i++)
        {
            float v = Math.Clamp(_bands[i], 0.02f, 1f);
            float bh = Math.Max(3, v * (H - 22));
            var rect = new RectangleF(left + i * bw + 1.5f, (H - bh) / 2f, Math.Max(2f, bw - 3f), bh);
            int alpha = (int)(46 + 60 * v);
            using var brush = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y - 1, rect.Width, rect.Height + 2),
                Color.FromArgb(alpha, 138, 100, 255),
                Color.FromArgb(alpha, 190, 96, 255),
                90f);
            using var capsule = AssetGenerator.RoundedRectF(rect, rect.Width / 2f);
            g.FillPath(brush, capsule);
        }
    }

    private void DrawTranscript(Graphics g, string text, string fallback, bool dim = false)
    {
        bool empty = string.IsNullOrWhiteSpace(text);
        string shown = empty ? fallback : text.Replace('\n', ' ');

        using var font = new Font("Segoe UI", 10.5f, empty ? FontStyle.Italic : FontStyle.Regular);
        var color = empty || dim ? Theme.SubText : Color.FromArgb(238, 238, 246);
        using var brush = new SolidBrush(color);

        var rect = new RectangleF(50, 0, W - 50 - 62, H);
        var sf = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        // show the TAIL of long transcripts — newest words stay visible
        var size = g.MeasureString(shown, font);
        if (size.Width > rect.Width)
        {
            sf.Alignment = StringAlignment.Far;
            g.SetClip(rect);
            g.DrawString(shown, font, brush, new RectangleF(rect.Right - size.Width - 4, 0, size.Width + 8, H), sf);
            g.ResetClip();

            // fade the clipped left edge so it doesn't look chopped
            using var fade = new LinearGradientBrush(
                new RectangleF(rect.X - 1, 0, 26, H),
                Color.FromArgb(230, 14, 14, 20), Color.FromArgb(0, 14, 14, 20), 0f);
            g.FillRectangle(fade, rect.X - 1, 0, 26, H);
        }
        else
        {
            sf.Alignment = StringAlignment.Near;
            g.DrawString(shown, font, brush, rect, sf);
        }
    }

    private void DrawTimer(Graphics g)
    {
        if (_recordingStarted == default) return;
        var s = (DateTime.UtcNow - _recordingStarted).TotalSeconds;
        using var font = new Font("Segoe UI", 9f);
        using var brush = new SolidBrush(Theme.SubText);
        var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString($"{(int)s / 60}:{(int)s % 60:00}", font, brush, new RectangleF(0, 0, W - 18, H), sf);
    }

    private void DrawDot(Graphics g, Color color, bool pulse, bool ring = false)
    {
        float phase = (float)Math.Abs(Math.Sin(Environment.TickCount64 / 450.0));
        int r = pulse ? 7 + (int)(3 * phase) : 8;
        using var b = new SolidBrush(color);
        g.FillEllipse(b, 26 - r / 2f, H / 2f - r / 2f, r, r);
        if (ring)
        {
            using var pen = new Pen(Color.FromArgb(120, color), 1.6f);
            g.DrawEllipse(pen, 26 - 7.5f, H / 2f - 7.5f, 15, 15);
        }
    }

    private void DrawSpinner(Graphics g)
    {
        var rect = new RectangleF(18, H / 2f - 8, 16, 16);
        using var pen = new Pen(Theme.Accent, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, rect, _spinAngle, 270);
    }

    private void DrawCheck(Graphics g)
    {
        using var pen = new Pen(Theme.Ok, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new PointF(19, H / 2f), new PointF(25, H / 2f + 6), new PointF(35, H / 2f - 6) });
    }
}

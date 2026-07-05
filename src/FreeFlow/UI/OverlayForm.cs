using System.Drawing.Drawing2D;
using FreeFlow.Core;

namespace FreeFlow.UI;

public enum OverlayState { Hidden, Listening, Latched, Processing, Success, Error }

/// <summary>
/// The floating pill at the bottom of the screen: live mic levels while listening,
/// a spinner while transcribing, a flash of green on success. Never steals focus.
/// </summary>
public sealed class OverlayForm : Form
{
    private OverlayState _state = OverlayState.Hidden;
    private string _message = "";
    private readonly float[] _levels = new float[28];
    private int _levelPos;
    private float _spinAngle;
    private DateTime _stateSince;
    private DateTime _recordingStarted;
    private readonly System.Windows.Forms.Timer _timer;

    private const int W = 240;
    private const int H = 46;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        BackColor = Color.FromArgb(18, 18, 24);
        DoubleBuffered = true;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - W) / 2, wa.Bottom - H - 28);

        using var path = RoundedRect(new Rectangle(0, 0, W, H), H / 2);
        Region = new Region(path);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => { _spinAngle += 9; AutoHideCheck(); Invalidate(); };
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

        if (state == OverlayState.Listening || state == OverlayState.Latched)
        {
            if (_recordingStarted == default)
                _recordingStarted = DateTime.UtcNow;
        }
        else if (state != OverlayState.Processing)
        {
            _recordingStarted = default;
        }

        if (state == OverlayState.Hidden)
        {
            _timer.Stop();
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

    public void PushLevel(float rms)
    {
        _levels[_levelPos] = Math.Min(1f, rms * 6f);
        _levelPos = (_levelPos + 1) % _levels.Length;
    }

    private void AutoHideCheck()
    {
        var age = DateTime.UtcNow - _stateSince;
        if (_state == OverlayState.Success && age > TimeSpan.FromMilliseconds(900))
            SetState(OverlayState.Hidden);
        else if (_state == OverlayState.Error && age > TimeSpan.FromMilliseconds(2200))
            SetState(OverlayState.Hidden);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var border = new Pen(Color.FromArgb(70, 70, 88), 1.5f);
        g.DrawPath(border, RoundedRect(new Rectangle(1, 1, W - 3, H - 3), (H - 3) / 2));

        switch (_state)
        {
            case OverlayState.Listening:
            case OverlayState.Latched:
                DrawDot(g, Color.FromArgb(235, 70, 70), pulse: true);
                DrawLevels(g);
                DrawText(g, _state == OverlayState.Latched
                    ? "listening — tap to stop"
                    : Elapsed(), Theme.SubText);
                break;
            case OverlayState.Processing:
                DrawSpinner(g);
                DrawText(g, "processing…", Theme.SubText);
                break;
            case OverlayState.Success:
                DrawCheck(g);
                DrawText(g, string.IsNullOrEmpty(_message) ? "inserted" : _message, Theme.Ok);
                break;
            case OverlayState.Error:
                DrawDot(g, Color.FromArgb(235, 160, 60), pulse: false);
                DrawText(g, _message, Color.FromArgb(235, 180, 120));
                break;
        }
    }

    private string Elapsed()
    {
        if (_recordingStarted == default) return "listening";
        var s = (DateTime.UtcNow - _recordingStarted).TotalSeconds;
        return $"{(int)s / 60}:{(int)s % 60:00}";
    }

    private void DrawDot(Graphics g, Color color, bool pulse)
    {
        int r = pulse ? 5 + (int)(2 * Math.Abs(Math.Sin(DateTime.UtcNow.Millisecond / 1000.0 * Math.PI))) : 6;
        using var b = new SolidBrush(color);
        g.FillEllipse(b, 18 - r / 2, H / 2 - r / 2, r, r);
    }

    private void DrawSpinner(Graphics g)
    {
        var rect = new RectangleF(12, H / 2f - 7, 14, 14);
        using var pen = new Pen(Theme.Accent, 2.5f);
        g.DrawArc(pen, rect, _spinAngle, 270);
    }

    private void DrawCheck(Graphics g)
    {
        using var pen = new Pen(Theme.Ok, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new PointF(13, H / 2f), new PointF(18, H / 2f + 5), new PointF(26, H / 2f - 5) });
    }

    private void DrawLevels(Graphics g)
    {
        int barCount = _levels.Length;
        float x0 = 36, width = W - x0 - 74, bw = width / barCount;
        using var brush = new SolidBrush(Theme.Accent);
        for (int i = 0; i < barCount; i++)
        {
            float v = _levels[(_levelPos + i) % barCount];
            float bh = Math.Max(2, v * (H - 18));
            g.FillRectangle(brush, x0 + i * bw, (H - bh) / 2, Math.Max(1.5f, bw - 1.5f), bh);
        }
    }

    private void DrawText(Graphics g, string text, Color color)
    {
        using var f = new Font("Segoe UI", 9f);
        using var b = new SolidBrush(color);
        var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString(text, f, b, new RectangleF(0, 0, W - 14, H), sf);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

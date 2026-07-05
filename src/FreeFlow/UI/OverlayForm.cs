using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

public enum OverlayState { Hidden, Listening, Latched, Processing, Success, Error }

/// <summary>
/// A small, quiet status pill at the bottom of the screen: a recording dot,
/// live level bars, and the elapsed time. Grows only to show an error message.
/// Never steals focus; click to stop recording.
/// </summary>
public sealed class OverlayForm : Form
{
    /// <summary>User clicked the pill while recording — treat as "stop".</summary>
    public event Action? StopRequested;

    private OverlayState _state = OverlayState.Hidden;
    private string _message = "";
    private float _spinAngle;
    private DateTime _stateSince;
    private DateTime _recordingStarted;
    private readonly System.Windows.Forms.Timer _timer;

    private volatile float[] _bandsTarget = new float[BarCount];
    private readonly float[] _bands = new float[BarCount];

    private const int BarCount = 12;
    private const int BaseW = 172;
    private const int H = 30;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(19, 19, 23);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;

        ApplyWidth(BaseW);

        _timer = new System.Windows.Forms.Timer { Interval = 33 };
        _timer.Tick += (_, _) => { _spinAngle += 9; AutoHideCheck(); Invalidate(); };
    }

    private void ApplyWidth(int width)
    {
        Size = new Size(width, H);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - width) / 2, wa.Bottom - H - 16);
        Region = new Region(Draw.RoundedRect(new Rectangle(0, 0, width, H), H / 2));
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

        // errors get room for their text; everything else stays small
        if (state == OverlayState.Error && message.Length > 0)
        {
            int textW = TextRenderer.MeasureText(message, ErrorFont).Width;
            ApplyWidth(Math.Clamp(textW + 44, BaseW, 380));
        }
        else if (Width != BaseW)
        {
            ApplyWidth(BaseW);
        }

        if (state is OverlayState.Listening or OverlayState.Latched)
        {
            if (_recordingStarted == default)
            {
                _recordingStarted = DateTime.UtcNow;
                Array.Clear(_bands);
                _bandsTarget = new float[BarCount];
            }
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

    public void PushSpectrum(float[] bands)
    {
        // fold incoming bands (any count) down to our bar count
        var folded = new float[BarCount];
        for (int i = 0; i < BarCount; i++)
            folded[i] = bands[i * bands.Length / BarCount];
        _bandsTarget = folded;
    }

    private void AutoHideCheck()
    {
        var age = DateTime.UtcNow - _stateSince;
        if (_state == OverlayState.Success && age > TimeSpan.FromMilliseconds(700))
            SetState(OverlayState.Hidden);
        else if (_state == OverlayState.Error && age > TimeSpan.FromMilliseconds(2400))
            SetState(OverlayState.Hidden);
    }

    private static readonly Font TimerFont = new("Segoe UI", 7.5f);
    private static readonly Font ErrorFont = new("Segoe UI", 8.5f);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var border = new Pen(Color.FromArgb(60, 60, 68), 1f))
            g.DrawPath(border, Draw.RoundedRect(new Rectangle(0, 0, Width - 1, H - 1), (H - 1) / 2));

        switch (_state)
        {
            case OverlayState.Listening:
            case OverlayState.Latched:
                DrawDot(g, Color.FromArgb(224, 82, 82), hollow: _state == OverlayState.Latched);
                DrawBars(g, live: true);
                DrawTimer(g);
                break;
            case OverlayState.Processing:
                DrawSpinner(g);
                DrawBars(g, live: false);
                break;
            case OverlayState.Success:
                DrawCheck(g);
                break;
            case OverlayState.Error:
                DrawDot(g, Color.FromArgb(224, 150, 70), hollow: false);
                using (var brush = new SolidBrush(Color.FromArgb(210, 210, 218)))
                {
                    var sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
                    g.DrawString(_message, ErrorFont, brush, new RectangleF(26, 0, Width - 30, H), sf);
                }
                break;
        }
    }

    private void DrawBars(Graphics g, bool live)
    {
        var target = _bandsTarget;
        for (int i = 0; i < BarCount; i++)
        {
            float t = live ? target[i] : 0f;
            _bands[i] = t > _bands[i]
                ? _bands[i] + (t - _bands[i]) * 0.55f
                : _bands[i] + (t - _bands[i]) * 0.18f;
        }

        const float left = 26, right = BaseW - 40;
        float pitch = (right - left) / BarCount;

        for (int i = 0; i < BarCount; i++)
        {
            float v = Math.Clamp(_bands[i], 0.04f, 1f);
            float bh = Math.Max(2.5f, v * (H - 12));
            var rect = new RectangleF(left + i * pitch, (H - bh) / 2f, 3f, bh);
            int alpha = live ? 90 + (int)(150 * v) : 60;
            using var brush = new SolidBrush(Color.FromArgb(alpha, 235, 235, 242));
            using var capsule = Draw.RoundedRectF(rect, 1.5f);
            g.FillPath(brush, capsule);
        }
    }

    private void DrawTimer(Graphics g)
    {
        if (_recordingStarted == default) return;
        var s = (DateTime.UtcNow - _recordingStarted).TotalSeconds;
        using var brush = new SolidBrush(Color.FromArgb(140, 140, 152));
        var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.DrawString($"{(int)s / 60}:{(int)s % 60:00}", TimerFont, brush, new RectangleF(0, 0, Width - 10, H), sf);
    }

    private void DrawDot(Graphics g, Color color, bool hollow)
    {
        float phase = (float)Math.Abs(Math.Sin(Environment.TickCount64 / 500.0));
        float r = 5f + 1.5f * phase;
        if (hollow)
        {
            using var pen = new Pen(color, 1.6f);
            g.DrawEllipse(pen, 13 - r / 2, H / 2f - r / 2, r, r);
        }
        else
        {
            using var b = new SolidBrush(color);
            g.FillEllipse(b, 13 - r / 2, H / 2f - r / 2, r, r);
        }
    }

    private void DrawSpinner(Graphics g)
    {
        var rect = new RectangleF(9, H / 2f - 5, 10, 10);
        using var pen = new Pen(Color.FromArgb(170, 170, 182), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, rect, _spinAngle, 270);
    }

    private void DrawCheck(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(96, 200, 130), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width / 2f;
        g.DrawLines(pen, new[] { new PointF(cx - 7, H / 2f), new PointF(cx - 2, H / 2f + 5), new PointF(cx + 7, H / 2f - 5) });
    }
}

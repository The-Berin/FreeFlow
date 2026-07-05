using System.Drawing.Drawing2D;

namespace FreeFlow.UI;

/// <summary>Small GDI+ helpers shared by the pill and tray icons.</summary>
public static class Draw
{
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
        => RoundedRectF(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);

    public static GraphicsPath RoundedRectF(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>The five-bar wave mark used on the tray icon tiles.</summary>
    public static void Wave(Graphics g, Rectangle bounds, float barW, Color color)
    {
        float[] bars = { 0.30f, 0.62f, 0.44f, 0.80f, 0.36f };
        float gap = barW * 0.9f;
        float totalW = bars.Length * barW + (bars.Length - 1) * gap;
        float x = bounds.X + (bounds.Width - totalW) / 2f;
        float cy = bounds.Y + bounds.Height / 2f;

        using var brush = new SolidBrush(color);
        foreach (var v in bars)
        {
            float h = bounds.Height * 0.62f * v;
            using var capsule = RoundedRectF(new RectangleF(x, cy - h / 2f, barW, h), barW / 2f);
            g.FillPath(brush, capsule);
            x += barW + gap;
        }
    }
}

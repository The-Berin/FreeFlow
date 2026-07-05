using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace FreeFlow;

/// <summary>
/// Draws the FreeFlow brand — a violet gradient tile with a white audio wave —
/// and writes app.ico (multi-size), logo PNGs, and the README banner.
/// Run with: FreeFlow.exe --makeassets &lt;outputDir&gt;
/// </summary>
public static class AssetGenerator
{
    private static readonly Color GradTop = Color.FromArgb(109, 74, 255);   // violet
    private static readonly Color GradBottom = Color.FromArgb(175, 74, 255); // purple-pink
    private static readonly float[] WaveBars = { 0.30f, 0.62f, 0.44f, 0.80f, 0.36f };

    public static int Generate(string outDir)
    {
        Directory.CreateDirectory(outDir);

        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var tiles = sizes.Select(DrawTile).ToArray();
        WriteIco(Path.Combine(outDir, "app.ico"), tiles);

        using (var logo = DrawTile(256))
            logo.Save(Path.Combine(outDir, "logo256.png"), ImageFormat.Png);
        using (var logo = DrawTile(64))
            logo.Save(Path.Combine(outDir, "logo64.png"), ImageFormat.Png);

        using (var banner = DrawBanner(1200, 360))
            banner.Save(Path.Combine(outDir, "banner.png"), ImageFormat.Png);

        foreach (var t in tiles) t.Dispose();
        return 0;
    }

    /// <summary>The app tile: rounded gradient square, white wave bars, soft top gloss.</summary>
    public static Bitmap DrawTile(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var rect = new Rectangle(0, 0, s, s);
        int radius = Math.Max(2, (int)(s * 0.22f));
        using var path = RoundedRect(rect, radius);
        using var grad = new LinearGradientBrush(rect, GradTop, GradBottom, 55f);
        g.FillPath(grad, path);

        // soft gloss fading out toward the middle
        var glossRect = new Rectangle(0, 0, s, Math.Max(2, (int)(s * 0.55f)));
        using (var glossPath = RoundedRect(rect, radius))
        using (var gloss = new LinearGradientBrush(glossRect, Color.FromArgb(52, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
        {
            g.SetClip(glossPath);
            g.FillRectangle(gloss, glossRect);
            g.ResetClip();
        }

        DrawWave(g, rect, s * 0.062f, Color.White);
        return bmp;
    }

    /// <summary>The wave mark alone (used by tray icons on their own colored tiles).</summary>
    public static void DrawWave(Graphics g, Rectangle bounds, float barW, Color color)
    {
        int n = WaveBars.Length;
        float gap = barW * 0.9f;
        float totalW = n * barW + (n - 1) * gap;
        float x = bounds.X + (bounds.Width - totalW) / 2f;
        float cy = bounds.Y + bounds.Height / 2f;

        using var brush = new SolidBrush(color);
        for (int i = 0; i < n; i++)
        {
            float h = bounds.Height * 0.62f * WaveBars[i];
            var r = new RectangleF(x, cy - h / 2f, barW, h);
            using var capsule = RoundedRectF(r, barW / 2f);
            g.FillPath(brush, capsule);
            x += barW + gap;
        }
    }

    private static Bitmap DrawBanner(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                   Color.FromArgb(19, 19, 26), Color.FromArgb(28, 24, 44), 35f))
            g.FillRectangle(bg, 0, 0, w, h);

        // faint oversized wave in the background
        using (var faint = new Bitmap(w, h))
        {
            using var fg = Graphics.FromImage(faint);
            fg.SmoothingMode = SmoothingMode.AntiAlias;
            DrawWave(fg, new Rectangle(w / 2, -h / 3, w / 2, h * 2), w * 0.03f, Color.FromArgb(14, 255, 255, 255));
            g.DrawImage(faint, 0, 0);
        }

        int tile = (int)(h * 0.52f);
        using (var logo = DrawTile(tile))
            g.DrawImage(logo, h * 0.35f, (h - tile) / 2f, tile, tile);

        float textX = h * 0.35f + tile + 36;
        using var name = new Font("Segoe UI", h * 0.24f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var tag = new Font("Segoe UI", h * 0.085f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(240, 240, 248));
        using var gray = new SolidBrush(Color.FromArgb(150, 150, 168));
        g.DrawString("FreeFlow", name, white, textX, h * 0.26f);
        g.DrawString("free • local • private voice dictation", tag, gray, textX + 6, h * 0.58f);

        return bmp;
    }

    private static void WriteIco(string path, Bitmap[] images)
    {
        var pngs = images.Select(b =>
        {
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);                 // reserved
        bw.Write((ushort)1);                 // type: icon
        bw.Write((ushort)images.Length);

        int offset = 6 + 16 * images.Length;
        for (int i = 0; i < images.Length; i++)
        {
            int wpx = images[i].Width, hpx = images[i].Height;
            bw.Write((byte)(wpx >= 256 ? 0 : wpx));
            bw.Write((byte)(hpx >= 256 ? 0 : hpx));
            bw.Write((byte)0);               // palette
            bw.Write((byte)0);               // reserved
            bw.Write((ushort)1);             // planes
            bw.Write((ushort)32);            // bpp
            bw.Write(pngs[i].Length);
            bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs)
            bw.Write(png);
    }

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
}

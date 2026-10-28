using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ShareKM;

// The app glyph: rounded square, white mouse pointer, and (from 32 px up) a left/right arrow.
// Used for the tray icon (tinted per state) and by the icon generator for Assets\ShareKM.ico.
static class Glyph
{
    public static readonly Color Blue = Color.FromArgb(0x23, 0x9B, 0xD8);

    static readonly PointF[] Pointer = [new(0, 0), new(0, 0.80f), new(0.20f, 0.62f), new(0.33f, 0.92f),
        new(0.46f, 0.86f), new(0.33f, 0.57f), new(0.58f, 0.57f)];

    public static Bitmap Render(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float pad = size <= 24 ? 0 : size * 0.04f, u = size - 2 * pad;
        var rect = new RectangleF(pad, pad, u, u);
        using (var bg = Rounded(rect, u * 0.22f))
        using (var fill = new LinearGradientBrush(rect, Shade(color, 1.12f), Shade(color, 0.78f), 90f))
            g.FillPath(fill, bg);

        bool small = size < 32;
        float scale = u * (small ? 0.78f : 0.58f);
        float x = pad + u * (small ? 0.30f : 0.37f), y = pad + u * (small ? 0.12f : 0.10f);
        using var white = new SolidBrush(Color.White);
        g.FillPolygon(white, [.. Pointer.Select(p => new PointF(x + p.X * scale, y + p.Y * scale))]);

        if (!small)
        {
            float x0 = pad + u * 0.18f, x1 = pad + u * 0.82f, yc = pad + u * 0.80f, h = u * 0.12f, t = u * 0.045f;
            PointF[] arrow = [
                new(x0, yc), new(x0 + h, yc - h), new(x0 + h, yc - t), new(x1 - h, yc - t), new(x1 - h, yc - h),
                new(x1, yc), new(x1 - h, yc + h), new(x1 - h, yc + t), new(x0 + h, yc + t), new(x0 + h, yc + h),
            ];
            g.FillPolygon(white, arrow);
        }
        return bmp;
    }

    static Color Shade(Color c, float f) =>
        Color.FromArgb(c.A, Math.Min(255, (int)(c.R * f)), Math.Min(255, (int)(c.G * f)), Math.Min(255, (int)(c.B * f)));

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

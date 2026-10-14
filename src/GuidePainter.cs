using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LayerChannelLive;

internal static class GuidePainter
{
    public static void Draw(Graphics g, Size size, UiPalette p, RectangleF canvas, RectangleF image,
        bool left, bool right, bool centerX, bool top, bool bottom, bool centerY,
        bool scaleSnapped, string label, PointF anchor)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var reference = new Pen(p.IsDark ? Color.FromArgb(116, 135, 153) : Color.FromArgb(126, 142, 158), 1)
        { DashPattern = new[] { 2f, 5f } };
        g.DrawRectangle(reference, canvas.X, canvas.Y, canvas.Width, canvas.Height);

        float x0 = Math.Max(0, Math.Min(canvas.Left, image.Left) - 18);
        float x1 = Math.Min(size.Width, Math.Max(canvas.Right, image.Right) + 18);
        float y0 = Math.Max(0, Math.Min(canvas.Top, image.Top) - 18);
        float y1 = Math.Min(size.Height, Math.Max(canvas.Bottom, image.Bottom) + 18);
        using var outline = new Pen(scaleSnapped ? p.Guide : p.Accent, 1.15f);
        g.DrawRectangle(outline, image.X, image.Y, image.Width, image.Height);
        using var guide = new Pen(p.Guide, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (left) g.DrawLine(guide, canvas.Left, y0, canvas.Left, y1);
        if (right) g.DrawLine(guide, canvas.Right, y0, canvas.Right, y1);
        if (top) g.DrawLine(guide, x0, canvas.Top, x1, canvas.Top);
        if (bottom) g.DrawLine(guide, x0, canvas.Bottom, x1, canvas.Bottom);
        if (centerX) g.DrawLine(guide, canvas.Left + canvas.Width / 2, y0, canvas.Left + canvas.Width / 2, y1);
        if (centerY) g.DrawLine(guide, x0, canvas.Top + canvas.Height / 2, x1, canvas.Top + canvas.Height / 2);

        // 短角标帮助辨认图像边界，不覆盖画布细节。
        const float corner = 6;
        foreach (PointF pt in new[] { new PointF(image.Left, image.Top), new PointF(image.Right, image.Top),
            new PointF(image.Left, image.Bottom), new PointF(image.Right, image.Bottom) })
        {
            float dx = pt.X == image.Left ? corner : -corner;
            float dy = pt.Y == image.Top ? corner : -corner;
            g.DrawLine(guide, pt, new PointF(pt.X + dx, pt.Y));
            g.DrawLine(guide, pt, new PointF(pt.X, pt.Y + dy));
        }
        if (string.IsNullOrWhiteSpace(label) || size.Width < 28 || size.Height < 28) return;
        using var pointFill = new SolidBrush(p.Surface);
        g.FillEllipse(pointFill, anchor.X - 3, anchor.Y - 3, 6, 6);
        g.DrawEllipse(guide, anchor.X - 3, anchor.Y - 3, 6, 6);

        using var font = new Font("Segoe UI", 9f);
        SizeF measured = g.MeasureString(label, font);
        float width = Math.Min(size.Width - 12, measured.Width + 30);
        const float height = 28;
        float x = Math.Clamp(anchor.X + 14, 6, Math.Max(6, size.Width - width - 6));
        float y = anchor.Y + 14;
        if (y + height > size.Height - 6) y = anchor.Y - height - 14;
        y = Math.Clamp(y, 6, Math.Max(6, size.Height - height - 6));
        UiStyle.Card(g, new RectangleF(x, y, width, height), p.Surface, p.Border, 7);
        using var dot = new SolidBrush(p.Guide);
        g.FillEllipse(dot, x + 9, y + 11, 5, 5);
        UiStyle.Text(g, label, font, new RectangleF(x + 19, y, Math.Max(1, width - 23), height), p.Text);
    }
}

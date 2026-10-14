using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace LayerChannelLive;

internal sealed record UiPalette(bool IsDark, Color Background, Color Surface, Color Raised,
    Color Border, Color Text, Color Muted, Color Accent, Color Selected, Color Hover, Color Guide,
    Color CheckerLight, Color CheckerDark);

internal static class UiStyle
{
    private static Type? colorsType;
    private static object? lastTheme;
    private static UiPalette? hostPalette;
    public static UiPalette Current(Control? fallback = null)
    {
        try
        {
            colorsType ??= AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PaintDotNet.VisualStyling.AeroColors", false)).FirstOrDefault(t => t != null);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            object? theme = colorsType?.GetProperty("CurrentTheme", flags)?.GetValue(null);
            if (theme != null)
            {
                if (ReferenceEquals(theme, lastTheme) && hostPalette != null) return hostPalette;
                Color Read(string name, Color backup) => colorsType?.GetProperty(name, flags)?.GetValue(null) is Color value ? value : backup;
                bool dark = colorsType?.GetProperty("IsDark", flags)?.GetValue(null) is true;
                Color surface = Read("FormBackColor", SystemColors.Control);
                Color background = Read("ContentBackColor", surface);
                Color text = Read("FormTextColor", SystemColors.ControlText);
                Color border = Read("SeparatorLineLineColor", SystemColors.ControlDark);
                Color accent = SystemColors.Highlight;
                hostPalette = new UiPalette(dark, background, surface,
                    Read("ToolBarBackFillGradMidColor", surface), border, text, Blend(surface, text, 0.60f), accent,
                    Blend(surface, accent, dark ? 0.22f : 0.12f), Blend(surface, text, 0.05f), accent,
                    Blend(surface, text, 0.08f), Blend(surface, text, 0.16f));
                lastTheme = theme;
                return hostPalette;
            }
        }
        catch { }
        Color back = fallback?.FindForm()?.BackColor ?? SystemColors.Control;
        bool isDark = back.GetBrightness() < 0.45f;
        Color foreground = fallback?.FindForm()?.ForeColor ?? SystemColors.ControlText;
        return new UiPalette(isDark, back, back, back, SystemColors.ControlDark, foreground,
            Blend(back, foreground, 0.60f), SystemColors.Highlight, Blend(back, SystemColors.Highlight, 0.15f),
            Blend(back, foreground, 0.05f), SystemColors.Highlight, Blend(back, foreground, 0.08f), Blend(back, foreground, 0.16f));
    }

    private static Color Blend(Color background, Color foreground, float amount) => Color.FromArgb(
        (int)Math.Round(background.R + (foreground.R - background.R) * amount),
        (int)Math.Round(background.G + (foreground.G - background.G) * amount),
        (int)Math.Round(background.B + (foreground.B - background.B) * amount));

    public static IDisposable? WatchTheme(Control owner, Action changed)
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("PaintDotNet.VisualStyling.ThemeConfig", false)).FirstOrDefault(t => t != null);
            var evt = type?.GetEvent("Changed", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (evt == null) return null;
            EventHandler handler = (_, _) =>
            {
                if (owner.IsDisposed) return;
                if (owner.InvokeRequired)
                {
                    if (owner.IsHandleCreated) try { owner.BeginInvoke(changed); } catch (InvalidOperationException) { }
                }
                else changed();
            };
            evt.AddEventHandler(null, handler);
            return new ThemeSubscription(evt, handler);
        }
        catch { return null; }
    }

    private sealed class ThemeSubscription(EventInfo evt, EventHandler handler) : IDisposable
    {
        public void Dispose() { try { evt.RemoveEventHandler(null, handler); } catch { } }
    }

    public static void Text(Graphics g, string text, Font font, RectangleF bounds, Color color,
        StringAlignment alignment = StringAlignment.Near)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = alignment, LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0) return path;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Card(Graphics g, RectangleF rect, Color fill, Color border, float radius = 7)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        using var path = Rounded(rect, radius);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    public static void Eye(Graphics g, Rectangle rect, bool visible, Color color)
    {
        // 单色轮廓与斜杠即可区分状态，不添加底色、边框、光晕或高亮。
        using var pen = new Pen(color, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x = rect.X + 1, y = rect.Y + rect.Height / 2f, width = rect.Width - 2;
        using var eye = new GraphicsPath();
        eye.AddBezier(x, y, x + width * 0.3f, y - 6, x + width * 0.7f, y - 6, x + width, y);
        eye.AddBezier(x + width, y, x + width * 0.7f, y + 6, x + width * 0.3f, y + 6, x, y);
        g.DrawPath(pen, eye);
        if (visible) g.DrawEllipse(pen, x + width / 2 - 2.2f, y - 2.2f, 4.4f, 4.4f);
        else g.DrawLine(pen, x + 1, y + 6, x + width - 1, y - 6);
    }
}

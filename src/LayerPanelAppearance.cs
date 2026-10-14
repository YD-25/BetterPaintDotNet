using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using PaintDotNet;

namespace LayerChannelLive;

internal sealed partial class LayerChannelPanel
{
    private readonly Font appearanceFont = new Font("Segoe UI", 9f);
    private UiPalette palette = UiStyle.Current();
    private IDisposable? themeSubscription;
    private Layer? hoveredLayer;

    private void SyncTheme()
    {
        UiPalette next = UiStyle.Current(this);
        if (next == this.palette && this.BackColor == next.Background) return;
        this.palette = next;
        this.BackColor = next.Background;
        this.ForeColor = next.Text;
        this.Invalidate();
    }

    private void DrawPreviewBackground(Graphics g, Rectangle rect)
    {
        using var light = new SolidBrush(this.palette.CheckerLight);
        using var dark = new SolidBrush(this.palette.CheckerDark);
        g.FillRectangle(light, rect);
        const int cell = 6;
        for (int y = 0; y < rect.Height; y += cell)
        for (int x = 0; x < rect.Width; x += cell)
            if (((x / cell + y / cell) & 1) != 0)
                g.FillRectangle(dark, rect.X + x, rect.Y + y, Math.Min(cell, rect.Width - x), Math.Min(cell, rect.Height - y));
    }

    private void UpdateHoveredLayer(Point mouse)
    {
        int y = mouse.Y - HeaderHeight + this.scrollOffset;
        Layer? next = mouse.Y >= HeaderHeight && mouse.Y < this.ClientSize.Height - FooterHeight
            ? this.layerRows.FirstOrDefault(row => y >= row.Top && y < row.Top + this.LayerRowH()).Layer : null;
        if (ReferenceEquals(next, this.hoveredLayer)) return;
        this.hoveredLayer = next;
        this.Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.hoveredLayer = null;
        this.Invalidate();
    }
}

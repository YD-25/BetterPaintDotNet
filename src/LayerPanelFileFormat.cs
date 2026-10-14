using System.IO;
using System.Reflection;
using System.Drawing;
using PaintDotNet;

namespace LayerChannelLive;

internal sealed partial class LayerChannelPanel
{
    private const int FooterHeight = 29;
    private ImageFormatInfo imageFormat = ImageFormatReader.Read(null);
    private Layer? formatLayer;
    private string? formatLayerName;
    private string? formatPath;
    private long formatLength = -1;
    private DateTime formatWriteTime;
    private long lastFormatPoll;
    private readonly System.Windows.Forms.ToolTip formatTip = new();

    private void SynchronizeImageFormat(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - this.lastFormatPoll < 300) return;
        this.lastFormatPoll = now;

        Layer? currentLayer = this.activeLayer;
        string? currentLayerName = currentLayer?.Name;
        bool layerChanged = !ReferenceEquals(currentLayer, this.formatLayer) || currentLayerName != this.formatLayerName;

        string? path = LayerFilePathResolver.Resolve(currentLayer, this.documentWorkspace, this.document);

        long length = -1;
        DateTime written = default;
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var file = new FileInfo(path);
                if (file.Exists) { length = file.Length; written = file.LastWriteTimeUtc; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }

        if (!force && !layerChanged && path == this.formatPath && length == this.formatLength && written == this.formatWriteTime) return;

        this.formatLayer = currentLayer;
        this.formatLayerName = currentLayerName;
        this.formatPath = path;
        this.formatLength = length;
        this.formatWriteTime = written;

        if (string.IsNullOrWhiteSpace(path))
        {
            if (this.document == null)
            {
                this.imageFormat = new("未保存", "格式未知", "当前没有打开的图片");
            }
            else
            {
                string layerDesc = currentLayer != null ? $"当前图层：{currentLayer.Name}" : "当前图层未保存";
                this.imageFormat = new("未保存", "格式未知", $"{layerDesc}（未找到对应的磁盘文件）");
            }
        }
        else
        {
            var rawFormat = ImageFormatReader.Read(path);
            string layerPrefix = currentLayer != null ? $"图层：{currentLayer.Name}\n文件：{path}\n" : $"文件：{path}\n";
            this.imageFormat = new(rawFormat.Container, rawFormat.Encoding, layerPrefix + rawFormat.Details);
        }

        this.Invalidate(new Rectangle(0, this.ClientSize.Height - FooterHeight, this.ClientSize.Width, FooterHeight));
    }

    private void DrawFormatFooter(Graphics g)
    {
        int y = Math.Max(0, this.ClientSize.Height - FooterHeight);
        using var fill = new SolidBrush(this.palette.Surface);
        g.FillRectangle(fill, 0, y, this.ClientSize.Width, FooterHeight);
        using var edge = new Pen(this.palette.Border);
        g.DrawLine(edge, 0, y, this.ClientSize.Width, y);
        using var font = new Font(this.Font.FontFamily, 8.25f);

        string displayText = this.document == null ? "没有打开的图片" : this.imageFormat.Display;
        UiStyle.Text(g, displayText,
            font, new RectangleF(9, y + 1, Math.Max(1, this.ClientSize.Width - 18), FooterHeight - 2), this.palette.Muted);
    }

    private void UpdateFormatTooltip(Point point)
    {
        string text = "";
        if (point.Y >= this.ClientSize.Height - FooterHeight && this.document != null)
        {
            text = this.imageFormat.Display;
            if (!string.IsNullOrWhiteSpace(this.imageFormat.Details))
            {
                text += "\n" + this.imageFormat.Details;
            }
        }
        if (this.formatTip.GetToolTip(this) != text) this.formatTip.SetToolTip(this, text);
    }
}

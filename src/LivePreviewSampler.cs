using PaintDotNet;
using PaintDotNet.Rendering;

namespace LayerChannelLive;

/// <summary>在后台从原生图层临时渲染器生成小尺寸快照，不修改图层或撤销记录。</summary>
internal static class LivePreviewSampler
{
    public static Surface Sample(IRenderer<ColorBgra> source, int maxWidth, int maxHeight, CancellationToken cancellation = default)
    {
        int width = source.Width, height = source.Height;
        if (width <= 0 || height <= 0 || maxWidth <= 0 || maxHeight <= 0) throw new ArgumentOutOfRangeException(nameof(source));
        double ratio = Math.Min(1, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        int outWidth = Math.Max(1, (int)Math.Round(width * ratio));
        int outHeight = Math.Max(1, (int)Math.Round(height * ratio));
        var result = new Surface(outWidth, outHeight);
        try
        {
            using var row = new Surface(width, 1);
            var sums = new long[outWidth * 4];
            var counts = new int[outWidth];
            for (int y = 0; y < outHeight; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                Array.Clear(sums);
                Array.Clear(counts);
                int y0 = y * height / outHeight;
                int y1 = Math.Max(y0 + 1, (y + 1) * height / outHeight);
                int samples = Math.Min(4, y1 - y0);
                for (int sample = 0; sample < samples; sample++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int sy = Math.Min(height - 1, y0 + (2 * sample + 1) * (y1 - y0) / (2 * samples));
                    source.Render(row.AsRegionPtr(), new Point2Int32(0, sy));
                    for (int x = 0; x < outWidth; x++)
                    {
                        int x0 = x * width / outWidth;
                        int x1 = Math.Max(x0 + 1, (x + 1) * width / outWidth);
                        for (int sx = x0; sx < x1; sx++)
                        {
                            ColorBgra pixel = row.GetPointUnchecked(sx, 0);
                            sums[x * 4] += pixel.B;
                            sums[x * 4 + 1] += pixel.G;
                            sums[x * 4 + 2] += pixel.R;
                            sums[x * 4 + 3] += pixel.A;
                            counts[x]++;
                        }
                    }
                }
                for (int x = 0; x < outWidth; x++)
                {
                    int n = Math.Max(1, counts[x]);
                    result[x, y] = ColorBgra.FromBgra((byte)(sums[x * 4] / n), (byte)(sums[x * 4 + 1] / n),
                        (byte)(sums[x * 4 + 2] / n), (byte)(sums[x * 4 + 3] / n));
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}

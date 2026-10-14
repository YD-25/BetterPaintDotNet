using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>四个通道的标识，顺序就是界面上从上到下的排列顺序。</summary>
internal enum ChannelKind
{
    R = 0,
    G = 1,
    B = 2,
    A = 3,
}

/// <summary>一次抽取的结果，调用方负责 Dispose。</summary>
internal sealed class ChannelSet : IDisposable
{
    public Bitmap?[] Channels { get; } = new Bitmap?[4];

    public Bitmap? this[ChannelKind kind]
    {
        get => this.Channels[(int)kind];
        set => this.Channels[(int)kind] = value;
    }

    /// <summary>抽取时使用的缩放比例（图层像素 → 预览像素）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>图层原始尺寸（用于界面标注与坐标反算）。</summary>
    public int SourceWidth { get; set; }

    public int SourceHeight { get; set; }

    public void Dispose()
    {
        for (int i = 0; i < this.Channels.Length; i++)
        {
            Safe.DisposeQuietly(this.Channels[i]);
            this.Channels[i] = null;
        }
    }
}

/// <summary>GDI+ 相关的防御性辅助。绘制路径上任何异常都必须被吞掉，否则会带崩宿主进程。</summary>
internal static class Safe
{
    /// <summary>GDI+ 对超大/超小尺寸会抛 ArgumentException，先挡一道。</summary>
    public static bool IsDrawableSize(int width, int height)
        => width > 0 && height > 0 && width <= 32767 && height <= 32767;

    /// <summary>GDI+ 能安全绘制的 Bitmap 判定。注意 new Bitmap(0,0) 可以成功构造，
    /// 但直到第一次绘制才抛 "Parameter is not valid"，所以必须查宽高而不是 null 判断。</summary>
    public static bool IsDrawable(Bitmap? bmp)
    {
        if (bmp is null)
        {
            return false;
        }

        try
        {
            return IsDrawableSize(bmp.Width, bmp.Height);
        }
        catch
        {
            return false;
        }
    }

    public static void DisposeQuietly(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>把任意绘制动作包起来。失败只记录，不往上抛。</summary>
    public static void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Boot.LogDiag("paint:" + what, "绘制失败(" + what + "): " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

/// <summary>
/// 从 PaintDotNet.Surface 里把 R / G / B / A 拆成四张**按预览尺寸缩放好**的灰阶位图。
///
/// 设计与取舍：
///   * 直接按目标尺寸抽取，而不是"先建全尺寸副本再缩放"。
///     早期做法会为 800×600 的图层建 4 张全尺寸位图（约 7.3MB），
///     每帧还要 GDI+ 缩放一次；现在数据量与内存都只有原来的 1/scale²。
///   * 缩小时用**块平均**（box filter），小图的通道预览才不会出现噪点闪烁。
///   * R/G/B 显示的是**反预乘**后的值（straight alpha）—— user 看到的就是
///     "这一层的红色分量是多少"，编辑时写回去也是同一个值。
///   * 输出格式一律 32bppRgb（不是 Argb）：GDI+ 的 Argb 会在缩放时对
///     alpha=0 的像素做混合，而我们要把通道亮度原样画出来。
/// </summary>
internal static class ChannelSplit
{
    /// <summary>单张通道位图的像素上限，避免超大文档把 GDI+ 撑爆。</summary>
    private const long MaxPixels = 16_000_000;

    /// <summary>
    /// 把 surface 拆成 4 张灰阶位图，尺寸不超过 maxW×maxH（保持宽高比）。
    /// 失败返回 null（绝不抛）。
    /// </summary>
    public static unsafe ChannelSet? ExtractChannels(Surface? surface, int maxW, int maxH)
    {
        if (surface is null || !Safe.IsDrawableSize(maxW, maxH))
        {
            return null;
        }

        int width;
        int height;
        int stride;
        IntPtr scan0;
        try
        {
            width = surface.Width;
            height = surface.Height;
            stride = surface.Stride;
            scan0 = surface.Scan0.Pointer;
        }
        catch
        {
            return null;
        }

        if (!Safe.IsDrawableSize(width, height) || scan0 == IntPtr.Zero)
        {
            return null;
        }

        if ((long)width * height > MaxPixels)
        {
            Boot.LogDiag("channels-too-big",
                $"通道抽取跳过：{width}x{height} 超过 {MaxPixels} 像素上限");
            return null;
        }

        // 等比缩放到 maxW×maxH 以内（允许放大到 1:1 为止）
        double scale = Math.Min((double)maxW / width, (double)maxH / height);
        if (scale > 1.0)
        {
            scale = 1.0;
        }

        int outW = Math.Max(1, (int)Math.Round(width * scale));
        int outH = Math.Max(1, (int)Math.Round(height * scale));

        var set = new ChannelSet
        {
            Scale = scale,
            SourceWidth = width,
            SourceHeight = height,
        };

        try
        {
            for (int c = 0; c < 4; c++)
            {
                set.Channels[c] = new Bitmap(outW, outH, PixelFormat.Format32bppRgb);
            }

            // 源图 → 目标图的采样区间（块平均）
            double invScale = 1.0 / scale;

            for (int oy = 0; oy < outH; oy++)
            {
                int sy0 = (int)(oy * invScale);
                int sy1 = Math.Min(height, (int)((oy + 1) * invScale));
                if (sy1 <= sy0)
                {
                    sy1 = sy0 + 1;
                }

                // 四个通道各自的目标行首
                var dst = new byte*[4];
                var locked = new BitmapData[4];
                for (int c = 0; c < 4; c++)
                {
                    locked[c] = set.Channels[c]!.LockBits(
                        new Rectangle(0, oy, outW, 1),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppRgb);
                    dst[c] = (byte*)locked[c].Scan0;
                }

                for (int ox = 0; ox < outW; ox++)
                {
                    int sx0 = (int)(ox * invScale);
                    int sx1 = Math.Min(width, (int)((ox + 1) * invScale));
                    if (sx1 <= sx0)
                    {
                        sx1 = sx0 + 1;
                    }

                    long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                    int n = 0;

                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        byte* row = (byte*)scan0 + ((long)sy * stride);
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            byte* px = row + sx * 4;
                            byte b = px[0];
                            byte g = px[1];
                            byte r = px[2];
                            byte a = px[3];

                            sumB += b;
                            sumG += g;
                            sumR += r;
                            sumA += a;
                            n++;
                        }
                    }

                    if (n == 0)
                    {
                        n = 1;
                    }

                    WriteGray3(dst[0] + ox * 4, (byte)(sumR / n), (byte)(sumR / n), (byte)(sumR / n));
                    WriteGray3(dst[1] + ox * 4, (byte)(sumG / n), (byte)(sumG / n), (byte)(sumG / n));
                    WriteGray3(dst[2] + ox * 4, (byte)(sumB / n), (byte)(sumB / n), (byte)(sumB / n));
                    WriteGray3(dst[3] + ox * 4, (byte)(sumA / n), (byte)(sumA / n), (byte)(sumA / n));
                }

                for (int c = 0; c < 4; c++)
                {
                    set.Channels[c]!.UnlockBits(locked[c]);
                }
            }
        }
        catch
        {
            set.Dispose();
            return null;
        }

        return set;
    }

    /// <summary>
    /// 取图层内容的缩放预览（图层列表左侧的小缩略图）。
    /// PDN 的 Surface 是预乘 alpha，显示前先反预乘，否则半透明区域偏暗。
    /// </summary>
    public static unsafe Bitmap? CreateThumbnail(
        Surface? surface,
        int maxW,
        int maxH,
        IList<ChannelKind>? order = null,
        bool[]? visibility = null)
    {
        if (surface is null || !Safe.IsDrawableSize(maxW, maxH))
        {
            return null;
        }

        int width;
        int height;
        int stride;
        IntPtr scan0;
        try
        {
            width = surface.Width;
            height = surface.Height;
            stride = surface.Stride;
            scan0 = surface.Scan0.Pointer;
        }
        catch
        {
            return null;
        }

        if (!Safe.IsDrawableSize(width, height) || scan0 == IntPtr.Zero)
        {
            return null;
        }

        double scale = Math.Min((double)maxW / width, (double)maxH / height);
        if (scale > 1.0)
        {
            scale = 1.0;
        }

        int outW = Math.Max(1, (int)Math.Round(width * scale));
        int outH = Math.Max(1, (int)Math.Round(height * scale));

        ChannelKind k0 = order is not null && order.Count > 0 ? order[0] : ChannelKind.R;
        ChannelKind k1 = order is not null && order.Count > 1 ? order[1] : ChannelKind.G;
        ChannelKind k2 = order is not null && order.Count > 2 ? order[2] : ChannelKind.B;

        bool v0 = visibility is null || (visibility.Length > (int)k0 && visibility[(int)k0]);
        bool v1 = visibility is null || (visibility.Length > (int)k1 && visibility[(int)k1]);
        bool v2 = visibility is null || (visibility.Length > (int)k2 && visibility[(int)k2]);

        Bitmap? thumb = null;
        try
        {
            thumb = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            double invScale = 1.0 / scale;

            for (int oy = 0; oy < outH; oy++)
            {
                int sy0 = (int)(oy * invScale);
                int sy1 = Math.Min(height, (int)((oy + 1) * invScale));
                if (sy1 <= sy0)
                {
                    sy1 = sy0 + 1;
                }

                BitmapData locked = thumb.LockBits(
                    new Rectangle(0, oy, outW, 1),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                byte* dst = (byte*)locked.Scan0;

                for (int ox = 0; ox < outW; ox++)
                {
                    int sx0 = (int)(ox * invScale);
                    int sx1 = Math.Min(width, (int)((ox + 1) * invScale));
                    if (sx1 <= sx0)
                    {
                        sx1 = sx0 + 1;
                    }

                    long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                    int n = 0;

                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        byte* row = (byte*)scan0 + ((long)sy * stride);
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            byte* px = row + sx * 4;
                            sumB += px[0];
                            sumG += px[1];
                            sumR += px[2];
                            sumA += px[3];
                            n++;
                        }
                    }

                    if (n == 0)
                    {
                        n = 1;
                    }

                    byte bVal = (byte)(sumB / n);
                    byte gVal = (byte)(sumG / n);
                    byte rVal = (byte)(sumR / n);
                    byte aVal = (byte)(sumA / n);

                    byte outR = !v0 ? (byte)0 : (k0 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });
                    byte outG = !v1 ? (byte)0 : (k1 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });
                    byte outB = !v2 ? (byte)0 : (k2 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });

                    WriteRgb(dst + ox * 4, outR, outG, outB);
                    dst[ox * 4 + 3] = aVal;
                }

                thumb.UnlockBits(locked);
            }

            Bitmap result = thumb;
            thumb = null;
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            Safe.DisposeQuietly(thumb);
        }
    }

    /// <summary>
    /// 将 truthSource 中的像素按通道排序与可见性掩码合成到 targetSurface。
    /// </summary>
    public static unsafe bool ComposeChannels(
        Surface truthSource,
        Surface targetSurface,
        IList<ChannelKind>? order = null,
        bool[]? visibility = null)
    {
        if (truthSource is null || targetSurface is null)
        {
            return false;
        }

        int width = truthSource.Width;
        int height = truthSource.Height;
        if (width != targetSurface.Width || height != targetSurface.Height)
        {
            return false;
        }

        IntPtr srcScan0 = truthSource.Scan0.Pointer;
        int srcStride = truthSource.Stride;
        IntPtr dstScan0 = targetSurface.Scan0.Pointer;
        int dstStride = targetSurface.Stride;

        if (srcScan0 == IntPtr.Zero || dstScan0 == IntPtr.Zero)
        {
            return false;
        }

        order ??= new[] { ChannelKind.R, ChannelKind.G, ChannelKind.B, ChannelKind.A };
        visibility ??= new[] { true, true, true, true };

        ChannelKind k0 = order.Count > 0 ? order[0] : ChannelKind.R;
        ChannelKind k1 = order.Count > 1 ? order[1] : ChannelKind.G;
        ChannelKind k2 = order.Count > 2 ? order[2] : ChannelKind.B;
        ChannelKind k3 = order.Count > 3 ? order[3] : ChannelKind.A;

        bool v0 = visibility.Length > (int)k0 && visibility[(int)k0];
        bool v1 = visibility.Length > (int)k1 && visibility[(int)k1];
        bool v2 = visibility.Length > (int)k2 && visibility[(int)k2];
        bool v3 = visibility.Length > (int)k3 && visibility[(int)k3];

        for (int y = 0; y < height; y++)
        {
            byte* srcRow = (byte*)srcScan0 + ((long)y * srcStride);
            byte* dstRow = (byte*)dstScan0 + ((long)y * dstStride);

            for (int x = 0; x < width; x++)
            {
                int px = x * 4;
                byte bVal = srcRow[px + 0];
                byte gVal = srcRow[px + 1];
                byte rVal = srcRow[px + 2];
                byte aVal = srcRow[px + 3];

                // Slot 0 -> Red
                byte outR = !v0 ? (byte)0 : (k0 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });

                // Slot 1 -> Green
                byte outG = !v1 ? (byte)0 : (k1 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });

                // Slot 2 -> Blue
                byte outB = !v2 ? (byte)0 : (k2 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });

                // Slot 3 -> Alpha
                byte outA = !v3 ? (byte)255 : (k3 switch { ChannelKind.B => bVal, ChannelKind.G => gVal, ChannelKind.R => rVal, _ => aVal });

                dstRow[px + 0] = outB;
                dstRow[px + 1] = outG;
                dstRow[px + 2] = outR;
                dstRow[px + 3] = outA;
            }
        }

        return true;
    }

    private static unsafe void WriteGray3(byte* p, byte r, byte g, byte b)
    {
        p[0] = b;
        p[1] = g;
        p[2] = r;
        p[3] = 0;
    }

    private static unsafe void WriteRgb(byte* p, byte r, byte g, byte b)
    {
        p[0] = b;
        p[1] = g;
        p[2] = r;
        p[3] = 0;
    }

    private static byte ClampToByte(double v)
    {
        if (v <= 0)
        {
            return 0;
        }

        return v >= 255 ? (byte)255 : (byte)v;
    }
}

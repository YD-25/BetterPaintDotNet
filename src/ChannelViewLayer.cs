using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using PaintDotNet;

namespace LayerChannelLive;

internal static class ChannelViewLayer
{
    /// <summary>当前正在显示通道的图层（null 表示未进入通道视图）。</summary>
    private static Layer? viewLayer;

    /// <summary>进入通道视图前的原始像素备份，退出时用它还原。</summary>
    private static Surface? backupSurface;

    /// <summary>当前显示的通道。</summary>
    private static ChannelKind currentKind;

    /// <summary>当前文档引用（用于在退出或重置时还原图层状态）。</summary>
    private static Document? currentDoc;

    /// <summary>进入通道视图前保存的所有图层原始可见性。</summary>
    private static readonly Dictionary<Layer, bool> savedLayerVisibilities = new();

    /// <summary>进入通道视图前保存的所有图层原始不透明度。</summary>
    private static readonly Dictionary<Layer, byte> savedLayerOpacities = new();

    /// <summary>进入通道视图前保存的所有图层原始混合模式。</summary>
    private static readonly Dictionary<Layer, LayerBlendMode> savedLayerBlendModes = new();

    /// <summary>查询图层被通道视图临时隔离前的原始用户可见性（UI 显示用）。</summary>
    public static bool TryGetSavedLayerVisibility(Layer layer, out bool origVisible)
    {
        return savedLayerVisibilities.TryGetValue(layer, out origVisible);
    }

    /// <summary>查询图层被通道视图临时隔离前的原始不透明度（UI 显示用）。</summary>
    public static bool TryGetSavedLayerOpacity(Layer layer, out byte origOpacity)
    {
        return savedLayerOpacities.TryGetValue(layer, out origOpacity);
    }

    /// <summary>是否正在显示某个通道。</summary>
    public static bool IsShowing => viewLayer is not null && backupSurface is not null;

    /// <summary>当前显示的是哪个通道。</summary>
    public static ChannelKind? CurrentKind => IsShowing ? currentKind : null;

    /// <summary>当前显示通道的图层。</summary>
    public static Layer? CurrentLayer => viewLayer;

    /// <summary>
    /// 取指定图层的**完整通道数据**（若该图层正在通道视图中）。
    ///
    /// 通道视图期间图层屏幕上的像素是灰阶显示值，不能代表"所有通道的总和"。
    /// 总图层预览需要的是完整数据，而完整数据就一直存在 backupSurface 里。
    /// 对不在通道视图中的图层返回 null，调用方照常用它自己的 Surface。
    /// </summary>
    public static Surface? GetCompositedSurfaceFor(Layer layer)
    {
        if (viewLayer is null || backupSurface is null)
        {
            return null;
        }

        return ReferenceEquals(viewLayer, layer) ? backupSurface : null;
    }

    /// <summary>
    /// 让画布显示指定通道（已在显示时直接切过去）。
    /// 无视图层遮挡：自动屏蔽其他图层，仅将当前图层的指定通道独占显示在画布上。
    /// 返回 true 表示成功。
    /// </summary>
    public static bool Show(Document? document, Layer? sourceLayer, ChannelKind kind, Surface? truthSource = null)
    {
        if (document is null || sourceLayer is null)
        {
            return false;
        }

        Surface? surface = PdnModel.TryGetSurface(sourceLayer);
        if (surface is null)
        {
            return false;
        }

        // 换图层：先把上一个图层的像素还回去
        if (viewLayer is not null && !ReferenceEquals(viewLayer, sourceLayer))
        {
            RestoreBackup();
        }

        currentDoc = document;

        // 第一次进入通道视图：记录所有图层的原始可见性、不透明度和混合模式
        if (savedLayerVisibilities.Count == 0 && document.Layers.Count > 0)
        {
            foreach (Layer l in document.Layers)
            {
                savedLayerVisibilities[l] = l.Visible;
                savedLayerOpacities[l] = l.Opacity;
                savedLayerBlendModes[l] = l.BlendMode;
            }
        }

        // 无视图层：隔离其他图层，当前图层独占显示通道内容
        foreach (Layer l in document.Layers)
        {
            if (!ReferenceEquals(l, sourceLayer))
            {
                if (l.Visible)
                {
                    l.Visible = false;
                }
            }
            else
            {
                if (!l.Visible)
                {
                    l.Visible = true;
                }
                if (l.Opacity != 255)
                {
                    l.Opacity = 255;
                }
                if (l.BlendMode != LayerBlendMode.Normal)
                {
                    l.BlendMode = LayerBlendMode.Normal;
                }
            }
        }

        // 第一次进入（或换了图层）时备份原始像素
        if (!ReferenceEquals(viewLayer, sourceLayer) || backupSurface is null)
        {
            backupSurface?.Dispose();
            backupSurface = TryClone(truthSource ?? surface);
            if (backupSurface is null)
            {
                Boot.LogDiag("viewlayer-backup", "备份原始像素失败，取消通道视图");
                RestoreLayersState(document);
                return false;
            }

            viewLayer = sourceLayer;
        }

        if (!WriteChannel(surface, backupSurface, kind))
        {
            RestoreLayersState(document);
            return false;
        }

        currentKind = kind;
        try { document.Invalidate(); } catch { }
        Invalidate(sourceLayer);
        Boot.Log($"[通道层] Show 成功 kind={kind} viewLayer='{viewLayer?.Name}'"
                 + $" backup={(backupSurface is null ? "null" : backupSurface.Width + "x" + backupSurface.Height)}"
                 + $"（已隔离其它 {document.Layers.Count - 1} 个图层）");
        return true;
    }

    /// <summary>退出通道视图：还原原始像素并恢复所有图层状态（支持应用通道顺序与可见性掩码）。</summary>
    public static bool Hide(Document? document, IList<ChannelKind>? order = null, bool[]? visibility = null)
    {
        bool restored = RestoreBackup(order, visibility);

        RestoreLayersState(document ?? currentDoc);

        viewLayer = null;
        backupSurface?.Dispose();
        backupSurface = null;
        currentDoc = null;
        try { document?.Invalidate(); } catch { }
        return restored;
    }

    /// <summary>还原所有被临时隔离图层的可见性、不透明度和混合模式。</summary>
    public static void RestoreLayersState(Document? document)
    {
        if (savedLayerVisibilities.Count == 0) return;

        try
        {
            Document? doc = document ?? currentDoc;
            if (doc != null && doc.Layers.Count > 0)
            {
                foreach (Layer l in doc.Layers)
                {
                    if (savedLayerVisibilities.TryGetValue(l, out bool v))
                    {
                        l.Visible = v;
                    }
                    if (savedLayerOpacities.TryGetValue(l, out byte op))
                    {
                        l.Opacity = op;
                    }
                    if (savedLayerBlendModes.TryGetValue(l, out LayerBlendMode bm))
                    {
                        l.BlendMode = bm;
                    }
                }
            }
        }
        catch { }
        finally
        {
            savedLayerVisibilities.Clear();
            savedLayerOpacities.Clear();
            savedLayerBlendModes.Clear();
            try { document?.Invalidate(); } catch { }
        }
    }

    /// <summary>文档切换时清理（旧文档的引用不再有效）。</summary>
    public static void Forget()
    {
        Boot.Log("[通道层] Forget 被调用（丢弃引用）");
        RestoreLayersState(currentDoc);
        viewLayer = null;
        backupSurface?.Dispose();
        backupSurface = null;
        currentDoc = null;
    }

    /// <summary>把备份的原始像素写回图层。</summary>
    public static bool RestoreBackup(IList<ChannelKind>? order = null, bool[]? visibility = null)
    {
        Layer? layer = viewLayer;
        Surface? backup = backupSurface;
        if (layer is null || backup is null)
        {
            return false;
        }

        Surface? surface = PdnModel.TryGetSurface(layer);
        if (surface is null)
        {
            return false;
        }

        bool ok;
        if (order is not null || visibility is not null)
        {
            ok = ChannelSplit.ComposeChannels(backup, surface, order, visibility);
        }
        else
        {
            ok = CopySurface(backup, surface);
        }

        if (ok)
        {
            Invalidate(layer);
        }

        return ok;
    }

    public static unsafe bool DumpRawChannels(string tag)
    {
        Surface? backup = backupSurface;
        if (backup is null)
        {
            return false;
        }

        int width, height, stride;
        IntPtr scan0;
        try
        {
            width = backup.Width;
            height = backup.Height;
            stride = backup.Stride;
            scan0 = backup.Scan0.Pointer;
        }
        catch
        {
            return false;
        }

        if (width <= 0 || height <= 0 || scan0 == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
            System.IO.Directory.CreateDirectory(dir);

            foreach (ChannelKind k in new[] { ChannelKind.R, ChannelKind.G, ChannelKind.B, ChannelKind.A })
            {
                int offset = k switch
                {
                    ChannelKind.R => 2,
                    ChannelKind.G => 1,
                    ChannelKind.B => 0,
                    _ => 3,
                };

                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                BitmapData locked = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* src = (byte*)scan0 + ((long)y * stride);
                        byte* dst = (byte*)locked.Scan0 + ((long)y * locked.Stride);

                        for (int x = 0; x < width; x++)
                        {
                            byte value = src[(x * 4) + offset];
                            byte* dp = dst + (x * 4);
                            dp[0] = value;
                            dp[1] = value;
                            dp[2] = value;
                            dp[3] = 255;
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(locked);
                }

                bmp.Save(
                    System.IO.Path.Combine(dir, $"raw_{k}_{tag}.png"),
                    System.Drawing.Imaging.ImageFormat.Png);
            }

            Boot.Log($"[验证] 四通道原始数据已导出 raw_*_{tag}.png（{width}x{height}）");
            return true;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("dump-raw", "导出原始通道失败: " + ex.GetType().Name);
            return false;
        }
    }

    public static unsafe bool DumpChannelSnapshot(string tag)
    {
        Surface? backup = backupSurface;
        if (backup is null)
        {
            return false;
        }

        int width;
        int height;
        int stride;
        IntPtr scan0;
        try
        {
            width = backup.Width;
            height = backup.Height;
            stride = backup.Stride;
            scan0 = backup.Scan0.Pointer;
        }
        catch
        {
            return false;
        }

        if (width <= 0 || height <= 0 || scan0 == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"channel_{currentKind}_{tag}.png");

            using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            BitmapData locked = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                int channelOffset = currentKind switch
                {
                    ChannelKind.R => 2,
                    ChannelKind.G => 1,
                    ChannelKind.B => 0,
                    _ => 3,
                };

                for (int y = 0; y < height; y++)
                {
                    byte* src = (byte*)scan0 + ((long)y * stride);
                    byte* dst = (byte*)locked.Scan0 + ((long)y * locked.Stride);

                    for (int x = 0; x < width; x++)
                    {
                        byte value = src[x * 4 + channelOffset];
                        byte* dp = dst + x * 4;
                        dp[0] = value;
                        dp[1] = value;
                        dp[2] = value;
                        dp[3] = 255;
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(locked);
            }

            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Boot.Log($"[验证] 通道快照已导出: channel_{currentKind}_{tag}.png");
            return true;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("dump-channel", "导出通道快照失败: " + ex.GetType().Name);
            return false;
        }
    }


    public static unsafe bool DumpAllChannels(string tag)
    {
        Surface? backup = backupSurface;
        if (backup is null)
        {
            Boot.LogDiag("dump-all", "没有备份，无法导出");
            return false;
        }

        int width, height, stride;
        IntPtr scan0;
        try
        {
            width = backup.Width;
            height = backup.Height;
            stride = backup.Stride;
            scan0 = backup.Scan0.Pointer;
        }
        catch
        {
            return false;
        }

        if (width <= 0 || height <= 0 || scan0 == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
            System.IO.Directory.CreateDirectory(dir);
            int thumb = Math.Min(width, 256);

            foreach (ChannelKind k in new[] { ChannelKind.R, ChannelKind.G, ChannelKind.B, ChannelKind.A })
            {
                int offset = k switch
                {
                    ChannelKind.R => 2,
                    ChannelKind.G => 1,
                    ChannelKind.B => 0,
                    _ => 3,
                };

                using var bmp = new Bitmap(thumb, thumb, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                BitmapData locked = bmp.LockBits(new Rectangle(0, 0, thumb, thumb),
                    ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                long sum = 0;
                try
                {
                    for (int y = 0; y < thumb; y++)
                    {
                        int sy = y * height / thumb;
                        byte* src = (byte*)scan0 + ((long)sy * stride);
                        byte* dst = (byte*)locked.Scan0 + ((long)y * locked.Stride);

                        for (int x = 0; x < thumb; x++)
                        {
                            int sx = x * width / thumb;
                            byte v = src[sx * 4 + offset];
                            sum += v;
                            byte* dp = dst + x * 4;
                            dp[0] = v; dp[1] = v; dp[2] = v; dp[3] = 255;
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(locked);
                }

                string path = System.IO.Path.Combine(dir, $"all_{tag}_{k}.png");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                double avg = sum / (double)(thumb * thumb);
                Boot.Log($"[验证] 通道 {k} 平均亮度={avg:F1}（{tag}）");
            }

            return true;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("dump-all", "导出失败: " + ex.GetType().Name);
            return false;
        }
    }


    /// <summary>
    /// 把指定图层上"当前通道"的显示值按给定的通道数据重写一遍。
    ///
    /// 供面板在直接改动 channelViewBackup 后调用 —— 面板自己写数据，
    /// 再让显示跟上，这样就不依赖本类的静态状态（程序集加载两次时它不可靠）。
    /// </summary>
    public static bool RefreshDisplay(Layer layer, Surface backup, ChannelKind kind)
    {
        Surface? display = PdnModel.TryGetSurface(layer);
        if (display is null)
        {
            return false;
        }

        return WriteChannel(display, backup, kind);
    }

    /// <summary>
    /// 从源像素的 BGRA 四分量提取目标通道需要写入的值。
    ///
    /// 核心规则：
    /// 1. 若源像素是灰阶（r == g && g == b），说明来源是复制出来的单通道或灰度遮罩，
    ///    此时不论写入哪个通道（R、G、B，包括 A 通道），都直接取该灰阶强度 r，
    ///    而绝不取容器 alpha（容器 alpha 通常为 255）；
    /// 2. 若源像素是彩色：
    ///    - 目标为 R/G/B 时取对应分量；
    ///    - 目标为 A 时：若有透明度 (a < 255) 则取 alpha，否则取标准亮度。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ExtractChannelByte(byte b, byte g, byte r, byte a, ChannelKind targetKind)
    {
        if (r == g && g == b)
        {
            return r;
        }

        return targetKind switch
        {
            ChannelKind.R => r,
            ChannelKind.G => g,
            ChannelKind.B => b,
            ChannelKind.A => a < 255 ? a : (byte)((299 * r + 587 * g + 114 * b + 500) / 1000),
            _ => r,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetCanvasChannelValue(byte b, byte g, byte r, byte a, ChannelKind targetKind)
    {
        byte val;
        if (r == g && g == b)
        {
            val = r;
        }
        else
        {
            val = targetKind switch
            {
                ChannelKind.R => r,
                ChannelKind.G => g,
                ChannelKind.B => b,
                _ => (byte)((299 * r + 587 * g + 114 * b + 500) / 1000),
            };
        }

        if (a < 255)
        {
            val = (byte)((val * a) / 255);
        }

        return val;
    }

    public static unsafe bool HasChannelDifferences(Surface canvasSurface, Surface truthSource, ChannelKind kind)
    {
        if (canvasSurface is null || truthSource is null)
        {
            return false;
        }

        int width = canvasSurface.Width;
        int height = canvasSurface.Height;
        if (width <= 0 || height <= 0 || width != truthSource.Width || height != truthSource.Height)
        {
            return false;
        }

        int cStride = canvasSurface.Stride;
        IntPtr cScan0 = canvasSurface.Scan0.Pointer;
        int tStride = truthSource.Stride;
        IntPtr tScan0 = truthSource.Scan0.Pointer;

        if (cScan0 == IntPtr.Zero || tScan0 == IntPtr.Zero)
        {
            return false;
        }

        int targetOffset = kind switch
        {
            ChannelKind.R => 2,
            ChannelKind.G => 1,
            ChannelKind.B => 0,
            _ => 3,
        };

        for (int y = 0; y < height; y++)
        {
            byte* cRow = (byte*)cScan0 + ((long)y * cStride);
            byte* tRow = (byte*)tScan0 + ((long)y * tStride);

            for (int x = 0; x < width; x++)
            {
                int px = x * 4;
                byte val = GetCanvasChannelValue(cRow[px + 0], cRow[px + 1], cRow[px + 2], cRow[px + 3], kind);
                if (tRow[px + targetOffset] != val)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static unsafe bool CommitSurfaceToChannel(Surface canvasSurface, Surface targetSurface, ChannelKind kind)
    {
        if (canvasSurface is null || targetSurface is null)
        {
            return false;
        }

        int width = canvasSurface.Width;
        int height = canvasSurface.Height;
        if (width <= 0 || height <= 0 || width != targetSurface.Width || height != targetSurface.Height)
        {
            return false;
        }

        int cStride = canvasSurface.Stride;
        IntPtr cScan0 = canvasSurface.Scan0.Pointer;
        int tStride = targetSurface.Stride;
        IntPtr tScan0 = targetSurface.Scan0.Pointer;

        if (cScan0 == IntPtr.Zero || tScan0 == IntPtr.Zero)
        {
            return false;
        }

        int targetOffset = kind switch
        {
            ChannelKind.R => 2,
            ChannelKind.G => 1,
            ChannelKind.B => 0,
            _ => 3,
        };

        bool anyChanged = false;

        for (int y = 0; y < height; y++)
        {
            byte* cRow = (byte*)cScan0 + ((long)y * cStride);
            byte* tRow = (byte*)tScan0 + ((long)y * tStride);

            for (int x = 0; x < width; x++)
            {
                int px = x * 4;
                byte val = GetCanvasChannelValue(cRow[px + 0], cRow[px + 1], cRow[px + 2], cRow[px + 3], kind);
                if (tRow[px + targetOffset] != val)
                {
                    tRow[px + targetOffset] = val;
                    anyChanged = true;
                }
            }
        }

        return anyChanged;
    }

    /// <summary>
    /// 把指定 Surface 的某个通道作为 32bpp 灰度图复制到系统剪贴板（同时支持 Bitmap 与 PNG 流）。
    /// </summary>
    public static unsafe bool CopyChannelToClipboard(Surface surface, ChannelKind kind)
    {
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
            return false;
        }

        if (width <= 0 || height <= 0 || scan0 == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int offset = kind switch
            {
                ChannelKind.R => 2,
                ChannelKind.G => 1,
                ChannelKind.B => 0,
                _ => 3,
            };

            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData locked = bmp.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);

            try
            {
                for (int y = 0; y < height; y++)
                {
                    byte* src = (byte*)scan0 + ((long)y * stride);
                    byte* dst = (byte*)locked.Scan0 + ((long)y * locked.Stride);

                    for (int x = 0; x < width; x++)
                    {
                        byte val = src[(x * 4) + offset];
                        byte* dp = dst + (x * 4);
                        dp[0] = val; // B
                        dp[1] = val; // G
                        dp[2] = val; // R
                        dp[3] = 255; // A (纯不透明灰度)
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(locked);
            }

            var dataObj = new DataObject();
            dataObj.SetData(DataFormats.Bitmap, true, bmp);
            var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            dataObj.SetData("PNG", false, ms);
            Clipboard.SetDataObject(dataObj, true);

            Boot.Log($"[复制] 已将 {kind} 通道 ({width}x{height}) 复制到剪贴板");
            return true;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("copy-clipboard", "复制通道失败: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }


    /// <summary>
    /// 把通道内容写成灰阶写进图层 Surface（以备份为数据源，保证可反复切换）。
    /// R/G/B/A 直接取通道原始分量直通显示。Alpha 通道整体保持不透明，
    /// 这样画布上看到的就是标准的通道灰阶图。
    /// </summary>
    private static unsafe bool WriteChannel(Surface surface, Surface backup, ChannelKind kind)
    {
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
            return false;
        }

        int bStride;
        IntPtr bScan0;
        try
        {
            bStride = backup.Stride;
            bScan0 = backup.Scan0.Pointer;
        }
        catch
        {
            return false;
        }

        if (width <= 0 || height <= 0 || scan0 == IntPtr.Zero || bScan0 == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            for (int y = 0; y < height; y++)
            {
                byte* dst = (byte*)scan0 + ((long)y * stride);
                byte* src = (byte*)bScan0 + ((long)y * bStride);

                for (int x = 0; x < width; x++)
                {
                    byte b = src[x * 4 + 0];
                    byte g = src[x * 4 + 1];
                    byte r = src[x * 4 + 2];
                    byte a = src[x * 4 + 3];

                    byte gray = kind switch
                    {
                        ChannelKind.R => r,
                        ChannelKind.G => g,
                        ChannelKind.B => b,
                        _ => a,
                    };

                    byte* px = dst + x * 4;

                    // Alpha 一律写成 255（不透明）。
                    //
                    // 这是"单独显示灰度通道"的关键：如果保留原 alpha，半透明区域会
                    // 与画布背景合成，灰度被压暗 —— 用户看到的就是"alpha 还在生效"。
                    // 通道视图要看的是**这条通道本身的数值**，不应再叠一层透明度。
                    //
                    // 注意：这不影响 A 通道的数值显示 —— A 通道的 gray 本身就是 alpha 值，
                    // 只是把"用于合成的不透明度"设为 1。真正存放通道数据的 backupSurface
                    // 完全没被改动，退出时按 backup 还原。
                    px[3] = 255;
                    px[0] = gray;
                    px[1] = gray;
                    px[2] = gray;
                }
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("viewlayer-write", "写通道灰阶失败: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }

        Invalidate(viewLayer);
        return true;
    }

    public static unsafe bool CopySurface(Surface from, Surface to)
    {
        try
        {
            if (from.Width != to.Width || from.Height != to.Height)
            {
                return false;
            }

            int width = from.Width;
            int height = from.Height;
            int fromStride = from.Stride;
            int toStride = to.Stride;
            IntPtr fromScan0 = from.Scan0.Pointer;
            IntPtr toScan0 = to.Scan0.Pointer;

            if (fromScan0 == IntPtr.Zero || toScan0 == IntPtr.Zero)
            {
                return false;
            }

            long rowBytes = (long)width * 4;
            for (int y = 0; y < height; y++)
            {
                byte* s = (byte*)fromScan0 + ((long)y * fromStride);
                byte* d = (byte*)toScan0 + ((long)y * toStride);
                Buffer.MemoryCopy(s, d, toStride, rowBytes);
            }

            return true;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("viewlayer-copy", "复制像素失败: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    public static Surface? TryClone(Surface surface)
    {
        try
        {
            var clone = new Surface(surface.Width, surface.Height);
            return CopySurface(surface, clone) ? clone : null;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("viewlayer-clone", "备份失败: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private static void Invalidate(Layer? layer)
    {
        try
        {
            layer?.Invalidate();
        }
        catch
        {
        }
    }
}

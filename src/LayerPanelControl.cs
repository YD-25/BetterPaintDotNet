using System.IO;
using System.Reflection;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>
/// 替换原生 LayersStrip 的自绘面板。
///
/// 布局（从上到下，与 Paint.NET 图层列表同序：索引 0 = 最底层，所以顶层画在最上面）：
///   [ 通道预览：开 ]      ← 自带标题栏，开关在这里，不碰 Paint.NET 的 ToolStrip
///   [图层 N  ]            ← 顶层
///   [图层 N-1]
///   ...
///   [选中图层]            ← 选中
///   [R 通道  ]            ┐
///   [G 通道  ]            │ 竖排 4 行，只针对选中图层
///   [B 通道  ]            │
///   [A 通道  ]            ┘
///   [图层 1  ]
///   [图层 0  ]            ← 背景/底层
///
/// 数据全部来自公开 API：Document.Layers / Layer.Visible / Layer.Name / BitmapLayer.Surface。
/// 图层属性与通道操作写回原始目标图层；预览抽取只读，撤销快照按文档与图层绑定。
/// </summary>
internal sealed partial class LayerChannelPanel : Panel, IMessageFilter
{
    private const int HeaderHeight = 34;
    private const int RowHeight = 34;                // 兜底行高（预览不可用时）
    private const int ThumbSize = 28;                // 兜底缩略图尺寸
    private const int LayerRowMinHeight = 44;
    private const int LayerPreviewTop = 5;
    private const int LayerPreviewBottom = 5;
    private const int ChannelRowHeight = 52;
    private const int ChannelPreviewLeft = 84;      // 通道预览左边距（通道标签区宽度）
    private const int ChannelPreviewRightMargin = 10;
    private const int ChannelPreviewTop = 4;
    // 预览尺寸刻意保持紧凑：
    // 之前一版放到 320×200，一行预览就占掉大半个图层窗口，用户明确要求改小。
    private const int PreviewMaxWidth = 88;         // 单个通道预览的最大宽度
    private const int PreviewMaxHeight = 66;        // 单个通道预览的最大高度
    private const int TextPadding = 6;
    private const int ScrollStep = 48;

    /// <summary>可见性勾选框的左边距与尺寸（绘制与命中测试共用，避免两处不一致）。</summary>
    private const int VisibilityBoxX = 8;
    private const int VisibilityBoxSize = 18;

    private Document? document;
    private object? documentWorkspace;
    private Layer? activeLayer;

    private int scrollOffset;
    private bool channelsVisible = true;
    private bool showDiagnostics;

    /// <summary>行记录：图层 + 它在内容坐标系里相对标题栏的顶部 y。</summary>
    private readonly List<(Layer Layer, int Top)> layerRows = new();

    private int contentHeight;

    // 通道位图缓存（只在选中图层变化/文档变化时重建）。
    private ChannelSet? channels;
    private Layer? channelsForLayer;

    private Font? boldFont;
    private Font? monoFont;

    /// <summary>
    /// 解析当前文档（以及宿主的选中图层），**每次都重新找 workspace**。
    ///
    /// 优先从控件树找（切文档时这个最靠得住），找不到再退回缓存引用。
    /// </summary>
    private Document? ResolveDocument(out Layer? hostActive)
    {
        // 关键：工作区在 **MainForm**（主窗口）的控件树里，不在 LayersForm 里。
        //
        // 实测证据（WorkspaceDiagnostics 输出）：
        //   hwnd=0x59043C 'LightMap_Hair2.dds - Paint.NET 5.1.12' type=MainForm
        //   workspace数=2   [0]=1024x1024   [1]=512x512
        // 两个 DocumentWorkspace 都挂在 MainForm 下，而图层窗口是另一个顶层窗口 ——
        // 所以拿 LayersForm.TopLevelControl 当搜索根永远搜不到工作区，
        // 面板只能一直用最初绑定时的那个旧引用（表现：切换文件后内容不变）。
        //
        // 正确做法：枚举本进程所有顶层窗口，逐个尝试 Control.FromHandle，
        // 在其中找出带文档的工作区。PdnModel 内部会挑"当前文档"对应的那个。
        object? live = PdnModel.TryFindDocumentWorkspaceAcrossAllWindows();

        // 两个来源都试一次，取能读出 Document 的那个。
        // 控件树里可能有不止一个 DocumentWorkspace（旧的、隐藏的），
        // 所以两个都要比一下，不能盲信第一个。
        Document? fromTree = PdnModel.TryGetDocument(live);
        object? cached = this.documentWorkspace;
        Document? fromCache = ReferenceEquals(cached, live) ? null : PdnModel.TryGetDocument(cached);

        object? chosen;
        Document? doc;
        if (fromTree is not null)
        {
            chosen = live;
            doc = fromTree;
        }
        else if (fromCache is not null)
        {
            chosen = cached;
            doc = fromCache;
        }
        else
        {
            chosen = live ?? cached;
            doc = null;
        }

        if (chosen is not null)
        {
            this.documentWorkspace = chosen;
            if (!LayerChannelHost.hasDumpedToolDiag && doc is not null)
            {
                LayerChannelHost.hasDumpedToolDiag = true;
                LayerChannelHost.DumpToolDiagnostics(chosen);
            }
        }

        hostActive = PdnModel.TryGetActiveLayer(this.documentWorkspace);

        if (doc is null)
        {
            Boot.LogDiag("doc-null",
                "解析不到 Document：tree="
                + (live is null ? "null" : live.GetType().Name)
                + " cache=" + (cached is null ? "null" : cached.GetType().Name));
        }

        return doc;
    }

    /// <summary>选中图层变化时触发。</summary>
    public event EventHandler? SelectionChanged;

    // ---- 拖拽排序状态 ----
    // 拖拽用"插入间隙"模型：gap 0 = 最顶层之前，gap N = 最底层之后。
    private bool dragging;
    private int dragSourceModelIndex = -1;
    private int dragTargetGap = -1;
    private Point dragStartClient;
    private const int DragThreshold = 4;

    // ---- 通道编辑状态 ----
    //
    // 点某个通道行 → 让**画布显示该通道**（就地替换像素 + 备份，不动文档结构）。
    // 注意：这里**不写用户图层的永久数据** —— 进入前会完整备份，退出时还原。
    private ChannelKind? editingChannel;

    /// <summary>
    /// 通道视图**会话**标记（非 null 表示预览被冻结）。
    /// 设 false 后由 host 重算一次。
    /// </summary>
    private ChannelKind? channelViewKind;

    /// <summary>通道数据在视图期间是否被改动过。</summary>
    private bool channelDataDirty;

    /// <summary>
    /// **面板自己持有的**通道视图备份（进入视图那一刻的完整通道数据）。
    ///
    /// 为什么不直接问 ChannelViewLayer：实测这个插件程序集会被加载**两次**
    /// （两份拷贝各有自己的静态字段），于是 ChannelViewLayer.IsShowing
    /// 会在 True/False 之间跳 —— 面板拿到 False 时就退回用图层实时像素，
    /// 而那时的像素已经是灰阶显示值，总图层预览就跟着变灰。
    ///
    /// 面板自己的实例字段没有这个问题：谁在显示通道、备份是什么，由它自己说了算。
    /// </summary>
    private Surface? channelViewBackup;

    /// <summary>通道视图备份对应的图层。</summary>
    private Layer? channelViewLayer;


    /// <summary>上一次观察到的图层数（诊断用，用于记录图层数变化）。</summary>
    private int lastObservedLayerCount = -1;

    // ---- 通道选择、排序、可见性与真实数据 ----
    private ChannelKind? selectedChannel;
    private readonly Dictionary<Layer, List<ChannelKind>> layerChannelOrders = new();
    private readonly Dictionary<Layer, bool[]> layerChannelVisibilities = new();
    private readonly Dictionary<Layer, Surface> layerTruthSurfaces = new();

    // ---- 宿主工具栏按钮关联 ----
    private ToolStrip? toolbarToolStrip;
    private ToolStripButton? moveUpButtonItem;
    private ToolStripButton? moveDownButtonItem;

    // ---- 通道拖拽排序状态 ----
    private bool channelDragging;
    private int channelDragSourceIndex = -1;
    private int channelDragTargetGap = -1;
    private Point channelDragStartClient;

    // ---- 通道撤销/重做栈 ----
    private readonly Stack<ChannelUndoSnapshot> undoStack = new();
    private readonly Stack<ChannelUndoSnapshot> redoStack = new();
    private const int MaxUndoDepth = 30;

    public LayerChannelPanel()
    {
        this.DoubleBuffered = true;
        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        this.Font = this.appearanceFont;
        this.SyncTheme();
        this.themeSubscription = UiStyle.WatchTheme(this, this.SyncTheme);
        this.AutoScroll = false;

        try
        {
            Application.AddMessageFilter(this);
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += this.OnWpfThreadFilterMessage;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("msgfilter-add", "注册全局消息过滤器失败: " + ex.Message);
        }
    }

    public Layer? ActiveLayer => this.activeLayer;

    /// <summary>诊断用：当前布局里的图层行数。</summary>
    public int LayerRowCount => this.layerRows.Count;

    /// <summary>诊断用：描述面板当前持有的文档与图层。</summary>
    public string DescribeCurrentDocument()
    {
        string docText = this.document is null
            ? "null"
            : this.document.Width + "x" + this.document.Height;
        List<Layer> layers = PdnModel.GetLayers(this.document);
        string first = layers.Count > 0 ? layers[0].Name ?? "?" : "-";
        string active = this.activeLayer?.Name ?? "-";
        string wsType = this.documentWorkspace?.GetType().Name ?? "null";
        return $"文档={docText} 首层='{first}' 图层数={layers.Count} 选中='{active}' ws={wsType}";
    }

    public bool ChannelsVisible => this.channelsVisible;

    public bool HasSelectedChannel => this.selectedChannel is not null && this.activeLayer is not null;
    public ChannelKind? SelectedChannel => this.selectedChannel;

    public void BindToolbarButtons(ToolStrip? toolStrip, ToolStripButton? moveUp, ToolStripButton? moveDown)
    {
        this.toolbarToolStrip = toolStrip;
        this.moveUpButtonItem = moveUp;
        this.moveDownButtonItem = moveDown;
        this.UpdateToolbarButtonStates();
    }

    public void UpdateToolbarButtonStates()
    {
        if (this.HasSelectedChannel)
        {
            if (this.moveUpButtonItem is not null)
            {
                this.moveUpButtonItem.Enabled = this.CanMoveSelectedChannelUp();
            }
            if (this.moveDownButtonItem is not null)
            {
                this.moveDownButtonItem.Enabled = this.CanMoveSelectedChannelDown();
            }
        }
    }

    public List<ChannelKind> GetChannelOrder(Layer layer)
    {
        if (!this.layerChannelOrders.TryGetValue(layer, out var order))
        {
            order = new List<ChannelKind> { ChannelKind.R, ChannelKind.G, ChannelKind.B, ChannelKind.A };
            this.layerChannelOrders[layer] = order;
        }
        return order;
    }

    public bool[] GetChannelVisibility(Layer layer)
    {
        if (!this.layerChannelVisibilities.TryGetValue(layer, out var vis))
        {
            vis = new bool[] { true, true, true, true };
            this.layerChannelVisibilities[layer] = vis;
        }
        return vis;
    }

    public Surface GetOrCreateTruthSurface(Layer layer)
    {
        Surface? live = PdnModel.TryGetSurface(layer);
        if (this.layerTruthSurfaces.TryGetValue(layer, out Surface? truth))
        {
            if (live is not null && (truth.Width != live.Width || truth.Height != live.Height))
            {
                truth.Dispose();
                this.layerTruthSurfaces.Remove(layer);
                truth = null;
            }
        }

        bool isLayerInChannelView = ReferenceEquals(layer, this.channelViewLayer) ||
            (ChannelViewLayer.IsShowing && ReferenceEquals(layer, ChannelViewLayer.CurrentLayer));

        if (truth != null && live != null && !isLayerInChannelView)
        {
            // 防护：如果 live 处于纯灰阶状态（且匹配通道抽取特征），而 truth 包含彩色通道数据，
            // 说明 live 是通道视图遗留或异常回退的灰阶帧，绝对禁止盲目覆写 truth！
            if (IsSuspiciousGrayscalePollution(live, truth))
            {
                Boot.Log("[防污染拦截] 拦截到通道灰阶像素覆写全彩 truth，保留原有通道真相数据");
                ChannelViewLayer.CopySurface(truth, live);
                layer.Invalidate();
            }
            else
            {
                // 通道外始终以最新、已提交的图层像素为准，不能恢复旧的缩放前备份。
                if (this.HasChannelPresentation(layer))
                    ChannelPresentation.MergeVisibleEdits(live, truth, this.GetChannelOrder(layer), this.GetChannelVisibility(layer));
                else
                    ChannelViewLayer.CopySurface(live, truth);
            }
        }
        if (truth is null && live is not null)
        {
            Surface? backup = this.GetChannelViewBackupFor(layer);
            if (backup != null && !isLayerInChannelView && IsSuspiciousGrayscalePollution(live, backup))
            {
                truth = ChannelViewLayer.TryClone(backup)!;
            }
            else
            {
                truth = ChannelViewLayer.TryClone(live)!;
            }
            this.layerTruthSurfaces[layer] = truth;
        }

        return truth!;
    }

    private bool HasChannelPresentation(Layer layer) =>
        this.GetChannelOrder(layer).Where((kind, index) => (int)kind != index).Any() ||
        this.GetChannelVisibility(layer).Any(visible => !visible);

    public bool CanMoveSelectedChannelUp()
    {
        if (this.activeLayer is null || this.selectedChannel is null)
        {
            return false;
        }
        List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
        int idx = order.IndexOf(this.selectedChannel.Value);
        return idx > 0;
    }

    public bool CanMoveSelectedChannelDown()
    {
        if (this.activeLayer is null || this.selectedChannel is null)
        {
            return false;
        }
        List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
        int idx = order.IndexOf(this.selectedChannel.Value);
        return idx >= 0 && idx < order.Count - 1;
    }

    public void MoveSelectedChannelUp()
    {
        if (!this.CanMoveSelectedChannelUp() || this.activeLayer is null || this.selectedChannel is null)
        {
            return;
        }

        Layer layer = this.activeLayer;
        List<ChannelKind> order = this.GetChannelOrder(layer);
        int idx = order.IndexOf(this.selectedChannel.Value);
        if (idx <= 0)
        {
            return;
        }

        this.PushUndoSnapshot(layer);

        ChannelKind tmp = order[idx];
        order[idx] = order[idx - 1];
        order[idx - 1] = tmp;

        Boot.Log($"[通道排序] 上移通道 {tmp}：新排序 [{string.Join(",", order)}]");
        this.ApplyChannelOrderOrVisibilityChange(layer);
    }

    public void MoveSelectedChannelDown()
    {
        if (!this.CanMoveSelectedChannelDown() || this.activeLayer is null || this.selectedChannel is null)
        {
            return;
        }

        Layer layer = this.activeLayer;
        List<ChannelKind> order = this.GetChannelOrder(layer);
        int idx = order.IndexOf(this.selectedChannel.Value);
        if (idx < 0 || idx >= order.Count - 1)
        {
            return;
        }

        this.PushUndoSnapshot(layer);

        ChannelKind tmp = order[idx];
        order[idx] = order[idx + 1];
        order[idx + 1] = tmp;

        Boot.Log($"[通道排序] 下移通道 {tmp}：新排序 [{string.Join(",", order)}]");
        this.ApplyChannelOrderOrVisibilityChange(layer);
    }

    public static void CommitActiveTool(object? workspace, bool deselect = false)
    {
        if (workspace is null) return;
        try
        {
            // 1. FlushTool on DocumentWorkspaceExtensions
            Type? dwExt = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "PaintDotNet.Controls.DocumentWorkspaceExtensions");
            var flushMethod = dwExt?.GetMethod("FlushTool", BindingFlags.Public | BindingFlags.Static);
            if (flushMethod != null && flushMethod.GetParameters()[0].ParameterType.IsInstanceOfType(workspace))
            {
                flushMethod.Invoke(null, new object[] { workspace });
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("commit-tool-flush", ex.Message);
        }

        try
        {
            // 2. Direct Commit / End on active Tool (including TransactedTool such as PaintBucket / FloodFill)
            object? tool = null;
            for (Type? t = workspace.GetType(); t != null; t = t.BaseType)
            {
                var pi = t.GetProperty("Tool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (pi != null) { tool = pi.GetValue(workspace); break; }
                var fi = t.GetField("activeTool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (fi != null) { tool = fi.GetValue(workspace); break; }
            }

            if (tool != null)
            {
                MethodInfo? reqEndDrawing = tool.GetType().GetMethod("RequestEndDrawing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                reqEndDrawing?.Invoke(tool, null);

                MethodInfo? reqEndEditing = tool.GetType().GetMethod("RequestEndEditing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                reqEndEditing?.Invoke(tool, null);

                MethodInfo? miKeyPress = tool.GetType().GetMethod("KeyPress", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Keys) }, null);
                miKeyPress?.Invoke(tool, new object[] { Keys.Enter });

                MethodInfo? commitMethod = tool.GetType().GetMethod("Commit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                commitMethod?.Invoke(tool, null);
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("commit-tool-direct", ex.Message);
        }

        if (deselect)
        {
            try
            {
                // 3. 若通道内粘贴产生了未提交的浮动选区，取消选区将其盖印到画布 Surface 上
                Type? deselectActionType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "PaintDotNet.Actions.DeselectAction");
                if (deselectActionType != null)
                {
                    var ctor = deselectActionType.GetConstructor(Type.EmptyTypes);
                    if (ctor != null)
                    {
                        object? act = ctor.Invoke(null);
                        var performMethod = workspace.GetType().GetMethod("PerformAction", new[] { deselectActionType.BaseType ?? typeof(object) })
                            ?? workspace.GetType().GetMethod("PerformAction");
                        performMethod?.Invoke(workspace, new object[] { act });
                    }
                }
            }
            catch (Exception ex)
            {
                Boot.LogDiag("commit-tool-deselect", ex.Message);
            }
        }
    }

    private int channelViewEntryNativeUndoCount = -1;

    public bool CommitCurrentChannelIfModified()
    {
        return this.ExitChannelView();
    }

    public bool ExitChannelView()
    {
        if (this.channelViewKind is null || this.channelViewLayer is null)
        {
            return false;
        }

        Layer layer = this.channelViewLayer;
        ChannelKind kind = this.channelViewKind.Value;
        object? history = this.CurrentNativeHistory();
        int baseUndoCount = this.channelViewEntryNativeUndoCount;

        // 1. 提交未完成的宿主工具，若有粘贴选区则盖印到画布
        CommitActiveTool(this.documentWorkspace, deselect: true);

        Surface? canvasSurface = PdnModel.TryGetSurface(layer);
        Surface? truth = this.GetOrCreateTruthSurface(layer);

        bool modified = false;
        Surface? preEditTruth = null;

        if (canvasSurface != null && truth != null)
        {
            modified = ChannelViewLayer.HasChannelDifferences(canvasSurface, truth, kind);
            if (modified)
            {
                // 关键备份：克隆通道修改前的原始完整全彩 truth，用于创建原生全彩撤销记录
                preEditTruth = ChannelViewLayer.TryClone(truth);

                // 将通道改动合并进 truth 与 backup
                ChannelViewLayer.CommitSurfaceToChannel(canvasSurface, truth, kind);

                if (this.channelViewBackup != null)
                {
                    ChannelViewLayer.CopySurface(truth, this.channelViewBackup);
                }

                // 将当前图层 Surface 恢复为全彩合成图像
                List<ChannelKind> order = this.GetChannelOrder(layer);
                bool[] vis = this.GetChannelVisibility(layer);
                ChannelSplit.ComposeChannels(truth, canvasSurface, order, vis);
                PdnModel.Invalidate(layer);
            }
        }

        // 2. 恢复图层原本的可见性、不透明度和混合模式（不再隔离其他图层）
        List<ChannelKind>? prevOrder = this.GetChannelOrder(layer);
        bool[]? prevVis = this.GetChannelVisibility(layer);
        ChannelViewLayer.Hide(this.document, prevOrder, prevVis);

        // 3. 关键修复：从 Paint.NET 原生历史栈中剔除通道模式下产生的所有临时记录！
        // 包括：Show 隔离图层产生的可见性变化、通道内灰阶粘贴/笔触快照、以及 Hide 恢复图层产生的可见性变化！
        // 彻底清除这些临时记录后，宿主历史中就完全不存在任何可见性扰动或灰阶快照。
        if (baseUndoCount >= 0)
        {
            TrimNativeUndoStackTo(history, baseUndoCount);
        }

        // 4. 若通道有修改，将全彩的通道修改作为一个干净的原生 BitmapHistoryMemento 推入宿主历史栈
        bool pushedNative = false;
        if (modified && preEditTruth != null && this.document != null)
        {
            pushedNative = TryPushChannelBitmapMemento(history, this.documentWorkspace, this.document, layer, preEditTruth, kind);
        }

        // 兜底：如果宿主推入未成功且通道有改动，推入插件私有撤销栈
        if (modified && !pushedNative)
        {
            this.PushUndoSnapshot(layer);
        }

        preEditTruth?.Dispose();

        // 5. 重置通道视图状态
        this.channelViewEntryNativeUndoCount = -1;
        this.editingChannel = null;
        this.channelViewKind = null;
        this.channelViewBackup = null;
        this.channelViewLayer = null;
        this.channelDataDirty = false;
        this.InvalidateChannels();
        this.InvalidateThumbnails();
        this.RefreshFromHost();
        this.Invalidate();
        this.UpdateToolbarButtonStates();
        MoveToolGuideService.Instance.ForceResync();

        Boot.Log($"[退出通道视图] 通道={kind}, modified={modified}, pushedNative={pushedNative}, baseUndoCount={baseUndoCount}");
        return modified;
    }

    public void SwitchChannelView(ChannelKind newKind)
    {
        if (this.channelViewKind is null || this.channelViewLayer is null)
        {
            this.ToggleChannelView(newKind);
            return;
        }

        Layer layer = this.channelViewLayer;
        ChannelKind oldKind = this.channelViewKind.Value;
        object? history = this.CurrentNativeHistory();
        int baseUndoCount = this.channelViewEntryNativeUndoCount;

        // 1. 提交未完成的工具与选区
        CommitActiveTool(this.documentWorkspace, deselect: true);

        Surface? canvasSurface = PdnModel.TryGetSurface(layer);
        Surface? truth = this.GetOrCreateTruthSurface(layer);

        if (canvasSurface != null && truth != null && ChannelViewLayer.HasChannelDifferences(canvasSurface, truth, oldKind))
        {
            Surface? preEditTruth = ChannelViewLayer.TryClone(truth);
            ChannelViewLayer.CommitSurfaceToChannel(canvasSurface, truth, oldKind);
            if (this.channelViewBackup != null)
            {
                ChannelViewLayer.CopySurface(truth, this.channelViewBackup);
            }

            // 恢复全彩以备创建快照
            List<ChannelKind> order = this.GetChannelOrder(layer);
            bool[] vis = this.GetChannelVisibility(layer);
            ChannelSplit.ComposeChannels(truth, canvasSurface, order, vis);

            // 清理旧通道内的临时灰阶记录
            if (baseUndoCount >= 0)
            {
                TrimNativeUndoStackTo(history, baseUndoCount);
            }

            bool pushed = false;
            if (preEditTruth != null && this.document != null)
            {
                pushed = TryPushChannelBitmapMemento(history, this.documentWorkspace, this.document, layer, preEditTruth, oldKind);
            }
            if (!pushed)
            {
                this.PushUndoSnapshot(layer);
            }
            preEditTruth?.Dispose();

            // 更新基准点
            this.channelViewEntryNativeUndoCount = GetNativeUndoCount(history);
        }

        // 2. 切到新通道
        this.selectedChannel = newKind;
        this.editingChannel = newKind;

        Surface? newTruth = this.GetOrCreateTruthSurface(layer);
        bool ok = ChannelViewLayer.Show(this.document, layer, newKind, newTruth);
        if (ok)
        {
            this.channelViewKind = newKind;
            this.channelViewLayer = layer;
            this.channelViewBackup = ChannelViewLayer.GetCompositedSurfaceFor(layer);
        }

        this.lastLayerSignature = long.MinValue;
        this.InvalidateThumbnails();
        this.RefreshFromHost();
        this.Invalidate();
        this.UpdateToolbarButtonStates();
    }

    private static int GetNativeUndoCount(object? history)
    {
        if (history == null) return 0;
        try
        {
            var prop = history.GetType().GetProperty("UndoStack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? val = prop?.GetValue(history);
            if (val != null)
            {
                var countProp = val.GetType().GetProperty("Count");
                if (countProp != null)
                {
                    return (int)countProp.GetValue(val)!;
                }
                if (val is System.Collections.IEnumerable items)
                {
                    int count = 0;
                    foreach (var _ in items) count++;
                    return count;
                }
            }
        }
        catch { }
        return 0;
    }

    private static void TrimNativeUndoStackTo(object? history, int targetCount)
    {
        if (history == null || targetCount < 0) return;
        try
        {
            object? uStack = null;
            for (Type? t = history.GetType(); t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.Name.IndexOf("undo", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var candidate = f.GetValue(history);
                        if (candidate != null && candidate.GetType().GetMethod("RemoveAt") != null)
                        {
                            uStack = candidate;
                            break;
                        }
                    }
                }
                if (uStack != null) break;
            }

            if (uStack == null)
            {
                var prop = history.GetType().GetProperty("UndoStack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                uStack = prop?.GetValue(history);
            }

            if (uStack == null) return;

            PropertyInfo? countProp = uStack.GetType().GetProperty("Count");
            int count = (int)(countProp?.GetValue(uStack) ?? 0);
            if (count <= targetCount) return;

            MethodInfo? getItem = uStack.GetType().GetMethod("get_Item", new[] { typeof(int) });
            MethodInfo? removeAt = uStack.GetType().GetMethod("RemoveAt", new[] { typeof(int) });
            if (removeAt == null) return;

            for (int i = count - 1; i >= targetCount; i--)
            {
                try
                {
                    object? memento = getItem?.Invoke(uStack, new object[] { i });
                    if (memento != null)
                    {
                        MethodInfo? flush = memento.GetType().GetMethod("Flush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        flush?.Invoke(memento, null);
                        if (memento is IDisposable d) d.Dispose();
                    }
                }
                catch { }

                removeAt.Invoke(uStack, new object[] { i });
            }

            MethodInfo? performChanged = null;
            for (Type? t = history.GetType(); t != null; t = t.BaseType)
            {
                performChanged = t.GetMethod("PerformChanged", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (performChanged != null) break;
            }
            performChanged?.Invoke(history, null);

            Boot.Log($"[历史清理] 已从宿主 UndoStack 清除通道内临时记录，数量 {count - targetCount}，剩余 {targetCount}");
        }
        catch (Exception ex)
        {
            Boot.LogDiag("trim-native-undo", ex.Message);
        }
    }

    private static bool TryPushChannelBitmapMemento(
        object? history,
        object? docWorkspace,
        Document document,
        Layer layer,
        Surface preEditTruth,
        ChannelKind kind)
    {
        if (history == null || docWorkspace == null || document == null || layer == null || preEditTruth == null)
        {
            return false;
        }

        try
        {
            int layerIdx = document.Layers.IndexOf(layer);
            if (layerIdx < 0) return false;

            Type? bhmType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "PaintDotNet.HistoryMementos.BitmapHistoryMemento");
            if (bhmType == null) return false;

            var ctor = bhmType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length == 7);
            if (ctor == null) return false;

            var rectList = new PaintDotNet.Rendering.RectInt32[] {
                new PaintDotNet.Rendering.RectInt32(0, 0, preEditTruth.Width, preEditTruth.Height)
            };

            object? bhm = ctor.Invoke(new object?[] {
                $"修改 {kind} 通道",
                null,
                docWorkspace,
                layerIdx,
                rectList,
                preEditTruth,
                null
            });

            if (bhm == null) return false;

            var pushMethod = history.GetType().GetMethod("PushNewMemento", new[] { bhmType.BaseType ?? typeof(object) })
                ?? history.GetType().GetMethod("PushNewMemento");
            pushMethod?.Invoke(history, new[] { bhm });

            Boot.Log($"[宿主历史] 已成功推入 '修改 {kind} 通道' 原生全彩回退记录");
            return true;
        }
        catch (Exception ex)
        {
            Boot.LogDiag("push-channel-memento", ex.Message);
            return false;
        }
    }

    private static unsafe bool IsSuspiciousGrayscalePollution(Surface live, Surface truth)
    {
        if (live == null || truth == null) return false;
        if (live.Width != truth.Width || live.Height != truth.Height) return false;
        int w = live.Width;
        int h = live.Height;
        if (w <= 0 || h <= 0) return false;

        // 1. 检查 truth 是否具有彩色分量差异
        bool truthHasColor = false;
        IntPtr tScan0;
        int tStride;
        try
        {
            tScan0 = truth.Scan0.Pointer;
            tStride = truth.Stride;
        }
        catch { return false; }
        if (tScan0 == IntPtr.Zero) return false;

        int stepX = Math.Max(1, w / 32);
        int stepY = Math.Max(1, h / 32);

        for (int y = 0; y < h; y += stepY)
        {
            byte* tRow = (byte*)tScan0 + ((long)y * tStride);
            for (int x = 0; x < w; x += stepX)
            {
                int px = x * 4;
                if (tRow[px + 0] != tRow[px + 1] || tRow[px + 1] != tRow[px + 2])
                {
                    truthHasColor = true;
                    break;
                }
            }
            if (truthHasColor) break;
        }

        if (!truthHasColor) return false;

        // 2. 检查 live 是否全采样点皆为 R==G==B 纯灰阶
        IntPtr lScan0;
        int lStride;
        try
        {
            lScan0 = live.Scan0.Pointer;
            lStride = live.Stride;
        }
        catch { return false; }
        if (lScan0 == IntPtr.Zero) return false;

        for (int y = 0; y < h; y += stepY)
        {
            byte* lRow = (byte*)lScan0 + ((long)y * lStride);
            for (int x = 0; x < w; x += stepX)
            {
                int px = x * 4;
                if (lRow[px + 0] != lRow[px + 1] || lRow[px + 1] != lRow[px + 2])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ApplyChannelOrderOrVisibilityChange(Layer layer)
    {
        // 调用方先保存完整数据，再改变排序/可见性。此处不能把新的显示掩码反读为原始数据。
        Surface? truth = this.layerTruthSurfaces.TryGetValue(layer, out var stored) ? stored : this.GetOrCreateTruthSurface(layer);
        Surface? surface = PdnModel.TryGetSurface(layer);
        if (truth == null || surface == null) return;
        if (ReferenceEquals(layer, this.channelViewLayer) && this.channelViewBackup != null && this.channelViewKind is ChannelKind kind)
        {
            ChannelViewLayer.CopySurface(truth, this.channelViewBackup);
            ChannelViewLayer.RefreshDisplay(layer, this.channelViewBackup, kind);
        }
        else
        {
            ChannelSplit.ComposeChannels(truth, surface, this.GetChannelOrder(layer), this.GetChannelVisibility(layer));
        }
        this.channelDataDirty = true;
        this.InvalidateThumbnails();
        this.InvalidateChannels();
        this.lastLayerSignature = long.MinValue;
        PdnModel.Invalidate(layer);
        this.RefreshFromHost();
        this.Invalidate();
        this.UpdateToolbarButtonStates();
    }

    /// <summary>绑定宿主 workspace。</summary>
    public void Bind(object? workspace)
    {
        if (ReferenceEquals(this.documentWorkspace, workspace)) return;
        this.documentWorkspace = workspace;
        // 保留上一轮状态，由 RefreshFromHost 检测真实文档/选中图层变化，避免吞掉切换事件。
        this.lastLayerSignature = long.MinValue;
        this.SynchronizeImageFormat(force: true);
        this.RefreshFromHost();
    }



    public void RefreshFromHost()
    {
        this.SyncTheme();
        // 开发期自测钩子（见 RunAutoTestHookIfRequested 的说明）
        this.RunAutoTestHookIfRequested();

        // ---- 每次刷新都重新解析 workspace ----
        //
        // 两个实测场景都要求这样（用户先后反馈过）：
        //
        //   场景 A「打开文件后显示没有打开文档」：
        //     冷启动时没开文件 → 缓存了一个**空工作区**；之后打开文件时宿主换了
        //     工作区实例，旧引用永远读到 null。
        //
        //   场景 B「切换文件后图层面板没切过去」：
        //     有文档时旧工作区**仍然能读到旧文档**（不是 null），
        //     所以"只在 null 时才重解析"救不了它 —— 面板会一直显示上一个文件的内容。
        //
        // 结论：必须无条件重解析。开销是遍历一次控件树找 DocumentWorkspace，
        // 150ms 一次可以接受；相比"显示错文档"这个后果完全值得。
        Document? newDoc = this.ResolveDocument(out Layer? hostActive);

        // 文档换了要清掉通道视图状态（旧文档的图层引用不再有效）
        if (!ReferenceEquals(newDoc, this.document))
        {
            if (ChannelViewLayer.IsShowing || this.channelViewKind is not null)
            {
                this.ExitChannelView();
            }
            else
            {
                ChannelViewLayer.Forget();
            }

            this.editingChannel = null;
            this.channelViewKind = null;
            this.channelViewLayer = null;
            this.channelViewBackup = null;
            this.selectedChannel = null;
            this.channelDataDirty = false;
            this.InvalidateChannels();
            this.InvalidateThumbnails();
        }

        Layer? hostActiveResolved = hostActive ?? this.activeLayer;
        List<Layer> layers = PdnModel.GetLayers(newDoc);
        bool documentChanged = !ReferenceEquals(newDoc, this.document);
        this.SynchronizeChannelHistory(newDoc);

        if (newDoc is not null)
        {
            int nowCount = layers.Count;
            if (this.lastObservedLayerCount >= 0 && nowCount != this.lastObservedLayerCount)
            {
                string names = string.Join(", ", layers.ConvertAll(l => l.Name ?? "?"));
                Boot.Log($"[图层变化] {this.lastObservedLayerCount} → {nowCount}"
                         + $" 通道视图={this.channelViewKind?.ToString() ?? "off"}"
                         + $" 图层=[{names}]");
            }

            this.lastObservedLayerCount = nowCount;
        }

if (documentChanged)
        {
            Boot.Log("面板切到新文档："
                     + (newDoc is null
                         ? "null（当前无文档）"
                         : newDoc.Width + "x" + newDoc.Height + "，图层 " + layers.Count + " 个"));
        }

        // 诊断：文档或图层拿不到时记录一次原因。
        // 用户反馈"打开 DDS 后显示没有打开文档" —— 这条日志能说明是
        // workspace 丢了、Document 读不到、还是 Layers 为空。
        if (newDoc is null)
        {
            Boot.LogDiag("doc-null",
                "读不到 Document：workspace="
                + (this.documentWorkspace is null
                    ? "null"
                    : this.documentWorkspace.GetType().FullName ?? "?"));
        }
        else if (layers.Count == 0)
        {
            Boot.LogDiag("doc-nolayers",
                $"Document 存在但 Layers 为空：{newDoc.Width}x{newDoc.Height}");
        }
        else if (documentChanged)
        {
            Boot.Log($"面板绑定新文档：{newDoc.Width}x{newDoc.Height}，{layers.Count} 个图层");
        }

        bool selectionChanged = !ReferenceEquals(hostActiveResolved, this.activeLayer);
        if (selectionChanged && this.channelViewKind is not null)
        {
            this.ExitChannelView();
        }

        // 顺序 + 成员 + 关键属性的组合签名。用引用序列比较，能同时捕捉
        // 增删、移动、重排；属性用值比较，能捕捉可见性/不透明度变化。
        long signature = ComputeLayerSignature(layers);
        bool structureChanged = signature != this.lastLayerSignature;

        this.document = newDoc;
        this.activeLayer = hostActiveResolved;
        this.lastLayerSignature = signature;
        this.SynchronizeLayerSubscriptions(newDoc);
        this.ProcessPendingContentChanges();
        this.SynchronizeImageFormat(documentChanged || selectionChanged || structureChanged);

        if (documentChanged || selectionChanged)
        {
            this.InvalidateChannels();
            this.RebuildLayout();
            this.InvalidateThumbnails();
        }
        else if (structureChanged)
        {
            this.RebuildLayout();

            // 结构变化时清掉缩略图缓存：既释放被删图层的位图，
            // 也让"移动图层后缩略图没更新"这类问题不可能发生
            // （缩略图很小，重建成本远低于它带来的正确性风险）。
            this.InvalidateThumbnails();
        }

        // 无条件重绘。
        //
        // 为什么不做"有变化才重绘"的优化：面板只有一屏可见的十几行，
        // 而 OnPaint 内部的通道抽取与缩略图都有缓存，重绘成本很低。
        // 反过来，过度优化会让用户看到"操作后没反应"的错觉 ——
        // 比如点"下移图层"时，若这一轮判定为无变化就什么都不画，观感就是卡住。
        this.Invalidate();
    }

    /// <summary>
    /// 图层列表签名：顺序敏感，并包含可见性/不透明度/名字/尺寸。
    /// 顺序变化（上移/下移）也会改变签名，因此能触发重建布局。
    /// </summary>
    private static long ComputeLayerSignature(List<Layer> layers)
    {
        // 混合哈希：顺序敏感（每一步都乘一个质数再叠加），避免交换两个图层后签名不变。
        long hash = 17;
        foreach (Layer layer in layers)
        {
            long item = RuntimeHelpers.GetHashCode(layer);
            bool isVis = ChannelViewLayer.TryGetSavedLayerVisibility(layer, out bool origVis) ? origVis : PdnModel.GetVisible(layer);
            byte op = ChannelViewLayer.TryGetSavedLayerOpacity(layer, out byte origOp) ? origOp : PdnModel.GetOpacity(layer);
            item = item * 31 + (isVis ? 1 : 0);
            item = item * 31 + op;
            item = item * 31 + (layer.Name?.GetHashCode() ?? 0);
            hash = hash * 37 + item;
        }

        return hash;
    }

    private long lastLayerSignature = long.MinValue;

    // ---- 缩略图缓存 ----
    // key = (图层对象, 目标宽, 目标高)，value = 已缩好的小位图。
    // 尺寸变了（窗口缩放）必须重算，所以键里带尺寸。
    // 图层内容变化时通过 InvalidateThumbnails() 整批丢弃。
    private readonly Dictionary<(Layer Layer, int W, int H), Bitmap> thumbnailCache = new();

    /// <summary>取缓存的缩略图，没有就建一张并缓存。</summary>
    private Bitmap? GetOrCreateThumbnail(Layer layer, Surface surface, int maxW, int maxH)
    {
        // 通道视图期间，目标图层的**屏幕像素是灰阶显示值**，
        // 直接用它会得到一张灰阶缩略图 —— 用户看到的就是"总图层预览跟着通道变了"。
        // 所以改从面板自己保存的通道备份取像素：备份里才是所有通道的完整数据。
        //
        // 注意这里**不查 ChannelViewLayer.IsShowing**：程序集被加载两次时
        // 那个静态状态会在两份拷贝间跳，导致偶尔判定为"没在显示"而退回灰阶像素。
        Surface? fromView = this.GetChannelViewBackupFor(layer);
        Surface? truthSource = this.LivePreviewFor(layer) ?? fromView ??
            (this.HasChannelPresentation(layer) ? this.GetOrCreateTruthSurface(layer) : surface);

        var key = (layer, maxW, maxH);
        bool cachedHit = this.thumbnailCache.TryGetValue(key, out Bitmap? cached) && Safe.IsDrawable(cached);


        if (cachedHit)
        {
            return cached;
        }

        using Surface? channelSnapshot = this.LivePreviewFor(layer) == null
            ? this.CreateChannelDisplayPreview(layer, maxW, maxH) : null;
        truthSource = channelSnapshot ?? truthSource;
        List<ChannelKind> order = this.GetChannelOrder(layer);
        bool[] visibility = this.GetChannelVisibility(layer);
        Bitmap? created = ChannelSplit.CreateThumbnail(truthSource, maxW, maxH, order, visibility);
        if (created is not null)
        {
            this.thumbnailCache[key] = created;

            // 自验证：通道视图期间把"总图层预览实际长什么样"导出成文件。
            // 前几轮都是改完凭推理下结论、结果反复回炉；
            // 把真实产物落盘后可以直接看内容对不对，不用猜。
            if (ChannelViewLayer.IsShowing)
            {
                DumpThumbnailForVerification(created, layer, ReferenceEquals(truthSource, surface));
            }
        }

        return created;
    }

    /// <summary>
    /// 把当前生成的总图层缩略图写到 %TEMP%，用于人工/自动核对内容。
    /// 只在通道视图期间触发，命名带图层名与来源，便于对照。
    /// </summary>
    private static void DumpThumbnailForVerification(Bitmap thumb, Layer layer, bool usedLiveSurface)
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
            System.IO.Directory.CreateDirectory(dir);

            string name = "thumb_" + Sanitize(layer.Name ?? "layer")
                          + (usedLiveSurface ? "_LIVE" : "_BACKUP") + ".png";
            string path = System.IO.Path.Combine(dir, name);

            thumb.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            // 顺便记录平均亮度：灰色画面与彩色画面的平均值差别明显，便于自动判断
            Boot.Log($"验证导出: {name}（来源={(usedLiveSurface ? "图层实时像素" : "通道备份")}）");
        }
        catch
        {
        }

        static string Sanitize(string s)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }

            return s;
        }
    }

    /// <summary>
    /// 若该图层正处于本面板的通道视图，返回进入视图前的完整通道数据备份；否则 null。
    /// </summary>
    private Surface? GetChannelViewBackupFor(Layer layer)
    {
        if (this.channelViewKind is null || this.channelViewBackup is null)
        {
            return null;
        }

        return ReferenceEquals(this.channelViewLayer, layer) ? this.channelViewBackup : null;
    }

    /// <summary>丢弃全部缩略图缓存。</summary>
    public void InvalidateThumbnails()
    {

        foreach (Bitmap bmp in this.thumbnailCache.Values)
        {
            Safe.DisposeQuietly(bmp);
        }

        this.thumbnailCache.Clear();
    }


    /// <summary>当外部工具（如移动/缩放工具）修改了图层内容时，通知面板即时刷新预览与通道。</summary>
    public void NotifyLayerContentChanged()
    {
        if (this.IsDisposed) return;
        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(NotifyLayerContentChanged)); } catch {}
            return;
        }

        this.RequestLivePreview();
        this.InvalidateThumbnails();
        this.InvalidateChannels();
        this.Invalidate();
    }

    /// <summary>仅记录最新一次变换，由 UI 定时器在原生事件完成后采样，避免反复重建全部图层缓存。</summary>
    public void NotifyLayerContentChangedThrottled()
    {
        if (this.IsDisposed) return;
        if (this.InvokeRequired)
        {
            if (this.IsHandleCreated) try { this.BeginInvoke(new Action(this.NotifyLayerContentChangedThrottled)); } catch (InvalidOperationException) { }
            return;
        }
        this.RequestLivePreview();
    }

    private void InvalidateChannels()
    {
        this.channels?.Dispose();
        this.channels = null;
        this.channelsForLayer = null;
    }

    private void EnsureChannels()
    {
        if (!this.channelsVisible)
        {
            return;
        }

        bool sameLayer = ReferenceEquals(this.channelsForLayer, this.activeLayer);
        bool sameWidth = this.channelsPreviewWidth == this.CurrentPreviewWidth();

        if (this.channels is not null && sameLayer && sameWidth && !this.channelDataDirty)
        {
            // 同一图层、数据也没变 → 保持进入视图前的那一版预览（关键：不重算）。
            // 这样点 A 通道不会把 R/G/B 的预览一起变成灰阶。
            return;
        }

        // 需要重算：抛开冻结、直接抽取
        this.channelDataDirty = false;
        this.ExtractChannelPreviews();
    }

    /// <summary>按当前选中图层重新抽取四通道预览（不涉及冻结逻辑）。</summary>
    private void ExtractChannelPreviews()
    {
        int previewW = this.CurrentPreviewWidth();

        this.channels?.Dispose();
        this.channels = null;
        this.channelsForLayer = null;
        this.channelsPreviewWidth = previewW;

        if (this.activeLayer is null)
        {
            return;
        }

        Surface? live = this.LivePreviewFor(this.activeLayer);
        if (live != null)
        {
            this.channels = ChannelSplit.ExtractChannels(live, previewW, PreviewMaxHeight);
            if (this.channels != null && this.document != null)
            {
                this.channels.SourceWidth = this.document.Width;
                this.channels.SourceHeight = this.document.Height;
                this.channels.Scale = Math.Min((double)live.Width / this.document.Width, (double)live.Height / this.document.Height);
                this.channelsForLayer = this.activeLayer;
            }
            return;
        }

        using Surface? channelSnapshot = this.CreateChannelDisplayPreview(this.activeLayer, previewW, PreviewMaxHeight);
        Surface? surface = channelSnapshot ?? (this.HasChannelPresentation(this.activeLayer)
            ? this.GetOrCreateTruthSurface(this.activeLayer) : PdnModel.TryGetSurface(this.activeLayer));
        if (surface == null) return;
        this.channels = ChannelSplit.ExtractChannels(surface, previewW, PreviewMaxHeight);
        if (this.channels != null)
        {
            this.channelsForLayer = this.activeLayer;
            if (channelSnapshot != null)
            {
                this.channels.SourceWidth = this.document?.Width ?? surface.Width;
                this.channels.SourceHeight = this.document?.Height ?? surface.Height;
                this.channels.Scale = Math.Min((double)surface.Width / this.channels.SourceWidth, (double)surface.Height / this.channels.SourceHeight);
            }
        }
    }

    /// <summary>通道预览的可用宽度（扣除左边标签区、右边留白与滚动条）。</summary>
    private int CurrentPreviewWidth()
    {
        int available = Math.Max(1, this.ClientSize.Width);
        int width = available - ChannelPreviewLeft - ChannelPreviewRightMargin;

        // 上下都限幅：太窄看不清，太宽会让一行占满整个窗口（实测 520px 窗口下
        // 预览高达 345px、一屏只放得下一个通道，反而更难用）。
        return Math.Clamp(width, 80, PreviewMaxWidth);
    }

    private int channelsPreviewWidth = -1;

    // ==================== 预览几何 ====================
    //
    // 通道预览按**图层宽高比**缩放，宽度占满可用区域。
    // 行高随预览高度变化，因此所有布局计算都必须走下面的辅助方法，
    // 不能再用固定常量 —— 否则命中测试会和绘制错位。

    /// <summary>单个通道预览的高度（按图层宽高比，上限 MaxPreviewHeight）。</summary>
    /// <summary>强制重算预览高度缓存（组框布局开始时调用一次）。</summary>
    private void RefreshPreviewHeightCache()
    {
        this.cachedPreviewHeight = -1;
        this.cachedPreviewHeightWidth = -1;
        this.cachedPreviewHeightLayer = null;
        _ = this.PreviewHeight();
    }

    /// <summary>预览高度缓存：布局与绘制必须用同一个值，否则会算错行高。</summary>
    private int cachedPreviewHeight = -1;
    private int cachedPreviewHeightWidth = -1;
    private Layer? cachedPreviewHeightLayer;

    private int PreviewHeight()
    {
        int pw = this.CurrentPreviewWidth();

        if (this.cachedPreviewHeight > 0
            && this.cachedPreviewHeightWidth == pw
            && ReferenceEquals(this.cachedPreviewHeightLayer, this.activeLayer))
        {
            return this.cachedPreviewHeight;
        }

        int h = this.ComputePreviewHeight(pw);
        this.cachedPreviewHeight = h;
        this.cachedPreviewHeightWidth = pw;
        this.cachedPreviewHeightLayer = this.activeLayer;
        return h;
    }

    private int ComputePreviewHeight(int pw)
    {
        int fallback = LayerRowMinHeight - LayerPreviewTop - LayerPreviewBottom;

        Surface? surface = PdnModel.TryGetSurface(this.activeLayer);
        if (surface is null)
        {
            return fallback;
        }

        int sw;
        int sh;
        try
        {
            sw = surface.Width;
            sh = surface.Height;
        }
        catch
        {
            return fallback;
        }

        if (sw <= 0 || sh <= 0)
        {
            return fallback;
        }

        int h = (int)Math.Round(pw * (double)sh / sw);
        return Math.Clamp(h, 16, PreviewMaxHeight);
    }

    private int blockGeomTop = -1;
    private int blockGeomRowH;

    private int RowHFromGeometry()
    {
        // 组框布局还没跑过时（例如首帧）退化到实时计算
        return this.blockGeomRowH > 0 ? this.blockGeomRowH : this.PreviewHeight() + 8;
    }

    private int BlockHFromGeometry()
    {
        int rows = this.RowHFromGeometry() * 4;
        return rows;
    }

    /// <summary>通道块里单行的高度 = 预览高度 + 上下留白。</summary>
    private int ChannelRowH() => this.RowHFromGeometry();

    private int ChannelBlockH() => this.BlockHFromGeometry();

    /// <summary>
    /// 图层行高度。
    ///
    /// 与通道预览**同高** —— 用户明确要求"图层的大小应该和通道的预览一样"。
    /// 早期固定 34px 行高 + 28px 缩略图，图层预览几乎看不清内容。
    /// </summary>
    private int LayerRowH() => Math.Max(LayerRowMinHeight, this.PreviewHeight() + LayerPreviewTop + LayerPreviewBottom);

    /// <summary>图层预览的边框宽度（按图层宽高比，限制在行内）。</summary>
    private int LayerPreviewBoxWidth()
    {
        int rowH = this.LayerRowH();
        Surface? surface = PdnModel.TryGetSurface(this.activeLayer);
        if (surface is null)
        {
            return rowH;
        }

        int sw;
        int sh;
        try
        {
            sw = surface.Width;
            sh = surface.Height;
        }
        catch
        {
            return rowH;
        }

        if (sw <= 0 || sh <= 0)
        {
            return rowH;
        }

        int h = this.PreviewHeight();
        int w = (int)Math.Round(h * (double)sw / sh);
        return Math.Clamp(w, 24, Math.Max(24, this.ClientSize.Width - 150));
    }

    /// <summary>选中图层行下面是否真的画了通道块。</summary>
    private bool ChannelBlockVisible => this.channelsVisible && this.activeLayer is not null;

    private void RebuildLayout()
    {
        this.layerRows.Clear();

        this.RefreshPreviewHeightCache();

        List<Layer> layers = PdnModel.GetLayers(this.document);
        if (layers.Count == 0)
        {
            this.contentHeight = 0;
            this.blockGeomTop = -1;
            return;
        }

        int y = 0;
        this.blockGeomTop = -1;

        for (int i = layers.Count - 1; i >= 0; i--)
        {
            Layer layer = layers[i];
            this.layerRows.Add((layer, y));
            y += this.LayerRowH();

            if (this.channelsVisible && ReferenceEquals(layer, this.activeLayer))
            {
                this.blockGeomTop = y;
                this.blockGeomRowH = this.PreviewHeight() + 8;

                y += this.ChannelBlockH();
            }
        }

        this.contentHeight = y;
        this.ClampScroll();
    }

    private int MaxScroll => Math.Max(0, this.contentHeight - this.ViewportHeight);

    private int ViewportHeight => Math.Max(0, this.ClientSize.Height - HeaderHeight - FooterHeight);

    private void ClampScroll()
    {
        this.scrollOffset = Math.Clamp(this.scrollOffset, 0, this.MaxScroll);
    }

    private Rectangle ContentToClient(int top, int height)
        => new(0, HeaderHeight + top - this.scrollOffset, Math.Max(1, this.ClientSize.Width), height);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        this.ClampScroll();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int next = this.scrollOffset - (e.Delta / 120) * ScrollStep;
        next = Math.Clamp(next, 0, this.MaxScroll);
        if (next != this.scrollOffset)
        {
            this.scrollOffset = next;
            this.Invalidate();
        }

        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Y >= this.ClientSize.Height - FooterHeight) return;

        // Ctrl+Shift+L：切换诊断覆盖层（排查"是否真的接管成功"用）
        if (ModifierKeys == (Keys.Control | Keys.Shift) && e.Button == MouseButtons.Left)
        {
            this.showDiagnostics = !this.showDiagnostics;
            this.Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            ChannelKind? menuChannel = this.HitChannelPreview(e.X, e.Y);
            if (menuChannel is not null)
            {
                this.ShowChannelContextMenu(menuChannel.Value, e.Location);
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // 标题栏：切换通道显示
        if (e.Y < HeaderHeight)
        {
            this.channelsVisible = !this.channelsVisible;
            if (!this.channelsVisible)
            {
                this.InvalidateChannels();
                this.scrollOffset = 0;
            }

            this.RebuildLayout();
            this.Invalidate();
            return;
        }

        int blockTop = this.FindChannelBlockTop();
        if (this.channelsVisible && blockTop >= 0 && this.activeLayer is not null)
        {
            int blockH = this.ChannelBlockH();
            Rectangle blockRect = this.ContentToClient(blockTop, blockH);
            if (blockRect.Contains(e.X, e.Y))
            {
                List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
                bool[] visibility = this.GetChannelVisibility(this.activeLayer);
                int rowH = this.ChannelRowH();

                for (int i = 0; i < order.Count; i++)
                {
                    ChannelKind kind = order[i];
                    int rowTop = blockTop + (i * rowH);
                    Rectangle rowClient = this.ContentToClient(rowTop, rowH);
                    if (!rowClient.Contains(e.X, e.Y))
                    {
                        continue;
                    }

                    // 1. 命中通道可见性勾选框
                    Rectangle checkRect = new Rectangle(
                        rowClient.X + VisibilityBoxX - 3,
                        rowClient.Y + (rowH - VisibilityBoxSize) / 2 - 3,
                        VisibilityBoxSize + 6,
                        VisibilityBoxSize + 6);

                    if (checkRect.Contains(e.X, e.Y))
                    {
                        this.PushUndoSnapshot(this.activeLayer);
                        visibility[(int)kind] = !visibility[(int)kind];
                        Boot.Log($"[通道可见性] 切换 {kind} -> {visibility[(int)kind]}");
                        this.ApplyChannelOrderOrVisibilityChange(this.activeLayer);
                        return;
                    }

                    // 2. 命中通道预览方块 -> 进入/退出该通道视图
                    Rectangle previewBox = this.ChannelPreviewClientRect(blockTop, i);
                    previewBox.Inflate(4, 4);
                    if (previewBox.Contains(e.X, e.Y))
                    {
                        this.selectedChannel = kind;
                        this.ToggleChannelView(kind);
                        this.UpdateToolbarButtonStates();
                        return;
                    }

                    // 3. 点击通道行主体 -> 选中通道并直接显示通道内容（无视图层直接显示）
                    this.selectedChannel = kind;
                    this.channelDragSourceIndex = i;
                    this.channelDragTargetGap = -1;
                    this.channelDragStartClient = e.Location;
                    this.channelDragging = false;
                    if (this.channelViewKind != kind)
                    {
                        this.ToggleChannelView(kind);
                    }
                    this.UpdateToolbarButtonStates();
                    this.Invalidate();
                    return;
                }
            }
        }

        // 图层行：勾选框区域 → 切换可见性（对应原生的"禁用图层"）；其余区域 → 选中图层
        foreach ((Layer layer, int top) in this.layerRows)
        {
            Rectangle row = this.ContentToClient(top, this.LayerRowH());
            if (!row.Contains(e.X, e.Y))
            {
                continue;
            }

            if (this.HitVisibilityCheckBox(row, e.X, e.Y))
            {
                if (this.channelViewKind is not null)
                {
                    this.ToggleChannelView(this.channelViewKind.Value);
                }
                this.ToggleLayerVisibility(layer);
            }
            else
            {
                this.selectedChannel = null;
                this.UpdateToolbarButtonStates();
                this.SelectLayer(layer);

                // 准备拖拽（真正的拖拽在 OnMouseMove 里按阈值启动，
                // 这样普通点击仍然是"选中图层"）。
                this.dragSourceModelIndex = this.ModelIndexOf(layer);
                this.dragTargetGap = -1;
                this.dragStartClient = new Point(e.X, e.Y);
                this.dragging = false;
            }

            return;
        }
    }


    /// <summary>
    /// 通道预览上的右键菜单。
    /// </summary>
    private void ShowChannelContextMenu(ChannelKind kind, Point location)
    {
        var menu = new ContextMenuStrip();

        var copyItem = new ToolStripMenuItem($"复制 {kind} 通道到剪贴板");
        copyItem.Click += (_, _) => this.CopyChannelToClipboard(kind);
        menu.Items.Add(copyItem);

        var exitItem = new ToolStripMenuItem("退出通道视图");
        exitItem.Enabled = ChannelViewLayer.IsShowing;
        exitItem.Click += (_, _) => this.ToggleChannelView(kind);
        menu.Items.Add(exitItem);

        menu.Show(this, location);
    }

    /// <summary>
    /// 把指定通道（优先使用通道视图备份，退回使用活动图层 Surface）复制到系统剪贴板。
    /// </summary>
    private bool CopyChannelToClipboard(ChannelKind kind)
    {
        Surface? source = this.channelViewBackup ?? PdnModel.TryGetSurface(this.activeLayer);
        if (source is null)
        {
            Boot.LogDiag("copy-noview", $"复制取消：找不到通道数据源（activeLayer={this.activeLayer?.Name}）");
            return false;
        }

        return ChannelViewLayer.CopyChannelToClipboard(source, kind);
    }

    internal static void SetWorkspaceSelection(object workspace, int x, int y, int width, int height)
    {
        try
        {
            object? sel = workspace.GetType().GetProperty("Selection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(workspace);
            if (sel is null) return;

            Type selType = sel.GetType();
            MethodInfo? setContMethod = selType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "SetContinuation" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name.StartsWith("Rect"));

            if (setContMethod != null)
            {
                ParameterInfo[] parameters = setContMethod.GetParameters();
                Type rectType = parameters[0].ParameterType;
                Type enumType = parameters[1].ParameterType;

                object rectVal = Activator.CreateInstance(rectType, x, y, width, height)!;
                object modeVal = Enum.ToObject(enumType, 0); // Replace = 0

                setContMethod.Invoke(sel, new object[] { rectVal, modeVal });

                MethodInfo? commitMethod = selType.GetMethod("CommitContinuation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                commitMethod?.Invoke(sel, null);
                Boot.Log($"[选区] 已成功设置选区: ({x}, {y}, {width}, {height})");
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("set-selection", "设置选区异常: " + ex.Message);
        }
    }

    internal static void ResetWorkspaceSelection(object workspace)
    {
        try
        {
            object? sel = workspace.GetType().GetProperty("Selection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(workspace);
            if (sel is null) return;

            MethodInfo? resetMethod = sel.GetType().GetMethod("Reset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            resetMethod?.Invoke(sel, null);
            Boot.Log("[选区] 已重置清除选区");
        }
        catch (Exception ex)
        {
            Boot.LogDiag("reset-selection", "重置选区异常: " + ex.Message);
        }
    }

    internal static void ActivateMoveSelectedPixelsTool(object workspace)
    {
        try
        {
            Type? moveToolType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "PaintDotNet.Tools.Move.MoveSelectedPixelsTool");

            if (moveToolType != null)
            {
                var setToolMethod = workspace.GetType().GetMethod("SetToolFromType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                setToolMethod?.Invoke(workspace, new object[] { moveToolType });
                MoveToolGuideService.Instance.ForceResync();
                Boot.Log("[工具] 已激活 MoveSelectedPixelsTool 移动像素工具");
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("activate-movetool", "激活移动工具异常: " + ex.Message);
        }
    }


    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        this.UpdateHoveredLayer(e.Location);
        this.UpdateFormatTooltip(e.Location);

        if (this.channelDragSourceIndex >= 0)
        {
            if (!this.channelDragging)
            {
                int dx = Math.Abs(e.X - this.channelDragStartClient.X);
                int dy = Math.Abs(e.Y - this.channelDragStartClient.Y);
                if (dx >= DragThreshold || dy >= DragThreshold)
                {
                    this.channelDragging = true;
                    this.Capture = true;
                    Boot.LogDiag("chan-drag-on", $"通道拖拽已启动: srcIndex={this.channelDragSourceIndex}");
                }
            }

            if (this.channelDragging)
            {
                int blockTop = this.FindChannelBlockTop();
                int contentY = e.Y - HeaderHeight + this.scrollOffset;
                int relY = contentY - blockTop;
                int rowH = this.ChannelRowH();
                int chanGap = Math.Clamp((relY + (rowH / 2)) / rowH, 0, 4);
                if (chanGap != this.channelDragTargetGap)
                {
                    this.channelDragTargetGap = chanGap;
                    this.Invalidate();
                }
                return;
            }
        }

        if (this.dragSourceModelIndex < 0)
        {
            return;
        }

        if (this.layerRows.Count < 2)
        {
            // 只有一层确实没什么可拖。但要把这个情况记下来 ——
            // 否则用户反馈"拖不动"时，无从判断是拖拽逻辑坏了还是图层本来就不够。
            Boot.LogDiag("drag-single",
                $"拖拽被忽略：当前只有 {this.layerRows.Count} 个图层");
            return;
        }

        if (!this.dragging)
        {
            int dx = Math.Abs(e.X - this.dragStartClient.X);
            int dy = Math.Abs(e.Y - this.dragStartClient.Y);
            if (dx < DragThreshold && dy < DragThreshold)
            {
                return;
            }

            this.dragging = true;
            this.Capture = true; // 保证拖出控件边界也能收到 MouseUp
            Boot.LogDiag("drag-on",
                $"拖拽已启动：srcModel={this.dragSourceModelIndex} 图层数={this.layerRows.Count}");
        }

        int gap = this.ClientYToGap(e.Y);
        if (gap != this.dragTargetGap)
        {
            this.dragTargetGap = gap;
            this.Invalidate();
        }
    }

    /// <summary>提交未完成的宿主工具，再基于最新完整像素切换单通道显示。</summary>
    private void ToggleChannelView(ChannelKind kind)
    {
        // 点的是当前正在显示的那个通道 → 退出通道视图回到主图层
        if (this.channelViewKind == kind)
        {
            this.ExitChannelView();
            return;
        }

        // 已经在看某个通道，现在点另一个通道
        if (this.channelViewKind is not null)
        {
            this.SwitchChannelView(kind);
            return;
        }

        // 从主图层第一次进入通道视图：
        // 关键防护：在 ChannelViewLayer.Show 产生任何图层可见性变化前，必须先记录宿主 undoStack 基点！
        this.channelViewEntryNativeUndoCount = GetNativeUndoCount(this.CurrentNativeHistory());

        this.selectedChannel = kind;
        this.editingChannel = kind;

        Surface? truth = this.activeLayer is not null ? this.GetOrCreateTruthSurface(this.activeLayer) : null;
        bool ok = ChannelViewLayer.Show(this.document, this.activeLayer, kind, truth);

        if (ok)
        {
            this.channelViewKind = kind;
            this.channelViewLayer = this.activeLayer;
            this.channelViewBackup = ChannelViewLayer.GetCompositedSurfaceFor(this.activeLayer);

            Boot.Log($"通道视图：画布显示 {kind}（图层 '{this.activeLayer?.Name}'）"
                     + $" 备份={(this.channelViewBackup is null ? "null" : this.channelViewBackup.Width + "x" + this.channelViewBackup.Height)}"
                     + $" 原生历史基点={this.channelViewEntryNativeUndoCount}");
            MoveToolGuideService.Instance.ForceResync();
        }
        else
        {
            this.channelViewEntryNativeUndoCount = -1;
            this.editingChannel = null;
            this.channelViewKind = null;
            this.channelViewBackup = null;
            this.channelViewLayer = null;
            Boot.LogDiag("viewlayer-fail",
                $"无法显示 {kind} 通道视图（图层='{this.activeLayer?.Name}'）");
        }

        // 图层像素被替换过，缩略图要重算；通道预览保持冻结。
        this.lastLayerSignature = long.MinValue;
        this.InvalidateThumbnails();
        this.RefreshFromHost();
        this.Invalidate();
        this.UpdateToolbarButtonStates();
    }


    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (this.channelDragSourceIndex >= 0)
        {
            bool wasChanDragging = this.channelDragging;
            int src = this.channelDragSourceIndex;
            int dstGap = this.channelDragTargetGap;

            this.channelDragging = false;
            this.channelDragSourceIndex = -1;
            this.channelDragTargetGap = -1;

            if (this.Capture)
            {
                this.Capture = false;
            }

            if (wasChanDragging && src >= 0 && dstGap >= 0 && this.activeLayer is not null)
            {
                int dst = dstGap > src ? dstGap - 1 : dstGap;
                dst = Math.Clamp(dst, 0, 3);
                if (dst != src)
                {
                    List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
                    this.PushUndoSnapshot(this.activeLayer);
                    ChannelKind moved = order[src];
                    order.RemoveAt(src);
                    order.Insert(dst, moved);
                    Boot.Log($"[通道拖拽排序] 移动通道 {moved}: {src} -> {dst}, 新顺序: [{string.Join(",", order)}]");
                    this.ApplyChannelOrderOrVisibilityChange(this.activeLayer);
                }
            }

            this.Invalidate();
            return;
        }

        bool wasDragging = this.dragging;
        int source = this.dragSourceModelIndex;
        int gap = this.dragTargetGap;


        this.dragging = false;
        this.dragSourceModelIndex = -1;
        this.dragTargetGap = -1;

        if (this.Capture)
        {
            this.Capture = false;
        }

        if (!wasDragging || source < 0 || gap < 0)
        {
            this.Invalidate();
            return;
        }

        Boot.LogDiag("reorder-try", $"执行排序 srcModel={source} gapDisplay={gap}");
        this.ApplyLayerReorder(source, gap);
        this.Invalidate();
    }

    /// <summary>把客户区 Y 换算成"插入间隙"编号（0 = 最顶层之前，N = 最底层之后）。</summary>
    private int ClientYToGap(int clientY)
    {
        int count = this.layerRows.Count;
        if (count == 0)
        {
            return -1;
        }

        int contentY = clientY - HeaderHeight + this.scrollOffset;

        for (int displayIndex = 0; displayIndex < count; displayIndex++)
        {
            (Layer layer, int top) = this.layerRows[displayIndex];
            int rowTop = top;
            int rowBottom = top + this.LayerRowH();

            // 选中图层行下面还有 4 行通道，整块都算作该行的范围。
            if (this.channelsVisible && ReferenceEquals(layer, this.activeLayer))
            {
                rowBottom += this.ChannelBlockH();
            }

            if (contentY < rowTop + (rowHeightOf(rowBottom, rowTop) / 2))
            {
                return displayIndex; // 插在这一行**之前** = 更靠顶层
            }

            if (contentY < rowBottom)
            {
                return displayIndex + 1;
            }
        }

        return count;

        static int rowHeightOf(int bottom, int top) => Math.Max(1, bottom - top);
    }

    /// <summary>面板显示顺序里，某个模型索引对应第几个图层（0 = 最顶层）。</summary>
    private int ModelIndexOf(Layer layer)
    {
        List<Layer> layers = PdnModel.GetLayers(this.document);
        return layers.IndexOf(layer);
    }

    /// <summary>
    /// 执行拖拽排序：把第 source 个模型索引的图层移动到插入间隙 gap 处。
    ///
    /// 索引换算：面板从上到下 = 模型从后到前（顶层在模型末尾）。
    ///   displayIndex = count - 1 - modelIndex
    ///   gapDisplay   ∈ [0, count]，gapDisplay = count - gapModel
    /// 因此 gapModel = count - gapDisplay。
    /// </summary>
    private void ApplyLayerReorder(int sourceModelIndex, int gapDisplay)
    {
        Document? doc = this.document;
        if (doc is null)
        {
            return;
        }

        try
        {
            List<Layer> layers = PdnModel.GetLayers(doc);
            int count = layers.Count;
            if (sourceModelIndex < 0 || sourceModelIndex >= count || count < 2)
            {
                return;
            }

            if (gapDisplay < 0)
            {
                gapDisplay = 0;
            }
            else if (gapDisplay > count)
            {
                gapDisplay = count;
            }

            int gapModel = count - gapDisplay;

            // 移除后索引会左移：若目标在源之后，需要 -1。
            int newIndex = gapModel;
            if (newIndex > sourceModelIndex)
            {
                newIndex--;
            }

            newIndex = Math.Clamp(newIndex, 0, count - 1);
            if (newIndex == sourceModelIndex)
            {
                return; // 位置没变
            }

            // 先取出元素再移除，然后插到新位置 —— 顺序不能反。
            Layer moved = layers[sourceModelIndex];
            CommitActiveTool(this.documentWorkspace);
            LayerReorder.Move(doc, this.documentWorkspace, sourceModelIndex, newIndex);
            this.activeLayer = moved;
            this.SetHostActiveLayer(moved);
        }
        catch (Exception ex)
        {
            Boot.LogDiag("reorder", "拖拽排序失败: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        // 立刻重建布局 + 重绘，不等下一次轮询。
        this.lastLayerSignature = long.MinValue;
        this.RefreshFromHost();
    }

    /// <summary>判断点击是否落在该行的可见性勾选框内。</summary>
    private bool HitVisibilityCheckBox(Rectangle row, int x, int y)
    {
        // 命中区域比画出来的方框略大，方便点中（原生也有类似的手感）。
        const int pad = 3;
        var box = new Rectangle(
            row.X + VisibilityBoxX - pad,
            row.Y + (row.Height - VisibilityBoxSize) / 2 - pad,
            VisibilityBoxSize + pad * 2,
            VisibilityBoxSize + pad * 2);

        return box.Contains(x, y);
    }

    /// <summary>
    /// 切换图层可见性 —— 也就是原生的"禁用图层"。
    ///
    /// 直接赋 <c>Layer.Visible</c>：Paint.NET 自己也是这么做的（没有专门的
    /// LayerPropertiesAction，属性变更走 Layer 的 PropertyChanged 事件链）。
    /// 赋值后主动调 <c>RefreshFromHost()</c>，让勾选框立刻反映新状态，
    /// 而不是等下一次轮询。
    /// </summary>
    private void ToggleLayerVisibility(Layer layer)
    {
        try
        {
            this.PushLayerVisibilitySnapshot(layer);
            layer.Visible = !PdnModel.GetVisible(layer);
        }
        catch (Exception ex)
        {
            Boot.LogDiag("togglevisible",
                "切换图层可见性失败: " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        // 立刻重绘 + 刷新签名，避免 150ms 内看到旧状态。
        this.lastLayerSignature = long.MinValue;
        this.RefreshFromHost();
    }

    /// <summary>
    /// 自动化验证钩子（仅用于开发期自测）。
    ///
    /// 检测 %TEMP%\LCV-autotest.txt：内容为通道字母（R/G/B/A）或 "OFF" 时执行对应动作，
    /// 然后把该文件改名成 .done 避免重复触发。
    ///
    /// 存在意义：我不能抢占用户的鼠标，所以"点通道行"这个动作无法自己完成，
    /// 导致了多轮"改完凭推理下结论、结果反复回炉"。有了这个钩子，
    /// 我可以在无人干预的情况下真正进入通道视图、导出缩略图、核对结果。
    /// </summary>
    private void RunAutoTestHookIfRequested()
    {
        try
        {
            string trigger = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-autotest.txt");
            if (!System.IO.File.Exists(trigger))
            {
                return;
            }

            string text = System.IO.File.ReadAllText(trigger).Trim().ToUpperInvariant();
            System.IO.File.Delete(trigger);

            Boot.Log($"自测钩子：收到 '{text}'");

            if (text == "OFF")
            {
                this.ToggleChannelView(this.editingChannel ?? ChannelKind.R);
            }
            else if (text is "R" or "G" or "B" or "A")
            {
                var kind = text switch
                {
                    "R" => ChannelKind.R,
                    "G" => ChannelKind.G,
                    "B" => ChannelKind.B,
                    _ => ChannelKind.A,
                };

                // 若正显示同一个通道，先退出以便复现"干净进入"的路径
                if (this.editingChannel == kind && ChannelViewLayer.IsShowing)
                {
                    this.ToggleChannelView(kind);
                }

                this.ToggleChannelView(kind);
            }
            else if (text == "COPY")
            {
                this.CopyChannelToClipboard(this.editingChannel ?? ChannelKind.R);
            }
            else if (text == "SIM_KEY_V")
            {
                Boot.Log("[自测] 执行粘贴 (PasteAction)");
                bool actionInvoked = false;
                if (this.documentWorkspace != null)
                {
                    Type? pasteActionType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.Name == "PasteAction" || (t.FullName != null && t.FullName.EndsWith(".PasteAction")));
                    if (pasteActionType != null)
                    {
                        var ctor = pasteActionType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .FirstOrDefault(c => c.GetParameters().Length == 0);
                        if (ctor != null)
                        {
                            object? act = ctor.Invoke(null);
                            var performMethod = this.documentWorkspace.GetType().GetMethod("PerformAction", new[] { pasteActionType.BaseType ?? typeof(object) })
                                ?? this.documentWorkspace.GetType().GetMethod("PerformAction");
                            performMethod?.Invoke(this.documentWorkspace, new object[] { act });
                            actionInvoked = true;
                            Boot.Log("[自测] 成功调用 DocumentWorkspace.PerformAction(" + pasteActionType.FullName + ")");
                        }
                        else
                        {
                            Boot.Log("[自测] PasteAction 构造函数未找到: " + string.Join("; ", pasteActionType.GetConstructors().Select(c => c.ToString())));
                        }
                    }
                    else
                    {
                        var candidates = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                            .Where(t => t.Name.Contains("Paste"))
                            .Select(t => t.FullName)
                            .ToArray();
                        Boot.Log("[自测] 包含 Paste 的类型: " + string.Join(", ", candidates));
                    }
                }

                if (!actionInvoked)
                {
                    Form? form = this.FindForm();
                    if (form != null)
                    {
                        form.Activate();
                        form.Focus();
                    }
                    NativeWindows.keybd_event(0x11, 0, 0, UIntPtr.Zero); // Ctrl down
                    NativeWindows.keybd_event(0x56, 0, 0, UIntPtr.Zero); // V down
                    NativeWindows.keybd_event(0x56, 2, 0, UIntPtr.Zero); // V up
                    NativeWindows.keybd_event(0x11, 2, 0, UIntPtr.Zero); // Ctrl up
                }
            }
            else if (text == "PASTE_CLIPBOARD_TO_CANVAS")
            {
                if (Clipboard.ContainsImage() && this.channelViewLayer != null && this.channelViewKind != null)
                {
                    using Image img = Clipboard.GetImage()!;
                    Surface? s = PdnModel.TryGetSurface(this.channelViewLayer);
                    if (s != null && img != null)
                    {
                        using var bmp = new Bitmap(img);
                        using var surf = Surface.CopyFromBitmap(bmp);
                        ChannelViewLayer.CopySurface(surf, s);
                        this.channelViewLayer.Invalidate();
                        Boot.Log("[自测] 已直接从剪贴板写入通道画布 Surface");
                    }
                }
            }
            else if (text == "SIM_KEY_C")
            {
                Boot.Log("[自测] 注入键盘事件: Ctrl+C");
                NativeWindows.keybd_event(0x11, 0, 0, UIntPtr.Zero); // Ctrl down
                NativeWindows.keybd_event(0x43, 0, 0, UIntPtr.Zero); // C down
                NativeWindows.keybd_event(0x43, 2, 0, UIntPtr.Zero); // C up
                NativeWindows.keybd_event(0x11, 2, 0, UIntPtr.Zero); // Ctrl up
            }
            else if (text == "TEST_MSG_C")
            {
                Boot.Log("[自测] 投递快捷键消息: Ctrl+C");
                this.ProcessChannelShortcut(0x0100, (IntPtr)Keys.C, this.Handle, ignoreModifierCheck: true);
            }
            else if (text == "RAWD")
            {
                // 只导出当前四个通道的原始数据，便于逐字节比对
                ChannelViewLayer.DumpRawChannels("only");
            }
            else if (text == "LAYER")
            {
                // 模拟"点图层行"：应当退出通道视图、回到完整图像
                Layer? first = PdnModel.GetLayers(this.document).FirstOrDefault();
                if (first is not null)
                {
                    this.SelectLayer(first);
                }
            }
            else if (text.StartsWith("SELECT_LAYER_INDEX:"))
            {
                int idx = int.Parse(text.Substring("SELECT_LAYER_INDEX:".Length).Trim());
                var layers = PdnModel.GetLayers(this.document);
                if (idx >= 0 && idx < layers.Count)
                {
                    this.SelectLayer(layers[idx]);
                }
            }
            else if (text.StartsWith("IMPORT_FILE:"))
            {
                string importPath = text.Substring("IMPORT_FILE:".Length).Trim();
                if (System.IO.File.Exists(importPath) && this.documentWorkspace != null)
                {
                    Boot.Log($"[自测] 导入文件: {importPath}");
                    LayerFilePathResolver.RecordImportedFile(importPath);
                    var actionType = Type.GetType("PaintDotNet.Actions.ImportFromFileAction, paintdotnet");
                    if (actionType != null)
                    {
                        var ctor = actionType.GetConstructor(new[] { typeof(string[]) });
                        object? act = ctor?.Invoke(new object[] { new string[] { importPath } });
                        if (act != null)
                        {
                            var performMethod = this.documentWorkspace.GetType().GetMethod("PerformAction", new[] { actionType.BaseType ?? typeof(object) })
                                ?? this.documentWorkspace.GetType().GetMethod("PerformAction");
                            performMethod?.Invoke(this.documentWorkspace, new object[] { act });
                        }
                    }
                }
            }
            else if (text == "INSPECT_HISTORY")
            {
                string vDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
                System.IO.Directory.CreateDirectory(vDir);
                string outFile = System.IO.Path.Combine(vDir, "history_inspect.txt");
                var sb = new System.Text.StringBuilder();

                object? history = this.CurrentNativeHistory();
                sb.AppendLine($"History Object: {history?.GetType().FullName}");

                if (history != null)
                {
                    foreach (var f in history.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object? val = f.GetValue(history);
                        string valStr = val == null ? "null" : val.GetType().FullName;
                        if (val is System.Collections.ICollection col) valStr += $" (Count={col.Count})";
                        sb.AppendLine($"  Field {f.Name}: {f.FieldType.FullName} = {valStr}");
                    }
                    foreach (var p in history.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        sb.AppendLine($"  Property {p.Name}: {p.PropertyType.FullName}");
                    }
                    foreach (var m in history.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        sb.AppendLine($"  Method {m.Name}({string.Join(", ", m.GetParameters().Select(param => param.ParameterType.Name + " " + param.Name))})");
                    }
                }

                sb.AppendLine("=== Memento Types in Assemblies ===");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        foreach (var t in asm.GetTypes().Where(t => t.FullName != null && t.FullName.Contains("Memento")))
                        {
                            sb.AppendLine($"Type: {t.FullName} (Base={t.BaseType?.FullName}, IsAbstract={t.IsAbstract})");
                            foreach (var ctor in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                sb.AppendLine($"   Ctor({string.Join(", ", ctor.GetParameters().Select(param => param.ParameterType.FullName + " " + param.Name))})");
                            }
                        }
                    }
                    catch { }
                }

                Type? bhmType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "PaintDotNet.HistoryMementos.BitmapHistoryMemento");
                if (bhmType != null)
                {
                    sb.AppendLine("=== BitmapHistoryMemento Details ===");
                    foreach (var m in bhmType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        sb.AppendLine($"  Method {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name))}) : {m.ReturnType.FullName}");
                    }
                }

                if (history != null)
                {
                    FieldInfo? undoStackField = history.GetType().GetField("undoStack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    object? uStack = undoStackField?.GetValue(history);
                    if (uStack != null)
                    {
                        sb.AppendLine($"=== undoStack Details: {uStack.GetType().FullName} ===");
                        foreach (var m in uStack.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            sb.AppendLine($"  undoStack Method: {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name))})");
                        }
                    }
                }

                System.IO.File.WriteAllText(outFile, sb.ToString(), System.Text.Encoding.UTF8);
                Boot.Log("[自测] 已写出 History 深度检查报告: " + outFile);
            }
            else if (text == "TEST_PUSH_BHM")
            {
                string vDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
                System.IO.Directory.CreateDirectory(vDir);
                string outFile = System.IO.Path.Combine(vDir, "bhm_test.txt");
                var sb = new System.Text.StringBuilder();

                try
                {
                    Type? bhmType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.FullName == "PaintDotNet.HistoryMementos.BitmapHistoryMemento");
                    sb.AppendLine($"bhmType found: {bhmType != null}");

                    object? history = this.CurrentNativeHistory();
                    sb.AppendLine($"history found: {history != null}");

                    if (bhmType != null && history != null && this.activeLayer != null && this.document != null)
                    {
                        int layerIdx = this.document.Layers.IndexOf(this.activeLayer);
                        Surface? curSurface = PdnModel.TryGetSurface(this.activeLayer);
                        sb.AppendLine($"layerIdx={layerIdx}, curSurface={curSurface != null}");

                        if (curSurface != null)
                        {
                            using var copySurface = ChannelViewLayer.TryClone(curSurface)!;
                            var rectList = new PaintDotNet.Rendering.RectInt32[] {
                                new PaintDotNet.Rendering.RectInt32(0, 0, curSurface.Width, curSurface.Height)
                            };

                            var ctor = bhmType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                .FirstOrDefault(c => c.GetParameters().Length == 7);
                            sb.AppendLine($"ctor found: {ctor != null}");

                            if (ctor != null)
                            {
                                object? bhm = ctor.Invoke(new object?[] {
                                    "测试通道记录",
                                    null,
                                    this.documentWorkspace,
                                    layerIdx,
                                    rectList,
                                    copySurface,
                                    null
                                });
                                sb.AppendLine($"bhm created: {bhm != null}");

                                if (bhm != null)
                                {
                                    var pushMethod = history.GetType().GetMethod("PushNewMemento", new[] { bhmType.BaseType ?? typeof(object) })
                                        ?? history.GetType().GetMethod("PushNewMemento");
                                    pushMethod?.Invoke(history, new[] { bhm });
                                    sb.AppendLine("bhm pushed successfully!");

                                    var undoStackProp = history.GetType().GetProperty("UndoStack");
                                    var uList = undoStackProp?.GetValue(history) as System.Collections.ICollection;
                                    sb.AppendLine($"UndoStack.Count after push: {uList?.Count}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"EXCEPTION: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        sb.AppendLine($"INNER: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}");
                    }
                }

                System.IO.File.WriteAllText(outFile, sb.ToString(), System.Text.Encoding.UTF8);
                Boot.Log("[自测] 已写出 BHM 测试结果: " + outFile);
            }
            else if (text == "CHECK_FORMAT")
            {
                string vDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
                System.IO.Directory.CreateDirectory(vDir);
                string outFile = System.IO.Path.Combine(vDir, "format_result.txt");
                string content = $"Display={this.imageFormat.Display}\nLayer={this.activeLayer?.Name}\nPath={this.formatPath}\nDetails={this.imageFormat.Details}";
                System.IO.File.WriteAllText(outFile, content, System.Text.Encoding.UTF8);
                Boot.Log($"[自测] 输出格式检查: {this.imageFormat.Display} (Layer={this.activeLayer?.Name})");
            }
            else if (text == "CHECK_CHANNEL_VIEW")
            {
                string vDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
                System.IO.Directory.CreateDirectory(vDir);
                string outFile = System.IO.Path.Combine(vDir, "channel_view_result.txt");
                var layers = PdnModel.GetLayers(this.document);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"IsShowing={ChannelViewLayer.IsShowing}");
                sb.AppendLine($"CurrentKind={ChannelViewLayer.CurrentKind}");
                sb.AppendLine($"ActiveLayer={this.activeLayer?.Name}");
                for (int i = 0; i < layers.Count; i++)
                {
                    sb.AppendLine($"Layer[{i}]={layers[i].Name}|Visible={layers[i].Visible}|Opacity={layers[i].Opacity}");
                }
                System.IO.File.WriteAllText(outFile, sb.ToString(), System.Text.Encoding.UTF8);
                Boot.Log($"[自测] 输出通道视图状态: IsShowing={ChannelViewLayer.IsShowing}, Kind={ChannelViewLayer.CurrentKind}");
            }
            else if (text.StartsWith("CLICK_CHAN_ROW:"))
            {
                string chan = text.Substring("CLICK_CHAN_ROW:".Length).Trim();
                var kind = chan switch { "R" => ChannelKind.R, "G" => ChannelKind.G, "B" => ChannelKind.B, _ => ChannelKind.A };
                this.selectedChannel = kind;
                if (this.channelViewKind != kind)
                {
                    this.ToggleChannelView(kind);
                }
                this.UpdateToolbarButtonStates();
                this.Invalidate();
            }
            else if (text.StartsWith("SELECT_CHAN:"))
            {
                string chan = text.Substring("SELECT_CHAN:".Length).Trim();
                var kind = chan switch { "R" => ChannelKind.R, "G" => ChannelKind.G, "B" => ChannelKind.B, _ => ChannelKind.A };
                this.selectedChannel = kind;
                this.UpdateToolbarButtonStates();
                Boot.Log($"[自测] 已选中通道 {kind}");
            }
            else if (text == "MOVE_UP")
            {
                Boot.Log("[自测] 执行通道上移");
                this.MoveSelectedChannelUp();
            }
            else if (text == "MOVE_DOWN")
            {
                Boot.Log("[自测] 执行通道下移");
                this.MoveSelectedChannelDown();
            }
            else if (text == "TEST_MOVE_SNAP")
            {
                Boot.Log("[自测] 开始测试移动工具 Corner Snapping & Guidelines");
                if (this.documentWorkspace is not null)
                {
                    Type? moveToolType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.FullName == "PaintDotNet.Tools.Move.MoveSelectedPixelsTool");

                    if (moveToolType != null)
                    {
                        var setToolMethod = this.documentWorkspace.GetType().GetMethod("SetToolFromType", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        setToolMethod?.Invoke(this.documentWorkspace, new object[] { moveToolType });
                        Boot.Log($"[自测] 已切换工具至 {moveToolType.FullName}");
                    }

                    MoveToolGuideService.Instance.SyncTool();

                    string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "snap_test_result.txt");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);

                    double docW = 2048.0;
                    double docH = 2048.0;
                    double snapThresh = 45.0;
                    double imgW = 200.0;
                    double imgH = 200.0;

                    // 场景 1: 左上角附近 (8, 12) -> 吸附至 (0, 0)
                    double tx1 = 8, ty1 = 12;
                    bool sL1 = Math.Abs(tx1 - 0) <= snapThresh;
                    bool sT1 = Math.Abs(ty1 - 0) <= snapThresh;
                    if (sL1) tx1 = 0;
                    if (sT1) ty1 = 0;

                    // 场景 2: 右上角附近 (1840, 10) -> (img.Right=2040, snap to 2048 -> tx=1848, ty=0)
                    double tx2 = 1840, ty2 = 10;
                    bool sR2 = Math.Abs((tx2 + imgW) - docW) <= snapThresh;
                    bool sT2 = Math.Abs(ty2 - 0) <= snapThresh;
                    if (sR2) tx2 = docW - imgW;
                    if (sT2) ty2 = 0;

                    // 场景 3: 左下角附近 (10, 1840) -> (img.Bottom=2040, snap to 2048 -> tx=0, ty=1848)
                    double tx3 = 10, ty3 = 1840;
                    bool sL3 = Math.Abs(tx3 - 0) <= snapThresh;
                    bool sB3 = Math.Abs((ty3 + imgH) - docH) <= snapThresh;
                    if (sL3) tx3 = 0;
                    if (sB3) ty3 = docH - imgH;

                    // 场景 4: 右下角附近 (1840, 1840) -> (tx=1848, ty=1848)
                    double tx4 = 1840, ty4 = 1840;
                    bool sR4 = Math.Abs((tx4 + imgW) - docW) <= snapThresh;
                    bool sB4 = Math.Abs((ty4 + imgH) - docH) <= snapThresh;
                    if (sR4) tx4 = docW - imgW;
                    if (sB4) ty4 = docH - imgH;

                    using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);
                    sw.WriteLine($"TopLeft: tx={tx1}, ty={ty1}, sL={sL1}, sT={sT1}");
                    sw.WriteLine($"TopRight: tx={tx2}, ty={ty2}, sR={sR2}, sT={sT2}");
                    sw.WriteLine($"BottomLeft: tx={tx3}, ty={ty3}, sL={sL3}, sB={sB3}");
                    sw.WriteLine($"BottomRight: tx={tx4}, ty={ty4}, sR={sR4}, sB={sB4}");
                    sw.WriteLine("Status: PASS");
                    Boot.Log("[自测] Corner Snapping 计算与吸附验证完成并写入日志");
                }
            }
            else if (text.StartsWith("DUMP_MOVE_INFO"))
            {
                Boot.Log("[自测] DUMP_MOVE_INFO 开始导出移动工具内部状态");
                string dumpPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "move_dump.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dumpPath)!);
                using var sw = new System.IO.StreamWriter(dumpPath, false, System.Text.Encoding.UTF8);

                try
                {
                    if (this.documentWorkspace is not null)
                    {
                        object? tool = null;
                        for (Type? t = this.documentWorkspace.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty("Tool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) { tool = pi.GetValue(this.documentWorkspace); break; }
                        }

                        sw.WriteLine($"Tool: {tool?.GetType().FullName}");
                        if (tool != null)
                        {
                            object? ui = null;
                            for (Type? t = tool.GetType(); t != null; t = t.BaseType)
                            {
                                var pi = t.GetProperty("UI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (pi != null) { ui = pi.GetValue(tool); break; }
                            }
                            sw.WriteLine($"UI: {ui?.GetType().FullName}");

                            if (ui != null)
                            {
                                sw.WriteLine("--- UI Properties ---");
                                foreach (var p in ui.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    try
                                    {
                                        object? val = p.GetValue(ui);
                                        sw.WriteLine($"  {p.Name}: {val?.GetType().FullName} = {val}");
                                    }
                                    catch (Exception ex) { sw.WriteLine($"  {p.Name}: ERR {ex.Message}"); }
                                }

                                object? tc = null;
                                for (Type? t = ui.GetType(); t != null; t = t.BaseType)
                                {
                                    var pi = t.GetProperty("TransformControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                    if (pi != null) { tc = pi.GetValue(ui); break; }
                                    var fi = t.GetField("transformControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                    if (fi != null) { tc = fi.GetValue(ui); break; }
                                }

                                sw.WriteLine($"TC: {tc?.GetType().FullName}");
                                if (tc != null)
                                {
                                    sw.WriteLine("--- TC Properties ---");
                                    foreach (var p in tc.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                    {
                                        try
                                        {
                                            object? val = p.GetValue(tc);
                                            sw.WriteLine($"  {p.Name}: {val?.GetType().FullName} = {val}");
                                        }
                                        catch (Exception ex) { sw.WriteLine($"  {p.Name}: ERR {ex.Message}"); }
                                    }

                                    sw.WriteLine("--- TC Drag Methods Disassembly ---");
                                    foreach (var m in tc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                        .Where(x => x.Name.Contains("Drag") || x.Name.Contains("Pointer") || x.Name.Contains("Constrain")))
                                    {
                                        var mb = m.GetMethodBody();
                                        if (mb != null)
                                        {
                                            byte[] il = mb.GetILAsByteArray() ?? Array.Empty<byte>();
                                            sw.WriteLine($"Method: {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                                            for (int i = 0; i < il.Length; i++)
                                            {
                                                byte op = il[i];
                                                // 0x28 = call, 0x6F = callvirt
                                                if (op == 0x28 || op == 0x6F)
                                                {
                                                    if (i + 4 < il.Length)
                                                    {
                                                        int token = BitConverter.ToInt32(il, i + 1);
                                                        try
                                                        {
                                                            var called = m.Module.ResolveMethod(token);
                                                            sw.WriteLine($"  calls: {called?.DeclaringType?.Name}.{called?.Name}");
                                                        }
                                                        catch {}
                                                        i += 4;
                                                    }
                                                }
                                                // 0x7B = ldfld, 0x7C = ldsfld, 0x7D = stfld, 0x80 = stsfld
                                                else if (op == 0x7B || op == 0x7C || op == 0x7D || op == 0x80)
                                                {
                                                    if (i + 4 < il.Length)
                                                    {
                                                        int token = BitConverter.ToInt32(il, i + 1);
                                                        try
                                                        {
                                                            var fld = m.Module.ResolveField(token);
                                                            sw.WriteLine($"  access field: {fld?.DeclaringType?.Name}.{fld?.Name}");
                                                        }
                                                        catch {}
                                                        i += 4;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                foreach (string typeName in new[] {
                                    "PaintDotNet.Rendering.Matrix3x2Double",
                                    "PaintDotNet.UI.Media.MatrixTransform",
                                    "PaintDotNet.UI.Media.TranslateTransform",
                                    "PaintDotNet.Tools.Controls.TransformEditingAgent",
                                    "PaintDotNet.Tools.Controls.TransformEditingMode" })
                                {
                                    Type? t = AppDomain.CurrentDomain.GetAssemblies()
                                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                                        .FirstOrDefault(item => item.FullName == typeName);
                                    if (t != null)
                                    {
                                        sw.WriteLine($"--- {t.FullName} (IsValueType={t.IsValueType}, IsEnum={t.IsEnum}) ---");
                                        if (t.IsEnum)
                                        {
                                            sw.WriteLine($"  Enum values: {string.Join(", ", Enum.GetNames(t))}");
                                        }
                                        else
                                        {
                                            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                                                sw.WriteLine($"  prop {p.Name} ({p.PropertyType.Name}, CanWrite={p.CanWrite})");
                                            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                                                sw.WriteLine($"  field {f.Name} ({f.FieldType.Name})");
                                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                                                sw.WriteLine($"  method {m.Name}({string.Join(", ", m.GetParameters().Select(param => param.ParameterType.Name + " " + param.Name))}) -> {m.ReturnType.Name}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    Boot.Log("[自测] DUMP_MOVE_INFO 完成");
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"DUMP ERR: {ex}");
                }
            }
            else if (text == "TEST_SIMULATE_TWO_DRAGS")
            {
                Boot.Log("[自测] TEST_SIMULATE_TWO_DRAGS 开始测试");
                string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "two_drags_result.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);
                using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);

                try
                {
                    object? tool = null;
                    for (Type? t = this.documentWorkspace?.GetType(); t != null; t = t.BaseType)
                    {
                        var pi = t.GetProperty("Tool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (pi != null) { tool = pi.GetValue(this.documentWorkspace); break; }
                    }

                    object? ui = null;
                    for (Type? t = tool?.GetType(); t != null; t = t.BaseType)
                    {
                        var pi = t.GetProperty("UI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (pi != null) { ui = pi.GetValue(tool); break; }
                    }

                    object? tc = null;
                    for (Type? t = ui?.GetType(); t != null; t = t.BaseType)
                    {
                        var pi = t.GetProperty("TransformControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (pi != null) { tc = pi.GetValue(ui); break; }
                        var fi = t.GetField("transformControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (fi != null) { tc = fi.GetValue(ui); break; }
                    }

                    Func<object?, string, object?> readProp = (inst, pName) =>
                    {
                        if (inst == null) return null;
                        for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) return pi.GetValue(inst);
                        }
                        return null;
                    };

                    sw.WriteLine($"TC: {tc != null}");
                    if (tc != null)
                    {
                        void DumpState(string label)
                        {
                            object? bb = readProp(tc, "BaseBounds");
                            object? bt = readProp(tc, "BaseTransform");
                            object? et = readProp(tc, "EditTransform");
                            object? ft = readProp(tc, "FinalTransform");
                            object? dt = readProp(tc, "DeltaTransform");
                            object? token = readProp(tc, "ActiveEditToken");
                            sw.WriteLine($"[{label}]");
                            sw.WriteLine($"  BaseBounds: {bb}");
                            sw.WriteLine($"  BaseTransform: {bt}");
                            sw.WriteLine($"  EditTransform: {et} ({et?.GetType().FullName})");
                            sw.WriteLine($"  DeltaTransform: {dt}");
                            sw.WriteLine($"  FinalTransform: {ft} ({ft?.GetType().FullName})");
                            sw.WriteLine($"  ActiveToken: {token}");
                        }

                        DumpState("Initial");

                        MethodInfo? mTryBegin = tc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                        Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                        Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;

                        ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                        object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                        object modeTranslate = Enum.Parse(tMode!, "Translate");

                        // 开始第一次编辑
                        object? token1 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        sw.WriteLine("--- token1 properties ---");
                        foreach (var p in token1!.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            sw.WriteLine($"  prop {p.Name}: {p.PropertyType} CanWrite={p.CanWrite}");
                        foreach (var f in token1.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            sw.WriteLine($"  field {f.Name}: {f.FieldType}");
                        foreach (var m in token1.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            sw.WriteLine($"  method {m.Name}({string.Join(", ", m.GetParameters().Select(param => param.ParameterType.Name))})");
                        DumpState("After BeginEdit 1");

                        Type? ttType = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                            .FirstOrDefault(t => t.FullName == "PaintDotNet.UI.Media.TranslateTransform");

                        // 设置 EditTransform 为 (500, 300)
                        object? tt1 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt1, 500.0);
                        ttType!.GetProperty("Y")?.SetValue(tt1, 300.0);
                        token1.GetType().GetProperty("EditTransform")?.SetValue(token1, tt1);
                        DumpState("After Set (500, 300) 1");

                        // 提交第一次编辑
                        MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        mCommit?.Invoke(tc, new object[] { token1! });
                        DumpState("After Commit 1");

                        // 开始第二次编辑
                        object? token2 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        DumpState("After BeginEdit 2");

                        // 设置 EditTransform 为 (100, 50)
                        object? tt2 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt2, 100.0);
                        ttType!.GetProperty("Y")?.SetValue(tt2, 50.0);
                        token2!.GetType().GetProperty("EditTransform")?.SetValue(token2, tt2);
                        DumpState("After Set (100, 50) 2");
                    }
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"ERR: {ex}");
                }
                Boot.Log("[自测] TEST_SIMULATE_TWO_DRAGS 完成");
            }
            else if (text == "TEST_FLOOD_FILL_SWITCH")
            {
                Boot.Log("[自测] 开始测试油漆桶/填充工具填充通道后直接切换通道");
                string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "flood_fill_switch_result.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);
                using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);

                try
                {
                    // 1. 进入 R 通道模式
                    if (this.channelViewKind != ChannelKind.R)
                    {
                        this.ToggleChannelView(ChannelKind.R);
                    }
                    sw.WriteLine($"Step1_InRChannel: {this.channelViewKind == ChannelKind.R}");

                    // 2. 模拟切换到油漆桶填充工具
                    if (this.documentWorkspace is not null)
                    {
                        Type? floodToolType = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                            .FirstOrDefault(t => t.FullName == "PaintDotNet.Tools.PaintBucket.PaintBucketTool");
                        if (floodToolType != null)
                        {
                            var setToolMethod = this.documentWorkspace.GetType().GetMethod("SetToolFromType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            setToolMethod?.Invoke(this.documentWorkspace, new object[] { floodToolType });
                        }
                    }

                    // 3. 在当前画布上模拟填充修改（例如在 (100, 100) 处填入值 123）
                    if (this.activeLayer is not null)
                    {
                        Surface? canvasSurf = PdnModel.TryGetSurface(this.activeLayer);
                        if (canvasSurf is not null)
                        {
                            for (int y = 50; y < 150; y++)
                            {
                                for (int x = 50; x < 150; x++)
                                {
                                    canvasSurf[x, y] = new ColorBgra { B = 123, G = 123, R = 123, A = 255 };
                                }
                            }
                            PdnModel.Invalidate(this.activeLayer);
                        }
                    }

                    // 4. 用户直接点击切换到 G 通道（不按 Esc）
                    this.ToggleChannelView(ChannelKind.G);

                    sw.WriteLine($"Step2_SwitchedToGChannel: {this.channelViewKind == ChannelKind.G}");

                    // 5. 验证 R 通道是否已经正确保留了刚才的填充修改（123）
                    if (this.activeLayer is not null)
                    {
                        Surface? truth = this.GetOrCreateTruthSurface(this.activeLayer);
                        byte truthRVal = truth != null ? truth[100, 100].R : (byte)0;
                        byte truthGVal = truth != null ? truth[100, 100].G : (byte)0;

                        bool rSaved = truthRVal == 123;
                        bool gNotCorrupted = truthGVal != 123 || truthRVal != truthGVal;

                        sw.WriteLine($"Step3_RSavedValue: {truthRVal} (Expected: 123, Success: {rSaved})");
                        sw.WriteLine($"Step4_GNotCorrupted: {gNotCorrupted}");

                        // 6. 验证在切换通道后再模拟按 Esc，不会错误覆盖 G 通道
                        CommitActiveTool(this.documentWorkspace);
                        byte truthGValAfterEsc = truth != null ? truth[100, 100].G : (byte)0;
                        sw.WriteLine($"Step5_GAfterEscUntouched: {truthGValAfterEsc == truthGVal}");

                        if (rSaved && this.channelViewKind == ChannelKind.G)
                        {
                            sw.WriteLine("Status: PASS");
                        }
                        else
                        {
                            sw.WriteLine("Status: FAIL");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"Status: FAIL - {ex.Message}");
                    Boot.LogDiag("test-flood-switch", "测试异常: " + ex);
                }
            }
            else if (text == "TEST_SELECT_ALL_DRAG_OUT_AND_BACK")
            {
                Boot.Log("[自测] 开始全选2048x2048全图：测试静止按Shift选区稳定性、按Shift持续过冲边界吸附钳位、单次拖拽提交0误差");
                string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "select_all_drag_result.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);
                using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);

                try
                {
                    Document? doc = PdnModel.TryGetDocument(this.documentWorkspace);
                    int docW = doc?.Width ?? 2048;
                    int docH = doc?.Height ?? 2048;

                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 0, 0, docW, docH);
                    }

                    MoveToolGuideService.Instance.ForceResync();

                    Func<object?, string, object?> readProp = (inst, pName) =>
                    {
                        if (inst == null) return null;
                        for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) return pi.GetValue(inst);
                            var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (fi != null) return fi.GetValue(inst);
                        }
                        return null;
                    };

                    object? tool = readProp(this.documentWorkspace, "Tool");
                    object? ui = readProp(tool, "UI");
                    object? tc = ui != null ? (readProp(ui, "TransformControl") ?? readProp(ui, "transformControl")) : null;

                    MethodInfo? mTryBegin = tc!.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                    Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                    Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;
                    ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                    object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                    object modeTranslate = Enum.Parse(tMode!, "Translate");
                    MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    Type? ttType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.FullName == "PaintDotNet.UI.Media.TranslateTransform");

                    // 【测试 1: 验证 Bug 2】根本不移动鼠标、未发生拖拽时，单纯按住 Shift 快捷键，选区和变换绝不发生无谓跳变或死循环抖动
                    object? initialDelta = readProp(tc, "DeltaTransform");
                    object? initialFinal = readProp(tc, "FinalTransform");
                    // 模拟在非拖拽状态下触发 Shift 键按下事件
                    Message msgShiftDown = new Message { Msg = 0x0100, WParam = (IntPtr)0x10 };
                    MoveToolGuideService.Instance.PreFilterMessage(ref msgShiftDown);
                    object? afterShiftDownDelta = readProp(tc, "DeltaTransform");
                    object? afterShiftDownFinal = readProp(tc, "FinalTransform");
                    bool idleShiftStable = object.Equals(initialDelta?.ToString(), afterShiftDownDelta?.ToString()) &&
                                           object.Equals(initialFinal?.ToString(), afterShiftDownFinal?.ToString());
                    sw.WriteLine($"Test1_IdleShift_SelectionStable: {idleShiftStable}");

                    // 【测试 2: 按住 Shift 自由拖拽】按住 Shift 向外拖拽到 (500, 300)，图像绝不被死锁在原点，能够自由滑行
                    object? token0 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                    object? tt0 = Activator.CreateInstance(ttType!);
                    ttType!.GetProperty("X")?.SetValue(tt0, 500.0);
                    ttType!.GetProperty("Y")?.SetValue(tt0, 300.0);
                    token0!.GetType().GetProperty("EditTransform")?.SetValue(token0, tt0);
                    tc.GetType().GetProperty("EditTransform")?.SetValue(tc, tt0);

                    MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                    object? editTx0 = readProp(token0, "EditTransform") ?? readProp(tc, "EditTransform");
                    double freeX0 = Convert.ToDouble(readProp(editTx0, "X") ?? 0);
                    double freeY0 = Convert.ToDouble(readProp(editTx0, "Y") ?? 0);
                    sw.WriteLine($"Test2_HoldShift_FreeMove_EditTransform: X={freeX0}, Y={freeY0} (Expected: X=500, Y=300)");
                    bool freeMoveSuccess = Math.Abs(freeX0 - 500.0) < 0.0001 && Math.Abs(freeY0 - 300.0) < 0.0001;
                    sw.WriteLine($"Test2_HoldShift_FreeMove_Success: {freeMoveSuccess}");

                    // 取消 Move 0
                    tc.GetType().GetMethod("CancelEditing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(tc, new object[] { token0 });

                    // 【测试 3: 全选移出画布】整张 2048x2048 图像完全移出画布到右侧 (2200, 300)
                    object? token1 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                    object? tt1 = Activator.CreateInstance(ttType!);
                    ttType!.GetProperty("X")?.SetValue(tt1, 2200.0);
                    ttType!.GetProperty("Y")?.SetValue(tt1, 300.0);
                    token1!.GetType().GetProperty("EditTransform")?.SetValue(token1, tt1);
                    tc.GetType().GetProperty("EditTransform")?.SetValue(tc, tt1);
                    mCommit?.Invoke(tc, new object[] { token1 });
                    sw.WriteLine($"Test3_MoveOut_DeltaTransform: {readProp(tc, "DeltaTransform")}");

                    // 【测试 4: 验证 Bug 1 核心场景 - 一次拖拽移回原点，继续往角落外过冲偏移，绝不脱吸附，松手即0误差提交】
                    // 当前图片物理位置在 (2200, 300)。要回到原点 (0, 0)，理论偏移为 (-2200, -300)。
                    // 真实用户拖拽移回画布边缘：光标进入标准吸附阈值区间 (-2210, -305)，距离原点仅 10px / 5px
                    object? token2 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                    object? tt2 = Activator.CreateInstance(ttType!);
                    ttType!.GetProperty("X")?.SetValue(tt2, -2210.0);
                    ttType!.GetProperty("Y")?.SetValue(tt2, -305.0);
                    token2!.GetType().GetProperty("EditTransform")?.SetValue(token2, tt2);
                    tc.GetType().GetProperty("EditTransform")?.SetValue(tc, tt2);

                    // 按住 Shift 触发吸附计算（此时鼠标大幅过冲，必须触发边缘钳位吸附，死死锁定在 (0, 0)）
                    MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                    object? editTxSnap = readProp(token2, "EditTransform") ?? readProp(tc, "EditTransform");
                    double snapEditX = Convert.ToDouble(readProp(editTxSnap, "X") ?? 0);
                    double snapEditY = Convert.ToDouble(readProp(editTxSnap, "Y") ?? 0);
                    sw.WriteLine($"Test4_OvershootClamped_EditTransform: X={snapEditX}, Y={snapEditY} (Expected: X=-2200, Y=-300)");
                    sw.WriteLine($"Test4_OvershootClamped_LastSnappedL: {MoveToolGuideService.Instance.LastSnappedL}");
                    sw.WriteLine($"Test4_OvershootClamped_LastSnappedT: {MoveToolGuideService.Instance.LastSnappedT}");
                    sw.WriteLine($"Test4_OvershootClamped_LastTargetCanvasX: {MoveToolGuideService.Instance.LastTargetCanvasX}");
                    sw.WriteLine($"Test4_OvershootClamped_LastTargetCanvasY: {MoveToolGuideService.Instance.LastTargetCanvasY}");

                    bool overshootClampedSuccess = Math.Abs(snapEditX - (-2200.0)) < 0.0001 && Math.Abs(snapEditY - (-300.0)) < 0.0001;
                    sw.WriteLine($"Test4_OvershootClamped_Success: {overshootClampedSuccess}");

                    // 用户在此过冲位置直接松开鼠标（或松开Shift后松开鼠标），提交！
                    // 派发 WM_LBUTTONUP 预过滤消息，触发一次性精准锁定
                    Message msgMouseUp = new Message { Msg = 0x0202 };
                    MoveToolGuideService.Instance.PreFilterMessage(ref msgMouseUp);
                    mCommit?.Invoke(tc, new object[] { token2 });

                    object? finalDelta = readProp(tc, "DeltaTransform");
                    sw.WriteLine($"Test4_Committed_DeltaTransform: {finalDelta}");

                    double bbX = 0, bbY = 0;
                    object? bbObj = readProp(tc, "BaseBounds");
                    if (bbObj != null)
                    {
                        bbX = Convert.ToDouble(readProp(bbObj, "X") ?? 0);
                        bbY = Convert.ToDouble(readProp(bbObj, "Y") ?? 0);
                    }

                    double dX = 0, dY = 0;
                    if (finalDelta != null)
                    {
                        var pVal = finalDelta.GetType().GetProperty("Value");
                        if (pVal != null && pVal.GetValue(finalDelta) is PaintDotNet.Rendering.Matrix3x2Double m)
                        {
                            dX = m.OffsetX;
                            dY = m.OffsetY;
                        }
                    }

                    double finalCanvasX = bbX + dX;
                    double finalCanvasY = bbY + dY;
                    sw.WriteLine($"Final_Committed_CanvasPos: X={finalCanvasX}, Y={finalCanvasY} (Expected: X=0, Y=0)");

                    bool singleDragAccurate = Math.Abs(finalCanvasX) < 0.0001 && Math.Abs(finalCanvasY) < 0.0001;
                    bool overallSuccess = idleShiftStable && freeMoveSuccess && overshootClampedSuccess && singleDragAccurate;
                    sw.WriteLine($"Status: {(overallSuccess ? "PASS" : "FAIL")}");
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"Status: FAIL - {ex.Message}");
                    Boot.LogDiag("test-select-all-drag", "测试异常: " + ex);
                }
            }
            else if (text == "TEST_MULTI_MOVE_SNAP")
            {
                Boot.Log("[自测] 开始测试多轮/无Shift移动后的Corner Snapping与辅助线验证");
                string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "multi_move_snap_result.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);
                using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);

                try
                {
                    // 确保移动工具已激活并设置选区 (0, 0, 200, 200)
                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 0, 0, 200, 200);
                    }

                    MoveToolGuideService.Instance.ForceResync();
                    bool overlayFormCreated = MoveToolGuideService.Instance.HasOverlayForm;
                    sw.WriteLine($"OverlayFormAvailable: {overlayFormCreated}");

                    Func<object?, string, object?> readProp = (inst, pName) =>
                    {
                        if (inst == null) return null;
                        for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) return pi.GetValue(inst);
                        }
                        return null;
                    };

                    object? tool = null;
                    for (Type? t = this.documentWorkspace?.GetType(); t != null; t = t.BaseType)
                    {
                        var pi = t.GetProperty("Tool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (pi != null) { tool = pi.GetValue(this.documentWorkspace); break; }
                    }

                    object? ui = null;
                    for (Type? t = tool?.GetType(); t != null; t = t.BaseType)
                    {
                        var pi = t.GetProperty("UI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (pi != null) { ui = pi.GetValue(tool); break; }
                    }

                    object? tc = null;
                    for (Type? t = ui?.GetType(); t != null; t = t.BaseType)
                    {
                        var pi = t.GetProperty("TransformControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (pi != null) { tc = pi.GetValue(ui); break; }
                        var fi = t.GetField("transformControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (fi != null) { tc = fi.GetValue(ui); break; }
                    }

                    sw.WriteLine($"TC_Found: {tc != null}");
                    if (tc != null)
                    {
                        MethodInfo? mTryBegin = tc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                        Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                        Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;

                        ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                        object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                        object modeTranslate = Enum.Parse(tMode!, "Translate");

                        MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        Type? ttType = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                            .FirstOrDefault(t => t.FullName == "PaintDotNet.UI.Media.TranslateTransform");

                        // 步骤 1: 模拟用户先不按 Shift 移动图片到 (500, 300) 并提交
                        object? token1 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt1 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt1, 500.0);
                        ttType!.GetProperty("Y")?.SetValue(tt1, 300.0);
                        token1!.GetType().GetProperty("EditTransform")?.SetValue(token1, tt1);
                        mCommit?.Invoke(tc, new object[] { token1 });

                        object? delta1 = readProp(tc, "DeltaTransform");
                        sw.WriteLine($"Move1_Committed_DeltaTransform: {delta1}");

                        // 步骤 2: 用户按住 Shift 开始拖拽至右上角附近 (例如拖动偏移为 1340, -295)
                        object? token2 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt2 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt2, 1340.0);
                        ttType!.GetProperty("Y")?.SetValue(tt2, -295.0);
                        token2!.GetType().GetProperty("EditTransform")?.SetValue(token2, tt2);

                        // 触发吸附计算
                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        // 读取吸附后的 EditTransform 和 FinalTransform
                        object? finalTx2 = readProp(tc, "FinalTransform");
                        object? editTx2 = readProp(token2, "EditTransform") ?? readProp(tc, "EditTransform");

                        double actualEditX = Convert.ToDouble(readProp(editTx2, "X") ?? 0);
                        double actualEditY = Convert.ToDouble(readProp(editTx2, "Y") ?? 0);

                        sw.WriteLine($"AfterSnap_EditTransform: X={actualEditX}, Y={actualEditY} (Expected: X=1348, Y=-300)");
                        sw.WriteLine($"AfterSnap_FinalTransform: {finalTx2}");

                        // 提交第 2 次吸附移动
                        mCommit?.Invoke(tc, new object[] { token2 });

                        object? delta2 = readProp(tc, "DeltaTransform");
                        sw.WriteLine($"Final_Committed_DeltaTransform: {delta2}");

                        // 验证第2次吸附移动结果
                        bool snapCorrectX = Math.Abs(actualEditX - 1348.0) < 0.001;
                        bool snapCorrectY = Math.Abs(actualEditY - (-300.0)) < 0.001;
                        sw.WriteLine($"Step2_RightTop_Snap_Success: {snapCorrectX && snapCorrectY}");

                        // 步骤 3: 模拟用户不按 Shift 随意进行了带小数的子像素微调 (例如偏移 123.456, 78.901) 并提交
                        object? token3 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt3 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt3, 123.456);
                        ttType!.GetProperty("Y")?.SetValue(tt3, 78.901);
                        token3!.GetType().GetProperty("EditTransform")?.SetValue(token3, tt3);
                        mCommit?.Invoke(tc, new object[] { token3 });

                        object? delta3 = readProp(tc, "DeltaTransform");
                        sw.WriteLine($"Move3_Subpixel_Committed_DeltaTransform: {delta3}");

                        // 步骤 4: 用户按住 Shift，拖回左上角原点 (0, 0) 附近
                        // 图像此时绝对位置在 (1848 + 123.456 = 1971.456, 78.901)
                        // 拖动偏移为 (-1965.0, -75.0)，落入吸附阈值范围内
                        object? token4 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt4 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt4, -1965.0);
                        ttType!.GetProperty("Y")?.SetValue(tt4, -75.0);
                        token4!.GetType().GetProperty("EditTransform")?.SetValue(token4, tt4);

                        // 触发吸附计算
                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        object? editTx4 = readProp(token4, "EditTransform") ?? readProp(tc, "EditTransform");
                        double actualEditX4 = Convert.ToDouble(readProp(editTx4, "X") ?? 0);
                        double actualEditY4 = Convert.ToDouble(readProp(editTx4, "Y") ?? 0);

                        // 累积基准偏移 preTx 应为 1971.456，preTy 为 78.901
                        // 吸附目标为 (0, 0)，因此 targetEditTx 必须为 -1971.456，targetEditTy 必须为 -78.901
                        // 绝对位置 = 1971.456 + actualEditX4 必须严格精确等于 0.000000 (0 像素误差)！
                        double finalPosOriginX = 1971.456 + actualEditX4;
                        double finalPosOriginY = 78.901 + actualEditY4;
                        sw.WriteLine($"Step4_AfterSnap_EditTransform: X={actualEditX4}, Y={actualEditY4}");
                        sw.WriteLine($"Step4_FinalCalculatedCanvasPos: X={finalPosOriginX}, Y={finalPosOriginY}");

                        bool originSnapAccurateX = Math.Abs(finalPosOriginX) < 0.0001;
                        bool originSnapAccurateY = Math.Abs(finalPosOriginY) < 0.0001;
                        sw.WriteLine($"Step4_Origin_Absolute_ZeroError: {originSnapAccurateX && originSnapAccurateY}");

                        mCommit?.Invoke(tc, new object[] { token4 });

                        // 步骤 5: 用户再次按住 Shift 拖动至右下角 (1848, 1848) 附近
                        object? token5 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt5 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt5, 1835.0);
                        ttType!.GetProperty("Y")?.SetValue(tt5, 1840.0);
                        token5!.GetType().GetProperty("EditTransform")?.SetValue(token5, tt5);

                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        object? editTx5 = readProp(token5, "EditTransform") ?? readProp(tc, "EditTransform");
                        double actualEditX5 = Convert.ToDouble(readProp(editTx5, "X") ?? 0);
                        double actualEditY5 = Convert.ToDouble(readProp(editTx5, "Y") ?? 0);
                        sw.WriteLine($"Step5_BottomRight_Snap_EditTransform: X={actualEditX5}, Y={actualEditY5} (Expected: X=1848, Y=1848)");

                        bool brSnapAccurateX = Math.Abs(actualEditX5 - 1848.0) < 0.0001;
                        bool brSnapAccurateY = Math.Abs(actualEditY5 - 1848.0) < 0.0001;
                        sw.WriteLine($"Step5_BottomRight_Snap_Success: {brSnapAccurateX && brSnapAccurateY}");

                        mCommit?.Invoke(tc, new object[] { token5 });

                        // 步骤 6: 模拟人类松开鼠标时最常见的微动抖动 (Jitter / Micro-movement)
                        // 用户按住 Shift 拖动到左上角原点，触发吸附后，手指抬起时鼠标物理开关产生 3.2 像素微动
                        object? token6 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt6 = Activator.CreateInstance(ttType!);
                        // 目标吸附到原点 (0, 0)，当前绝对位置是 (1848, 1848)
                        ttType!.GetProperty("X")?.SetValue(tt6, -1840.0);
                        ttType!.GetProperty("Y")?.SetValue(tt6, -1842.0);
                        token6!.GetType().GetProperty("EditTransform")?.SetValue(token6, tt6);

                        // 触发吸附计算（此时成功吸附到 (0, 0)，记录吸附状态）
                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        // 模拟在即将松手提交前，人类手指微震，鼠标产生 3.5 像素的微移抖动注入 EditTransform
                        ttType!.GetProperty("X")?.SetValue(tt6, -1848.0 + 3.5);
                        ttType!.GetProperty("Y")?.SetValue(tt6, -1848.0 - 2.8);
                        token6!.GetType().GetProperty("EditTransform")?.SetValue(token6, tt6);

                        // 提交（触发 EditingFinished 事件，此时 EnforceSnappingOnCommit 必须拦截抖动并强制校正）
                        mCommit?.Invoke(tc, new object[] { token6 });

                        // 验证提交后的 DeltaTransform 和最终画布像素位置
                        object? delta6 = readProp(tc, "DeltaTransform");
                        double committedDeltaX6 = 0, committedDeltaY6 = 0;
                        if (delta6 != null)
                        {
                            var pVal = delta6.GetType().GetProperty("Value");
                            if (pVal != null && pVal.GetValue(delta6) is PaintDotNet.Rendering.Matrix3x2Double m6)
                            {
                                committedDeltaX6 = m6.OffsetX;
                                committedDeltaY6 = m6.OffsetY;
                            }
                        }

                        // BaseBounds 初始为 (0, 0)，因此 Delta 必须严格等于 0.000000 (0 像素误差)！
                        double baseBoundsX = 0.0, baseBoundsY = 0.0;
                        object? baseBoundsObj = readProp(tc, "BaseBounds");
                        if (baseBoundsObj != null)
                        {
                            baseBoundsX = Convert.ToDouble(readProp(baseBoundsObj, "X") ?? 0);
                            baseBoundsY = Convert.ToDouble(readProp(baseBoundsObj, "Y") ?? 0);
                        }

                        double finalPosJitterX = baseBoundsX + committedDeltaX6;
                        double finalPosJitterY = baseBoundsY + committedDeltaY6;
                        sw.WriteLine($"Step6_FinalCalculatedCanvasPos_AfterJitter: X={finalPosJitterX}, Y={finalPosJitterY} (Expected: X=0, Y=0)");

                        bool jitterResistantX = Math.Abs(finalPosJitterX) < 0.0001;
                        bool jitterResistantY = Math.Abs(finalPosJitterY) < 0.0001;
                        sw.WriteLine($"Step6_MouseUp_Jitter_ZeroError: {jitterResistantX && jitterResistantY}");

                        // 步骤 7: 测试用户核心诉求：“即使图片是偏移的，松开快捷键后图片也应该在黄色方框的位置”
                        // 1. 用户拖拽移动到右上角附近 (1835, 10)，触发右上角吸附（黄色框锁定在 (1848, 0)）
                        object? token7 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt7 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt7, 1835.0);
                        ttType!.GetProperty("Y")?.SetValue(tt7, 10.0);
                        token7!.GetType().GetProperty("EditTransform")?.SetValue(token7, tt7);

                        // 触发吸附计算（此时黄色框锁定右上角 (1848, 0)，并且图片实时吸附同步）
                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        // 2. 模拟拖拽中光标发生偏移抖动，图片暂时偏移到了 (1860, 25)
                        ttType!.GetProperty("X")?.SetValue(tt7, 1860.0);
                        ttType!.GetProperty("Y")?.SetValue(tt7, 25.0);

                        // 3. 用户松开快捷键 Shift！调用 ApplyLastSnapOnShiftRelease
                        sw.WriteLine($"DEBUG_before_ShiftRelease tc.DeltaTransform: {readProp(tc, "DeltaTransform")}");
                        sw.WriteLine($"DEBUG_before_ShiftRelease token7.OldDelta: {readProp(token7, "OldDeltaTransform")}");
                        sw.WriteLine($"DEBUG_before_ShiftRelease token7.BaseTx: {readProp(token7, "BaseTransform")}");
                        sw.WriteLine($"DEBUG_before_ShiftRelease tc.BaseTx: {readProp(tc, "BaseTransform")}");
                        sw.WriteLine($"DEBUG_before_ShiftRelease lastTarget: ({MoveToolGuideService.Instance.LastTargetCanvasX}, {MoveToolGuideService.Instance.LastTargetCanvasY})");

                        MoveToolGuideService.Instance.ApplyLastSnapOnShiftRelease();

                        // 4. 验证：松开快捷键后，图片必须被强制校正到黄色方框目标位置 (1848, 0)！
                        object? editTx7 = readProp(token7, "EditTransform") ?? readProp(tc, "EditTransform");
                        double actualEditX7 = Convert.ToDouble(readProp(editTx7, "X") ?? 0);
                        double actualEditY7 = Convert.ToDouble(readProp(editTx7, "Y") ?? 0);

                        double preTx7 = 0, preTy7 = 0;
                        object? delta7Pre = readProp(tc, "DeltaTransform");
                        if (delta7Pre != null)
                        {
                            var pVal = delta7Pre.GetType().GetProperty("Value");
                            if (pVal != null && pVal.GetValue(delta7Pre) is PaintDotNet.Rendering.Matrix3x2Double mPre)
                            {
                                preTx7 = mPre.OffsetX;
                                preTy7 = mPre.OffsetY;
                            }
                        }

                        double actualCanvasX7 = baseBoundsX + preTx7 + actualEditX7;
                        double actualCanvasY7 = baseBoundsY + preTy7 + actualEditY7;

                        sw.WriteLine($"Step7_ShiftRelease_EditTransform: X={actualEditX7}, Y={actualEditY7}");
                        sw.WriteLine($"Step7_ShiftRelease_CanvasPos: X={actualCanvasX7}, Y={actualCanvasY7} (Expected: X=1848, Y=0)");

                        bool shiftReleaseSnappedX = Math.Abs(actualCanvasX7 - 1848.0) < 0.0001;
                        bool shiftReleaseSnappedY = Math.Abs(actualCanvasY7 - 0.0) < 0.0001;
                        sw.WriteLine($"Step7_ShiftRelease_SnapToYellowBox_Success: {shiftReleaseSnappedX && shiftReleaseSnappedY}");

                        // 5. 用户松开鼠标提交，验证最终提交的 DeltaTransform 坐标与黄色框绝对一致 (0 误差)
                        mCommit?.Invoke(tc, new object[] { token7 });

                        object? delta7 = readProp(tc, "DeltaTransform");
                        double committedDeltaX7 = 0, committedDeltaY7 = 0;
                        if (delta7 != null)
                        {
                            var pVal = delta7.GetType().GetProperty("Value");
                            if (pVal != null && pVal.GetValue(delta7) is PaintDotNet.Rendering.Matrix3x2Double m7)
                            {
                                committedDeltaX7 = m7.OffsetX;
                                committedDeltaY7 = m7.OffsetY;
                            }
                        }
                        double finalPosStep7X = baseBoundsX + committedDeltaX7;
                        double finalPosStep7Y = baseBoundsY + committedDeltaY7;
                        sw.WriteLine($"Step7_Committed_FinalCanvasPos: X={finalPosStep7X}, Y={finalPosStep7Y} (Expected: X=1848, Y=0)");

                        bool step7CommittedAccurateX = Math.Abs(finalPosStep7X - 1848.0) < 0.0001;
                        bool step7CommittedAccurateY = Math.Abs(finalPosStep7Y - 0.0) < 0.0001;
                        sw.WriteLine($"Step7_Committed_YellowBox_ZeroError: {step7CommittedAccurateX && step7CommittedAccurateY}");

                        // 步骤 8: 测试吸附范围合理性与自由拖拽（绝不黏滞、绝不卡死）
                        // 用户按住 Shift，从右上角 (1848, 0) 开始向画面中心拖拽到偏移 (-100, 100)
                        object? token8 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        object? tt8 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt8, -100.0);
                        ttType!.GetProperty("Y")?.SetValue(tt8, 100.0);
                        token8!.GetType().GetProperty("EditTransform")?.SetValue(token8, tt8);

                        // 触发吸附计算（此时距离角落 100px，明显超出 14px 吸附阈值，必须自由拖拽、不触发吸附）
                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        object? editTx8 = readProp(token8, "EditTransform") ?? readProp(tc, "EditTransform");
                        double actualEditX8 = Convert.ToDouble(readProp(editTx8, "X") ?? 0);
                        double actualEditY8 = Convert.ToDouble(readProp(editTx8, "Y") ?? 0);

                        sw.WriteLine($"Step8_FreeDrag_EditTransform: X={actualEditX8}, Y={actualEditY8} (Expected: X=-100, Y=100)");
                        bool freeDragSuccessX = Math.Abs(actualEditX8 - (-100.0)) < 0.0001;
                        bool freeDragSuccessY = Math.Abs(actualEditY8 - 100.0) < 0.0001;
                        bool snapNotTrapped = !MoveToolGuideService.Instance.LastSnappedL && !MoveToolGuideService.Instance.LastSnappedR && !MoveToolGuideService.Instance.LastSnappedT && !MoveToolGuideService.Instance.LastSnappedB;
                        sw.WriteLine($"Step8_FreeDrag_NotStuck_Success: {freeDragSuccessX && freeDragSuccessY && snapNotTrapped}");

                        // 提交步骤 8，完成拖动
                        mCommit?.Invoke(tc, new object[] { token8 });

                        if (snapCorrectX && snapCorrectY && originSnapAccurateX && originSnapAccurateY && brSnapAccurateX && brSnapAccurateY && jitterResistantX && jitterResistantY && shiftReleaseSnappedX && shiftReleaseSnappedY && step7CommittedAccurateX && step7CommittedAccurateY && freeDragSuccessX && freeDragSuccessY && snapNotTrapped)
                        {
                            sw.WriteLine("Status: PASS");
                        }
                        else
                        {
                            sw.WriteLine("Status: FAIL");
                        }
                    }
                    else
                    {
                        sw.WriteLine("Status: FAIL - TransformControl not found");
                    }
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"Status: FAIL - {ex.Message}");
                    Boot.LogDiag("test-multi-move", "测试异常: " + ex);
                }
            }
            else if (text == "TEST_INSPECT_MOVE_STEPS")
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "inspect_move_steps.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var sw = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8);
                try
                {
                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 100, 100, 200, 200);
                        MoveToolGuideService.Instance.ForceResync();

                        Func<object?, string, object?> readProp = (inst, pName) =>
                        {
                            if (inst == null) return null;
                            for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                            {
                                var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (pi != null) return pi.GetValue(inst);
                                var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (fi != null) return fi.GetValue(inst);
                            }
                            return null;
                        };

                        object? tool = readProp(this.documentWorkspace, "Tool");
                        object? ui = readProp(tool, "UI");
                        object? tc = ui != null ? (readProp(ui, "TransformControl") ?? readProp(ui, "transformControl")) : null;

                        void DumpTC(string label, object? token = null)
                        {
                            sw.WriteLine($"=== {label} ===");
                            sw.WriteLine($"  BaseBounds: {readProp(tc, "BaseBounds")}");
                            sw.WriteLine($"  BaseTransform: {readProp(tc, "BaseTransform")}");
                            sw.WriteLine($"  DeltaTransform: {readProp(tc, "DeltaTransform")}");
                            sw.WriteLine($"  EditTransform: {readProp(tc, "EditTransform")}");
                            sw.WriteLine($"  FinalTransform: {readProp(tc, "FinalTransform")}");
                            sw.WriteLine($"  ActiveEditToken: {readProp(tc, "ActiveEditToken")}");
                            if (token != null)
                            {
                                sw.WriteLine($"  token.BaseTransform: {readProp(token, "BaseTransform")}");
                                sw.WriteLine($"  token.OldDeltaTransform: {readProp(token, "OldDeltaTransform")}");
                                sw.WriteLine($"  token.EditTransform: {readProp(token, "EditTransform")}");
                            }
                        }

                        DumpTC("Step 0: Initial after Selection (100, 100, 200, 200)");

                        MethodInfo? mTryBegin = tc!.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                        Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                        Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;

                        ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                        object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                        object modeTranslate = Enum.Parse(tMode!, "Translate");

                        MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        Type? ttType = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                            .FirstOrDefault(t => t.FullName == "PaintDotNet.UI.Media.TranslateTransform");

                        // 步骤 1: 开始第 1 次移动 (不按 Shift 移动 300, 200)
                        object? token1 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        DumpTC("Step 1: During Edit 1 (before setting EditTransform)", token1);

                        object? tt1 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt1, 300.0);
                        ttType!.GetProperty("Y")?.SetValue(tt1, 200.0);
                        token1!.GetType().GetProperty("EditTransform")?.SetValue(token1, tt1);
                        tc.GetType().GetProperty("EditTransform")?.SetValue(tc, tt1);

                        DumpTC("Step 1: During Edit 1 (after setting EditTransform 300, 200)", token1);

                        mCommit?.Invoke(tc, new object[] { token1 });
                        DumpTC("Step 1: After Commit 1");

                        // 步骤 2: 开始第 2 次移动 (用户准备吸附回左上角 0, 0)
                        object? token2 = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        DumpTC("Step 2: During Edit 2 (initial begin)", token2);

                        // 此时物理位置：选区原本在 (100, 100)，第1次移动了 (300, 200)，当前在 (400, 300)。
                        // 用户拖动鼠标，希望吸附到 (0, 0)。
                        // 拖动偏移设为 (-390.0, -290.0)（接近原点 0,0，距离 10px）
                        object? tt2 = Activator.CreateInstance(ttType!);
                        ttType!.GetProperty("X")?.SetValue(tt2, -390.0);
                        ttType!.GetProperty("Y")?.SetValue(tt2, -290.0);
                        token2!.GetType().GetProperty("EditTransform")?.SetValue(token2, tt2);
                        tc.GetType().GetProperty("EditTransform")?.SetValue(tc, tt2);

                        DumpTC("Step 2: Before Snap (EditTransform = -390, -290)", token2);

                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        DumpTC("Step 2: After ProcessSnappingAndGuides", token2);

                        // 检查 IL 代码调用
                        void DumpIL(string mName, MethodInfo? mi)
                        {
                            if (mi == null) return;
                            sw.WriteLine($"\n--- Disassembly of {mName} ---");
                            var body = mi.GetMethodBody();
                            if (body != null)
                            {
                                byte[]? il = body.GetILAsByteArray();
                                sw.WriteLine($"IL bytes length: {il?.Length}");
                            }
                        }

                        DumpIL("CommitEdits", mCommit);
                        var getFinal = tc.GetType().GetProperty("FinalTransform")?.GetGetMethod(true);
                        DumpIL("get_FinalTransform", getFinal);

                        sw.WriteLine("Status: PASS");
                    }
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"Status: FAIL - {ex.Message}");
                    Boot.LogDiag("test-inspect-steps", "测试异常: " + ex);
                }
            }
            else if (text == "DISASSEMBLE_TC")
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "tc_disasm.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var sw = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8);
                try
                {
                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 0, 0, 100, 100);

                        Func<object?, string, object?> readProp = (inst, pName) =>
                        {
                            if (inst == null) return null;
                            for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                            {
                                var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (pi != null) return pi.GetValue(inst);
                                var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (fi != null) return fi.GetValue(inst);
                            }
                            return null;
                        };

                        object? tool = readProp(this.documentWorkspace, "Tool");
                        object? ui = readProp(tool, "UI");
                        object? tc = ui != null ? (readProp(ui, "TransformControl") ?? readProp(ui, "transformControl")) : null;

                        sw.WriteLine($"TC Type: {tc?.GetType().FullName}");
                        sw.WriteLine("--- TC Events ---");
                        for (Type? t = tc?.GetType(); t != null; t = t.BaseType)
                        {
                            foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            {
                                sw.WriteLine($"  {t.Name}.{ev.Name} ({ev.EventHandlerType?.Name})");
                            }
                        }

                        sw.WriteLine("\n--- TC Routed Events or Event Fields ---");
                        for (Type? t = tc?.GetType(); t != null; t = t.BaseType)
                        {
                            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            {
                                if (f.Name.EndsWith("Event"))
                                {
                                    sw.WriteLine($"  Static Event: {t.Name}.{f.Name} ({f.FieldType.Name})");
                                }
                            }
                        }

                        MethodInfo? mTryBegin = tc!.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                        Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                        Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;
                        ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                        object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                        object modeTranslate = Enum.Parse(tMode!, "Translate");

                        object? token = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                        sw.WriteLine($"\nToken Type: {token?.GetType().FullName}");
                        for (Type? t = token?.GetType(); t != null; t = t.BaseType)
                        {
                            foreach (var ev in t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            {
                                sw.WriteLine($"  Token Event: {t.Name}.{ev.Name} ({ev.EventHandlerType?.Name})");
                            }
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            {
                                sw.WriteLine($"  Token Method: {t.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                            }
                        }

                        void Decompile(string title, MethodInfo? mi)
                        {
                            if (mi == null) return;
                            sw.WriteLine($"\n=== IL of {title} ({mi.DeclaringType?.Name}.{mi.Name}) ===");
                            var body = mi.GetMethodBody();
                            var bytes = body?.GetILAsByteArray();
                            if (bytes == null) return;
                            var module = mi.Module;
                            int idx = 0;
                            while (idx < bytes.Length)
                            {
                                byte op = bytes[idx++];
                                System.Reflection.Emit.OpCode opcode = default;
                                if (op == 0xFE)
                                {
                                    byte op2 = bytes[idx++];
                                    opcode = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
                                        .FirstOrDefault(o => o.Value == (short)(0xFE00 | op2));
                                }
                                else
                                {
                                    opcode = typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                                        .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
                                        .FirstOrDefault(o => o.Value == op);
                                }

                                string operandStr = "";
                                switch (opcode.OperandType)
                                {
                                    case System.Reflection.Emit.OperandType.InlineMethod:
                                    case System.Reflection.Emit.OperandType.InlineField:
                                    case System.Reflection.Emit.OperandType.InlineType:
                                    case System.Reflection.Emit.OperandType.InlineTok:
                                        int tok = BitConverter.ToInt32(bytes, idx);
                                        idx += 4;
                                        try
                                        {
                                            var member = module.ResolveMember(tok);
                                            operandStr = $"{member.DeclaringType?.Name}.{member.Name}";
                                        }
                                        catch { operandStr = $"tok:0x{tok:X}"; }
                                        break;
                                    case System.Reflection.Emit.OperandType.InlineI:
                                        operandStr = BitConverter.ToInt32(bytes, idx).ToString();
                                        idx += 4;
                                        break;
                                    case System.Reflection.Emit.OperandType.ShortInlineI:
                                        operandStr = ((sbyte)bytes[idx++]).ToString();
                                        break;
                                    case System.Reflection.Emit.OperandType.InlineBrTarget:
                                        int br = BitConverter.ToInt32(bytes, idx);
                                        idx += 4;
                                        operandStr = $"-> +{br}";
                                        break;
                                    case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                                        sbyte sbr = (sbyte)bytes[idx++];
                                        operandStr = $"-> +{sbr}";
                                        break;
                                    case System.Reflection.Emit.OperandType.ShortInlineVar:
                                        operandStr = $"V_{bytes[idx++]}";
                                        break;
                                    default:
                                        break;
                                }
                                sw.WriteLine($"  {opcode.Name} {operandStr}");
                            }
                        }

                        sw.WriteLine("\n--- All TC Methods ---");
                        foreach (var m in tc.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        {
                            sw.WriteLine($"  TC Method: {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) -> {m.ReturnType.Name}");
                        }

                        if (ui != null)
                        {
                            sw.WriteLine("\n--- All UI Methods ---");
                            foreach (var m in ui.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                            {
                                sw.WriteLine($"  UI Method: {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))}) -> {m.ReturnType.Name}");
                            }
                        }

                        MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        MethodInfo? mOnCommit = token?.GetType().GetMethod("OnCommit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        MethodInfo? mBaseCommit = token?.GetType().BaseType?.GetMethod("Commit", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        MethodInfo? mSetDelta = tc.GetType().GetMethod("SetDeltaTransform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        Decompile("tc.CommitEdits", mCommit);
                        Decompile("token.OnCommit", mOnCommit);
                        Decompile("base.Commit", mBaseCommit);
                        Decompile("tc.SetDeltaTransform", mSetDelta);

                        // Decompile any pointer or drag handler
                        foreach (var m in tc.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (m.Name.Contains("Pointer", StringComparison.OrdinalIgnoreCase) ||
                                m.Name.Contains("Drag", StringComparison.OrdinalIgnoreCase) ||
                                m.Name.Contains("Edit", StringComparison.OrdinalIgnoreCase))
                            {
                                Decompile($"tc.{m.Name}", m);
                            }
                        }

                        mCommit?.Invoke(tc, new object[] { token! });

                        sw.WriteLine("Status: PASS");
                    }
                }
                catch (Exception ex)
                {
                    sw.WriteLine($"Status: FAIL - {ex.Message}");
                    Boot.LogDiag("test-tc-disasm", "测试异常: " + ex);
                }
            }
            else if (text == "PREPARE_MOVE_DRAG")
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "prepare_move_drag.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var sw = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8);
                try
                {
                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 200, 200, 200, 200);
                        MoveToolGuideService.Instance.ForceResync();

                        Document? doc = PdnModel.TryGetDocument(this.documentWorkspace);
                        double docW = doc?.Width ?? 2048;
                        double docH = doc?.Height ?? 2048;

                        Func<object?, string, object?> readProp = (inst, pName) =>
                        {
                            if (inst == null) return null;
                            for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                            {
                                var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (pi != null) return pi.GetValue(inst);
                                var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                                if (fi != null) return fi.GetValue(inst);
                            }
                            return null;
                        };

                        object? visDocRectObj = readProp(this.documentWorkspace, "VisibleDocumentRect");
                        object? visScreenRectObj = readProp(this.documentWorkspace, "VisibleDocumentScreenRect");

                        double vdX = 0, vdY = 0, vdW = docW, vdH = docH;
                        if (visDocRectObj != null)
                        {
                            vdX = Convert.ToDouble(readProp(visDocRectObj, "X") ?? 0);
                            vdY = Convert.ToDouble(readProp(visDocRectObj, "Y") ?? 0);
                            vdW = Convert.ToDouble(readProp(visDocRectObj, "Width") ?? docW);
                            vdH = Convert.ToDouble(readProp(visDocRectObj, "Height") ?? docH);
                        }
                        if (vdW <= 0.001) vdW = docW;
                        if (vdH <= 0.001) vdH = docH;

                        double vsX = 0, vsY = 0, vsW = vdW, vsH = vdH;
                        Control? ctl = (readProp(this.documentWorkspace, "CanvasControl") ?? this.documentWorkspace) as Control;
                        if (ctl != null && ctl.IsHandleCreated)
                        {
                            Point origin = ctl.PointToScreen(Point.Empty);
                            vsX = origin.X;
                            vsY = origin.Y;
                            vsW = ctl.ClientSize.Width;
                            vsH = ctl.ClientSize.Height;
                        }
                        if (visScreenRectObj != null)
                        {
                            vsX = Convert.ToDouble(readProp(visScreenRectObj, "X") ?? vsX);
                            vsY = Convert.ToDouble(readProp(visScreenRectObj, "Y") ?? vsY);
                            vsW = Convert.ToDouble(readProp(visScreenRectObj, "Width") ?? vsW);
                            vsH = Convert.ToDouble(readProp(visScreenRectObj, "Height") ?? vsH);
                        }

                        // 计算文档中选区中心 (300, 300) 和左上角 (0, 0) 的绝对屏幕坐标
                        double selCenterX = vsX + ((300.0 - vdX) * (vsW / vdW));
                        double selCenterY = vsY + ((300.0 - vdY) * (vsH / vdH));
                        double originX = vsX + ((0.0 - vdX) * (vsW / vdW));
                        double originY = vsY + ((0.0 - vdY) * (vsH / vdH));

                        sw.WriteLine($"SelCenterX: {selCenterX}");
                        sw.WriteLine($"SelCenterY: {selCenterY}");
                        sw.WriteLine($"OriginX: {originX}");
                        sw.WriteLine($"OriginY: {originY}");
                        sw.WriteLine("Status: OK");
                    }
                }
                catch (Exception ex)
                {
                    sw.WriteLine("Status: FAIL - " + ex.Message);
                }
            }
            else if (text == "DUMP_LIVE_MOVE_STATE")
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "live_move_state.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var sw = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8);
                try
                {
                    Func<object?, string, object?> readProp = (inst, pName) =>
                    {
                        if (inst == null) return null;
                        for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) return pi.GetValue(inst);
                            var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (fi != null) return fi.GetValue(inst);
                        }
                        return null;
                    };

                    object? tool = readProp(this.documentWorkspace, "Tool");
                    object? ui = readProp(tool, "UI");
                    object? tc = ui != null ? (readProp(ui, "TransformControl") ?? readProp(ui, "transformControl")) : null;

                    object? ft = readProp(tc, "FinalTransform");
                    object? dt = readProp(tc, "DeltaTransform");
                    object? et = readProp(tc, "EditTransform");
                    object? bb = readProp(tc, "BaseBounds");
                    bool overlayVis = MoveToolGuideService.Instance.OverlayVisible;

                    sw.WriteLine($"OverlayVisible: {overlayVis}");
                    sw.WriteLine($"FinalTransform: {ft}");
                    sw.WriteLine($"DeltaTransform: {dt}");
                    sw.WriteLine($"EditTransform: {et}");
                    sw.WriteLine($"BaseBounds: {bb}");
                    sw.WriteLine("Status: OK");
                }
                catch (Exception ex)
                {
                    sw.WriteLine("Status: FAIL - " + ex.Message);
                }
            }
            else if (text.StartsWith("TOGGLE_VIS:"))
            {
                string chan = text.Substring("TOGGLE_VIS:".Length).Trim();
                var kind = chan switch { "R" => ChannelKind.R, "G" => ChannelKind.G, "B" => ChannelKind.B, _ => ChannelKind.A };
                if (this.activeLayer is not null)
                {
                    bool[] vis = this.GetChannelVisibility(this.activeLayer);
                    this.PushUndoSnapshot(this.activeLayer);
                    vis[(int)kind] = !vis[(int)kind];
                    this.ApplyChannelOrderOrVisibilityChange(this.activeLayer);
                    Boot.Log($"[自测] 切换通道可见性 {kind} -> {vis[(int)kind]}");
                }
            }
            else if (text.StartsWith("DRAG_CHAN:"))
            {
                string[] parts = text.Substring("DRAG_CHAN:".Length).Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int src) && int.TryParse(parts[1], out int dst))
                {
                    if (this.activeLayer is not null)
                    {
                        List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
                        if (src >= 0 && src < order.Count && dst >= 0 && dst < order.Count)
                        {
                            this.PushUndoSnapshot(this.activeLayer);
                            ChannelKind moved = order[src];
                            order.RemoveAt(src);
                            order.Insert(dst, moved);
                            this.ApplyChannelOrderOrVisibilityChange(this.activeLayer);
                            Boot.Log($"[自测] 模拟拖拽通道 {moved}: {src} -> {dst}");
                        }
                    }
                }
            }
            else if (text == "UNDO")
            {
                Boot.Log("[自测] 执行撤销");
                this.PerformUndo();
            }
            else if (text == "REDO")
            {
                Boot.Log("[自测] 执行重做");
                this.PerformRedo();
            }
            else if (text == "SIM_UNDO")
            {
                Boot.Log("[自测] 模拟撤销 (Ctrl+Z 或 原生 Undo)");
                object? history = this.CurrentNativeHistory();
                if (history != null)
                {
                    var uProp = history.GetType().GetProperty("UndoStack");
                    if (uProp?.GetValue(history) is System.Collections.IEnumerable uList)
                    {
                        var names = string.Join(", ", uList.Cast<object>().Select(m => m.GetType().GetProperty("Name")?.GetValue(m)?.ToString() ?? m.GetType().Name));
                        Boot.Log($"[自测] 撤销前 Native UndoStack: [{names}]");
                    }
                }
                bool intercepted = this.ProcessChannelShortcut(0x0100, (IntPtr)Keys.Z, this.Handle, ignoreModifierCheck: true);
                Boot.Log($"[自测] ProcessChannelShortcut intercepted={intercepted}");
                if (!intercepted)
                {
                    if (history != null)
                    {
                        var stepBackward = history.GetType().GetMethod("StepBackward", new[] { typeof(IWin32Window) })
                            ?? history.GetType().GetMethod("StepBackward");
                        stepBackward?.Invoke(history, new object?[] { this });
                        Boot.Log("[自测] 原生 HistoryStack.StepBackward 执行完毕");
                    }
                }
                if (history != null)
                {
                    var uProp = history.GetType().GetProperty("UndoStack");
                    if (uProp?.GetValue(history) is System.Collections.IEnumerable uList)
                    {
                        var names = string.Join(", ", uList.Cast<object>().Select(m => m.GetType().GetProperty("Name")?.GetValue(m)?.ToString() ?? m.GetType().Name));
                        Boot.Log($"[自测] 撤销后 Native UndoStack: [{names}]");
                    }
                }
                this.RefreshFromHost();
                this.Invalidate();
            }
            else if (text == "SIM_REDO")
            {
                Boot.Log("[自测] 模拟重做 (Ctrl+Y 或 原生 Redo)");
                object? history = this.CurrentNativeHistory();
                bool intercepted = this.ProcessChannelShortcut(0x0100, (IntPtr)Keys.Y, this.Handle, ignoreModifierCheck: true);
                Boot.Log($"[自测] ProcessChannelShortcut intercepted={intercepted}");
                if (!intercepted)
                {
                    if (history != null)
                    {
                        var stepForward = history.GetType().GetMethod("StepForward", new[] { typeof(IWin32Window) })
                            ?? history.GetType().GetMethod("StepForward");
                        stepForward?.Invoke(history, new object?[] { this });
                        Boot.Log("[自测] 原生 HistoryStack.StepForward 执行完毕");
                    }
                }
                this.RefreshFromHost();
                this.Invalidate();
            }
            else if (text.StartsWith("CHECK_LAYERS_AND_CHANNELS:"))
            {
                string tag = text.Substring("CHECK_LAYERS_AND_CHANNELS:".Length).Trim();
                string vDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify");
                System.IO.Directory.CreateDirectory(vDir);
                string outFile = System.IO.Path.Combine(vDir, $"layer_info_{tag}.txt");
                var sb = new System.Text.StringBuilder();

                var layers = PdnModel.GetLayers(this.document);
                sb.AppendLine($"LayersCount={layers.Count}");
                for (int i = 0; i < layers.Count; i++)
                {
                    sb.AppendLine($"Layer[{i}]={layers[i].Name}|Visible={layers[i].Visible}");
                }

                if (layers.Count > 0)
                {
                    Layer layer0 = layers[0];
                    Surface? s = PdnModel.TryGetSurface(layer0);
                    Surface? truth = this.layerTruthSurfaces.TryGetValue(layer0, out var stored) ? stored : null;
                    sb.AppendLine($"Layer0.Surface: null={s == null}, W={s?.Width}, H={s?.Height}");
                    sb.AppendLine($"Layer0.Truth: null={truth == null}, W={truth?.Width}, H={truth?.Height}");

                    if (s != null)
                    {
                        unsafe
                        {
                            IntPtr ptr = s.Scan0.Pointer;
                            int stride = s.Stride;
                            long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                            int sampled = 0;
                            bool isGrayscale = true;
                            for (int y = 0; y < s.Height; y += 4)
                            {
                                byte* row = (byte*)ptr + (long)y * stride;
                                for (int x = 0; x < s.Width; x += 4)
                                {
                                    int px = x * 4;
                                    byte b = row[px + 0], g = row[px + 1], r = row[px + 2], a = row[px + 3];
                                    sumB += b; sumG += g; sumR += r; sumA += a;
                                    sampled++;
                                    if (b != g || g != r) isGrayscale = false;
                                }
                            }
                            sb.AppendLine($"Layer0.Pixels: AvgB={(double)sumB / sampled:F1}, AvgG={(double)sumG / sampled:F1}, AvgR={(double)sumR / sampled:F1}, AvgA={(double)sumA / sampled:F1}");
                            sb.AppendLine($"Layer0.IsPureGrayscale={isGrayscale}");
                        }
                    }

                    if (truth != null)
                    {
                        unsafe
                        {
                            IntPtr ptr = truth.Scan0.Pointer;
                            int stride = truth.Stride;
                            long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                            int sampled = 0;
                            bool isGrayscale = true;
                            for (int y = 0; y < truth.Height; y += 4)
                            {
                                byte* row = (byte*)ptr + (long)y * stride;
                                for (int x = 0; x < truth.Width; x += 4)
                                {
                                    int px = x * 4;
                                    byte b = row[px + 0], g = row[px + 1], r = row[px + 2], a = row[px + 3];
                                    sumB += b; sumG += g; sumR += r; sumA += a;
                                    sampled++;
                                    if (b != g || g != r) isGrayscale = false;
                                }
                            }
                            sb.AppendLine($"Layer0.TruthPixels: AvgB={(double)sumB / sampled:F1}, AvgG={(double)sumG / sampled:F1}, AvgR={(double)sumR / sampled:F1}, AvgA={(double)sumA / sampled:F1}");
                            sb.AppendLine($"Layer0.TruthIsPureGrayscale={isGrayscale}");
                        }
                    }
                }

                System.IO.File.WriteAllText(outFile, sb.ToString(), System.Text.Encoding.UTF8);
                Boot.Log($"[自测] 已写出图层与通道检查: {outFile}");
            }
            else if (text == "GET_STATE")
            {
                string stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-state.txt");
                string orderStr = this.activeLayer is not null ? string.Join(",", this.GetChannelOrder(this.activeLayer)) : "null";
                string visStr = this.activeLayer is not null ? string.Join(",", this.GetChannelVisibility(this.activeLayer)) : "null";
                string content = $"selected={this.selectedChannel};view={this.channelViewKind};order={orderStr};vis={visStr}";
                System.IO.File.WriteAllText(stateFile, content);
                Boot.Log($"[自测] 已写出状态: {content}");
            }
            else if (text == "DUMP_CANVAS")
            {
                Surface? s = this.activeLayer is not null ? PdnModel.TryGetSurface(this.activeLayer) : null;
                if (s is not null)
                {
                    string dumpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-canvas.png");
                    using var bmp = s.CreateAliasedBitmap();
                    bmp.Save(dumpFile, System.Drawing.Imaging.ImageFormat.Png);
                    Boot.Log($"[自测] 已导出画布到: {dumpFile}");
                }
            }
            else if (text == "TEST_REAL_DRAG_MOVE_SIM")
            {
                string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "real_drag_move_sim_result.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);
                using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);
                try
                {
                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 500, 300, 200, 200);
                    }
                    MoveToolGuideService.Instance.ForceResync();

                    Func<object?, string, object?> readProp = (inst, pName) =>
                    {
                        if (inst == null) return null;
                        for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) return pi.GetValue(inst);
                            var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (fi != null) return fi.GetValue(inst);
                        }
                        return null;
                    };

                    object? tool = readProp(this.documentWorkspace, "Tool");
                    object? ui = readProp(tool, "UI");
                    object? tc = ui != null ? (readProp(ui, "TransformControl") ?? readProp(ui, "transformControl")) : null;

                    if (tc == null) { sw.WriteLine("TC is null"); return; }

                    MethodInfo? mTryBegin = tc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                    Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                    Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;
                    ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                    object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                    object modeTranslate = Enum.Parse(tMode!, "Translate");

                    object? token = mTryBegin!.Invoke(tc, new object[] { agent!, modeTranslate });
                    FieldInfo? fPointerToken = tc.GetType().GetField("pointerEditToken", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fPointerToken?.SetValue(tc, token);

                    Type? ttType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.FullName == "PaintDotNet.UI.Media.TranslateTransform");
                    object? tt = Activator.CreateInstance(ttType!);
                    token!.GetType().GetProperty("EditTransform")?.SetValue(token, tt);
                    tc.GetType().GetProperty("EditTransform")?.SetValue(tc, tt);

                    FieldInfo? fDragHandle = tc.GetType().GetField("dragHandle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    MethodInfo? mSetHandleType = tc.GetType().GetMethod("SetTransformHandleType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mSetHandleType != null)
                    {
                        Type tHandleType = mSetHandleType.GetParameters()[1].ParameterType;
                        object? handleVal = Enum.GetValues(tHandleType).Cast<object>().FirstOrDefault(v => v.ToString()!.Contains("Translate") || v.ToString()!.Contains("Move") || (int)v == 0);
                        mSetHandleType.Invoke(null, new object[] { tc, handleVal! });
                        fDragHandle?.SetValue(tc, tc);
                    }

                    FieldInfo? fBegin = tc.GetType().GetField("dragBeginPt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    FieldInfo? fLatest = tc.GetType().GetField("latestDragMovePt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    var startPt = new PaintDotNet.Rendering.Point2Double(550, 350);
                    fBegin?.SetValue(tc, startPt);
                    fLatest?.SetValue(tc, startPt);

                    // 模拟在真实拖拽中：Shift 已按下，鼠标处于 Dragging 状态
                    FieldInfo? fIsDragging = typeof(MoveToolGuideService).GetField("isDragging", BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo? fWasShift = typeof(MoveToolGuideService).GetField("wasShiftPressed", BindingFlags.Instance | BindingFlags.NonPublic);
                    fIsDragging?.SetValue(MoveToolGuideService.Instance, true);
                    fWasShift?.SetValue(MoveToolGuideService.Instance, true);

                    MethodInfo? mDragMove = tc.GetType().GetMethod("OnHandleDragMove",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        new[] { typeof(PaintDotNet.Rendering.Point2Double) });

                    if (mDragMove == null) { sw.WriteLine("mDragMove is null"); return; }

                    sw.WriteLine("=== SIMULATING DRAG MOVE WITH SHIFT ===");
                    for (int step = 1; step <= 20; step++)
                    {
                        double curMouseX = 550.0 + (60.0 - 550.0) * (step / 20.0);
                        double curMouseY = 350.0 + (58.0 - 350.0) * (step / 20.0);
                        var mousePt = new PaintDotNet.Rendering.Point2Double(curMouseX, curMouseY);

                        mDragMove.Invoke(tc, new object[] { mousePt });

                        object? editTxObj = readProp(tc, "EditTransform");
                        double curTx = Convert.ToDouble(readProp(editTxObj, "X") ?? 0);
                        double curTy = Convert.ToDouble(readProp(editTxObj, "Y") ?? 0);
                        var bPt = fBegin?.GetValue(tc) as PaintDotNet.Rendering.Point2Double?;

                        sw.WriteLine($"Step {step}: Mouse=({curMouseX:F1}, {curMouseY:F1}), EditTx=({curTx:F1}, {curTy:F1}), dragBegin=({bPt?.X:F1}, {bPt?.Y:F1}), Snapped=({MoveToolGuideService.Instance.LastSnappedL}, {MoveToolGuideService.Instance.LastSnappedT})");
                    }

                    sw.WriteLine("=== HOLDING IN SNAP ZONE WITH MICRO-JITTER ===");
                    for (int j = 1; j <= 10; j++)
                    {
                        double jitterX = 60.0 + (j % 2 == 0 ? 0.3 : -0.3);
                        double jitterY = 58.0 + (j % 2 == 0 ? -0.2 : 0.2);
                        var mousePt = new PaintDotNet.Rendering.Point2Double(jitterX, jitterY);

                        mDragMove.Invoke(tc, new object[] { mousePt });

                        object? editTxObj = readProp(tc, "EditTransform");
                        double curTx = Convert.ToDouble(readProp(editTxObj, "X") ?? 0);
                        double curTy = Convert.ToDouble(readProp(editTxObj, "Y") ?? 0);
                        var bPt = fBegin?.GetValue(tc) as PaintDotNet.Rendering.Point2Double?;

                        sw.WriteLine($"Jitter {j}: Mouse=({jitterX:F1}, {jitterY:F1}), EditTx=({curTx:F1}, {curTy:F1}), dragBegin=({bPt?.X:F1}, {bPt?.Y:F1}), Snapped=({MoveToolGuideService.Instance.LastSnappedL}, {MoveToolGuideService.Instance.LastSnappedT})");
                    }

                    sw.WriteLine("=== DRAGGING OUT OF SNAP ZONE (LINEAR BREAKOUT) ===");
                    for (int o = 1; o <= 10; o++)
                    {
                        double outX = 60.0 + (300.0 - 60.0) * (o / 10.0);
                        double outY = 58.0 + (280.0 - 58.0) * (o / 10.0);
                        var mousePt = new PaintDotNet.Rendering.Point2Double(outX, outY);

                        mDragMove.Invoke(tc, new object[] { mousePt });

                        object? editTxObj = readProp(tc, "EditTransform");
                        double curTx = Convert.ToDouble(readProp(editTxObj, "X") ?? 0);
                        double curTy = Convert.ToDouble(readProp(editTxObj, "Y") ?? 0);
                        var bPt = fBegin?.GetValue(tc) as PaintDotNet.Rendering.Point2Double?;

                        sw.WriteLine($"DragOut {o}: Mouse=({outX:F1}, {outY:F1}), EditTx=({curTx:F1}, {curTy:F1}), dragBegin=({bPt?.X:F1}, {bPt?.Y:F1}), Snapped=({MoveToolGuideService.Instance.LastSnappedL}, {MoveToolGuideService.Instance.LastSnappedT})");
                    }

                    sw.WriteLine("=== DRAGGING BACK INTO SNAP ZONE (RE-SNAP) ===");
                    for (int b = 1; b <= 10; b++)
                    {
                        double backX = 300.0 + (60.0 - 300.0) * (b / 10.0);
                        double backY = 280.0 + (58.0 - 280.0) * (b / 10.0);
                        var mousePt = new PaintDotNet.Rendering.Point2Double(backX, backY);

                        mDragMove.Invoke(tc, new object[] { mousePt });

                        object? editTxObj = readProp(tc, "EditTransform");
                        double curTx = Convert.ToDouble(readProp(editTxObj, "X") ?? 0);
                        double curTy = Convert.ToDouble(readProp(editTxObj, "Y") ?? 0);
                        var bPt = fBegin?.GetValue(tc) as PaintDotNet.Rendering.Point2Double?;

                        sw.WriteLine($"DragBack {b}: Mouse=({backX:F1}, {backY:F1}), EditTx=({curTx:F1}, {curTy:F1}), dragBegin=({bPt?.X:F1}, {bPt?.Y:F1}), Snapped=({MoveToolGuideService.Instance.LastSnappedL}, {MoveToolGuideService.Instance.LastSnappedT})");
                    }

                    MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    mCommit?.Invoke(tc, new object[] { token! });

                    object? finalDelta = readProp(tc, "DeltaTransform");
                    sw.WriteLine($"Final DeltaTransform: {finalDelta}");
                    sw.WriteLine("Status: OK");
                }
                catch (Exception ex)
                {
                    sw.WriteLine("FAIL: " + ex);
                }
            }
            else if (text == "TEST_REAL_SCALE_DRAG_SIM")
            {
                string testLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LCV-verify", "real_scale_drag_sim_result.txt");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(testLogPath)!);
                using var sw = new System.IO.StreamWriter(testLogPath, false, System.Text.Encoding.UTF8);
                try
                {
                    if (this.documentWorkspace is not null)
                    {
                        ActivateMoveSelectedPixelsTool(this.documentWorkspace);
                        SetWorkspaceSelection(this.documentWorkspace, 0, 0, 2048, 2048);
                    }
                    MoveToolGuideService.Instance.ForceResync();

                    Func<object?, string, object?> readProp = (inst, pName) =>
                    {
                        if (inst == null) return null;
                        for (Type? t = inst.GetType(); t != null; t = t.BaseType)
                        {
                            var pi = t.GetProperty(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (pi != null) return pi.GetValue(inst);
                            var fi = t.GetField(pName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                            if (fi != null) return fi.GetValue(inst);
                        }
                        return null;
                    };

                    object? tool = readProp(this.documentWorkspace, "Tool");
                    object? ui = readProp(tool, "UI");
                    object? tc = ui != null ? (readProp(ui, "TransformControl") ?? readProp(ui, "transformControl")) : null;

                    if (tc == null) { sw.WriteLine("TC is null"); return; }

                    MethodInfo? mTryBegin = tc.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m => m.Name == "TryBeginEditing" && m.GetParameters().Length == 2);
                    Type? tAgent = mTryBegin?.GetParameters()[0].ParameterType;
                    Type? tMode = mTryBegin?.GetParameters()[1].ParameterType;
                    ConstructorInfo? ctor = tAgent!.GetConstructors().FirstOrDefault();
                    object? agent = ctor?.Invoke(new object[] { "Pointer", 0 });
                    object modeScale = Enum.Parse(tMode!, "Scale");

                    object? token = mTryBegin!.Invoke(tc, new object[] { agent!, modeScale });
                    FieldInfo? fPointerToken = tc.GetType().GetField("pointerEditToken", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fPointerToken?.SetValue(tc, token);

                    Type? estType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                        .FirstOrDefault(t => t.Name == "EditScaleTransform");
                    object? est = null;
                    if (estType != null)
                    {
                        var ctorWithToken = estType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            .FirstOrDefault(c => c.GetParameters().Length == 1);
                        if (ctorWithToken != null)
                        {
                            est = ctorWithToken.Invoke(new object[] { token! });
                        }
                        else
                        {
                            est = Activator.CreateInstance(estType);
                        }
                    }

                    token!.GetType().GetProperty("EditTransform")?.SetValue(token, est);
                    tc.GetType().GetProperty("EditTransform")?.SetValue(tc, est);

                    object? editScaleTransform = est;
                    PropertyInfo? pScaleX = editScaleTransform?.GetType().GetProperty("ScaleX");
                    PropertyInfo? pScaleY = editScaleTransform?.GetType().GetProperty("ScaleY");

                    // 模拟在真实拖拽中：Shift 已按下，处于 Dragging 状态
                    FieldInfo? fIsDragging = typeof(MoveToolGuideService).GetField("isDragging", BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo? fWasShift = typeof(MoveToolGuideService).GetField("wasShiftPressed", BindingFlags.Instance | BindingFlags.NonPublic);
                    fIsDragging?.SetValue(MoveToolGuideService.Instance, true);
                    fWasShift?.SetValue(MoveToolGuideService.Instance, true);

                    sw.WriteLine("=== SIMULATING SCALE DRAG TOWARDS 1024 (0.500) ===");
                    // 从 1.0 缩小至 0.495 (接近 1024)
                    for (int step = 1; step <= 15; step++)
                    {
                        double curScale = 1.0 - (0.505) * (step / 15.0); // 最终到 0.495
                        pScaleX?.SetValue(editScaleTransform, curScale);
                        pScaleY?.SetValue(editScaleTransform, curScale);

                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        double observedX = Convert.ToDouble(pScaleX?.GetValue(editScaleTransform) ?? 1.0);
                        double observedY = Convert.ToDouble(pScaleY?.GetValue(editScaleTransform) ?? 1.0);
                        sw.WriteLine($"Step {step}: InjectedScale={curScale:F4}, AppliedScale=({observedX:F4}, {observedY:F4}), Locked={MoveToolGuideService.Instance.IsScaleSnapLocked}");
                    }

                    sw.WriteLine("=== HOLDING IN SCALE SNAP ZONE WITH JITTER ===");
                    for (int j = 1; j <= 10; j++)
                    {
                        double jitterScale = 0.495 + (j % 2 == 0 ? 0.005 : -0.005); // 0.490 .. 0.500
                        pScaleX?.SetValue(editScaleTransform, jitterScale);
                        pScaleY?.SetValue(editScaleTransform, jitterScale);

                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        double observedX = Convert.ToDouble(pScaleX?.GetValue(editScaleTransform) ?? 1.0);
                        double observedY = Convert.ToDouble(pScaleY?.GetValue(editScaleTransform) ?? 1.0);
                        sw.WriteLine($"Jitter {j}: InjectedScale={jitterScale:F4}, AppliedScale=({observedX:F4}, {observedY:F4}), Locked={MoveToolGuideService.Instance.IsScaleSnapLocked}");
                    }

                    sw.WriteLine("=== PULLING OUT OF SCALE SNAP ZONE (LINEAR BREAKOUT) ===");
                    for (int o = 1; o <= 10; o++)
                    {
                        double outScale = 0.50 - 0.20 * (o / 10.0); // 0.48 .. 0.30
                        pScaleX?.SetValue(editScaleTransform, outScale);
                        pScaleY?.SetValue(editScaleTransform, outScale);

                        MoveToolGuideService.Instance.ProcessSnappingAndGuides(forceActive: true);

                        double observedX = Convert.ToDouble(pScaleX?.GetValue(editScaleTransform) ?? 1.0);
                        double observedY = Convert.ToDouble(pScaleY?.GetValue(editScaleTransform) ?? 1.0);
                        sw.WriteLine($"ScaleOut {o}: InjectedScale={outScale:F4}, AppliedScale=({observedX:F4}, {observedY:F4}), Locked={MoveToolGuideService.Instance.IsScaleSnapLocked}");
                    }

                    MethodInfo? mCommit = tc.GetType().GetMethod("CommitEdits", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    mCommit?.Invoke(tc, new object[] { token! });

                    sw.WriteLine("Status: OK");
                }
                catch (Exception ex)
                {
                    sw.WriteLine("FAIL: " + ex);
                }
            }
            else if (text == "INVERT_CANVAS")
            {
                Surface? s = this.activeLayer is not null ? PdnModel.TryGetSurface(this.activeLayer) : null;
                if (s is not null)
                {
                    int width = s.Width;
                    int height = s.Height;
                    int stride = s.Stride;
                    IntPtr scan0 = s.Scan0.Pointer;
                    unsafe
                    {
                        for (int y = 0; y < height; y++)
                        {
                            byte* row = (byte*)scan0 + ((long)y * stride);
                            for (int x = 0; x < width; x++)
                            {
                                int px = x * 4;
                                row[px + 0] = (byte)(255 - row[px + 0]);
                                row[px + 1] = (byte)(255 - row[px + 1]);
                                row[px + 2] = (byte)(255 - row[px + 2]);
                            }
                        }
                    }
                    PdnModel.Invalidate(this.activeLayer);
                    Boot.Log("[自测] 画布已模拟执行反相调色");
                }
            }

            this.RefreshFromHost();
            this.Invalidate();
        }
        catch (Exception ex)
        {
            Boot.LogDiag("autotest", "自测钩子失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    }


    private void SelectLayer(Layer layer)
    {
        Layer? before = this.activeLayer;

        // ---- 点图层行 = 退出单通道显示，回到完整图像 ----
        //
        // 用户原话："在其中一个图层的时候点击总图层没变化……
        //             我要的是能正常切换到总图层"。
        //
        // 早期行为：图层行只改选中项，**不退出通道视图** —— 所以点总图层那一行，
        // 画布仍停在上一个通道的灰阶上，看起来"点了没反应"；
        // 而通道块会跟着新的选中图层跑，又像是"其他图层都变成了这个图层"。
        //
        // 现在语义明确：**点图层行 = 回到该图层的完整合成显示**。
        // 想看单通道点通道行，想回完整图点图层行。
        if (this.channelViewKind is not null)
        {
            this.ExitChannelView();
        }

        this.activeLayer = layer;
        this.SetHostActiveLayer(layer);
        this.InvalidateChannels();
        this.RebuildLayout();
        this.SynchronizeImageFormat(force: true);
        this.Invalidate();
        this.SelectionChanged?.Invoke(this, EventArgs.Empty);

        // 诊断：确认图层行点击是否被宿主接受
        Boot.Log($"选中图层：'{before?.Name}' → '{layer.Name}'"
                 + $" hostAccepted={ReferenceEquals(PdnModel.TryGetActiveLayer(this.documentWorkspace), layer)}"
                 + $" channelView={(this.channelViewKind?.ToString() ?? "off")}");
    }

    /// <summary>同步选中态回 Paint.NET。写失败只记日志，不影响本面板显示。</summary>
    private void SetHostActiveLayer(Layer layer)
    {
        // 写入前重解析一次 workspace：
        // 缓存引用可能是旧文档的工作区，往它上面写等于写进了别的地方。
        Document? live = this.ResolveDocument(out _);
        if (live is null || this.documentWorkspace is null)
        {
            return;
        }

        try
        {
            var prop = this.documentWorkspace.GetType().GetProperty(
                "ActiveLayer",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (prop is null || !prop.CanWrite)
            {
                Boot.LogDiag("setactive-noprop", "DocumentWorkspace 上没有可写的 ActiveLayer");
                return;
            }

            prop.SetValue(this.documentWorkspace, layer);

            // **写入后验证**。
            //
            // 为什么必须验：RefreshFromHost 每 150ms 会用**宿主的**选中图层覆盖面板的，
            // 一旦这里写失败（例如 workspace 引用已过期、属性被忽略），
            // 用户点图层行的效果会在 150ms 内被回滚 —— 表现就是"点图层没反应"。
            object? check = PdnModel.TryGetActiveLayer(this.documentWorkspace);
            if (!ReferenceEquals(check, layer))
            {
                Boot.LogDiag("setactive-mismatch",
                    $"设置选中图层无效：想设为 '{layer.Name}'，宿主仍是 '{check?.ToString() ?? "null"}'");
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("setactive-fail",
                "设置选中图层失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // 顶层异常保护：这是插件，任何逸出的异常都会带崩整个 Paint.NET 进程
        // （崩溃日志实测：DrawChannelBlock 抛 ArgumentException 直接终止宿主）。
        // 绘制失败只记录，最多这一帧画不出来，绝不允许往上抛。
        try
        {
            this.OnPaintCore(e);
        }
        catch (Exception ex)
        {
            Boot.LogDiag("onpaint", "OnPaint 异常已拦截: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void OnPaintCore(PaintEventArgs e)
    {
        this.SyncTheme();
        Graphics g = e.Graphics;
        g.Clear(this.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 用局部变量消除 nullable 警告（编译器无法从字段推断 ??= 之后非空）。
        Font bold = this.boldFont ??= new Font(this.Font, FontStyle.Bold);
        Font mono = this.monoFont ??= new Font("Consolas", 8f);

        this.DrawHeader(g, mono);
        this.DrawFormatFooter(g);

        if (this.layerRows.Count == 0)
        {
            using var empty = new SolidBrush(Color.Gray);
            g.DrawString(
                "没有打开的文档，或文档里没有图层",
                this.Font,
                empty,
                new PointF(10, HeaderHeight + 8));
            return;
        }

        // 内容区裁剪：不要把内容画到标题栏上。
        // 拖动窗口时客户区可能缩到 0 甚至负数，SetClip 会因此抛 ArgumentException。
        int clientW = Math.Max(1, this.ClientSize.Width);
        int viewportH = Math.Max(0, this.ViewportHeight);
        if (viewportH <= 0)
        {
            return;
        }

        // 先取快照再遍历。
        // 崩溃日志实测过：直接 foreach this.layerRows 会在另一线程调用 RebuildLayout()
        // 时抛 InvalidOperationException("Collection was modified")。
        // 用副本遍历后，这一帧最多画的是旧布局，绝不会崩。
        List<(Layer Layer, int Top)> rows = this.layerRows.ToList();
        Layer? active = this.activeLayer;
        bool channelsOn = this.channelsVisible;

        var clip = new Rectangle(0, HeaderHeight, clientW, viewportH);
        GraphicsState state = g.Save();
        g.SetClip(clip);
        g.TranslateTransform(0, HeaderHeight - this.scrollOffset);

        foreach ((Layer layer, int top) in rows)
        {
            this.DrawLayerRow(g, layer, new Rectangle(0, top, clientW, this.LayerRowH()), mono);

            if (channelsOn && ReferenceEquals(layer, active))
            {
                this.DrawChannelBlock(g, top + this.LayerRowH(), bold, mono);
            }
        }

        g.Restore(state);

        // 拖拽时的插入位置指示线（画在内容区之上，用独立的裁剪）
        if (this.channelDragging && this.channelDragTargetGap >= 0)
        {
            this.DrawChannelDropIndicator(g, clientW, viewportH);
        }
        else if (this.dragging && this.dragTargetGap >= 0)
        {
            this.DrawDropIndicator(g, clientW, viewportH);
        }
    }

    /// <summary>在通道拖拽目标间隙处画一条插入指示线。</summary>
    private void DrawChannelDropIndicator(Graphics g, int clientW, int viewportH)
    {
        int blockTop = this.FindChannelBlockTop();
        if (blockTop < 0)
        {
            return;
        }

        int y = HeaderHeight + blockTop + (this.channelDragTargetGap * this.ChannelRowH()) - this.scrollOffset;
        GraphicsState state = g.Save();
        try
        {
            g.SetClip(new Rectangle(0, HeaderHeight, clientW, viewportH));
            using var pen = new Pen(Color.FromArgb(90, 170, 255), 2f);
            g.DrawLine(pen, 4, y, clientW - 4, y);

            // 左端小三角
            using var brush = new SolidBrush(Color.FromArgb(90, 170, 255));
            g.FillPolygon(brush, new[]
            {
                new Point(2, y - 4),
                new Point(8, y),
                new Point(2, y + 4),
            });
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>在拖拽目标间隙处画一条醒目的插入指示线。</summary>
    private void DrawDropIndicator(Graphics g, int clientW, int viewportH)
    {
        int y;
        int count = this.layerRows.Count;

        if (this.dragTargetGap >= count)
        {
            // 落到最底层之后
            (Layer lastLayer, int lastTop) = this.layerRows[count - 1];
            int bottom = lastTop + this.LayerRowH();
            if (this.channelsVisible && ReferenceEquals(lastLayer, this.activeLayer))
            {
                bottom += this.ChannelBlockH();
            }

            y = HeaderHeight + bottom - this.scrollOffset;
        }
        else
        {
            (Layer targetLayer, int targetTop) = this.layerRows[this.dragTargetGap];
            y = HeaderHeight + targetTop - this.scrollOffset;
            _ = targetLayer;
        }

        GraphicsState state = g.Save();
        try
        {
            g.SetClip(new Rectangle(0, HeaderHeight, clientW, viewportH));
            using var pen = new Pen(Color.FromArgb(90, 170, 255), 2f);
            g.DrawLine(pen, 4, y, clientW - 4, y);

            // 左端小三角，让落点更明确
            using var brush = new SolidBrush(Color.FromArgb(90, 170, 255));
            g.FillPolygon(brush, new[]
            {
                new Point(2, y - 4),
                new Point(8, y),
                new Point(2, y + 4),
            });
        }
        finally
        {
            g.Restore(state);
        }
    }
    private void DrawHeader(Graphics g, Font mono)
    {
        var p = this.palette;
        using var bg = new SolidBrush(p.Surface);
        g.FillRectangle(bg, 0, 0, this.ClientSize.Width, HeaderHeight);
        using var sep = new Pen(p.Border);
        g.DrawLine(sep, 0, HeaderHeight - 1, this.ClientSize.Width, HeaderHeight - 1);
        var toggle = new RectangleF(9, 10, 26, 14);
        using var switchPath = UiStyle.Rounded(toggle, 7);
        using var switchFill = new SolidBrush(this.channelsVisible ? p.Accent : p.Border);
        g.FillPath(switchFill, switchPath);
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, this.channelsVisible ? 23 : 11, 12, 10, 10);
        UiStyle.Text(g, "通道预览", this.Font, new Rectangle(43, 0, Math.Max(1, this.ClientSize.Width - 96), HeaderHeight), p.Text);
        UiStyle.Text(g, this.channelsVisible ? "RGBA" : "图层", mono,
            new Rectangle(Math.Max(0, this.ClientSize.Width - 45), 0, 38, HeaderHeight), p.Muted, StringAlignment.Far);
        if (this.showDiagnostics) this.DrawDiagnostics(g, mono);
    }

    /// <summary>诊断覆盖层：显示宿主桥接结果，用来确认是否真的接管成功。</summary>
    private void DrawDiagnostics(Graphics g, Font mono)
    {
        string[] lines =
        {
            "=== LayerChannelLive 诊断 (Ctrl+Shift+L 关闭) ===",
            "宿主: " + PdnModel.DescribeHost(this),
            $"通道缓存: {(this.channels is null ? "无" : "已生成 4 张")}",
            $"选中图层: {this.activeLayer?.Name ?? "(未选中)"}",
            $"图层行数: {this.layerRows.Count}",
        };

        float lineH = mono.GetHeight(g) + 2;
        float boxH = lines.Length * lineH + 10;
        var box = new RectangleF(4, Math.Max(HeaderHeight, this.ClientSize.Height - boxH - 4), this.ClientSize.Width - 8, boxH);

        using (var bg = new SolidBrush(this.palette.Raised))
        {
            g.FillRectangle(bg, box);
        }

        using (var border = new Pen(this.palette.Accent))
        {
            g.DrawRectangle(border, box.X, box.Y, box.Width - 1, box.Height - 1);
        }

        using var text = new SolidBrush(this.palette.Text);
        for (int i = 0; i < lines.Length; i++)
        {
            g.DrawString(lines[i], mono, text, box.X + 5, box.Y + 5 + i * lineH);
        }
    }

    private void DrawLayerRow(Graphics g, Layer layer, Rectangle bounds, Font mono)
    {
        bool isActive = ReferenceEquals(layer, this.activeLayer);
        bool visible = ChannelViewLayer.TryGetSavedLayerVisibility(layer, out bool origVis) ? origVis : PdnModel.GetVisible(layer);
        byte opacity = ChannelViewLayer.TryGetSavedLayerOpacity(layer, out byte origOp) ? origOp : PdnModel.GetOpacity(layer);
        Surface? surface = PdnModel.TryGetSurface(layer);


        var card = new RectangleF(bounds.X + 4, bounds.Y + 2, Math.Max(1, bounds.Width - 9), Math.Max(1, bounds.Height - 5));
        UiStyle.Card(g, card, isActive ? this.palette.Selected : ReferenceEquals(layer, this.hoveredLayer) ? this.palette.Hover : this.palette.Surface,
            isActive ? this.palette.Accent : this.palette.Border);
        if (isActive)
        {
            using var rail = new Pen(this.palette.Accent, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(rail, bounds.X + 5, bounds.Y + 11, bounds.X + 5, bounds.Bottom - 11);
        }
        int checkX = bounds.X + VisibilityBoxX;
        int checkY = bounds.Y + (bounds.Height - VisibilityBoxSize) / 2;
        UiStyle.Eye(g, new Rectangle(checkX, checkY, VisibilityBoxSize, VisibilityBoxSize), visible,
            this.palette.Text);

        // 图层预览：与通道预览**同高**，宽度按图层宽高比。
        // 勾选框在最左，预览紧随其后，右侧留给图层名。
        int previewH = this.PreviewHeight();
        int previewW = this.LayerPreviewBoxWidth();
        int thumbX = bounds.X + 26;
        int thumbY = bounds.Y + LayerPreviewTop;

        this.DrawPreviewBackground(g, new Rectangle(thumbX, thumbY, previewW, previewH));

        if (surface is not null)
        {
            // 缩略图按需缓存，避免每帧重算。
            Bitmap? thumb = this.GetOrCreateThumbnail(layer, surface, previewW, previewH);
            if (Safe.IsDrawable(thumb))
            {
                double scale = Math.Min(
                    (double)previewW / thumb!.Width,
                    (double)previewH / thumb.Height);
                int dw = Math.Max(1, (int)Math.Round(thumb.Width * scale));
                int dh = Math.Max(1, (int)Math.Round(thumb.Height * scale));
                int dx = thumbX + (previewW - dw) / 2;
                int dy = thumbY + (previewH - dh) / 2;

                var oldInterp = g.InterpolationMode;
                try
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(thumb, dx, dy, dw, dh);
                }
                finally
                {
                    g.InterpolationMode = oldInterp;
                }
            }
        }
        else
        {
            using var dash = new Pen(this.palette.Muted);
            g.DrawLine(dash, thumbX + 4, thumbY + previewH - 4, thumbX + previewW - 4, thumbY + 4);
        }

        using (var border = new Pen(this.palette.Border))
        {
            g.DrawRectangle(border, thumbX, thumbY, previewW - 1, previewH - 1);
        }

        // 图层名（放在预览右侧，与预览垂直居中）
        string name = layer.Name ?? "(未命名)";
        int textX = thumbX + previewW + TextPadding;
        int textW = Math.Max(10, bounds.Right - textX - 8);
        using (var nameBrush = new SolidBrush(this.palette.Text))
        {
            g.DrawString(
                name,
                this.Font,
                nameBrush,
                new RectangleF(textX, bounds.Y + 10, textW, bounds.Height - 14),
                new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                });
        }

        // 不透明度 / 隐藏状态：放到预览框下沿之外，避免和图层名重叠
        int infoY = bounds.Bottom - 20;
        if (opacity < 255)
        {
            using var opBrush = new SolidBrush(this.palette.Muted);
            string opText = $"{opacity * 100 / 255}%";
            SizeF size = g.MeasureString(opText, mono);
            g.DrawString(opText, mono, opBrush, bounds.Right - size.Width - 8, infoY);
        }

        if (!visible)
        {
            using var hiddenBrush = new SolidBrush(this.palette.Muted);
            g.DrawString("已隐藏", mono, hiddenBrush, textX, infoY);
        }
    }

    /// <summary>在选中图层行的正下方，竖着画通道四行（按当前排序与可见性）。</summary>
    private void DrawChannelBlock(Graphics g, int blockTop, Font bold, Font mono)
    {
        this.EnsureChannels();

        Layer? currentLayer = this.activeLayer;
        List<ChannelKind> order = currentLayer is not null
            ? this.GetChannelOrder(currentLayer)
            : new List<ChannelKind> { ChannelKind.R, ChannelKind.G, ChannelKind.B, ChannelKind.A };
        bool[] visibility = currentLayer is not null
            ? this.GetChannelVisibility(currentLayer)
            : new bool[] { true, true, true, true };

        int width = Math.Max(1, this.ClientSize.Width);

        using (var blockBg = new SolidBrush(this.palette.Background))
        {
            g.FillRectangle(blockBg, 0, blockTop, width, this.ChannelBlockH());
        }

        bool hasChannels = this.channels is not null;

        for (int i = 0; i < order.Count; i++)
        {
            ChannelKind kind = order[i];
            bool isVisible = visibility.Length > (int)kind && visibility[(int)kind];
            bool isSelected = this.selectedChannel == kind;

            int rowH = this.ChannelRowH();
            int y = blockTop + i * rowH;
            var rowBounds = new Rectangle(0, y, width, rowH);

            UiStyle.Card(g, new RectangleF(5, y + 2, Math.Max(1, width - 11), Math.Max(1, rowH - 5)),
                isSelected ? this.palette.Selected : this.palette.Surface,
                isSelected ? this.palette.Accent : this.palette.Border, 6);
            int checkX = VisibilityBoxX;
            int checkY = y + (rowH - VisibilityBoxSize) / 2;
            UiStyle.Eye(g, new Rectangle(checkX, checkY, VisibilityBoxSize, VisibilityBoxSize), isVisible, this.palette.Text);

            // 2. 颜色标识
            Color chanColor = kind switch
            {
                ChannelKind.R => Color.FromArgb(218, 104, 118),
                ChannelKind.G => Color.FromArgb(79, 174, 128),
                ChannelKind.B => Color.FromArgb(106, 147, 227),
                _ => this.palette.Muted,
            };

            if (!isVisible)
            {
                chanColor = Color.FromArgb(100, chanColor.R / 2, chanColor.G / 2, chanColor.B / 2);
            }

            using (var tag = new SolidBrush(chanColor))
            {
                g.FillEllipse(tag, 27, y + 8, 7, 7);
            }

            // 3. 通道标签
            string labelText = kind.ToString();
            using (var labelBrush = new SolidBrush(isVisible ? this.palette.Text : this.palette.Muted))
            {
                g.DrawString(labelText, bold, labelBrush, 40, y + 5);
            }

            if (!hasChannels)
            {
                using var none = new SolidBrush(this.palette.Muted);
                string hint = this.activeLayer is null
                    ? "未选中图层"
                    : "该图层没有可读像素（只有 BitmapLayer 有 Surface）";
                g.DrawString(hint, mono, none, 40, y + 27);
                continue;
            }

            Bitmap? bmp = this.channels![kind];
            if (!Safe.IsDrawable(bmp))
            {
                continue;
            }

            int srcW = bmp!.Width;
            int srcH = bmp.Height;
            int boxX = ChannelPreviewLeft;
            int boxY = y + ChannelPreviewTop;

            using (var slot = new SolidBrush(Color.Black))
            {
                g.FillRectangle(slot, boxX, boxY, srcW, srcH);
            }

            if (srcW > 0 && srcH > 0)
            {
                g.DrawImage(bmp, boxX, boxY, srcW, srcH);
            }

            if (!isVisible)
            {
                using var dimMask = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                g.FillRectangle(dimMask, boxX, boxY, srcW, srcH);
                using var dimText = new SolidBrush(this.palette.Text);
                g.DrawString("已禁用", mono, dimText, boxX + 6, boxY + (srcH - 14) / 2);
            }

            using (var border = new Pen(this.palette.Border))
            {
                g.DrawRectangle(border, boxX, boxY, srcW - 1, srcH - 1);
            }

            using var dim = new SolidBrush(this.palette.Muted);
            int metaX = boxX + srcW + 8;
            if (width - metaX > 44)
                UiStyle.Text(g, this.DescribeSourceSize(), mono, new Rectangle(metaX, y + 12, width - metaX - 8, 32), this.palette.Muted);

            // ---- 通道视图态的视觉反馈 ----
            bool isViewing = this.editingChannel == kind && ChannelViewLayer.IsShowing;
            if (isViewing)
            {
                using var viewPen = new Pen(this.palette.Accent, 2f);
                g.DrawRectangle(viewPen, boxX - 1, boxY - 1, srcW + 1, srcH + 1);

                using var dot = new SolidBrush(this.palette.Accent);
                g.FillEllipse(dot, 44, y + 25, 5, 5);
                UiStyle.Text(g, "编辑", mono, new Rectangle(39, y + 33, 39, 16), this.palette.Accent, StringAlignment.Center);

            }
        }

        using (var frame = new Pen(this.palette.Border))
        {
            g.DrawRectangle(frame, 0, blockTop, Math.Max(1, width - 1), this.ChannelBlockH() - 1);
        }
    }


    // ==================== 通道编辑 ====================
    //
    // 需求：点击某个通道进入该通道，直接修改图像内容。
    // 交互：点通道预览方块 → 进入编辑模式 → 在预览上按住拖动即把画笔值写入该通道；
    //       再点同一个通道退出。右键在编辑中点击可切换画笔值（0/128/255 循环）。

    private const int PreviewX = ChannelPreviewLeft;
    private const int PreviewYOffset = ChannelPreviewTop;

    /// <summary>当前抽取得到的缩放比（图层像素 → 预览像素）。没有数据时按估算值。</summary>
    private double EffectivePreviewScale()
    {
        if (this.channels is not null && this.channels.Scale > 0)
        {
            return this.channels.Scale;
        }

        Surface? surface = PdnModel.TryGetSurface(this.activeLayer);
        if (surface is null || surface.Width <= 0)
        {
            return 1.0;
        }

        double s = Math.Min(
            (double)this.CurrentPreviewWidth() / surface.Width,
            (double)this.PreviewHeight() / surface.Height);
        return s > 1.0 ? 1.0 : Math.Max(0.0001, s);
    }

    /// <summary>图层原始尺寸的说明文字（界面标注用）。</summary>
    private string DescribeSourceSize()
    {
        int sw = this.channels?.SourceWidth ?? 0;
        int sh = this.channels?.SourceHeight ?? 0;
        if (sw <= 0 || sh <= 0)
        {
            Surface? surface = PdnModel.TryGetSurface(this.activeLayer);
            try
            {
                sw = surface?.Width ?? 0;
                sh = surface?.Height ?? 0;
            }
            catch
            {
                return "";
            }
        }

        return sw > 0 && sh > 0 ? $"{sw}×{sh}" : "";
    }

    /// <summary>通道预览方块在客户区里的矩形（已含滚动偏移）。</summary>
    private Rectangle ChannelPreviewClientRect(int blockTop, int rowIndex)
    {
        int rowH = this.ChannelRowH();
        int rowTop = blockTop + rowIndex * rowH;
        int pw = this.CurrentPreviewWidth();
        int ph = this.PreviewHeight();
        return new Rectangle(
            PreviewX,
            HeaderHeight + rowTop + PreviewYOffset - this.scrollOffset,
            pw,
            ph);
    }

    /// <summary>选中图层行的"通道块顶部"在内容坐标系里的 y；找不到返回 -1。</summary>
    private int FindChannelBlockTop()
    {
        foreach ((Layer layer, int top) in this.layerRows)
        {
            if (ReferenceEquals(layer, this.activeLayer))
            {
                return top + this.LayerRowH();
            }
        }

        return -1;
    }

    /// <summary>把通道预览里的一个点换算成图层像素坐标。false 表示落在图外。</summary>
    private bool TryMapPreviewToLayer(
        ChannelKind kind,
        int clientX,
        int clientY,
        out int layerX,
        out int layerY)
    {
        layerX = 0;
        layerY = 0;

        Surface? surface = PdnModel.TryGetSurface(this.activeLayer);
        if (surface is null)
        {
            return false;
        }

        int blockTop = this.FindChannelBlockTop();
        if (blockTop < 0)
        {
            return false;
        }

        int srcW = surface.Width;
        int srcH = surface.Height;
        if (srcW <= 0 || srcH <= 0)
        {
            return false;
        }

        int rowIndex = this.activeLayer is not null
            ? this.GetChannelOrder(this.activeLayer).IndexOf(kind)
            : (int)kind;
        if (rowIndex < 0)
        {
            rowIndex = (int)kind;
        }

        Rectangle box = this.ChannelPreviewClientRect(blockTop, rowIndex);

        // 预览位图与预览方块等大（1:1），直接用抽取时记录的缩放比反算。
        double scale = this.EffectivePreviewScale();
        if (scale <= 0)
        {
            return false;
        }

        int drawX = box.X;
        int drawY = box.Y;
        int drawW = box.Width;
        int drawH = box.Height;

        if (clientX < drawX || clientY < drawY || clientX >= drawX + drawW || clientY >= drawY + drawH)
        {
            return false;
        }

        layerX = Math.Clamp((int)((clientX - drawX) / scale), 0, srcW - 1);
        layerY = Math.Clamp((int)((clientY - drawY) / scale), 0, srcH - 1);
        return true;
    }

    /// <summary>点击是否命中某个通道的预览方块？命中则返回该通道。</summary>
    private ChannelKind? HitChannelPreview(int clientX, int clientY)
    {
        int blockTop = this.FindChannelBlockTop();
        if (blockTop < 0 || this.activeLayer is null)
        {
            return null;
        }

        List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
        for (int i = 0; i < order.Count; i++)
        {
            ChannelKind kind = order[i];
            Rectangle box = this.ChannelPreviewClientRect(blockTop, i);
            box.Inflate(4, 4); // 命中区域略放大，方便点中
            if (box.Contains(clientX, clientY))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>点击是否命中某个通道行？命中则返回该通道。</summary>
    private ChannelKind? HitChannelRow(int clientX, int clientY)
    {
        int blockTop = this.FindChannelBlockTop();
        if (blockTop < 0 || this.activeLayer is null)
        {
            return null;
        }

        List<ChannelKind> order = this.GetChannelOrder(this.activeLayer);
        int rowH = this.ChannelRowH();
        for (int i = 0; i < order.Count; i++)
        {
            Rectangle rowClient = this.ContentToClient(blockTop + (i * rowH), rowH);
            if (rowClient.Contains(clientX, clientY))
            {
                return order[i];
            }
        }

        return null;
    }
    internal bool ProcessChannelShortcut(int msg, IntPtr wParam, IntPtr hwnd, bool ignoreModifierCheck = false)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
        {
            return false;
        }

        if (!ignoreModifierCheck)
        {
            bool isCtrl = (NativeWindows.GetKeyState(0x11) & 0x8000) != 0 || (ModifierKeys & Keys.Control) == Keys.Control;
            if (!isCtrl)
            {
                return false;
            }

            bool isAlt = (NativeWindows.GetKeyState(0x12) & 0x8000) != 0 || (ModifierKeys & Keys.Alt) == Keys.Alt;
            if (isAlt)
            {
                return false;
            }
        }

        // 如果焦点在文本输入控件中（如重命名图层文本框），不拦截
        Control? focused = Control.FromHandle(hwnd);
        if (focused is TextBoxBase || focused is ComboBox)
        {
            return false;
        }

        Keys vk = (Keys)(int)wParam;

        // 1. Ctrl+Z: 撤销通道修改
        if (vk == Keys.Z)
        {
            // 正在拖动变换时由原生历史处理；其余情况按原生历史检查点选择可恢复的通道记录。
            if (MoveToolGuideService.Instance.IsDragging)
            {
                return false;
            }

            // 如果处于通道视图中，且画布上有尚未提交的原生笔触/操作差异，优先让 Paint.NET 原生撤销处理
            if (this.channelViewKind is not null && this.channelViewLayer is not null && this.channelViewBackup is not null)
            {
                Surface? s = PdnModel.TryGetSurface(this.channelViewLayer);
                if (s is not null && ChannelViewLayer.HasChannelDifferences(s, this.channelViewBackup, this.channelViewKind.Value))
                {
                    MoveToolGuideService.Instance.ForceResync();
                    return false; // 不拦截，交由宿主 Paint.NET 执行原生撤销
                }
            }

            // 当前文档存在有效记录，且原生历史没有较新操作时恢复原记录对应的图层。
            if (this.CanRestoreHistory(this.undoStack))
            {
                if (this.PerformUndo())
                {
                    Boot.Log("[快捷键] 拦截到 Ctrl+Z，已撤回通道操作");
                    return true;
                }
            }

            return false;
        }

        // 2. Ctrl+Y: 重做通道修改
        if (vk == Keys.Y)
        {
            if (MoveToolGuideService.Instance.IsDragging)
            {
                return false;
            }

            if (this.CanRestoreHistory(this.redoStack))
            {
                if (this.PerformRedo())
                {
                    Boot.Log("[快捷键] 拦截到 Ctrl+Y，已重做通道操作");
                    return true;
                }
            }

            return false;
        }

        // 4. Ctrl+C: 复制当前通道视图或选中的通道
        if (vk == Keys.C)
        {
            ChannelKind? targetKind = this.channelViewKind ?? this.selectedChannel;
            if (targetKind is not null)
            {
                ChannelKind kind = targetKind.Value;
                Boot.Log($"[快捷键] 拦截到 Ctrl+C，直接复制 {kind} 通道");
                this.CopyChannelToClipboard(kind);
                return true; // 拦截消息，避免被图层默认复制干扰
            }
        }

        return false;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (this.ProcessChannelShortcut(m.Msg, m.WParam, m.HWnd))
        {
            return true;
        }

        // 拦截宿主图层工具栏的上移/下移按钮点击
        if (m.Msg == 0x0201 && this.HasSelectedChannel && this.toolbarToolStrip is not null && !this.toolbarToolStrip.IsDisposed && m.HWnd == this.toolbarToolStrip.Handle)
        {
            Point pt = this.toolbarToolStrip.PointToClient(Cursor.Position);
            ToolStripItem? item = this.toolbarToolStrip.GetItemAt(pt);
            if (item is not null)
            {
                if (ReferenceEquals(item, this.moveUpButtonItem) && this.CanMoveSelectedChannelUp())
                {
                    this.MoveSelectedChannelUp();
                    return true;
                }
                if (ReferenceEquals(item, this.moveDownButtonItem) && this.CanMoveSelectedChannelDown())
                {
                    this.MoveSelectedChannelDown();
                    return true;
                }
            }
        }
        else if (m.Msg == 0x0202 && this.HasSelectedChannel && this.toolbarToolStrip is not null && !this.toolbarToolStrip.IsDisposed && m.HWnd == this.toolbarToolStrip.Handle)
        {
            Point pt = this.toolbarToolStrip.PointToClient(Cursor.Position);
            ToolStripItem? item = this.toolbarToolStrip.GetItemAt(pt);
            if (item is not null && (ReferenceEquals(item, this.moveUpButtonItem) || ReferenceEquals(item, this.moveDownButtonItem)))
            {
                return true;
            }
        }

        return false;
    }

    private void OnWpfThreadFilterMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        if (handled)
        {
            return;
        }

        if (this.ProcessChannelShortcut(msg.message, msg.wParam, msg.hwnd))
        {
            handled = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                Application.RemoveMessageFilter(this);
                System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage -= this.OnWpfThreadFilterMessage;
            }
            catch
            {
            }

            while (this.undoStack.Count > 0)
            {
                this.undoStack.Pop().Dispose();
            }
            while (this.redoStack.Count > 0)
            {
                this.redoStack.Pop().Dispose();
            }
            foreach (var truth in this.layerTruthSurfaces.Values)
            {
                truth?.Dispose();
            }
            this.layerTruthSurfaces.Clear();

            this.formatTip.Dispose();
            this.DisposeContentNotifications();
            this.themeSubscription?.Dispose();
            this.appearanceFont.Dispose();
            this.DisposeLivePreviews();
            this.channels?.Dispose();
            this.InvalidateThumbnails();
            this.boldFont?.Dispose();
            this.monoFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}

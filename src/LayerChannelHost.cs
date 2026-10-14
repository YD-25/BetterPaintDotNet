using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows.Forms;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>
/// 把 LayerChannelPanel 装进 Paint.NET 的原生图层窗口。
///
/// ================== 线程模型（血泪教训，别改） ==================
///
/// **绝对不要调用 <c>Control.FromHandle</c>。**
///
/// 它按"调用线程"查 WinForms 的全局注册表；从错误的线程调用会为该句柄创建一个
/// 绑定到错误线程的托管包装，污染注册表。后果是宿主自己遍历控件树时
/// （<c>ApplicationUtil.FindFocus</c>）撞上这些包装，抛
/// "Cross-thread operation not valid: Control '' accessed from a thread other than
/// the thread it was created on"，把 Paint.NET 整个带崩。
///
/// 实测两次崩溃分别落在 <c>Control.get_Handle()</c> 与 <c>Control.get_Focused()</c>，
/// 且**进程启动 7 秒内即崩** —— 因为污染在加载那一刻就产生了，与用户操作无关。
///
/// 正确做法只有两条：
///   1. 用 <c>Application.OpenForms</c> 找 LayersForm —— 它**按线程隔离**，
///      只有真正的 UI 线程才看得见（观察器线程看到的是空集合，实测已确认）；
///   2. 进入安装前用 <c>InvokeRequired</c> 做硬门禁：拿到的窗体若不属于当前线程，
///      立刻放弃这一轮，绝不在错误线程上碰控件树。
///
/// 接管成功的唯一标志：LayersForm.InvokeRequired == false。
/// </summary>
internal sealed class LayerChannelHost : IDisposable
{
    private const string LayersFormTypeName = "PaintDotNet.Dialogs.LayersForm";

    private static LayerChannelHost? current;

    private System.Windows.Forms.Timer? syncTimer;
    private Control? layersStrip;
    private Control? layersScrollBar;
    private Form? layersForm;
    private ToolStrip? toolStrip;
    private ToolStripButton? moveUpButton;
    private ToolStripButton? moveDownButton;
    private object? documentWorkspace;
    private LayerChannelPanel? panel;
    private Layer? lastSeenHostLayer;
    private int lastLayerCount = -1;
    private bool disposed;
    private readonly HashSet<Form> hookedForms = new();

    private LayerChannelHost(Form layersForm)
    {
        this.layersForm = layersForm;
    }

    /// <summary>当前活动的宿主实例（未接管时为 null）。</summary>
    public static LayerChannelHost? Current => current;

    /// <summary>当前挂载的面板（未接管时为 null）。</summary>
    public LayerChannelPanel? Panel => this.panel;

    /// <summary>
    /// 幂等安装。返回 null 表示这一轮没能接管（图层窗口没开，或当前不在正确的线程上）。
    /// </summary>
    public static LayerChannelHost? Install()
    {
        if (current is not null && !current.disposed)
        {
            current.Sync();
            return current;
        }

        return InstallCore(null);
    }

    /// <summary>给线程钩子用的入口：尝试安装一次，返回新建的面板（失败返回 null）。</summary>
    public static LayerChannelPanel? TryInstallNow()
    {
        LayerChannelPanel? created = null;
        LayerChannelHost? host = InstallCore(p => created = p);
        return host is null ? null : created;
    }

    private static LayerChannelHost? InstallCore(Action<LayerChannelPanel>? panelCreated)
    {
        if (current is not null && !current.disposed)
        {
            return current;
        }

        // ---- 门禁：必须先确认自己在正确的线程上 ----
        //
        // 实测结论：即使是在插件构造函数（Paint.NET 的 UI 线程）里，
        // Application.OpenForms.Count 也是 **0** —— Paint.NET 根本不把它的窗体
        // 登记到 OpenForms。所以只能走"枚举顶层窗口 HWND + Control.FromHandle"这条路。
        //
        // 而 Control.FromHandle 是按"调用线程"查表的：
        //   * 在**正确**的线程上调用 → 返回已登记的控件，安全；
        //   * 在**错误**的线程上调用 → 建立错误线程的托管包装，污染全局注册表，
        //     导致宿主 FindFocus() 抛跨线程异常并崩溃（已实测两次）。
        //
        // 因此进入本方法前，调用方必须已经在正确的线程上。
        // 这里再用主窗口做一个线程归属校验，不通过就直接放弃。
        if (!IsOnMainWindowThread())
        {
            Boot.LogDiag("wrongthread",
                "InstallCore: 当前不在 Paint.NET 主窗口线程上，本轮放弃（避免污染控件注册表）");
            return null;
        }

        Form? form = FindLayersFormOrNull();
        if (form is null)
        {
            Boot.LogDiag("noform", "InstallCore: 没能定位到 LayersForm");
            return null;
        }

        bool invokeRequired;
        try
        {
            invokeRequired = form.InvokeRequired;
        }
        catch
        {
            return null;
        }

        if (invokeRequired)
        {
            Boot.LogDiag("wrongthread2",
                "InstallCore: LayersForm 不属于当前线程，本轮放弃");
            return null;
        }

        return InstallOnCorrectThread(form, panelCreated);
    }

    /// <summary>此时已确认在 LayersForm 所属线程上。</summary>
    /// <summary>
    /// 进程级标记：本进程是否已经接管过。
    ///
    /// 为什么需要（实测）：本程序集在进程里会被加载两份（Managed assemblies 列表里
    /// 出现过两条 LayerChannelLive），两份有**独立的静态状态**，会并发安装并各起一个
    /// sync 定时器 —— 白耗 CPU，还会在错误的实例上刷新。
    /// 用跨副本共享的命名 Mutex 做进程级一次性开关：每个副本只尝试一次，
    /// 真正抢到 Mutex 的那个才继续往下装。
    /// </summary>
    private const string ProcessGateName = @"Local\LayerChannelLive.InstallGate";

    private static readonly Mutex ProcessGate = new(false, ProcessGateName);
    private static readonly HashSet<int> TriedGate = new();

    private static LayerChannelHost? InstallOnCorrectThread(
        Form form,
        Action<LayerChannelPanel>? panelCreated)
    {
        // 每个程序集副本只尝试一次（避免每 400ms 反复抢锁）。
        int copyId = RuntimeHelpers.GetHashCode(ProcessGate);
        lock (TriedGate)
        {
            if (!TriedGate.Add(copyId))
            {
                return null;
            }
        }

        // 进程级串行化：整段安装持有命名 Mutex，后到的副本拿不到就干净退出。
        bool lockTaken = false;
        try
        {
            try
            {
                lockTaken = ProcessGate.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                Boot.LogDiag("gatelock", "没能拿到进程级安装锁，跳过");
                return null;
            }

            return InstallOnCorrectThreadCore(form, panelCreated);
        }
        finally
        {
            if (lockTaken)
            {
                try { ProcessGate.ReleaseMutex(); } catch { }
            }
        }
    }

    /// <summary>
    /// 在窗体控件树里找**本拷贝**的 LayerChannelPanel（用于接管已有面板）。
    /// </summary>
    private static LayerChannelPanel? FindExistingPanel(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is LayerChannelPanel panel)
            {
                return panel;
            }

            LayerChannelPanel? nested = FindExistingPanel(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// 控件树里是否存在**任何一份程序集拷贝**建的 LayerChannelPanel。
    ///
    /// 用类型名字符串判断，因此跨程序集副本也成立（见安装处注释）。
    /// </summary>
    private static bool HasForeignPanel(Control root)
    {
        foreach (Control child in root.Controls)
        {
            string? typeName = child.GetType().Name;
            if (typeName == "LayerChannelPanel")
            {
                return true;
            }

            if (HasForeignPanel(child))
            {
                return true;
            }
        }

        return false;
    }

    private static LayerChannelHost? InstallOnCorrectThreadCore(
        Form form,
        Action<LayerChannelPanel>? panelCreated)
    {
        Control? strip = ReadField(form, "layersStrip") as Control;
        Control? parent = strip?.Parent;
        if (strip is null || parent is null)
        {
            Boot.LogDiag("nofields", "layersStrip 或其父容器不可用");
            return null;
        }

        // ---- 幂等保护：容器里已经有我们的面板就不再装 ----
        //
        // !! 关键：必须用**类型名字符串**判断，不能用 `existing is LayerChannelPanel`。
        //
        // 实测这个程序集会被加载两次，两份拷贝各有自己的 LayerChannelPanel 类型。
        // 用 C# 的 is 判断时，B 拷贝看 A 拷贝的面板"不是本类型的实例"，
        // 于是又建了第二个面板 —— 显示的是 B 的，而 A 的定时器还在覆盖状态，
        // 表现就是"点通道没反应 / 预览不变 / 状态自相矛盾"。
        // 字符串比较跨拷贝成立。
        LayerChannelPanel? existingPanel = FindExistingPanel(form);
        if (existingPanel is not null || HasForeignPanel(form))
        {
            Boot.LogDiag("alreadyinstalled", "已存在 LayerChannelPanel，跳过重复安装");
            return null;
        }

        var host = new LayerChannelHost(form)
        {
            layersStrip = strip,
            layersScrollBar = ReadField(form, "layersScrollBar") as Control,
        };

        // 隐藏原生列表与滚动条（不改它们的逻辑，只是不再显示）。
        try
        {
            strip.Visible = false;
            if (host.layersScrollBar is not null)
            {
                host.layersScrollBar.Visible = false;
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("hidefail", "隐藏原生控件失败: " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }

        // 用 Dock.Fill 而不是 Anchor：原生 strip 自己并不随容器缩放
        // （它的父容器由 LayersForm.OnLayout 手动排布），照抄 Bounds 会在 resize 后留白。
        var panel = new LayerChannelPanel
        {
            Dock = DockStyle.Fill,
        };

        host.panel = panel;
        try
        {
            parent.Controls.Add(panel);
            panel.BringToFront();
        }
        catch (Exception ex)
        {
            Boot.LogDiag("addfail", "添加面板失败: " + ex.GetType().Name + ": " + ex.Message);
            try { strip.Visible = true; } catch { }
            return null;
        }

        strip.SizeChanged += host.OnStripLayoutChanged;
        strip.LocationChanged += host.OnStripLayoutChanged;
        parent.SizeChanged += host.OnStripLayoutChanged;
        strip.VisibleChanged += host.OnStripVisibleChanged;

        host.BindWorkspaceFromStrip();

        host.toolStrip = ReadField(form, "toolStrip") as ToolStrip;
        host.moveUpButton = ReadField(form, "moveLayerUpButton") as ToolStripButton;
        host.moveDownButton = ReadField(form, "moveLayerDownButton") as ToolStripButton;
        panel.BindToolbarButtons(host.toolStrip, host.moveUpButton, host.moveDownButton);

        host.syncTimer = new System.Windows.Forms.Timer { Interval = 150 };
        host.syncTimer.Tick += host.OnSyncTick;
        host.syncTimer.Start();

        panelCreated?.Invoke(panel);

        Boot.Log("InstallOnCorrectThread: 接管成功 formType=" + form.GetType().Name
                 + " parentControls=" + parent.Controls.Count
                 + " panelDock=" + panel.Dock);

        current = host;
        return host;
    }

    /// <summary>
    /// 定位 LayersForm。
    ///
    /// 实测：Paint.NET **不把窗体登记进 <c>Application.OpenForms</c>**
    /// （在插件构造函数/UI 线程上实测 Count == 0），所以只能枚举顶层窗口的 HWND，
    /// 再用 <c>Control.FromHandle</c> 换回托管控件。
    ///
    /// <c>Control.FromHandle</c> 的线程要求：**只能在创建该控件的线程上调用**。
    /// 调用方已通过 <see cref="IsOnMainWindowThread"/> 校验过线程。
    /// </summary>
    private static Form? FindLayersFormOrNull()
    {
        // 快路径：万一某个版本登记了 OpenForms，这里能直接命中。
        try
        {
            foreach (Form f in Application.OpenForms)
            {
                if (IsLayersFormType(f.GetType()))
                {
                    return f;
                }
            }
        }
        catch
        {
        }

        // 主路径：枚举本进程顶层窗口 → Control.FromHandle → 按类型名认。
        foreach (IntPtr hwnd in NativeWindows.TopLevelHandles())
        {
            Control? control;
            try
            {
                control = Control.FromHandle(hwnd);
            }
            catch
            {
                continue;
            }

            if (control is null)
            {
                continue;
            }

            if (control is Form direct && IsLayersFormType(direct.GetType()))
            {
                return direct;
            }

            // 有些版本 LayersForm 藏在别的窗体下面，往下走几层。
            Form? nested = FindFormInTree(control, 0);
            if (nested is not null)
            {
                return nested;
            }
        }

        Boot.LogDiag("noform2",
            "FindLayersFormOrNull: 顶层窗口里没找到 LayersForm（共 "
            + NativeWindows.TopLevelHandles().Count + " 个顶层窗口）");

        return null;
    }

    private static Form? FindFormInTree(Control root, int depth)
    {
        if (depth > 3)
        {
            return null;
        }

        if (root is Form f && IsLayersFormType(f.GetType()))
        {
            return f;
        }

        foreach (Control child in root.Controls)
        {
            Form? found = FindFormInTree(child, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 校验当前线程是否就是 Paint.NET 主窗口所在线程。
    ///
    /// 这是防崩的关键：只有在这个线程上，<c>Control.FromHandle</c> 才是安全的。
    /// 用主窗口（标题最长、有可见标题的那个）的 owning thread 做判据。
    /// </summary>
    private static bool IsOnMainWindowThread()
    {
        try
        {
            uint current = NativeWindows.CurrentThreadId();
            uint mainThread = NativeWindows.MainWindowThreadId();
            if (mainThread == 0)
            {
                return false;
            }

            bool ok = current == mainThread;
            Boot.LogDiag("threadcheck",
                $"线程校验: 当前={current} 主窗口线程={mainThread} 通过={ok}");
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLayersFormType(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            if (string.Equals(t.FullName, LayersFormTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>诊断：列出当前线程能看到的 OpenForms。</summary>
    public static string DescribeVisibleForms()
    {
        try
        {
            var names = new List<string>();
            foreach (Form f in Application.OpenForms)
            {
                names.Add(f.GetType().Name);
            }

            return names.Count == 0 ? "(空)" : string.Join(",", names);
        }
        catch (Exception ex)
        {
            return "ERR " + ex.GetType().Name;
        }
    }

    /// <summary>卸载：恢复原生列表显示，移除面板。</summary>
    public static void Uninstall()
    {
        current?.Dispose();
        current = null;
    }

    /// <summary>手动刷新。面板属于 UI 线程；若被别的线程调用则编组过去。</summary>
    public void Sync()
    {
        if (this.disposed)
        {
            return;
        }

        LayerChannelPanel? panel = this.panel;
        if (panel is null)
        {
            return;
        }

        bool needMarshal;
        try
        {
            needMarshal = panel.InvokeRequired;
        }
        catch
        {
            return;
        }

        if (needMarshal)
        {
            try
            {
                panel.BeginInvoke(new Action(() => this.SyncOnUiThread()));
            }
            catch (Exception ex)
            {
                Boot.LogDiag("sync-marshal", "Sync 编组失败: " + ex.GetType().Name);
            }

            return;
        }

        this.SyncOnUiThread();
    }

    private void SyncOnUiThread()
    {
        if (this.disposed)
        {
            return;
        }

        this.BindWorkspaceFromStrip();
        this.panel?.RefreshFromHost();
    }

    public string Describe()
        => this.panel is null ? "面板未挂载" : PdnModel.DescribeHost(this.panel);

    /// <summary>LayersStrip 上有 DocumentWorkspace 属性，借它拿 workspace（比爬控件树稳）。</summary>
    private void BindWorkspaceFromStrip()
    {
        // 首选：跨所有顶层窗口找**当前文档**对应的工作区。
        //
        // 为什么不用 layersStrip.DocumentWorkspace（原来的做法）：
        // 那个 strip 是原生图层列表，早已被我隐藏；它身上挂的 DocumentWorkspace
        // 在**切换文档后不会更新**，仍是第一次绑定的那个旧工作区。
        // 实测：MainForm 里同时存在两个 DocumentWorkspace
        // （[0]=1024x1024 当前、[1]=512x512 旧），strip 指向的是旧的那个 ——
        // 用它覆盖就会把面板拉回上一个文档。
        object? workspace = PdnModel.TryFindDocumentWorkspaceAcrossAllWindows();

        // 退路：strip 上的引用（仅当跨窗口查找失败时）
        if (workspace is null && this.layersStrip is not null)
        {
            try
            {
                PropertyInfo? prop = this.layersStrip.GetType().GetProperty(
                    "DocumentWorkspace",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                workspace = prop?.GetValue(this.layersStrip);
            }
            catch
            {
            }
        }

        // 退路：从控件树里找（此时已在正确的线程上，安全）。
        if (workspace is null && this.panel is not null)
        {
            workspace = PdnModel.TryFindDocumentWorkspace(this.panel);
        }

        if (workspace is not null)
        {
            this.documentWorkspace = workspace;
            this.panel?.Bind(workspace);
            this.panel?.RefreshFromHost();
            MoveToolGuideService.Instance.Attach(workspace);
            this.panel?.SynchronizeLivePreviews();
        }
    }

    private void OnSyncTick(object? sender, EventArgs e)
    {
        if (this.disposed)
        {
            return;
        }

        MoveToolGuideService.Instance.SyncTool();
        if (this.syncTickCount % 10 == 0)
        {
            this.HookTopLevelDragDrop();
        }

        if (this.layersStrip is null || this.layersStrip.IsDisposed)
        {
            this.RebindStrip();
            return;
        }

        if (!hasDumpedToolDiag && this.documentWorkspace is not null)
        {
            hasDumpedToolDiag = true;
            DumpToolDiagnostics(this.documentWorkspace);
        }

        // 诊断心跳：默认关闭（每 150ms 写磁盘日志没有意义，只在排查时开）。
        // 排查"打开文件后没有文档 / 切换文件后不跟进"这类问题时，
        // 把 DiagHeartbeat 改成 true，日志里就会出现 [host]/[panel] 两行状态，
        // 以及各顶层窗口里能找到的 DocumentWorkspace 及各自带的文档尺寸。
        if (DiagHeartbeat && ++this.syncTickCount % 13 == 0)
        {
            Document? hostDoc = PdnModel.TryGetDocument(this.documentWorkspace);
            List<Layer> hostLayers = PdnModel.GetLayers(hostDoc);
            string hostLayerName = hostLayers.Count > 0 ? hostLayers[0].Name ?? "?" : "-";
            int panelRows = this.panel?.LayerRowCount ?? -1;
            string panelDoc = this.panel?.DescribeCurrentDocument() ?? "(no-panel)";

            Boot.Log($"心跳[host] 文档="
                     + (hostDoc is null ? "null" : hostDoc.Width + "x" + hostDoc.Height)
                     + $" 首层='{hostLayerName}' 图层数={hostLayers.Count} 面板行数={panelRows}");
            Boot.Log($"心跳[panel] {panelDoc}");
            WorkspaceDiagnostics.Dump("periodic");
        }

        // 每 150ms 无条件让面板自己去比对一次。
        //
        // 这里**不要**再做"宿主侧先判断有没有变化"的守卫 —— 实测踩过坑：
        // 早期版本写的是"选中图层变了 或 图层数量变了 才刷新"，
        // 结果点『下移图层』时两者都没变，RefreshFromHost() 压根没被调用，
        // 顺序变化就永远不显示（要等用户再点一下工具栏触发别的变化）。
        //
        // 面板内部的 RefreshFromHost() 自带顺序敏感签名比对，
        // 无变化时只做一次 Invalidate()，开销可以忽略。
        this.lastSeenHostLayer = PdnModel.TryGetActiveLayer(this.documentWorkspace);

        // 文档换了（打开新文件 / 关闭文档）才重新解析工作区。
        //
        // 不在每 tick 都做：跨所有顶层窗口找当前文档要枚举窗口并做 Control.FromHandle，
        // 150ms 一次太浪费。而"文档变了"这件事本身正是需要重新解析的信号。
        //
        // 实测两种场景都靠这一步：
        //   * 冷启动没开文件 → 之后打开文件时宿主换了工作区实例；
        //   * 切换文件 → 控件树里同时存在新旧两个工作区，原 strip 指向旧的。
        // BindWorkspaceFromStrip 现在优先走"跨所有顶层窗口找当前文档"，
        // 不会再被 layersStrip 上的旧引用带偏。
        Document? currentDoc = PdnModel.TryGetDocument(this.documentWorkspace);
        if (!ReferenceEquals(currentDoc, this.lastBoundDocument))
        {
            this.lastBoundDocument = currentDoc;
            this.BindWorkspaceFromStrip();
        }

        this.panel?.RefreshFromHost();

        if (this.panel is not null && this.panel.HasSelectedChannel)
        {
            if (this.moveUpButton is not null)
            {
                this.moveUpButton.Enabled = this.panel.CanMoveSelectedChannelUp();
            }
            if (this.moveDownButton is not null)
            {
                this.moveDownButton.Enabled = this.panel.CanMoveSelectedChannelDown();
            }
        }
    }

    /// <summary>上一次解析到的文档，用于判断是否需要重新解析工作区。</summary>
    private Document? lastBoundDocument;

    private int syncTickCount;

    /// <summary>
    /// 诊断开关：true 时每次心跳把 host/panel 的文档状态与工作区探测写进日志。
    /// 排查"打开文件后没有文档""切换文件后不跟进"这类问题时打开它。
    /// 默认 false —— 每 150ms 写磁盘日志会影响日常使用。
    /// </summary>
    private const bool DiagHeartbeat = false;

    private void HookTopLevelDragDrop()
    {
        try
        {
            foreach (Control root in NativeWindows.TopLevelControls())
            {
                if (root is Form form && this.hookedForms.Add(form))
                {
                    form.DragDrop += (s, e) =>
                    {
                        try
                        {
                            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                            {
                                if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                                {
                                    foreach (var f in files)
                                    {
                                        LayerFilePathResolver.RecordImportedFile(f);
                                    }
                                }
                            }
                        }
                        catch { }
                    };
                }
            }
        }
        catch { }
    }

    private void RebindStrip()
    {
        Form? form = this.layersForm;
        if (form is null || form.IsDisposed)
        {
            return;
        }

        Control? strip = ReadField(form, "layersStrip") as Control;
        if (strip is not null)
        {
            this.layersStrip = strip;
            this.toolStrip = ReadField(form, "toolStrip") as ToolStrip;
            this.moveUpButton = ReadField(form, "moveLayerUpButton") as ToolStripButton;
            this.moveDownButton = ReadField(form, "moveLayerDownButton") as ToolStripButton;
            this.panel?.BindToolbarButtons(this.toolStrip, this.moveUpButton, this.moveDownButton);
            this.BindWorkspaceFromStrip();
        }
    }

    private void OnStripVisibleChanged(object? sender, EventArgs e)
    {
        if (this.layersStrip is { Visible: true } && this.panel is { Visible: true })
        {
            this.layersStrip.Visible = false;
        }
    }

    private void OnStripLayoutChanged(object? sender, EventArgs e)
    {
        if (this.layersStrip is { Visible: true } && this.panel is { Visible: true })
        {
            this.layersStrip.Visible = false;
        }
    }

    private static object? ReadField(object instance, string fieldName)
    {
        FieldInfo? field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (field is null)
        {
            return null;
        }

        try
        {
            return field.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        if (this.syncTimer is not null)
        {
            this.syncTimer.Stop();
            this.syncTimer.Tick -= this.OnSyncTick;
            this.syncTimer.Dispose();
            this.syncTimer = null;
        }

        if (this.layersStrip is not null)
        {
            this.layersStrip.SizeChanged -= this.OnStripLayoutChanged;
            this.layersStrip.LocationChanged -= this.OnStripLayoutChanged;
            this.layersStrip.VisibleChanged -= this.OnStripVisibleChanged;

            try
            {
                this.layersStrip.Visible = true;
            }
            catch
            {
            }
        }

        if (this.layersScrollBar is not null)
        {
            try
            {
                this.layersScrollBar.Visible = true;
            }
            catch
            {
            }
        }

        if (this.panel is not null)
        {
            Control? parent = this.panel.Parent;
            if (parent is not null)
            {
                parent.SizeChanged -= this.OnStripLayoutChanged;
                try
                {
                    parent.Controls.Remove(this.panel);
                }
                catch
                {
                }
            }

            this.panel.Dispose();
            this.panel = null;
        }

        this.toolStrip = null;
        this.moveUpButton = null;
        this.moveDownButton = null;

        MoveToolGuideService.Instance.Dispose();

        if (ReferenceEquals(current, this))
        {
            current = null;
        }
    }

    internal static bool hasDumpedToolDiag;

    internal static void DumpToolDiagnostics(object workspace)
    {
        try
        {
            string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pdn_tools_diag.log");
            using var sw = new System.IO.StreamWriter(logPath, false, System.Text.Encoding.UTF8);

            sw.WriteLine("=== DOCUMENT WORKSPACE DIAGNOSTIC ===");
            Type wsType = workspace.GetType();
            sw.WriteLine($"Workspace Type: {wsType.FullName} (Assembly: {wsType.Assembly.FullName})");

            foreach (var prop in wsType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string valStr = "-";
                try
                {
                    if (prop.GetIndexParameters().Length == 0)
                    {
                        var val = prop.GetValue(workspace);
                        valStr = val?.ToString() ?? "null";
                        if (val != null) valStr += $" ({val.GetType().FullName})";
                    }
                }
                catch (Exception ex) { valStr = "<err: " + ex.Message + ">"; }
                sw.WriteLine($"  Property: {prop.PropertyType.Name} {prop.Name} = {valStr}");
            }

            foreach (var field in wsType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string valStr = "-";
                try
                {
                    var val = field.GetValue(workspace);
                    valStr = val?.ToString() ?? "null";
                    if (val != null) valStr += $" ({val.GetType().FullName})";
                }
                catch (Exception ex) { valStr = "<err: " + ex.Message + ">"; }
                sw.WriteLine($"  Field: {field.FieldType.Name} {field.Name} = {valStr}");
            }

            foreach (var m in wsType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                sw.WriteLine($"  Method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
            }

            object? sel = wsType.GetProperty("Selection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(workspace);
            if (sel != null)
            {
                sw.WriteLine("\n=== SELECTION DIAGNOSTIC ===");
                Type selType = sel.GetType();
                foreach (var p in selType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    sw.WriteLine($"  Sel Prop: {p.PropertyType.Name} {p.Name}");
                }
                foreach (var m in selType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    sw.WriteLine($"  Sel Method: {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                }
            }

            object? tool = wsType.GetProperty("Tool")?.GetValue(workspace);
            if (tool != null)
            {
                sw.WriteLine("\n=== ACTIVE TOOL DIAGNOSTIC ===");
                Type toolType = tool.GetType();
                sw.WriteLine($"Tool Type: {toolType.FullName}");
                foreach (var p in toolType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    sw.WriteLine($"  Tool Prop: {p.PropertyType.Name} {p.Name}");
                }
                object? ui = toolType.GetProperty("UI")?.GetValue(tool);
                if (ui != null)
                {
                    sw.WriteLine($"UI Type: {ui.GetType().FullName}");
                    foreach (var p in ui.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        sw.WriteLine($"  UI Prop: {p.PropertyType.Name} {p.Name}");
                    }
                }
            }

            sw.WriteLine("\n=== APP WORKSPACE / PARENT ===");
            Control? wsCtrl = workspace as Control;
            Control? cur = wsCtrl?.Parent;
            while (cur != null)
            {
                sw.WriteLine($"Parent: {cur.GetType().FullName} ({cur.Name})");
                if (cur.GetType().Name.Contains("AppWorkspace"))
                {
                    foreach (var p in cur.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length == 0)
                            {
                                var v = p.GetValue(cur);
                                sw.WriteLine($"  AppWS Prop: {p.PropertyType.Name} {p.Name} = {v?.GetType().FullName ?? "null"}");
                            }
                        }
                        catch {}
                    }
                    foreach (var f in cur.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        try
                        {
                            var v = f.GetValue(cur);
                            sw.WriteLine($"  AppWS Field: {f.FieldType.Name} {f.Name} = {v?.GetType().FullName ?? "null"}");
                        }
                        catch {}
                    }
                }
                cur = cur.Parent;
            }

            sw.WriteLine("\n=== TOOL TYPES IN LOADED ASSEMBLIES ===");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name ?? "";
                if (asmName.Contains("paint", StringComparison.OrdinalIgnoreCase))
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        types = rtle.Types.Where(t => t != null).Select(t => t!).ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var t in types)
                    {
                        string fn = t.FullName ?? t.Name;
                        if (fn.Contains("Tool") || fn.Contains("Move") || fn.Contains("Guide") || fn.Contains("Snap") || fn.Contains("CanvasControl") || fn.Contains("Transform"))
                        {
                            sw.WriteLine($"  [{asmName}] {fn} (Base: {t.BaseType?.Name})");
                            if (fn.Contains("Move") || fn.Contains("CanvasControl") || fn.Contains("Transform") || fn.Contains("ToolUI") || fn == "PaintDotNet.Tools.Tool")
                            {
                                sw.WriteLine($"    --- Properties for {t.Name} ---");
                                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                {
                                    sw.WriteLine($"      {p.PropertyType.Name} {p.Name}");
                                }
                                sw.WriteLine($"    --- Methods for {t.Name} ---");
                                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                {
                                    sw.WriteLine($"      {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                                }
                                sw.WriteLine($"    --- Fields for {t.Name} ---");
                                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                {
                                    sw.WriteLine($"      {f.FieldType.Name} {f.Name}");
                                }
                                sw.WriteLine($"    --- Events for {t.Name} ---");
                                foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                {
                                    sw.WriteLine($"      {e.EventHandlerType?.Name} {e.Name}");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("dump-tool-err", ex.ToString());
        }
    }
}

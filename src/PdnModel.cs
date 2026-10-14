using System.Collections;
using System.Reflection;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>
/// Paint.NET 宿主桥接层。
///
/// 关键结论（实测 Paint.NET 5.0.13，见 ApiFindings/*.txt）：
///   PaintDotNet.Controls.DocumentWorkspace  = internal  → 只能反射
///   PaintDotNet.Document                    = public    → 反射拿到之后可以直接用
///   Document.Layers (LayerList)             = public
///   Layer.Visible / Opacity / Name / Bounds = public
///   BitmapLayer.Surface (PaintDotNet.Surface) = public
///   Surface.Scan0 / Stride / Width / Height = public
///
/// 也就是说：只有「拿到 Document」这一步需要反射，图层和像素全部走公开 API，零补丁。
/// </summary>
internal static class PdnModel
{
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>按类型名找 Workspace 控件（AppWorkspace / DocumentWorkspace / DocumentView）。</summary>
    private static readonly string[] WorkspaceTypeNames =
    {
        "PaintDotNet.Controls.DocumentWorkspace",
        "PaintDotNet.Controls.AppWorkspace",
        "PaintDotNet.Controls.DocumentView",
    };

    /// <summary>
    /// 找到**当前活动**的 DocumentWorkspace。
    ///
    /// 为什么不能简单地"深度优先找到第一个就返回"（实测踩过坑）：
    /// 切换文档后，控件树里可能同时存在旧的与新的 DocumentWorkspace 实例
    /// （旧的可能还挂在某个隐藏容器下）。先撞到旧的那个，就会一直读到上一个文档 ——
    /// 表现就是"切换文件后图层面板没切过去"（窗口标题变了、面板内容没变）。
    ///
    /// 所以这里做两件事：
    ///   1. **优先**读 AppWorkspace / DocumentView 的 ActiveDocumentWorkspace 属性
    ///      （这是宿主自己维护的"当前工作区"，权威来源）；
    ///   2. 退而扫描控件树里**所有** DocumentWorkspace，挑出真正能读出 Document 的那个。
    /// </summary>
    public static object? TryFindDocumentWorkspace(Control root)
    {
        if (root is null)
        {
            return null;
        }

        // 1. 首选：宿主维护的 ActiveDocumentWorkspace
        object? active = FindActiveDocumentWorkspace(root, 0);
        if (active is not null && TryGetDocument(active) is not null)
        {
            return active;
        }

        // 2. 退路：扫描所有 DocumentWorkspace。
        //
        // 实测：同一个窗体里会有**多个** DocumentWorkspace
        // （MainForm 里就出现过 2 个：[0]=1024x1024 当前文档、[1]=512x512 旧文档）。
        // 所以不能"撞到第一个带文档的就返回" —— 那会一直读到上一个文档。
        var candidates = new List<object>();
        CollectDocumentWorkspaces(root, candidates, 0);

        // 2a. 优先选"确实是当前文档"的那个：
        //     通过 MainForm / AppWorkspace 上指向当前文档的属性来交叉验证。
        object? currentDoc = FindCurrentDocument(root);
        if (currentDoc is not null)
        {
            foreach (object candidate in candidates)
            {
                if (ReferenceEquals(TryGetDocument(candidate), currentDoc))
                {
                    return candidate;
                }
            }
        }

        // 2b. 退一步：挑"候选列表里最后出现的那个带文档的 workspace"。
        //     实测新文档对应的 workspace 会追加在列表后面。
        object? lastWithDoc = null;
        foreach (object candidate in candidates)
        {
            if (TryGetDocument(candidate) is not null)
            {
                lastWithDoc = candidate;
            }
        }

        if (lastWithDoc is not null)
        {
            return lastWithDoc;
        }

        // 3. 都没有文档（例如刚启动还没打开文件）时，返回 active 或第一个候选，
        //    至少让调用方拿到一个可用引用（后续轮询会再解析）。
        return active ?? (candidates.Count > 0 ? candidates[0] : null);
    }

    /// <summary>
    /// 找宿主维护的"当前活动工作区"：读 AppWorkspace / DocumentView /
    /// DocumentWorkspace 上的 ActiveDocumentWorkspace 属性。
    /// </summary>
    private static object? FindActiveDocumentWorkspace(Control root, int depth)
    {
        if (depth > 24)
        {
            return null;
        }

        string? typeName = root.GetType().FullName;
        if (typeName is "PaintDotNet.Controls.AppWorkspace"
            or "PaintDotNet.Controls.DocumentView"
            or "PaintDotNet.Controls.DocumentWorkspace")
        {
            object? active = ReadMember(root, "ActiveDocumentWorkspace");
            if (active is not null)
            {
                return active;
            }
        }

        foreach (Control child in root.Controls)
        {
            object? found = FindActiveDocumentWorkspace(child, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 找宿主认为的"当前文档"。
    /// 依次尝试 MainForm / AppWorkspace 上常见的几个属性名。
    /// </summary>
    private static object? FindCurrentDocument(Control root)
    {
        string[] names = { "CurrentDocument", "ActiveDocument", "Document" };

        foreach (string name in names)
        {
            object? found = FindMemberOfType(root, name, 0);
            if (found is Document)
            {
                return found;
            }
        }

        return null;
    }

    private static object? FindMemberOfType(Control root, string memberName, int depth)
    {
        if (depth > 24)
        {
            return null;
        }

        object? value = ReadMember(root, memberName);
        if (value is Document)
        {
            return value;
        }

        foreach (Control child in root.Controls)
        {
            object? found = FindMemberOfType(child, memberName, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 在**本进程所有顶层窗口**里找带文档的 DocumentWorkspace。
    ///
    /// 为什么必须这么做（实测）：文档工作区挂在 **MainForm** 下，
    /// 而图层窗口是另一个顶层窗口。只从图层窗口的控件树里搜是搜不到的，
    /// 于是面板会一直用最初绑定的旧引用 —— 表现就是"切换文件后图层面板没切过去"。
    ///
    /// 只能在 UI 线程调用（用到 Control.FromHandle）。
    /// </summary>
    public static object? TryFindDocumentWorkspaceAcrossAllWindows()
    {
        object? firstCandidate = null;

        foreach (Control root in NativeWindows.TopLevelControls())
        {
            object? found;
            try
            {
                found = TryFindDocumentWorkspace(root);
            }
            catch
            {
                continue;
            }

            if (found is null)
            {
                continue;
            }

            if (TryGetDocument(found) is not null)
            {
                return found;
            }

            firstCandidate ??= found;
        }

        return firstCandidate;
    }

    private static void CollectDocumentWorkspaces(Control root, List<object> result, int depth)
    {
        if (depth > 24)
        {
            return;
        }

        if (root.GetType().FullName == "PaintDotNet.Controls.DocumentWorkspace")
        {
            result.Add(root);
        }

        foreach (Control child in root.Controls)
        {
            CollectDocumentWorkspaces(child, result, depth + 1);
        }
    }

    /// <summary>从 DocumentWorkspace 上取 Document。</summary>
    public static Document? TryGetDocument(object? documentWorkspace)
    {
        if (documentWorkspace is null)
        {
            return null;
        }

        object? doc = ReadMember(documentWorkspace, "Document");
        return doc as Document;
    }

    /// <summary>DocumentWorkspace 上的 ActiveLayer（选中图层）。</summary>
    public static Layer? TryGetActiveLayer(object? documentWorkspace)
    {
        if (documentWorkspace is null)
        {
            return null;
        }

        return ReadMember(documentWorkspace, "ActiveLayer") as Layer;
    }

    /// <summary>
    /// 枚举文档图层。LayerList : LayerListBase : ArrayList，所以直接当 IList 走即可。
    /// 返回顺序与 Paint.NET 自己的图层列表一致（索引 0 = 最底层）。
    /// </summary>
    public static List<Layer> GetLayers(Document? document)
    {
        var result = new List<Layer>();
        if (document is null)
        {
            return result;
        }

        IEnumerable? layers;
        try
        {
            layers = document.Layers;
        }
        catch
        {
            return result;
        }

        if (layers is null)
        {
            return result;
        }

        foreach (object? item in layers)
        {
            if (item is Layer layer)
            {
                result.Add(layer);
            }
        }

        return result;
    }

    /// <summary>图层可见性。根图层（背景）语义特殊，读取失败时按可见处理。</summary>
    public static bool GetVisible(Layer layer)
    {
        try
        {
            return layer.Visible;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>图层不透明度。</summary>
    public static byte GetOpacity(Layer layer)
    {
        try
        {
            return layer.Opacity;
        }
        catch
        {
            return 255;
        }
    }

    /// <summary>只有 BitmapLayer 有 Surface；其它 Layer 子类拿不到像素。</summary>
    public static Surface? TryGetSurface(Layer layer)
    {
        if (layer is not BitmapLayer bitmapLayer)
        {
            return null;
        }

        try
        {
            return bitmapLayer.Surface;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>反射读一个属性或字段，按名字匹配。</summary>
    private static object? ReadMember(object instance, string name)
    {
        Type type = instance.GetType();

        PropertyInfo? property = type.GetProperty(name, AnyInstance);
        if (property is not null)
        {
            try
            {
                return property.GetValue(instance);
            }
            catch
            {
                // 属性存在但 getter 抛异常（例如图层被释放），继续尝试字段。
            }
        }

        FieldInfo? field = type.GetField(name, AnyInstance);
        if (field is not null)
        {
            try
            {
                return field.GetValue(instance);
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>诊断用：当前宿主是否满足我们的前提。</summary>
    public static string DescribeHost(Control root)
    {
        object? workspace = TryFindDocumentWorkspace(root);
        Document? document = TryGetDocument(workspace);
        if (workspace is null)
        {
            return "未找到 DocumentWorkspace（插件可能不在 Paint.NET 进程内运行）";
        }

        if (document is null)
        {
            return $"找到 {workspace.GetType().FullName}，但没有打开的文档";
        }

        List<Layer> layers = GetLayers(document);
        int bitmapLayers = layers.Count(l => TryGetSurface(l) is not null);
        return $"Document {document.Width}x{document.Height}，图层 {layers.Count} 个（其中 {bitmapLayers} 个可读像素）";
    }

    public static void Invalidate(Layer? layer)
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

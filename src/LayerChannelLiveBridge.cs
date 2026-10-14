namespace LayerChannelLive;

/// <summary>
/// 对外入口。提供图层通道与吸附特性的接管、卸载与状态查询接口。
///
/// 用法：
///     LayerChannelLiveBridge.Install();     // 把原生图层窗口改成「图层 + 选中图层 R/G/B/A 通道」并启用吸附特性
///     LayerChannelLiveBridge.Uninstall();   // 还原
///     string info = LayerChannelLiveBridge.Describe();
///
/// 线程要求：**必须在 Paint.NET 的 UI 线程（也就是创建图层面板的那个线程）上调用**。
/// 从错误的线程调用不会崩，但会被内部的 InvokeRequired 门禁拒绝（返回 false），
/// 因为在那样的线程上创建控件会给宿主埋雷。
/// </summary>
public static class LayerChannelLiveBridge
{
    /// <summary>
    /// 接管图层窗口。返回 false 表示这一轮没接管成功 —— 可能是图层窗口没打开，
    /// 也可能当前线程不对（此时应改从 Paint.NET 的 UI 线程调用）。
    /// </summary>
    public static bool Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return LayerChannelHost.Install() is not null;
    }

    public static void Uninstall() => LayerChannelHost.Uninstall();

    /// <summary>重新读取宿主状态（文档切换、图层增删后可以调一次）。</summary>
    public static void Sync() => LayerChannelHost.Install()?.Sync();

    /// <summary>诊断信息：当前接管的是哪个文档、多少图层。</summary>
    public static string Describe() => LayerChannelHost.Install()?.Describe() ?? "图层窗口未打开";

    /// <summary>诊断：当前线程能看到的窗体清单（用来判断是否在正确的 UI 线程上）。</summary>
    public static string DescribeForms() => LayerChannelHost.DescribeVisibleForms();

    /// <summary>诊断：本进程顶层窗口与其所属线程。</summary>
    public static string DescribeWindows() => NativeWindows.Describe();

    /// <summary>诊断：当前线程 ID。</summary>
    public static uint CurrentThreadId() => NativeWindows.CurrentThreadId();

    /// <summary>诊断：主窗口所在线程 ID。</summary>
    public static uint MainWindowThreadId() => NativeWindows.MainWindowThreadId();

    /// <summary>诊断：内部日志路径。</summary>
    public static string LogPath => Boot.LogPath;
}

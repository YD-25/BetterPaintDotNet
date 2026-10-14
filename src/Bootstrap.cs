using System.IO;
using System.Text;
using PaintDotNet;
using PaintDotNet.Effects;

[assembly: PluginSupportInfo(typeof(LayerChannelLive.LayerChannelLiveSupportInfo))]

namespace LayerChannelLive;

/// <summary>
/// 本程序集的加载入口（一个不参与渲染的空效果）。
///
/// ================== 线程模型（血泪教训） ==================
///
/// 目标：Paint.NET 一启动，图层工具栏上就出现 R/G/B/A 通道，用户不需要点任何东西。
///
/// 道路上的两个硬事实（都是实测）：
///
///   1) Paint.NET **启动时不加载** Effects 目录里的插件 —— 它只读程序集元数据来建菜单。
///      所以"自动生效"需要外部把本程序集注入进程（install.ps1 用 LoadLibrary 完成），
///      注入后模块初始化跑在一条**非 UI 线程**上。
///
///   2) 控件树只能在它所属的线程上操作。在错误线程上创建/触碰 WinForms 控件，
///      会在全局控件注册表里留下属于那条线程的匿名条目，导致宿主自己遍历控件树
///      （ApplicationUtil.FindFocus）时抛跨线程异常并崩溃 ——
///      崩溃堆栈里**没有一帧是本插件的代码**，极难定位。已实测踩过两次。
///      另：<c>Application.OpenForms</c> 靠不住，实测在 UI 线程上 Count 也是 0。
///
/// 所以这里**只挂钩子，不碰控件树**：
/// <see cref="UiThreadHook"/> 用 SetWindowsHookEx(WH_GETMESSAGE) 给每个 GUI 线程挂钩子，
/// 回调会被系统派发到目标线程上，安装动作写在**回调里** —— 那才是图层窗口所属的线程。
/// </summary>
public sealed class LayerChannelLiveBootstrapEffect
    : BitmapEffect<PropertyBasedEffectConfigToken>
{
    public LayerChannelLiveBootstrapEffect()
        : base(
            "更好的Paint.NET (常驻)",
            null,
            BitmapEffectOptions.Create() with { IsConfigurable = false })
    {
        // 本构造函数可能在任意线程上执行（实测 Paint.NET 用后台线程实例化效果类），
        // 所以这里**只挂钩子，不碰控件树**。
        //
        // 钩子的回调会被系统派发到各个 GUI 线程上，安装动作写在回调里 ——
        // 那才是图层窗口所属的线程。详见 UiThreadHook 的说明。
        try
        {
            UiThreadHook.EnsureHooked();
            Boot.Log("bootstrap: 已请求挂载 UI 线程钩子");
        }
        catch (Exception ex)
        {
            Boot.Log("bootstrap: 挂钩子失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>桩子只是加载入口，点了也不做任何事。</summary>
    protected override void OnRender(IBitmapEffectOutput output)
    {
        // 故意留空。
    }
}

/// <summary>诊断日志。写到 %TEMP%\LayerChannelLive.log。</summary>
internal static class Boot
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastDiagByKey = new();

    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "LayerChannelLive.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 记日志失败绝不能影响宿主。
        }
    }

    /// <summary>同样的 key 最多每 10 秒记一次，避免刷爆日志。</summary>
    public static void LogDiag(string key, string message)
    {
        DateTime now = DateTime.UtcNow;
        lock (Gate)
        {
            if (LastDiagByKey.TryGetValue(key, out DateTime prev) && (now - prev).TotalSeconds < 10)
            {
                return;
            }

            LastDiagByKey[key] = now;
        }

        Log("diag: " + message);
    }
}

/// <summary>插件支持信息（Paint.NET 用来在插件列表里显示）。</summary>
public sealed class LayerChannelLiveSupportInfo : IPluginSupportInfo
{
    public string? DisplayName => "更好的Paint.NET";

    public string? Author => "YD-25";

    public string? Copyright => "Copyright (c) 2026 YD-25";

    public Version Version =>
        typeof(LayerChannelLiveSupportInfo).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

    public Uri? WebsiteUri => new Uri("https://github.com/YD-25/BetterPaintDotNet");
}

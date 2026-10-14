using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DdsInspect")]
[assembly: InternalsVisibleTo("Inspect")]

namespace LayerChannelLive;

/// <summary>
/// 模块初始化器：在程序集**被加载的那一刻**执行，不依赖任何类型被实例化。
///
/// 为什么需要它：Paint.NET 启动时不加载 Effects 目录里的插件（只读元数据建菜单），
/// 所以"自动接管图层窗口"依赖外部把本程序集注入进程。
/// 注入靠 <c>LoadLibrary</c>，CLR 的加载器 shim 会在加载托管程序集时执行模块初始化 ——
/// 这里就是我们的自动入口，比"等效果类被实例化"可靠得多。
///
/// 本方法**只挂钩子，绝不创建线程/窗体/控件**（那些正是此前崩溃的根因）。
/// </summary>
internal static class ModuleEntry
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            Boot.Log("module: 程序集已加载，请求挂载 UI 线程钩子");
            UiThreadHook.EnsureHooked();
        }
        catch (Exception ex)
        {
            Boot.Log("module: 初始化失败: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

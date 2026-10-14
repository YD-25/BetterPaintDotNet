using System.Runtime.InteropServices;

namespace LayerChannelLive;

/// <summary>
/// 把安装动作派发到**创建图层窗口的那个 UI 线程**上执行。
///
/// ================== 需求与难点 ==================
///
/// 需求：Paint.NET 一启动，图层工具栏上就该出现通道功能，用户**不需要点任何东西**。
///
/// 难点链：
///   1. Paint.NET **启动时不加载** Effects 目录里的插件（只读程序集元数据建菜单），
///      所以本程序集需要由 install.ps1 用 LoadLibrary 注入进程。
///   2. 注入模块的初始化跑在一条**非 UI 线程**上。
///   3. 控件树只能在它所属的线程上操作：
///      * 错误线程上调 <c>Control.FromHandle</c> → 创建错误线程的托管包装 →
///        污染 WinForms 全局控件注册表 → 宿主 <c>FindFocus()</c> 抛跨线程异常 → 崩溃
///        （已实测两次，崩溃堆栈里没有本插件的代码）。
///      * <c>Application.OpenForms</c> 靠不住 —— 实测在 UI 线程上 Count 也是 0。
///
/// ================== 解法 ==================
///
/// <c>SetWindowsHookEx(WH_GETMESSAGE)</c> 的回调**一定在目标线程的上下文里执行**
/// （已实测：钩子装上后回调线程 = 目标线程）。
///
/// 所以给每个 GUI 线程都挂一个钩子，然后在**回调里直接尝试安装**。
/// <see cref="LayerChannelHost"/> 内部用 <c>InvokeRequired</c> 做判据：
/// 当前线程就是图层窗口的线程时才真正安装，否则安静跳过。
/// 哪个线程通过就在那里装成功，随后立刻卸载所有钩子。
///
/// 与之前失败版本的关键区别：
///   安装动作必须写在**钩子回调里**，而不是"钩子只投递消息、由另一条线程去安装"。
///   旧版本把安装放在中继线程上，那条线程不是创建控件的线程，所以永远被门禁拒绝。
///
/// 本类**不创建任何线程、任何窗体、任何控件** —— 这是刻意的：
/// 在非 UI 线程创建 WinForms 控件正是前一版崩溃的根因。
/// </summary>
internal static class UiThreadHook
{
    private const int WH_GETMESSAGE = 3;
    private const int PM_REMOVE = 1;
    private const int RetryIntervalMs = 400;
    private const int ReenumerateEveryNth = 40;

    private delegate IntPtr GetMsgProc(int code, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowsHookExW(int hookId, GetMsgProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly object Gate = new();

    /// <summary>静态持有回调委托，防止被 GC 回收导致钩子回调变野指针。</summary>
    private static readonly GetMsgProc Callback = HookCallback;

    private static readonly List<IntPtr> HookHandles = new();
    private static readonly HashSet<uint> HookedThreads = new();

    private static long lastAttemptTicks;
    private static int callbackCount;
    private static bool succeeded;

    public static bool Succeeded => succeeded;

    /// <summary>给每个 GUI 线程挂上钩子。可从任意线程调用。</summary>
    public static void EnsureHooked()
    {
        lock (Gate)
        {
            if (succeeded)
            {
                return;
            }
        }

        IntPtr module = GetModuleHandleW(null);
        if (module == IntPtr.Zero)
        {
            Boot.LogDiag("nomodule", "GetModuleHandleW(null) 返回 0，无法挂钩子");
            return;
        }

        foreach (uint tid in EnumerateGuiThreadIds())
        {
            bool already;
            lock (Gate)
            {
                already = HookedThreads.Contains(tid) || succeeded;
            }

            if (already)
            {
                continue;
            }

            IntPtr handle = SetWindowsHookExW(WH_GETMESSAGE, Callback, module, tid);
            if (handle == IntPtr.Zero)
            {
                Boot.LogDiag("hookfail-" + tid,
                    $"SetWindowsHookEx 失败 thread={tid} err={Marshal.GetLastWin32Error()}");
                continue;
            }

            lock (Gate)
            {
                HookedThreads.Add(tid);
                HookHandles.Add(handle);
            }

            Boot.Log($"watcher: 已挂 WH_GETMESSAGE 钩子 thread={tid}");
        }
    }

    private static List<uint> EnumerateGuiThreadIds()
    {
        var threads = new List<uint>();
        uint self = (uint)Environment.ProcessId;

        try
        {
            EnumWindows(
                (hwnd, _) =>
                {
                    uint ownerPid;
                    uint tid = GetWindowThreadProcessId(hwnd, out ownerPid);
                    if (ownerPid == self && tid != 0 && IsWindow(hwnd) && !threads.Contains(tid))
                    {
                        threads.Add(tid);
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch
        {
        }

        return threads;
    }

    /// <summary>
    /// 钩子回调 —— **运行在目标线程上**，这是本方案的核心。
    /// 在这里直接尝试安装；是否真装由 LayerChannelHost 内部的 InvokeRequired 判据决定。
    /// </summary>
    private static IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // code < 0 必须原样转交，不做任何事。
            if (code >= 0 && !succeeded && wParam == (IntPtr)PM_REMOVE)
            {
                // 周期性补挂新出现的 GUI 线程（图层窗口所在线程可能比钩子晚创建）。
                if (++callbackCount % ReenumerateEveryNth == 0)
                {
                    EnsureHooked();
                }

                long now = Environment.TickCount64;
                long last = Interlocked.Read(ref lastAttemptTicks);

                if (now - last >= RetryIntervalMs
                    && Interlocked.CompareExchange(ref lastAttemptTicks, now, last) == last)
                {
                    LayerChannelPanel? panel = LayerChannelHost.TryInstallNow();
                    if (panel is not null)
                    {
                        succeeded = true;
                        Boot.Log("watcher: 接管成功（发生在 UI 线程 " + GetCurrentThreadId() + "）");
                        UnhookAll();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Boot.LogDiag("hookcb", "钩子回调异常: " + ex.GetType().Name + ": " + ex.Message);
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public static void UnhookAll()
    {
        List<IntPtr> handles;
        lock (Gate)
        {
            handles = new List<IntPtr>(HookHandles);
            HookHandles.Clear();
            HookedThreads.Clear();
        }

        foreach (IntPtr handle in handles)
        {
            try
            {
                UnhookWindowsHookEx(handle);
            }
            catch
            {
            }
        }

        Boot.Log("watcher: 钩子已全部卸载");
    }

    public static void Shutdown()
    {
        succeeded = false;
        Interlocked.Exchange(ref lastAttemptTicks, 0);
        UnhookAll();
    }
}

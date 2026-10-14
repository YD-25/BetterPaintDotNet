using System.Runtime.InteropServices;
using System.Text;

namespace LayerChannelLive;

/// <summary>
/// 纯原生的窗口/线程查询工具。
///
/// 这里**只碰 HWND 和线程 ID，绝不创建托管控件** —— 这是刻意的：
/// 从非 UI 线程创建 WinForms 控件会在全局注册表里留下属于那条线程的匿名条目，
/// 导致宿主 Paint.NET 自己在遍历控件树查焦点时崩掉（已实测两次，堆栈里没有本插件的代码）。
///
/// 托管控件的获取统一交给调用方在**已验证的 UI 线程**上用 Control.FromHandle 完成。
/// </summary>
internal static class NativeWindows
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    public static uint CurrentThreadId() => GetCurrentThreadId();

    /// <summary>
    /// 把本进程所有顶层窗口句柄换成托管控件。
    ///
    /// **只能在 UI 线程调用**（Control.FromHandle 的线程要求：从错误线程调
    /// 会在全局控件注册表里留下属于那条线程的包装，导致宿主 FindFocus() 崩溃）。
    ///
    /// 为什么需要：文档工作区挂在 MainForm 下，而图层窗口是另一个顶层窗口 ——
    /// 只从图层窗口的控件树搜索是找不到工作区的。
    /// </summary>
    public static List<System.Windows.Forms.Control> TopLevelControls()
    {
        var result = new List<System.Windows.Forms.Control>();

        foreach (IntPtr hwnd in TopLevelHandles())
        {
            try
            {
                var c = System.Windows.Forms.Control.FromHandle(hwnd);
                if (c is not null)
                {
                    result.Add(c);
                }
            }
            catch
            {
                // 句柄不是托管控件（例如 IME 窗口）会抛，跳过
            }
        }

        return result;
    }

    /// <summary>列出本进程所有顶层窗口的 HWND。</summary>
    public static List<IntPtr> TopLevelHandles()
    {
        var result = new List<IntPtr>();
        uint self = (uint)Environment.ProcessId;

        try
        {
            EnumWindows(
                (hwnd, _) =>
                {
                    uint ownerPid;
                    GetWindowThreadProcessId(hwnd, out ownerPid);
                    if (ownerPid == self && IsWindow(hwnd))
                    {
                        result.Add(hwnd);
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch
        {
        }

        return result;
    }

    /// <summary>
    /// 主窗口所在线程。
    ///
    /// 判据：**可见**且**标题最长**的顶层窗口。
    /// 必须加可见性过滤 —— 实测不可见的 <c>.NET-BroadcastEventWindow.14a4f1c.0</c>
    /// 标题比真主窗口还长，不加过滤会选错线程。
    /// </summary>
    public static uint MainWindowThreadId()
    {
        IntPtr best = IntPtr.Zero;
        int bestLen = -1;
        uint self = (uint)Environment.ProcessId;

        try
        {
            EnumWindows(
                (hwnd, _) =>
                {
                    uint ownerPid;
                    GetWindowThreadProcessId(hwnd, out ownerPid);
                    if (ownerPid != self || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
                    {
                        return true;
                    }

                    var sb = new StringBuilder(512);
                    int len = GetWindowTextW(hwnd, sb, sb.Capacity);
                    if (len > bestLen)
                    {
                        bestLen = len;
                        best = hwnd;
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch
        {
        }

        if (best == IntPtr.Zero)
        {
            return 0;
        }

        return GetWindowThreadProcessId(best, out _);
    }

    /// <summary>诊断：把本进程顶层窗口的标题与所属线程列出来。</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        foreach (IntPtr hwnd in TopLevelHandles())
        {
            var text = new StringBuilder(512);
            GetWindowTextW(hwnd, text, text.Capacity);
            string title = text.ToString();
            if (title.Length == 0)
            {
                continue;
            }

            uint tid = GetWindowThreadProcessId(hwnd, out _);
            sb.Append("0x").Append(hwnd.ToInt64().ToString("X"))
              .Append(" tid=").Append(tid)
              .Append(" vis=").Append(IsWindowVisible(hwnd))
              .Append(" '").Append(title).Append("'").AppendLine();
        }

        return sb.Length == 0 ? "(没有带标题的顶层窗口)" : sb.ToString();
    }
}

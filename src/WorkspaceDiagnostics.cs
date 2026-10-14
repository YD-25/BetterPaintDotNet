using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PaintDotNet;

namespace LayerChannelLive;

/// <summary>
/// 诊断：枚举本进程所有顶层窗口，对每个窗口尝试 Control.FromHandle，
/// 然后报告它能找到哪些 DocumentWorkspace、各自带不带 Document。
///
/// 为什么需要：切换文档后图层面板读到的仍是旧文档，说明"从控件树找 workspace"
/// 这条路没摸到宿主真正在用的那个实例。这里用实测数据找出正确的入口，
/// 而不是继续猜。
///
/// 只能在 **UI 线程** 上调用（Control.FromHandle 的线程要求）。
/// </summary>
internal static class WorkspaceDiagnostics
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>把探测结果写进日志。</summary>
    public static void Dump(string tag)
    {
        var sb = new StringBuilder();
        sb.Append("WorkspaceDiagnostics[").Append(tag).AppendLine("]");

        uint self = (uint)Environment.ProcessId;
        var handles = new List<IntPtr>();

        try
        {
            EnumWindows(
                (hwnd, _) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid == self && IsWindow(hwnd))
                    {
                        handles.Add(hwnd);
                    }

                    return true;
                },
                IntPtr.Zero);
        }
        catch
        {
        }

        sb.Append("  顶层窗口数=").Append(handles.Count).AppendLine();

        foreach (IntPtr hwnd in handles)
        {
            Control? c;
            try
            {
                c = Control.FromHandle(hwnd);
            }
            catch
            {
                continue;
            }

            if (c is null)
            {
                continue;
            }

            var title = new StringBuilder(256);
            GetWindowTextW(hwnd, title, title.Capacity);

            var found = new List<object>();
            try
            {
                Collect(c, found, 0);
            }
            catch
            {
            }

            if (found.Count == 0)
            {
                continue;
            }

            sb.Append("  hwnd=0x").Append(hwnd.ToInt64().ToString("X"))
              .Append(" '").Append(title.ToString()).Append("'")
              .Append(" type=").Append(c.GetType().Name)
              .Append(" workspace数=").Append(found.Count);

            for (int i = 0; i < found.Count; i++)
            {
                Document? doc = PdnModel.TryGetDocument(found[i]);
                sb.Append("  [").Append(i).Append("]=")
                  .Append(doc is null ? "null" : doc.Width + "x" + doc.Height);
            }

            sb.AppendLine();
        }

        Boot.Log(sb.ToString());
    }

    private static void Collect(Control root, List<object> result, int depth)
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
            Collect(child, result, depth + 1);
        }
    }
}

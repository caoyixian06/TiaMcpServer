using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace TiaMcpServer.Runtime
{
    /// <summary>
    /// TIA Portal Openness 连接授权确认窗口自动点击器（全自动工作流）。
    ///
    /// 背景：外部进程通过 Openness Attach/Start 连接 TIA 时，TIA GUI 会弹出
    /// "Openness 访问确认"模态窗口（标题含 Openness），该窗口不经 TiaPortal.Confirmation
    /// 事件、不设超时，会阻塞连接直到人工点击确认按钮，是自动化工作流的唯一人工卡点。
    ///
    /// 方案：本服务在 MCP 进程内常驻后台线程，每 300ms 用 Win32 EnumWindows 扫描
    /// 属于 TIA 进程（Siemens.Automation.Portal*）且标题含 "Openness" 的可见窗口，
    /// 命中后用 UI Automation 精确点击确认按钮（中文/英文按钮名全覆盖），
    /// UIA 失败时兜底置前台发送回车键（默认按钮）。
    ///
    /// 开关：环境变量 TIA_MCP_AUTO_CONFIRM=0 可关闭（默认开启）。
    /// </summary>
    public static class OpennessDialogAutoClicker
    {
        private static Thread? _thread;
        private static volatile bool _running;

        private static readonly string[] ConfirmButtonNames =
        {
            // 优先级从高到低：YestoallButton(总是允许, 一次授权永久免弹窗) > YesButton(仅本次)
            "YestoallButton", "全部是", "总是允许", "Yestoall", "Always allow",
            "YesButton", "Yes", "允许", "确认", "是", "确定", "接受", "允许访问",
            "OK", "Ok", "ok", "Confirm", "Allow", "Allow access", "Accept", "Apply"
        };

        private static readonly string[] RejectButtonNames =
        {
            "NoButton", "拒绝", "取消", "否", "Deny", "Cancel", "Reject", "Close", "No"
        };

        // 诊断日志去重：同一窗口失败重试时避免刷屏
        private static DateTime _lastDiagTime = DateTime.MinValue;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>启动后台自动点击线程（幂等）。</summary>
        public static void Start()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("TIA_MCP_AUTO_CONFIRM"), "0",
                    StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("[tia-mcp] Openness 授权确认自动点击已关闭 (TIA_MCP_AUTO_CONFIRM=0)");
                return;
            }
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "OpennessDialogAutoClicker"
            };
            _thread.Start();
            Console.Error.WriteLine("[tia-mcp] Openness 授权确认自动点击器已启动 (TIA_MCP_AUTO_CONFIRM=0 可关闭)");
        }

        public static void Stop() => _running = false;

        private static void Loop()
        {
            while (_running)
            {
                try { ScanWindows(); }
                catch { }
                Thread.Sleep(300);
            }
        }

        private static void ScanWindows()
        {
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == 0) return true;

                    try
                    {
                        using (var proc = Process.GetProcessById((int)pid))
                        {
                            if (proc == null ||
                                !proc.ProcessName.StartsWith("Siemens.Automation.Portal",
                                    StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    catch { return true; } // 进程已退出，跳过

                    var len = GetWindowTextLength(hWnd);
                    if (len <= 0) return true;
                    var sb = new StringBuilder(len + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    var title = sb.ToString();
                    if (title.IndexOf("Openness", StringComparison.OrdinalIgnoreCase) < 0)
                        return true;

                    Console.Error.WriteLine($"[tia-mcp] 检测到 Openness 确认窗口: '{title}' (hwnd=0x{hWnd.ToInt64():X})");
                    TryClickConfirm(hWnd);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        private static void TryClickConfirm(IntPtr hWnd)
        {
            // UIA 多轮重试：窗口弹出初期 UIA 树可能未就绪（实测首轮 FindAll 常为空）。
            // 必须精确命中"允许"类按钮；严禁无脑回车——默认按钮可能是"拒绝"，
            // 回车兜底会点到拒绝导致 Openness 返回 Security error。
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (TryClickViaUia(hWnd)) return;
                Thread.Sleep(400);
            }

            // 最后兜底：列出窗口内按钮名（便于诊断），仍不盲点回车。10 秒内只打一次避免刷屏。
            try
            {
                if ((DateTime.UtcNow - _lastDiagTime).TotalSeconds >= 10)
                {
                    _lastDiagTime = DateTime.UtcNow;
                    var root = AutomationElement.FromHandle(hWnd);
                    if (root != null)
                    {
                        var buttons = root.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        var names = new StringBuilder();
                        foreach (AutomationElement b in buttons)
                        {
                            try { names.Append('[').Append(b.Current.Name).Append("] "); } catch { }
                        }
                        Console.Error.WriteLine($"[tia-mcp] Openness 窗口按钮: {names}");
                    }
                }
            }
            catch { }
        }

        private static bool TryClickViaUia(IntPtr hWnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(hWnd);
                if (root == null) return false;
                var buttons = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement b in buttons)
                {
                    string name;
                    try { name = b.Current.Name ?? ""; }
                    catch { continue; }
                    name = name.Trim();
                    if (name.Length == 0) continue;

                    // ★修复★ 命中"拒绝"类按钮时跳过（continue），不能 return false——
                    // 该窗口同时含 No/Close 按钮，直接返回会导致后面的 YesButton 永远点不到
                    bool isReject = false;
                    foreach (var reject in RejectButtonNames)
                    {
                        if (name.IndexOf(reject, StringComparison.OrdinalIgnoreCase) >= 0) { isReject = true; break; }
                    }
                    if (isReject) continue;

                    foreach (var candidate in ConfirmButtonNames)
                    {
                        if (name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            var pattern = b.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                            if (pattern == null) continue;
                            pattern.Invoke();
                            Console.Error.WriteLine($"[tia-mcp] 已自动点击确认按钮: '{name}'");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[tia-mcp] UIA Invoke 失败: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tia-mcp] UIA 探测失败: {ex.Message}");
            }
            return false;
        }
    }
}

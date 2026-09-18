using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace TiaMcpServer;

internal static class HealthReportService
{
    public static string GenerateHtml(EnvironmentCheckReport? report = null, ServerRuntimeState? state = null)
    {
        report ??= EnvironmentCheckService.Run();
        var root = Path.Combine(EnvironmentDiscoveryService.EnsureWorkspace(), "体检报告");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "TIA系统检测报告.html");
        var security = report.FailedCount > 0 ? "红色" : report.WarningCount > 0 ? "黄色" : "绿色";
        var color = report.FailedCount > 0 ? "#b42318" : report.WarningCount > 0 ? "#b54708" : "#067647";
        var selected = report.Discovery.SelectedTia;
        var runtime = state?.Snapshot();

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><title>TIA系统检测报告</title>");
        sb.Append("<style>body{font-family:'Microsoft YaHei UI','Microsoft YaHei',sans-serif;background:#f6f8fb;color:#1f2937;margin:0;padding:28px}main{max-width:1100px;margin:auto}.hero,.card{background:white;border:1px solid #e5e7eb;border-radius:14px;padding:22px;margin-bottom:16px}.hero h1{margin:0 0 8px}.badge{display:inline-block;padding:7px 14px;border-radius:999px;color:white;font-weight:700}table{width:100%;border-collapse:collapse}th,td{padding:12px;border-bottom:1px solid #e5e7eb;text-align:left;vertical-align:top}th{background:#f9fafb}.ok{color:#067647}.warn{color:#b54708}.bad{color:#b42318}.muted{color:#667085}.mono{font-family:Consolas,monospace;word-break:break-all}</style></head><body><main>");
        sb.Append("<section class='hero'><h1>TIA 系统检测报告</h1><div class='muted'>生成时间：").Append(H(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))).Append("</div><p>安全等级：<span class='badge' style='background:").Append(color).Append("'>").Append(security).Append("</span></p>");
        sb.Append("<p>通过 ").Append(report.PassedCount).Append(" 项；提醒 ").Append(report.WarningCount).Append(" 项；失败 ").Append(report.FailedCount).Append(" 项。</p></section>");

        sb.Append("<section class='card'><h2>核心状态</h2><table>");
        Row(sb, "电脑环境", Environment.OSVersion.VersionString, report.FailedCount == 0 ? "可用" : "需处理");
        Row(sb, "TIA Portal", selected == null ? "未发现" : selected.Version + " / " + selected.RootPath, selected == null ? "失败" : "已发现");
        Row(sb, "Openness", selected == null ? "未检测" : (selected.HasOpenness ? "组件完整" : "组件不完整"), selected?.HasOpenness == true ? "正常" : "需处理");
        var installed = report.Discovery.Clients.Where(x => x.Installed).ToList();
        Row(sb, "AI 客户端", installed.Count == 0 ? "未发现" : string.Join("、", installed.Select(x => x.DisplayName)), installed.Count == 0 ? "提醒" : "已发现");
        Row(sb, "工作空间", report.Discovery.WorkspaceRoot, Directory.Exists(report.Discovery.WorkspaceRoot) ? "正常" : "需处理");
        if (runtime != null)
        {
            Row(sb, "MCP", runtime.ServerRunning ? "运行中 / " + runtime.Version : "未运行", runtime.ServerRunning ? "正常" : "需处理");
            Row(sb, "当前控制端", string.IsNullOrWhiteSpace(runtime.ControllerClientName) ? "未确定" : runtime.ControllerClientName, runtime.IsController ? "当前实例可写" : "当前实例只读");
            Row(sb, "TIA 会话", runtime.TiaConnected ? "已连接" : "未连接", runtime.TiaConnected ? "正常" : "提醒");
            Row(sb, "工程", string.IsNullOrWhiteSpace(runtime.TiaProjectPath) ? "未读取" : runtime.TiaProjectPath, string.IsNullOrWhiteSpace(runtime.TiaProjectPath) ? "提醒" : "可访问");
        }
        sb.Append("</table></section>");

        sb.Append("<section class='card'><h2>逐项检查与解决办法</h2><table><tr><th>项目</th><th>结果</th><th>说明</th><th>解决办法</th></tr>");
        foreach (var item in report.Items)
        {
            var level = item.Level == EnvironmentCheckLevel.Passed ? "通过" : item.Level == EnvironmentCheckLevel.Warning ? "提醒" : "失败";
            var cls = item.Level == EnvironmentCheckLevel.Passed ? "ok" : item.Level == EnvironmentCheckLevel.Warning ? "warn" : "bad";
            sb.Append("<tr><td>").Append(H(item.Name)).Append("</td><td class='").Append(cls).Append("'><b>").Append(level).Append("</b></td><td>").Append(H(item.Summary)).Append("</td><td>").Append(H(item.Guidance)).Append("</td></tr>");
        }
        sb.Append("</table></section>");

        sb.Append("<section class='card'><h2>发现的 TIA 安装</h2><table><tr><th>版本</th><th>路径</th><th>Openness</th><th>来源</th></tr>");
        foreach (var tia in report.Discovery.TiaInstallations)
            sb.Append("<tr><td>").Append(H(tia.Version)).Append("</td><td class='mono'>").Append(H(tia.RootPath)).Append("</td><td>").Append(tia.HasOpenness ? "正常" : "缺失/不完整").Append("</td><td>").Append(H(tia.DiscoverySource)).Append("</td></tr>");
        sb.Append("</table></section>");

        sb.Append("<section class='card'><h2>AI 客户端适配状态</h2><table><tr><th>客户端</th><th>安装</th><th>MCP</th><th>配置方式</th><th>位置</th></tr>");
        foreach (var client in report.Discovery.Clients)
            sb.Append("<tr><td>").Append(H(client.DisplayName)).Append("</td><td>").Append(client.Installed ? "已发现" : "未发现").Append("</td><td>").Append(client.Configured ? "已配置" : "未配置").Append("</td><td>").Append(client.SupportsDirectWrite ? "自动写入" : "安全导入").Append("</td><td class='mono'>").Append(H(client.ConfigPath)).Append("</td></tr>");
        sb.Append("</table></section>");

        sb.Append("<section class='card'><h2>路径兼容性说明</h2><p>中文工程路径不会被 V4.1 直接判定为错误，但会作为兼容性风险提示。若 AI 客户端、旧脚本或第三方工具出现启动/引用异常，优先把工程或 MCP 放到短路径，例如 <span class='mono'>D:\\PLC工程\\项目名</span>。</p></section>");
        sb.Append("</main></body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    public static void OpenReport(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string H(string value) => WebUtility.HtmlEncode(value ?? "");

    private static void Row(StringBuilder sb, string name, string value, string status)
    {
        sb.Append("<tr><th>").Append(H(name)).Append("</th><td class='mono'>").Append(H(value)).Append("</td><td>").Append(H(status)).Append("</td></tr>");
    }
}

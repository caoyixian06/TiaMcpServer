using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

internal sealed class ChangeAuditContext
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public Stopwatch Stopwatch { get; set; } = Stopwatch.StartNew();
    public string ToolName { get; set; } = "";
    public string ToolDescription { get; set; } = "";
    public ToolMetadata Metadata { get; set; } = new ToolMetadata();
    public string ProjectName { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string SnapshotPath { get; set; } = "";
    public string ExecutionMode { get; set; } = "";
    public bool ApprovalRequired { get; set; }
    public bool ApprovalGranted { get; set; }
}

/// <summary>
/// 统一变更审计：敏感参数脱敏、变更前安全归档、JSON/HTML 报告以及界面摘要。
/// </summary>
internal sealed class ChangeAuditService
{
    private readonly ServerRuntimeState _state;
    private readonly string _dataRoot;
    private readonly string _reportsDir;
    private readonly string _snapshotsDir;

    public ChangeAuditService(ServerRuntimeState state)
    {
        _state = state;
        var configuredRoot = Environment.GetEnvironmentVariable("TIA_MCP_DATA_DIR");
        _dataRoot = !string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(configuredRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "博途智能工程助手", "运行数据");
        _reportsDir = Path.Combine(_dataRoot, "变更报告");
        _snapshotsDir = Path.Combine(_dataRoot, "安全快照");
        Directory.CreateDirectory(_reportsDir);
        Directory.CreateDirectory(_snapshotsDir);
    }

    public string ReportsDirectory => _reportsDir;
    public string SnapshotsDirectory => _snapshotsDir;

    public ChangeAuditContext Begin(string toolName, string description, ToolMetadata metadata, JObject args,
        RuntimeStatusSnapshot status, PortalService? portal, bool approvalRequired, bool approvalGranted)
    {
        var ctx = new ChangeAuditContext
        {
            ToolName = toolName ?? "",
            ToolDescription = description ?? "",
            Metadata = metadata,
            ProjectName = status.TiaProjectName ?? "",
            ProjectPath = status.TiaProjectPath ?? "",
            ArgumentsJson = RedactArguments(args).ToString(Formatting.Indented),
            ExecutionMode = status.ExecutionMode.ToString(),
            ApprovalRequired = approvalRequired,
            ApprovalGranted = approvalGranted
        };

        if (metadata.RequiresSnapshot && portal != null && status.TiaConnected && !string.IsNullOrWhiteSpace(status.TiaProjectName))
        {
            try
            {
                ctx.SnapshotPath = portal.CreateSafetySnapshot(_snapshotsDir, "变更前安全快照");
                if (!string.IsNullOrWhiteSpace(ctx.SnapshotPath))
                    _state.AddLog("变更前安全快照已创建");
            }
            catch (Exception ex)
            {
                // ★修复★ 快照归档失败不应阻止工程写入：TIA 归档可能因项目状态/瞬时问题失败，
                // 把它当作写入门槛会导致工程操作整体不可用。降级为记录警告并继续写入。
                _state.AddLog("变更前安全快照创建失败（已降级继续写入）：" + ex.Message);
            }
        }

        return ctx;
    }

    public void Complete(ChangeAuditContext ctx, bool success, string resultText, string errorText = "")
    {
        if (ctx == null) return;
        ctx.Stopwatch.Stop();

        try
        {
            var finishedAt = DateTime.Now;
            var resultSummary = success ? "成功" : "失败";
            var safeResult = SanitizeResult(resultText);
            var safeError = string.IsNullOrWhiteSpace(errorText) ? "" : errorText;

            var report = new JObject
            {
                ["记录编号"] = ctx.Id,
                ["开始时间"] = ctx.StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ["结束时间"] = finishedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ["耗时毫秒"] = ctx.Stopwatch.ElapsedMilliseconds,
                ["执行结果"] = resultSummary,
                ["操作说明"] = ctx.ToolDescription,
                ["内部工具标识"] = ctx.ToolName,
                ["风险等级"] = RiskText(ctx.Metadata.RiskLevel),
                ["影响范围"] = ScopeText(ctx.Metadata.Scope),
                ["执行模式"] = ModeText(ctx.ExecutionMode),
                ["是否修改工程"] = ctx.Metadata.MutatesProject,
                ["是否涉及在线设备"] = ctx.Metadata.TouchesOnlineDevice,
                ["是否需要审批"] = ctx.ApprovalRequired,
                ["是否获得审批"] = ctx.ApprovalGranted,
                ["工程名称"] = ctx.ProjectName,
                ["工程路径"] = ctx.ProjectPath,
                ["安全快照"] = ctx.SnapshotPath,
                ["调用参数_已脱敏"] = JToken.Parse(ctx.ArgumentsJson),
                ["返回摘要"] = safeResult,
                ["错误信息"] = safeError
            };

            var baseName = finishedAt.ToString("yyyyMMdd_HHmmss_fff") + "_" + ctx.Id.Substring(0, 8);
            var jsonPath = Path.Combine(_reportsDir, baseName + ".json");
            var htmlPath = Path.Combine(_reportsDir, baseName + ".html");
            File.WriteAllText(jsonPath, report.ToString(Formatting.Indented), new UTF8Encoding(false));
            File.WriteAllText(htmlPath, BuildHtml(report), new UTF8Encoding(false));

            _state.AddChangeRecord(new ChangeRecordSummary
            {
                Id = ctx.Id,
                Time = finishedAt,
                Operation = string.IsNullOrWhiteSpace(ctx.ToolDescription) ? "工程操作" : ctx.ToolDescription,
                RiskLevel = ctx.Metadata.RiskLevel,
                Result = resultSummary,
                ProjectName = ctx.ProjectName,
                ReportPath = htmlPath,
                SnapshotPath = ctx.SnapshotPath
            });
            _state.AddLog("变更审计报告已保存");
        }
        catch (Exception ex)
        {
            _state.AddLog("变更审计报告写入失败：" + ex.Message);
        }
    }

    private static JObject RedactArguments(JObject source)
    {
        var clone = source == null ? new JObject() : (JObject)source.DeepClone();
        RedactToken(clone);
        return clone;
    }

    private static void RedactToken(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (var prop in obj.Properties().ToList())
            {
                var key = prop.Name.ToLowerInvariant();
                if (key.Contains("password") || key.Contains("secret") || key.Contains("token") ||
                    key.Contains("credential") || key.Contains("pin") || key.Contains("apikey") || key.Contains("api_key"))
                {
                    prop.Value = "***已脱敏***";
                }
                else
                {
                    RedactToken(prop.Value);
                }
            }
        }
        else if (token is JArray arr)
        {
            foreach (var item in arr) RedactToken(item);
        }
    }

    private static string SanitizeResult(string resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText)) return "";
        var text = resultText.Trim();
        if (text.Length > 8000) text = text.Substring(0, 8000) + "\n……内容过长，已截断……";
        try
        {
            var token = JToken.Parse(text);
            RedactToken(token);
            return token.ToString(Formatting.Indented);
        }
        catch
        {
            return text;
        }
    }

    private static string BuildHtml(JObject report)
    {
        string Cell(string name)
        {
            var value = report[name];
            var text = value == null ? "" : (value.Type == JTokenType.String ? value.ToString() : value.ToString(Formatting.Indented));
            return WebUtility.HtmlEncode(text);
        }

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>博途智能工程助手变更报告</title>");
        sb.Append("<style>body{font-family:'Microsoft YaHei UI','Microsoft YaHei',sans-serif;margin:32px;color:#222}h1{font-size:24px}table{border-collapse:collapse;width:100%;margin:18px 0}th,td{border:1px solid #ddd;padding:10px;text-align:left;vertical-align:top}th{width:180px;background:#f5f5f5}pre{white-space:pre-wrap;word-break:break-word;background:#f7f7f7;padding:12px;border-radius:6px}</style></head><body>");
        sb.Append("<h1>博途智能工程助手变更报告</h1><table>");
        foreach (var key in new[] { "记录编号", "开始时间", "结束时间", "耗时毫秒", "执行结果", "操作说明", "风险等级", "影响范围", "执行模式", "是否修改工程", "是否涉及在线设备", "是否需要审批", "是否获得审批", "工程名称", "工程路径", "安全快照" })
            sb.Append("<tr><th>").Append(WebUtility.HtmlEncode(key)).Append("</th><td>").Append(Cell(key)).Append("</td></tr>");
        sb.Append("</table><h2>调用参数（已脱敏）</h2><pre>").Append(Cell("调用参数_已脱敏")).Append("</pre>");
        sb.Append("<h2>返回摘要</h2><pre>").Append(Cell("返回摘要")).Append("</pre>");
        if (!string.IsNullOrWhiteSpace(Cell("错误信息"))) sb.Append("<h2>错误信息</h2><pre>").Append(Cell("错误信息")).Append("</pre>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string RiskText(ToolRiskLevel level)
    {
        switch (level)
        {
            case ToolRiskLevel.Low: return "低";
            case ToolRiskLevel.Medium: return "中";
            case ToolRiskLevel.High: return "高";
            case ToolRiskLevel.Critical: return "关键";
            default: return "未知";
        }
    }

    private static string ScopeText(ToolOperationScope scope)
    {
        switch (scope)
        {
            case ToolOperationScope.ReadOnly: return "只读查询";
            case ToolOperationScope.Session: return "工程会话";
            case ToolOperationScope.Project: return "离线工程";
            case ToolOperationScope.OnlineDevice: return "在线设备";
            case ToolOperationScope.FileSystem: return "本机文件";
            case ToolOperationScope.Security: return "安全与权限";
            default: return "未知";
        }
    }

    private static string ModeText(string mode)
    {
        switch (mode)
        {
            case "Observe": return "只读观察";
            case "Sandbox": return "沙盒保护";
            case "Engineering": return "工程编辑";
            case "Full": return "完整权限";
            default: return mode;
        }
    }
}

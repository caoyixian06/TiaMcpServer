using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;

namespace TiaMcpServer;

internal enum EnvironmentCheckLevel
{
    Passed,
    Warning,
    Failed
}

internal sealed class EnvironmentCheckItem
{
    public string Name { get; set; } = "";
    public EnvironmentCheckLevel Level { get; set; }
    public string Summary { get; set; } = "";
    public string Guidance { get; set; } = "";
}

internal sealed class EnvironmentCheckReport
{
    public List<EnvironmentCheckItem> Items { get; } = new List<EnvironmentCheckItem>();
    public EnvironmentDiscoverySnapshot Discovery { get; set; } = new EnvironmentDiscoverySnapshot();
    public bool CanRun => Items.All(x => x.Level != EnvironmentCheckLevel.Failed);
    public int PassedCount => Items.Count(x => x.Level == EnvironmentCheckLevel.Passed);
    public int WarningCount => Items.Count(x => x.Level == EnvironmentCheckLevel.Warning);
    public int FailedCount => Items.Count(x => x.Level == EnvironmentCheckLevel.Failed);
}

internal static class EnvironmentCheckService
{
    public static EnvironmentCheckReport Run()
    {
        var report = new EnvironmentCheckReport { Discovery = EnvironmentDiscoveryService.Discover() };
        CheckWindows(report);
        CheckDotNetFramework(report);
        CheckTia(report);
        CheckOpenness(report);
        CheckUserPermission(report);
        CheckWorkspace(report);
        CheckClients(report);
        CheckServerFiles(report);
        return report;
    }

    /// <summary>保留旧调用兼容；V4.1 返回当前自动选择的 TIA 根目录。</summary>
    public static string FindTiaV19Root() => EnvironmentDiscoveryService.Discover().SelectedTia?.RootPath ?? "";

    private static void CheckWindows(EnvironmentCheckReport report)
    {
        var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        report.Items.Add(new EnvironmentCheckItem
        {
            Name = "操作系统",
            Level = windows ? EnvironmentCheckLevel.Passed : EnvironmentCheckLevel.Failed,
            Summary = windows ? "已检测到受支持的 Windows 系统。" : "当前不是受支持的 Windows 系统。",
            Guidance = windows ? "无需处理。" : "请在安装 TIA Portal 的 Windows 电脑上运行本程序。"
        });
    }

    private static void CheckDotNetFramework(EnvironmentCheckReport report)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            report.Items.Add(new EnvironmentCheckItem { Name = "运行框架", Level = EnvironmentCheckLevel.Failed, Summary = "无法检测 .NET Framework。", Guidance = "请使用 Windows 并安装 .NET Framework 4.8。" });
            return;
        }
        var release = 0;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\");
            if (key?.GetValue("Release") is int r) release = r;
        }
        catch { }
        var ok = release >= 528040;
        report.Items.Add(new EnvironmentCheckItem { Name = "运行框架", Level = ok ? EnvironmentCheckLevel.Passed : EnvironmentCheckLevel.Failed, Summary = ok ? ".NET Framework 版本满足要求。" : "没有检测到 .NET Framework 4.8。", Guidance = ok ? "无需处理。" : "请安装 .NET Framework 4.8 后重新启动电脑。" });
    }

    private static void CheckTia(EnvironmentCheckReport report)
    {
        var installs = report.Discovery.TiaInstallations;
        var selected = report.Discovery.SelectedTia;
        if (installs.Count == 0 || selected == null)
        {
            report.Items.Add(new EnvironmentCheckItem { Name = "TIA Portal", Level = EnvironmentCheckLevel.Failed, Summary = "没有发现可用的 TIA Portal 安装。", Guidance = "可安装 TIA Portal，或设置 TIA_PORTAL_DIR 指向实际安装目录；V4.1 会扫描不同盘符。" });
            return;
        }
        var versions = string.Join("、", installs.Select(x => string.IsNullOrWhiteSpace(x.Version) ? "未知版本" : x.Version).Distinct());
        report.Items.Add(new EnvironmentCheckItem
        {
            Name = "TIA Portal",
            Level = EnvironmentCheckLevel.Passed,
            Summary = $"发现 {installs.Count} 个安装：{versions}；当前自动选择 {selected.Version}。",
            Guidance = $"当前目录：{selected.RootPath}。需要固定版本时可设置 TIA_PORTAL_DIR 或 TIA_PORTAL_VERSION。"
        });
    }

    private static void CheckOpenness(EnvironmentCheckReport report)
    {
        var selected = report.Discovery.SelectedTia;
        if (selected == null)
        {
            report.Items.Add(new EnvironmentCheckItem { Name = "TIA Portal Openness", Level = EnvironmentCheckLevel.Failed, Summary = "因为没有找到 TIA Portal，所以无法检查 Openness。", Guidance = "先安装 TIA Portal，再重新检测。" });
            return;
        }
        report.Items.Add(new EnvironmentCheckItem
        {
            Name = "TIA Portal Openness",
            Level = selected.HasOpenness ? EnvironmentCheckLevel.Passed : EnvironmentCheckLevel.Failed,
            Summary = selected.HasOpenness ? $"{selected.Version} 的 Openness 组件完整。" : $"已找到 {selected.Version}，但没有找到完整 Openness 组件。",
            Guidance = selected.HasOpenness ? "无需处理。" : "打开西门子安装程序，为当前 TIA 版本补装 TIA Portal Openness，然后重新登录 Windows。"
        });
    }

    private static void CheckUserPermission(EnvironmentCheckReport report)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        {
            report.Items.Add(new EnvironmentCheckItem { Name = "Openness 用户权限", Level = EnvironmentCheckLevel.Warning, Summary = "当前无法检查 Openness 用户组。", Guidance = "请在 Windows 电脑上重新检测。" });
            return;
        }
        var found = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            foreach (var sid in identity.Groups ?? new IdentityReferenceCollection())
            {
                try
                {
                    var account = sid.Translate(typeof(NTAccount)).Value ?? "";
                    if (account.IndexOf("Siemens TIA Openness", StringComparison.OrdinalIgnoreCase) >= 0) { found = true; break; }
                }
                catch { }
            }
        }
        catch { }
        report.Items.Add(new EnvironmentCheckItem { Name = "Openness 用户权限", Level = found ? EnvironmentCheckLevel.Passed : EnvironmentCheckLevel.Warning, Summary = found ? "当前账户已加入 Siemens TIA Openness 用户组。" : "没有确认当前账户属于 Siemens TIA Openness 用户组。", Guidance = found ? "无需处理。" : "若连接 TIA 失败，把当前 Windows 账户加入 Siemens TIA Openness 用户组后注销并重新登录。" });
    }

    private static void CheckWorkspace(EnvironmentCheckReport report)
    {
        var root = report.Discovery.WorkspaceRoot;
        try
        {
            Directory.CreateDirectory(root);
            var test = Path.Combine(root, "写入检测.tmp");
            File.WriteAllText(test, "检测");
            File.Delete(test);
            report.Items.Add(new EnvironmentCheckItem { Name = "工作空间", Level = EnvironmentCheckLevel.Passed, Summary = "工作空间已初始化并且可以写入。", Guidance = root });
        }
        catch
        {
            report.Items.Add(new EnvironmentCheckItem { Name = "工作空间", Level = EnvironmentCheckLevel.Failed, Summary = "工作空间无法创建或写入。", Guidance = "检查磁盘与账户权限，或设置 TIA_MCP_WORKSPACE 到可写目录。" });
        }
    }

    private static void CheckClients(EnvironmentCheckReport report)
    {
        var installed = report.Discovery.Clients.Where(x => x.Installed).ToList();
        var configured = installed.Where(x => x.Configured).ToList();
        report.Items.Add(new EnvironmentCheckItem
        {
            Name = "AI 客户端",
            Level = installed.Count == 0 ? EnvironmentCheckLevel.Warning : EnvironmentCheckLevel.Passed,
            Summary = installed.Count == 0 ? "没有自动发现支持的 AI 客户端。" : $"发现 {installed.Count} 个客户端，其中 {configured.Count} 个已配置本 MCP。",
            Guidance = installed.Count == 0 ? "可先安装 OpenCode、Claude Desktop、Cursor、VS Code/Copilot 等任一支持的客户端。" : string.Join("、", installed.Select(x => x.DisplayName))
        });
    }

    private static void CheckServerFiles(EnvironmentCheckReport report)
    {
        var baseDir = AppContext.BaseDirectory;
        var policy = File.Exists(Path.Combine(baseDir, "server_policy.json"));
        var filter = File.Exists(Path.Combine(baseDir, "tools_filter.json"));
        var ok = policy && filter;
        report.Items.Add(new EnvironmentCheckItem { Name = "服务器配置文件", Level = ok ? EnvironmentCheckLevel.Passed : EnvironmentCheckLevel.Failed, Summary = ok ? "服务器配置文件完整。" : "服务器配置文件缺失。", Guidance = ok ? "无需处理。" : "请重新解压完整发布包，不要只复制主程序。" });
    }
}

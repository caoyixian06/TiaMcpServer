using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TiaMcpServer;

internal sealed class TiaInstallationInfo
{
    public string Version { get; set; } = "";
    public int VersionNumber { get; set; }
    public string RootPath { get; set; } = "";
    public string BinPath => Path.Combine(RootPath, "Bin");
    public string PublicApiPath { get; set; } = "";
    public bool HasOpenness { get; set; }
    public string DiscoverySource { get; set; } = "";
    public bool Exists => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);
    public string ProjectExtension => VersionNumber > 0 ? ".ap" + VersionNumber : ".ap17";
    public string ArchiveExtension => VersionNumber > 0 ? ".zap" + VersionNumber : ".zap17";
    public override string ToString() => string.IsNullOrWhiteSpace(Version) ? RootPath : $"{Version}  {RootPath}";
}

internal sealed class ClientInstallationInfo
{
    public SupportedClient Client { get; set; }
    public string DisplayName { get; set; } = "";
    public bool Installed { get; set; }
    public string InstallPath { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public bool Configured { get; set; }
    public bool SupportsDirectWrite { get; set; } = true;
}

internal sealed class EnvironmentDiscoverySnapshot
{
    public List<TiaInstallationInfo> TiaInstallations { get; } = new List<TiaInstallationInfo>();
    public List<ClientInstallationInfo> Clients { get; } = new List<ClientInstallationInfo>();
    public TiaInstallationInfo? SelectedTia { get; set; }
    public string WorkspaceRoot { get; set; } = "";
}

/// <summary>
/// V4.1 环境发现中心：不假设软件安装在 C 盘，也不把 TIA 版本写死为 V19。
/// 发现顺序（性能优先）：注册表 -> 各盘 Siemens\Automation 标准目录 -> 显式环境变量 ->
/// 全盘递归（仅最后手段，每盘 30 秒超时）。
/// </summary>
internal static class EnvironmentDiscoveryService
{
    private static readonly Regex PortalFolderRegex = new Regex(@"Portal\s+V(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static EnvironmentDiscoverySnapshot Discover()
    {
        var snapshot = new EnvironmentDiscoverySnapshot();
        snapshot.WorkspaceRoot = EnsureWorkspace();
        snapshot.TiaInstallations.AddRange(DiscoverTiaInstallations());
        snapshot.SelectedTia = SelectBestTia(snapshot.TiaInstallations);
        snapshot.Clients.AddRange(ClientConfigService.DiscoverClients());
        return snapshot;
    }

    public static IReadOnlyList<TiaInstallationInfo> DiscoverTiaInstallations()
    {
        var found = new Dictionary<string, TiaInstallationInfo>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string path, string source, string versionHint = "")
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
            catch { return; }
            if (!Directory.Exists(path)) return;

            var version = NormalizeVersion(versionHint);
            if (string.IsNullOrWhiteSpace(version)) version = VersionFromPath(path);
            if (string.IsNullOrWhiteSpace(version)) version = DetectVersionFromPublicApi(path);
            var number = ParseVersionNumber(version);
            var api = ResolvePublicApiDirectory(path, version);
            var engineering = string.IsNullOrWhiteSpace(api) ? "" : Path.Combine(api, "Siemens.Engineering.dll");
            var contractA = Path.Combine(path, "Bin", "PublicAPI", "Siemens.Engineering.Contract.dll");
            var contractB = string.IsNullOrWhiteSpace(api) ? "" : Path.Combine(api, "Siemens.Engineering.Contract.dll");
            var openness = File.Exists(engineering) && (File.Exists(contractA) || File.Exists(contractB));

            var key = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (found.TryGetValue(key, out var existing))
            {
                if (!existing.HasOpenness && openness) existing.HasOpenness = true;
                if (string.IsNullOrWhiteSpace(existing.Version) && !string.IsNullOrWhiteSpace(version))
                {
                    existing.Version = version;
                    existing.VersionNumber = number;
                }
                if (string.IsNullOrWhiteSpace(existing.PublicApiPath) && !string.IsNullOrWhiteSpace(api)) existing.PublicApiPath = api;
                return;
            }

            found[key] = new TiaInstallationInfo
            {
                Version = version,
                VersionNumber = number,
                RootPath = key,
                PublicApiPath = api,
                HasOpenness = openness,
                DiscoverySource = source
            };
        }

        // 发现顺序（性能优先）：注册表 -> 各盘 Siemens\Automation 标准目录 -> 显式环境变量 -> 全盘递归（仅最后手段，带超时）。
        foreach (var item in DiscoverFromRegistry()) AddCandidate(item.path, "注册表", item.version);

        foreach (var root in EnumerateLocalDriveRoots())
        {
            foreach (var pf in new[] { "Program Files", "Program Files (x86)" })
            {
                var automation = Path.Combine(root, pf, "Siemens", "Automation");
                ScanAutomationDirectory(automation, AddCandidate);
            }
            // 兼容用户把 Portal 直接安装到自定义盘根目录下的常见布局。
            foreach (var custom in new[] { "Siemens\\Automation", "Automation", "TIA", "Portal" })
                ScanAutomationDirectory(Path.Combine(root, custom), AddCandidate);
        }

        AddCandidate(Environment.GetEnvironmentVariable("TIA_PORTAL_DIR") ?? "", "环境变量", Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION") ?? "");

        // 全盘递归仅在所有快速扫描均未命中时执行（最后手段），避免拖慢启动。
        if (found.Count == 0)
        {
            foreach (var root in EnumerateLocalDriveRoots())
                ScanPortalEverywhere(root, AddCandidate);
        }

        return found.Values
            .OrderByDescending(x => x.HasOpenness)
            .ThenByDescending(x => x.VersionNumber)
            .ThenBy(x => x.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }


    // 全盘扫描 Portal Vxx（最后手段），支持任意盘符和自定义安装路径。
    // 用 Task + 30 秒超时兜底：超时则中止该盘扫描，避免全盘递归拖慢启动。
    private static void ScanPortalEverywhere(string root, Action<string, string, string> add)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            var task = Task.Run(() => EnumeratePortalDirectories(root));
            if (!task.Wait(TimeSpan.FromSeconds(30)))
                return; // 超时：丢弃结果，中止该盘扫描（后台枚举不再影响主流程）

            foreach (var dir in task.Result)
            {
                add(dir, "全盘扫描", VersionFromPath(dir));
            }
        }
        catch
        {
        }
    }

    private static List<string> EnumeratePortalDirectories(string root)
    {
        var dirs = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(
                     root,
                     "Portal V*",
                     SearchOption.AllDirectories))
        {
            dirs.Add(dir);
        }
        return dirs;
    }

    private static void ScanAutomationDirectory(string automation, Action<string, string, string> add)
    {
        try
        {
            if (!Directory.Exists(automation)) return;
            foreach (var dir in Directory.GetDirectories(automation, "Portal V*", SearchOption.TopDirectoryOnly))
                add(dir, "磁盘扫描", VersionFromPath(dir));
        }
        catch { }
    }

    private static IEnumerable<(string path, string version)> DiscoverFromRegistry()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                RegistryKey? portal = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    portal = baseKey.OpenSubKey(@"SOFTWARE\Siemens\Automation\Portal");
                    if (portal == null) continue;
                    foreach (var sub in portal.GetSubKeyNames())
                    {
                        using var key = portal.OpenSubKey(sub);
                        var path = key?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(path)) yield return (path!, NormalizeVersion(sub));
                    }
                }
                finally
                {
                    portal?.Dispose();
                    baseKey?.Dispose();
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateLocalDriveRoots()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); } catch { drives = Array.Empty<DriveInfo>(); }
            foreach (var drive in drives)
            {
                bool ready;
                try { ready = drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable); }
                catch { ready = false; }
                if (ready) yield return drive.RootDirectory.FullName;
            }
            yield break;
        }

        yield return Path.GetPathRoot(Environment.CurrentDirectory) ?? Path.DirectorySeparatorChar.ToString();
    }

    public static TiaInstallationInfo? SelectBestTia(IEnumerable<TiaInstallationInfo> installs)
    {
        var all = installs.Where(x => x.Exists).ToList();
        if (all.Count == 0) return null;

        TiaInstallationInfo chosen;

        var preferredDir = Environment.GetEnvironmentVariable("TIA_PORTAL_DIR");
        if (!string.IsNullOrWhiteSpace(preferredDir))
        {
            var exact = all.FirstOrDefault(x => PathsEqual(x.RootPath, preferredDir));
            if (exact != null) chosen = exact;
            else chosen = PickDefault();
        }
        else
        {
            var preferredVersion = ParseVersionNumber(Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION") ?? "");
            if (preferredVersion > 0)
            {
                var exactVersion = all.Where(x => x.VersionNumber == preferredVersion)
                    .OrderByDescending(x => x.HasOpenness).FirstOrDefault();
                if (exactVersion != null) chosen = exactVersion;
                else chosen = PickDefault();
            }
            else
            {
                chosen = PickDefault();
            }
        }

        // ★MCP 协议★ 必须写 stderr：stdout 是 MCP JSON-RPC 通道，混入日志行会破坏客户端解析
        Console.Error.WriteLine($"[tia-mcp] 绑定 TIA Portal 版本: {chosen.Version} ({chosen.RootPath})");
        return chosen;

        // 无显式指定时优先选择 V17（与 CurrentEngineeringVersion 默认一致），无 V17 才选最高版本。
        TiaInstallationInfo PickDefault()
        {
            var v17 = all.Where(x => x.VersionNumber == 17)
                .OrderByDescending(x => x.HasOpenness)
                .FirstOrDefault();
            return v17 ?? all.OrderByDescending(x => x.HasOpenness).ThenByDescending(x => x.VersionNumber).First();
        }
    }

    public static string EnsureWorkspace()
    {
        var configured = Environment.GetEnvironmentVariable("TIA_MCP_WORKSPACE");
        var root = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "博途智能工程助手", "工作空间");
        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "工程副本"));
            Directory.CreateDirectory(Path.Combine(root, "客户端配置备份"));
            Directory.CreateDirectory(Path.Combine(root, "体检报告"));
            Directory.CreateDirectory(Path.Combine(root, "客户端配置导入"));
        }
        catch { }
        return root;
    }

    public static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        // 锚定到开头，且两位数字后必须跟非数字或结尾，避免 "V180" 之类误判为 V18。
        var m = Regex.Match(value, @"^V?\s*(\d{2})(?!\d)", RegexOptions.IgnoreCase);
        return m.Success ? "V" + m.Groups[1].Value : "";
    }

    public static int ParseVersionNumber(string value)
    {
        var v = NormalizeVersion(value);
        if (string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(value))
        {
            // 兼容 ".ap17"/".zap17" 扩展名入参（ProjectWizardForm 等 UI 从文件扩展名取版本）。
            // 锚定到开头且限两位数字，不会引入普通子串误判。
            var extMatch = Regex.Match(value, @"^\.(?:ap|zap)(\d{2})(?!\d)", RegexOptions.IgnoreCase);
            if (extMatch.Success) v = "V" + extMatch.Groups[1].Value;
        }
        return int.TryParse(v.TrimStart('V', 'v'), out var n) ? n : 0;
    }

    private static string VersionFromPath(string path)
    {
        var m = PortalFolderRegex.Match(path ?? "");
        return m.Success ? "V" + m.Groups["n"].Value : "";
    }

    private static string DetectVersionFromPublicApi(string root)
    {
        try
        {
            var p = Path.Combine(root, "PublicAPI");
            if (!Directory.Exists(p)) return "";
            return Directory.GetDirectories(p, "V*", SearchOption.TopDirectoryOnly)
                .Select(VersionFromPath)
                .Where(x => ParseVersionNumber(x) > 0)
                .OrderByDescending(ParseVersionNumber)
                .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    public static string ResolvePublicApiDirectory(string root, string version)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        var normalized = NormalizeVersion(version);
        var explicitPath = string.IsNullOrWhiteSpace(normalized) ? "" : Path.Combine(root, "PublicAPI", normalized);
        if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath)) return explicitPath;
        try
        {
            var apiRoot = Path.Combine(root, "PublicAPI");
            if (!Directory.Exists(apiRoot)) return "";
            return Directory.GetDirectories(apiRoot, "V*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => ParseVersionNumber(Path.GetFileName(x)))
                .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    public static string CurrentEngineeringVersion()
    {
        var v = NormalizeVersion(Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION") ?? "");
        return string.IsNullOrWhiteSpace(v) ? "V17" : v;
    }

    public static string CurrentProjectExtension()
    {
        var n = ParseVersionNumber(Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION") ?? "");
        return n > 0 ? ".ap" + n : ".ap17";
    }

    public static string CurrentArchiveExtension()
    {
        var n = ParseVersionNumber(Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION") ?? "");
        return n > 0 ? ".zap" + n : ".zap17";
    }

    public static bool IsTiaProjectFile(string path, bool includeArchive = true)
    {
        var ext = Path.GetExtension(path ?? "");
        if (Regex.IsMatch(ext, @"^\.ap\d+$", RegexOptions.IgnoreCase)) return true;
        return includeArchive && Regex.IsMatch(ext, @"^\.zap\d+$", RegexOptions.IgnoreCase);
    }

    public static bool ContainsNonAscii(string path) => !string.IsNullOrEmpty(path) && path.Any(c => c > 127);

    public static string DescribePathRisk(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "路径为空。";
        var risks = new List<string>();
        if (ContainsNonAscii(path)) risks.Add("路径包含中文或其他非 ASCII 字符；TIA 本身通常可以处理，但部分第三方脚本、旧插件或命令行工具可能处理失败。");
        if (path.Length >= 220) risks.Add("路径较长，接近旧版 Windows/工具链常见路径长度风险区间。");
        if (path.Contains("&") || path.Contains("(") || path.Contains(")") || path.Contains("!")) risks.Add("路径包含命令行敏感字符，某些客户端启动器可能需要额外转义。");
        if (risks.Count == 0) return "未发现明显路径兼容性风险。";
        return string.Join(" ", risks) + " 建议优先使用短路径，例如 D:\\PLC工程\\项目名。";
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}

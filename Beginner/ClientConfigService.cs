using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

internal enum SupportedClient
{
    OpenCode,
    ClaudeDesktop,
    Cursor,
    VsCodeCopilot,
    CherryStudio,
    Cline,
    RooCode,
    Continue,
    Codex,
    Trae,
    Windsurf
}

internal sealed class ClientConfigResult
{
    public bool Success { get; set; }
    public bool RequiresManualImport { get; set; }
    public string Message { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string Verification { get; set; } = "";
}

internal static class ClientConfigService
{
    private const string ServerKey = "tia-portal";

    public static IReadOnlyList<SupportedClient> AllClients { get; } = new[]
    {
        SupportedClient.OpenCode,
        SupportedClient.ClaudeDesktop,
        SupportedClient.Cursor,
        SupportedClient.VsCodeCopilot,
        SupportedClient.CherryStudio,
        SupportedClient.Cline,
        SupportedClient.RooCode,
        SupportedClient.Continue,
        SupportedClient.Codex,
        SupportedClient.Trae,
        SupportedClient.Windsurf
    };

    public static string GetDisplayName(SupportedClient client)
    {
        switch (client)
        {
            case SupportedClient.OpenCode: return "OpenCode";
            case SupportedClient.ClaudeDesktop: return "Claude Desktop";
            case SupportedClient.Cursor: return "Cursor";
            case SupportedClient.VsCodeCopilot: return "VS Code + Copilot";
            case SupportedClient.CherryStudio: return "Cherry Studio";
            case SupportedClient.Cline: return "Cline";
            case SupportedClient.RooCode: return "Roo Code";
            case SupportedClient.Continue: return "Continue";
            case SupportedClient.Codex: return "Codex";
            case SupportedClient.Trae: return "Trae";
            case SupportedClient.Windsurf: return "Windsurf";
            default: return client.ToString();
        }
    }

    public static bool SupportsDirectWrite(SupportedClient client)
    {
        // Cherry Studio 与 Trae 的公开文档以应用内 MCP 设置/导入为主，没有稳定公开的全局 JSON 存储契约。
        // V4.1 对这两个客户端默认生成可导入配置，避免直接修改 Electron 内部数据库。
        return client != SupportedClient.CherryStudio && client != SupportedClient.Trae;
    }

    public static List<ClientInstallationInfo> DiscoverClients()
    {
        return AllClients.Select(client =>
        {
            var config = GetConfigPath(client);
            var install = FindInstallPath(client);
            var installed = !string.IsNullOrWhiteSpace(install) || ClientDataExists(client) || File.Exists(config);
            return new ClientInstallationInfo
            {
                Client = client,
                DisplayName = GetDisplayName(client),
                Installed = installed,
                InstallPath = install,
                ConfigPath = config,
                Configured = IsConfigured(client),
                SupportsDirectWrite = SupportsDirectWrite(client)
            };
        }).ToList();
    }

    public static string GetConfigPath(SupportedClient client)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var workspace = EnvironmentDiscoveryService.EnsureWorkspace();

        switch (client)
        {
            case SupportedClient.OpenCode:
                {
                    var dir = Path.Combine(home, ".config", "opencode");
                    var json = Path.Combine(dir, "opencode.json");
                    var jsonc = Path.Combine(dir, "opencode.jsonc");
                    return File.Exists(jsonc) && !File.Exists(json) ? jsonc : json;
                }
            case SupportedClient.ClaudeDesktop:
                return Path.Combine(appData, "Claude", "claude_desktop_config.json");
            case SupportedClient.Cursor:
                return Path.Combine(home, ".cursor", "mcp.json");
            case SupportedClient.VsCodeCopilot:
                return FindExistingFile(
                    Path.Combine(appData, "Code", "User"), new[] { "mcp.json" })
                    ?? FindExistingFile(Path.Combine(appData, "Code - Insiders", "User"), new[] { "mcp.json" })
                    ?? Path.Combine(appData, "Code", "User", "mcp.json");
            case SupportedClient.CherryStudio:
                return FindExistingFile(Path.Combine(appData, "CherryStudio"), new[] { "mcp_config.json", "mcp.json" })
                       ?? Path.Combine(workspace, "客户端配置导入", "Cherry Studio-tia-portal.json");
            case SupportedClient.Cline:
                return ResolveExtensionConfig(
                    Path.Combine(home, ".cline", "data", "settings", "cline_mcp_settings.json"),
                    "saoudrizwan.claude-dev", "cline_mcp_settings.json");
            case SupportedClient.RooCode:
                return ResolveExtensionConfig(
                    Path.Combine(appData, "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json"),
                    "rooveterinaryinc.roo-cline", "mcp_settings.json");
            case SupportedClient.Continue:
                return Path.Combine(home, ".continue", "mcpServers", "tia-portal.json");
            case SupportedClient.Codex:
                return Path.Combine(home, ".codex", "config.toml");
            case SupportedClient.Trae:
                return FindExistingFile(Path.Combine(appData, "Trae"), new[] { "mcp.json", "mcp_config.json" })
                       ?? Path.Combine(workspace, "客户端配置导入", "Trae-tia-portal.json");
            case SupportedClient.Windsurf:
                return Path.Combine(home, ".codeium", "windsurf", "mcp_config.json");
            default:
                return Path.Combine(local, "博途智能工程助手", "未知客户端.json");
        }
    }

    public static bool IsConfigured(SupportedClient client)
    {
        var path = GetConfigPath(client);
        if (!File.Exists(path)) return false;
        try
        {
            if (client == SupportedClient.Codex)
            {
                var text = File.ReadAllText(path);
                return Regex.IsMatch(text, @"(?im)^\s*\[mcp_servers\.tia-portal\]\s*$");
            }

            var root = JObject.Parse(File.ReadAllText(path));
            switch (client)
            {
                case SupportedClient.OpenCode:
                    return root["mcp"]?["servers"]?[ServerKey] != null || root["mcp"]?[ServerKey] != null;
                case SupportedClient.VsCodeCopilot:
                    return root["servers"]?[ServerKey] != null;
                default:
                    return root["mcpServers"]?[ServerKey] != null;
            }
        }
        catch { return false; }
    }

    public static ClientConfigResult Configure(SupportedClient client, string executablePath)
    {
        var result = new ClientConfigResult { ConfigPath = GetConfigPath(client) };
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                result.Message = "没有找到服务器主程序。请先完成发布，再执行客户端配置。";
                return result;
            }

            // 对公开文档没有稳定配置文件契约的客户端，不直接写内部数据库；生成标准 JSON 导入文件。
            if (!SupportsDirectWrite(client) && !IsKnownDirectConfigFile(result.ConfigPath, client))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(result.ConfigPath) ?? EnvironmentDiscoveryService.EnsureWorkspace());
                var root = NewMcpServersRoot(client, executablePath);
                AtomicWrite(result.ConfigPath, root.ToString(Formatting.Indented));
                result.Success = true;
                result.RequiresManualImport = true;
                result.Verification = File.Exists(result.ConfigPath) ? "导入配置文件已生成。" : "导入配置文件生成失败。";
                result.Message = $"已为 {GetDisplayName(client)} 生成安全导入配置。请在该客户端的 MCP 设置中选择导入/添加服务器，并使用此文件。为避免破坏客户端内部数据，V4.1 不直接修改其未公开的内部数据库。";
                return result;
            }

            var dir = Path.GetDirectoryName(result.ConfigPath) ?? "";
            Directory.CreateDirectory(dir);
            result.BackupPath = BackupExisting(result.ConfigPath, client);

            if (client == SupportedClient.Codex)
                ConfigureCodexToml(result.ConfigPath, executablePath);
            else
                ConfigureJson(client, result.ConfigPath, executablePath);

            var verified = IsConfigured(client);
            result.Success = verified;
            result.Verification = verified ? "已重新读取配置并验证 tia-portal 条目存在。" : "写入完成，但重新读取验证没有找到 tia-portal 条目。";
            result.Message = verified
                ? $"{GetDisplayName(client)} 配置已完成：已执行检测、备份、写入和验证。请完全退出并重新启动客户端。"
                : $"{GetDisplayName(client)} 配置写入后验证失败，已保留备份，请不要继续覆盖原配置。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "自动配置失败。原配置不会被主动清空。原因：" + SafeException(ex.Message);
            return result;
        }
    }

    private static void ConfigureJson(SupportedClient client, string path, string executablePath)
    {
        JObject root;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            try { root = JObject.Parse(existing); }
            catch { throw new InvalidDataException("现有配置不是有效 JSON/JSONC，已停止修改。请先修复配置语法。"); }
        }
        else root = new JObject();

        if (client == SupportedClient.OpenCode)
        {
            var mcp = root["mcp"] as JObject ?? new JObject();
            root["mcp"] = mcp;

            // OpenCode 当前稳定配置把服务器名称直接放在 mcp 下。
            // 清理旧自动配置器写入的 mcp.servers.tia-portal，避免把 "servers"
            // 误识别为一个缺少 type/command/enabled 的 MCP 服务器。
            if (mcp["servers"] is JObject legacyServers)
            {
                legacyServers.Remove(ServerKey);
                if (!legacyServers.Properties().Any())
                    mcp.Remove("servers");
            }

            mcp[ServerKey] = new JObject
            {
                ["type"] = "local",
                ["command"] = new JArray(executablePath, "--gui", "--client=OpenCode"),
                ["enabled"] = true
            };
        }
        else if (client == SupportedClient.VsCodeCopilot)
        {
            var servers = root["servers"] as JObject ?? new JObject();
            root["servers"] = servers;
            servers[ServerKey] = new JObject
            {
                ["type"] = "stdio",
                ["command"] = executablePath,
                ["args"] = new JArray("--gui", "--client=VSCodeCopilot")
            };
        }
        else
        {
            var servers = root["mcpServers"] as JObject ?? new JObject();
            root["mcpServers"] = servers;
            var entry = new JObject
            {
                ["command"] = executablePath,
                ["args"] = new JArray("--gui", "--client=" + ClientHint(client))
            };
            if (client == SupportedClient.Cline)
            {
                entry["disabled"] = false;
                entry["autoApprove"] = new JArray();
            }
            if (client == SupportedClient.RooCode)
            {
                entry["disabled"] = false;
                entry["alwaysAllow"] = new JArray();
            }
            servers[ServerKey] = entry;
        }

        AtomicWrite(path, root.ToString(Formatting.Indented));
    }

    private static JObject NewMcpServersRoot(SupportedClient client, string executablePath)
    {
        return new JObject
        {
            ["mcpServers"] = new JObject
            {
                [ServerKey] = new JObject
                {
                    ["command"] = executablePath,
                    ["args"] = new JArray("--gui", "--client=" + ClientHint(client))
                }
            }
        };
    }

    private static void ConfigureCodexToml(string path, string executablePath)
    {
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var output = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            var section = Regex.Match(line, @"^\s*\[(?<name>[^\]]+)\]\s*$");
            if (section.Success)
            {
                var name = section.Groups["name"].Value.Trim();
                if (string.Equals(name, "mcp_servers.tia-portal", StringComparison.OrdinalIgnoreCase))
                {
                    skipping = true;
                    continue;
                }
                if (skipping) skipping = false;
            }
            if (!skipping) output.Add(line);
        }
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[output.Count - 1])) output.RemoveAt(output.Count - 1);
        if (output.Count > 0) output.Add("");
        output.Add("[mcp_servers.tia-portal]");
        output.Add("command = \"" + EscapeToml(executablePath) + "\"");
        output.Add("args = [\"--gui\", \"--client=Codex\"]");
        output.Add("enabled = true");
        output.Add("");
        AtomicWrite(path, string.Join(Environment.NewLine, output));
    }

    private static string BackupExisting(string path, SupportedClient client)
    {
        if (!File.Exists(path)) return "";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        var localBackup = path + ".tia-mcp-backup_" + stamp;
        File.Copy(path, localBackup, true);
        try
        {
            var workspaceBackupDir = Path.Combine(EnvironmentDiscoveryService.EnsureWorkspace(), "客户端配置备份", GetDisplayName(client));
            Directory.CreateDirectory(workspaceBackupDir);
            var name = Path.GetFileName(path) + "." + stamp + ".bak";
            File.Copy(path, Path.Combine(workspaceBackupDir, name), true);
        }
        catch { }
        return localBackup;
    }

    private static void AtomicWrite(string path, string content)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tia-mcp-tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.Replace(temp, path, null); }
            catch
            {
                // 不删除原配置，避免权限/杀毒软件占用导致配置丢失
                var fallback = path + ".tia-mcp-fallback-" + Guid.NewGuid().ToString("N");
                File.Copy(temp, fallback, true);
                File.Delete(temp);
                throw new IOException("原配置文件被占用，已生成安全副本: " + fallback);
            }
        }
        else File.Move(temp, path);
    }

    public static string ResolveCurrentExecutable()
    {
        var localExe = Path.Combine(AppContext.BaseDirectory, "TiaMcpServer.exe");
        if (File.Exists(localExe)) return localExe;
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (!string.IsNullOrWhiteSpace(path) &&
                File.Exists(path) &&
                !path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        catch { }
        return localExe;
    }

    private static string FindInstallPath(SupportedClient client)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>();
        switch (client)
        {
            case SupportedClient.OpenCode:
                candidates.Add(Path.Combine(home, ".opencode", "bin", "opencode.exe"));
                candidates.Add(Path.Combine(local, "Programs", "opencode", "opencode.exe"));
                candidates.Add(FindOnPath("opencode.exe"));
                candidates.Add(FindOnPath("opencode2.exe"));
                break;
            case SupportedClient.ClaudeDesktop:
                candidates.Add(Path.Combine(local, "Programs", "Claude", "Claude.exe"));
                candidates.Add(Path.Combine(local, "AnthropicClaude", "Claude.exe"));
                break;
            case SupportedClient.Cursor:
                candidates.Add(Path.Combine(local, "Programs", "cursor", "Cursor.exe"));
                candidates.Add(Path.Combine(local, "Programs", "Cursor", "Cursor.exe"));
                break;
            case SupportedClient.VsCodeCopilot:
                candidates.Add(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
                candidates.Add(Path.Combine(pf, "Microsoft VS Code", "Code.exe"));
                break;
            case SupportedClient.CherryStudio:
                candidates.Add(Path.Combine(local, "Programs", "Cherry Studio", "Cherry Studio.exe"));
                candidates.Add(Path.Combine(local, "CherryStudio", "Cherry Studio.exe"));
                break;
            case SupportedClient.Codex:
                candidates.Add(FindOnPath("codex.exe"));
                candidates.Add(Path.Combine(home, ".codex", "bin", "codex.exe"));
                break;
            case SupportedClient.Trae:
                candidates.Add(Path.Combine(local, "Programs", "Trae", "Trae.exe"));
                candidates.Add(Path.Combine(local, "Programs", "TRAE", "TRAE.exe"));
                break;
            case SupportedClient.Windsurf:
                candidates.Add(Path.Combine(local, "Programs", "Windsurf", "Windsurf.exe"));
                candidates.Add(Path.Combine(pf, "Windsurf", "Windsurf.exe"));
                break;
            case SupportedClient.Cline:
                return FindExtensionDirectory("saoudrizwan.claude-dev") ?? "";
            case SupportedClient.RooCode:
                return FindExtensionDirectory("rooveterinaryinc.roo-cline") ?? "";
            case SupportedClient.Continue:
                return FindExtensionDirectory("continue.continue") ?? (Directory.Exists(Path.Combine(home, ".continue")) ? Path.Combine(home, ".continue") : "");
        }
        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x)) ?? "";
    }

    private static bool ClientDataExists(SupportedClient client)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        switch (client)
        {
            case SupportedClient.OpenCode: return Directory.Exists(Path.Combine(home, ".config", "opencode"));
            case SupportedClient.ClaudeDesktop: return Directory.Exists(Path.Combine(appData, "Claude"));
            case SupportedClient.Cursor: return Directory.Exists(Path.Combine(home, ".cursor"));
            case SupportedClient.VsCodeCopilot: return Directory.Exists(Path.Combine(appData, "Code"));
            case SupportedClient.CherryStudio: return Directory.Exists(Path.Combine(appData, "CherryStudio"));
            case SupportedClient.Cline: return Directory.Exists(Path.Combine(home, ".cline")) || FindExtensionDirectory("saoudrizwan.claude-dev") != null;
            case SupportedClient.RooCode: return FindExtensionDirectory("rooveterinaryinc.roo-cline") != null;
            case SupportedClient.Continue: return Directory.Exists(Path.Combine(home, ".continue"));
            case SupportedClient.Codex: return Directory.Exists(Path.Combine(home, ".codex"));
            case SupportedClient.Trae: return Directory.Exists(Path.Combine(appData, "Trae"));
            case SupportedClient.Windsurf: return Directory.Exists(Path.Combine(home, ".codeium", "windsurf"));
            default: return false;
        }
    }

    private static string ResolveExtensionConfig(string preferred, string extensionId, string fileName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            preferred,
            Path.Combine(appData, "Code", "User", "globalStorage", extensionId, "settings", fileName),
            Path.Combine(appData, "Cursor", "User", "globalStorage", extensionId, "settings", fileName),
            Path.Combine(appData, "Windsurf", "User", "globalStorage", extensionId, "settings", fileName)
        };
        return candidates.FirstOrDefault(File.Exists) ?? preferred;
    }

    private static string? FindExtensionDirectory(string extensionId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(home, ".vscode", "extensions"), Path.Combine(home, ".cursor", "extensions"), Path.Combine(home, ".windsurf", "extensions") })
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var match = Directory.GetDirectories(root, extensionId + "-*", SearchOption.TopDirectoryOnly).OrderByDescending(x => x).FirstOrDefault();
                if (match != null) return match;
            }
            catch { }
        }
        return null;
    }

    private static string? FindExistingFile(string root, IEnumerable<string> names)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            foreach (var name in names)
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
            }
            foreach (var name in names)
            {
                var match = Directory.GetFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                if (match != null) return match;
            }
        }
        catch { }
        return null;
    }

    private static bool IsKnownDirectConfigFile(string path, SupportedClient client)
    {
        if (!File.Exists(path)) return false;
        var file = Path.GetFileName(path);
        if (client == SupportedClient.CherryStudio) return file.Equals("mcp_config.json", StringComparison.OrdinalIgnoreCase) || file.Equals("mcp.json", StringComparison.OrdinalIgnoreCase);
        if (client == SupportedClient.Trae) return file.Equals("mcp_config.json", StringComparison.OrdinalIgnoreCase) || file.Equals("mcp.json", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return "";
    }

    private static string ClientHint(SupportedClient client)
    {
        switch (client)
        {
            case SupportedClient.VsCodeCopilot: return "VSCodeCopilot";
            case SupportedClient.ClaudeDesktop: return "ClaudeDesktop";
            case SupportedClient.CherryStudio: return "CherryStudio";
            case SupportedClient.RooCode: return "RooCode";
            default: return GetDisplayName(client).Replace(" ", "");
        }
    }

    private static string EscapeToml(string value) => (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string SafeException(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "未知错误";
        return value.Length > 300 ? value.Substring(0, 300) : value;
    }
}

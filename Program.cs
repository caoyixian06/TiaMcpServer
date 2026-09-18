using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TiaMcpServer;

/// <summary>
/// V4.3.0 进程入口：UTF-8、多盘符/多版本 TIA 自动发现、动态 Openness 程序集解析、MCP + 中文控制中心启动。
/// </summary>
internal static class Program
{

    private static void EnableDpiAwareness()
    {
        try
        {
            if (Environment.OSVersion.Version.Major >= 6)
                SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    // STAThread：进程主线程必须是 STA。所有 Openness COM 调用已收敛到
    // TiaStaDispatcher 的专用 STA 线程；主线程保持 STA 可避免 COM 混用模型。
    [STAThread]
    private static async Task Main(string[] args)
    {
        EnableDpiAwareness();
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch { }

        var discovery = EnvironmentDiscoveryService.Discover();
        var selectedTia = discovery.SelectedTia;
        var tiaBinDir = selectedTia?.BinPath;
        var tiaPublicApiDir = selectedTia?.PublicApiPath;

        if (selectedTia == null)
        {
            // 自动发现未命中时的兜底：扫描标准安装目录解析 Siemens.Engineering.dll，
            // 避免首次使用 Openness 类型时直接抛 FileNotFoundException。
            var fallback = TryResolveFallbackTia();
            if (fallback != null)
            {
                tiaBinDir = Path.Combine(fallback.Value.PortalRoot, "Bin");
                tiaPublicApiDir = fallback.Value.PublicApiDir;
                // 与正常发现路径保持一致：写入进程级环境变量，供各运行时服务统一读取。
                Environment.SetEnvironmentVariable("TIA_PORTAL_DIR", fallback.Value.PortalRoot, EnvironmentVariableTarget.Process);
                Environment.SetEnvironmentVariable("TIA_PORTAL_VERSION", fallback.Value.Version, EnvironmentVariableTarget.Process);
                Console.Error.WriteLine($"[tia-mcp] 自动发现未命中，已回退到标准目录: {fallback.Value.PublicApiDir}");
            }
            else
            {
                Console.Error.WriteLine("[tia-mcp] 错误: 未自动发现 TIA Portal，且标准目录中未找到 Siemens.Engineering.dll。");
                Console.Error.WriteLine("[tia-mcp] 请运行 scripts\\构建V4.4.4.ps1 自动发现，或设置 TIA_PORTAL_DIR / TIA_PORTAL_VERSION 后重启。");
            }
        }

        if (selectedTia != null)
        {
            // 只影响当前服务器进程，便于后续服务统一读取，不修改用户系统级环境变量。
            Environment.SetEnvironmentVariable("TIA_PORTAL_DIR", selectedTia.RootPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("TIA_PORTAL_VERSION", selectedTia.Version, EnvironmentVariableTarget.Process);
        }

        var debug = Environment.GetEnvironmentVariable("TIA_MCP_DEBUG");
        if (string.Equals(debug, "1", StringComparison.Ordinal))
        {
            if (selectedTia != null)
                Console.Error.WriteLine($"[tia-mcp] 已选择 {selectedTia.Version}: {selectedTia.RootPath}; Openness={(selectedTia.HasOpenness ? "是" : "否")}");
            else
                Console.Error.WriteLine("[tia-mcp] 警告: 未自动发现 TIA Portal。可设置 TIA_PORTAL_DIR / TIA_PORTAL_VERSION。");
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) => ResolveSiemensAssembly(eventArgs, tiaBinDir, tiaPublicApiDir, debug);
        PreloadSiemensAssemblies(tiaBinDir, tiaPublicApiDir);

        AppDomain.CurrentDomain.FirstChanceException += (_, eventArgs) =>
        {
            if (!string.Equals(debug, "1", StringComparison.Ordinal)) return;
            var ex = eventArgs.Exception;
            if (ex is TypeInitializationException || ex is FileNotFoundException || ex is ReflectionTypeLoadException || ex is ArgumentException)
            {
                Console.Error.WriteLine($"[tia-mcp] FirstChance: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    Console.Error.WriteLine($"[tia-mcp]   Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        };

        var showGui = args.Any(a => string.Equals(a, "--gui", StringComparison.OrdinalIgnoreCase)) ||
                      string.Equals(Environment.GetEnvironmentVariable("TIA_MCP_GUI"), "1", StringComparison.OrdinalIgnoreCase);
        var profile = GetArgument(args, "--profile=");
        var clientHint = GetArgument(args, "--client=") ?? Environment.GetEnvironmentVariable("TIA_MCP_CLIENT");

        var runtimeState = new ServerRuntimeState();
        RuntimePolicyConfig.Apply(runtimeState);
        var server = new McpServer(profile, runtimeState, clientHint);

        // ★全自动工作流★ 常驻后台自动点击 TIA Openness 连接授权确认窗口，
        // 否则每次 Attach/Start 都会弹出模态确认框阻塞连接直到人工确认。
        TiaMcpServer.Runtime.OpennessDialogAutoClicker.Start();

        if (showGui) DashboardHost.Start(server, runtimeState);
        try
        {
            await server.RunAsync();
        }
        finally
        {
            TiaMcpServer.Runtime.OpennessDialogAutoClicker.Stop();
            DashboardHost.Stop();
        }
    }

    private static string? GetArgument(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Substring(prefix.Length);

    // 自动发现未命中时的标准目录兜底：ProgramFiles(x86)/ProgramFiles 下 Siemens\Automation\Portal V*，
    // V17 优先，其次按版本号降序；返回找到的第一个含 Siemens.Engineering.dll 的 PublicAPI 目录。
    private static (string PortalRoot, string PublicApiDir, string Version)? TryResolveFallbackTia()
    {
        try
        {
            foreach (var pf in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            })
            {
                var automation = Path.Combine(pf, "Siemens", "Automation");
                if (!Directory.Exists(automation)) continue;
                var portals = Directory.GetDirectories(automation, "Portal V*", SearchOption.TopDirectoryOnly)
                    .Where(p => EnvironmentDiscoveryService.ParseVersionNumber(Path.GetFileName(p)) > 0)
                    .OrderBy(p => EnvironmentDiscoveryService.ParseVersionNumber(Path.GetFileName(p)) != 17) // V17 优先
                    .ThenByDescending(p => EnvironmentDiscoveryService.ParseVersionNumber(Path.GetFileName(p)))
                    .ToList();
                foreach (var portal in portals)
                {
                    var version = "V" + EnvironmentDiscoveryService.ParseVersionNumber(Path.GetFileName(portal));
                    var api = new[]
                    {
                        Path.Combine(portal, "PublicAPI", version),
                        Path.Combine(portal, "Bin", "PublicAPI")
                    }.FirstOrDefault(Directory.Exists);
                    if (api == null) continue;
                    if (File.Exists(Path.Combine(api, "Siemens.Engineering.dll")))
                        return (portal, api, version);
                }
            }
        }
        catch { }
        return null;
    }

    private static Assembly? ResolveSiemensAssembly(ResolveEventArgs eventArgs, string? tiaBinDir, string? tiaPublicApiDir, string? debug)
    {
        try
        {
            var assemblyName = new AssemblyName(eventArgs.Name).Name;
            if (string.IsNullOrWhiteSpace(assemblyName)) return null;
            foreach (var dir in GetAssemblySearchDirectories(tiaBinDir, tiaPublicApiDir))
            {
                var dllPath = Path.Combine(dir, assemblyName + ".dll");
                if (!File.Exists(dllPath)) continue;
                try { return Assembly.LoadFrom(dllPath); }
                catch (Exception ex)
                {
                    if (string.Equals(debug, "1", StringComparison.Ordinal))
                        Console.Error.WriteLine($"[tia-mcp] AssemblyResolve 失败: {dllPath} -> {ex.Message}");
                }
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> GetAssemblySearchDirectories(string? tiaBinDir, string? tiaPublicApiDir)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(tiaPublicApiDir)) dirs.Add(tiaPublicApiDir!);
        if (!string.IsNullOrWhiteSpace(tiaBinDir))
        {
            dirs.Add(Path.Combine(tiaBinDir!, "PublicAPI"));
            dirs.Add(tiaBinDir!);
        }
        return dirs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void PreloadSiemensAssemblies(string? tiaBinDir, string? tiaPublicApiDir)
    {
        var verbose = string.Equals(Environment.GetEnvironmentVariable("TIA_MCP_DEBUG"), "1", StringComparison.Ordinal);
        var coreAssemblies = new[] { "Siemens.Engineering.Contract", "Siemens.Engineering", "Siemens.Engineering.Hmi" };
        foreach (var asmName in coreAssemblies)
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == asmName)) continue;
            var foundPath = GetAssemblySearchDirectories(tiaBinDir, tiaPublicApiDir)
                .Select(dir => Path.Combine(dir, asmName + ".dll"))
                .FirstOrDefault(File.Exists);
            if (foundPath == null)
            {
                if (verbose) Console.Error.WriteLine($"[tia-mcp] 预加载: 未找到 {asmName}.dll");
                continue;
            }
            try
            {
                Assembly.LoadFrom(foundPath);
                if (verbose) Console.Error.WriteLine($"[tia-mcp] 预加载成功: {foundPath}");
            }
            catch (Exception ex)
            {
                if (verbose) Console.Error.WriteLine($"[tia-mcp] 预加载失败: {foundPath} -> {ex.Message}");
            }
        }
    }
}

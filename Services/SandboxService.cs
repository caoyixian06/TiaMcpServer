using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Siemens.Engineering;

namespace TiaMcpServer
{
    /// <summary>
    /// 工程沙盒与安全快照。沙盒通过“归档原工程 -> 恢复到独立目录”创建真实工程副本，
    /// 不直接复制项目内部文件，避免破坏 TIA Portal 项目结构。
    /// </summary>
    public partial class PortalService
    {
        private bool _sandboxActive;
        private string _sandboxOriginalProjectPath = "";
        private string _sandboxProjectPath = "";
        private string _sandboxArchivePath = "";

        public bool IsSandboxActive => _sandboxActive;
        public string SandboxOriginalProjectPath => _sandboxOriginalProjectPath;
        public string SandboxProjectPath => _sandboxProjectPath;
        public string SandboxArchivePath => _sandboxArchivePath;

        public string GetSandboxStatus()
        {
            lock (_lock)
            {
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    active = _sandboxActive,
                    originalProjectPath = _sandboxOriginalProjectPath,
                    sandboxProjectPath = _sandboxProjectPath,
                    archivePath = _sandboxArchivePath
                });
            }
        }

        public string CreateSafeSandbox(string sandboxRoot)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (_tiaPortal == null) return Err("当前未连接博途。请先连接博途并打开工程。");
                    if (_sandboxActive) return Err("当前已经处于沙盒工程中。请先退出现有沙盒。");

                    var originalPath = _project!.Path?.FullName ?? "";
                    if (string.IsNullOrWhiteSpace(originalPath)) return Err("无法读取当前工程路径，不能创建沙盒。");

                    if (string.IsNullOrWhiteSpace(sandboxRoot))
                    {
                        var configuredRoot = Environment.GetEnvironmentVariable("TIA_MCP_SANDBOX_ROOT");
                        sandboxRoot = !string.IsNullOrWhiteSpace(configuredRoot)
                            ? configuredRoot
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "博途智能助手", "沙盒工程");
                    }
                    sandboxRoot = Path.GetFullPath(sandboxRoot);
                    Directory.CreateDirectory(sandboxRoot);

                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var safeName = SanitizeFileName(_project.Name);
                    var archiveDir = Path.Combine(sandboxRoot, "安全归档");
                    var retrieveDir = Path.Combine(sandboxRoot, "工程副本", safeName + "_" + stamp);
                    Directory.CreateDirectory(archiveDir);
                    Directory.CreateDirectory(retrieveDir);

                    var archiveName = safeName + "_沙盒源_" + stamp + EnvironmentDiscoveryService.CurrentArchiveExtension();
                    var archivePath = Path.Combine(archiveDir, archiveName);

                    // 归档使用最近保存状态，因此先显式保存。
                    _project.Save();
                    _project.Archive(new DirectoryInfo(archiveDir), archiveName, ProjectArchivationMode.Compressed);

                    // 关闭原工程后恢复归档到独立目录。恢复完成后 _project 指向副本。
                    _project.Close();
                    _project = null;
                    PortalService.ClearAllLadCaches();

                    try
                    {
                        _project = _tiaPortal.Projects.Retrieve(new FileInfo(archivePath), new DirectoryInfo(retrieveDir));
                    }
                    catch
                    {
                        // 恢复失败时尽最大努力重新打开原工程，避免用户留在“无工程”状态。
                        try { _project = _tiaPortal.Projects.Open(new FileInfo(originalPath)); } catch { }
                        throw;
                    }

                    _sandboxActive = true;
                    _sandboxOriginalProjectPath = originalPath;
                    _sandboxArchivePath = archivePath;
                    _sandboxProjectPath = _project.Path?.FullName ?? retrieveDir;
                    PortalService.ClearAllLadCaches();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "沙盒工程已创建并打开，原工程未被修改。",
                        originalProjectPath = _sandboxOriginalProjectPath,
                        sandboxProjectPath = _sandboxProjectPath,
                        archivePath = _sandboxArchivePath
                    });
                }
                catch (Exception ex)
                {
                    return Err("创建沙盒失败：" + ex.Message);
                }
            }
        }

        public string LeaveSafeSandbox(bool saveSandboxChanges)
        {
            lock (_lock)
            {
                try
                {
                    if (!_sandboxActive) return Err("当前不在沙盒工程中。");
                    if (_tiaPortal == null) return Err("当前未连接博途，无法退出沙盒。");

                    var originalPath = _sandboxOriginalProjectPath;
                    var sandboxPath = _project?.Path?.FullName ?? _sandboxProjectPath;

                    if (_project != null)
                    {
                        if (saveSandboxChanges) _project.Save();
                        _project.Close();
                        _project = null;
                    }

                    if (string.IsNullOrWhiteSpace(originalPath) || !File.Exists(originalPath))
                    {
                        // 原工程不可用时尽量重新打开沙盒，避免当前会话落入无工程状态。
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(sandboxPath) && File.Exists(sandboxPath))
                                _project = _tiaPortal.Projects.Open(new FileInfo(sandboxPath));
                        }
                        catch { }
                        return Err("原工程文件不存在，未能返回原工程。沙盒副本仍保留在磁盘中。");
                    }

                    try
                    {
                        _project = _tiaPortal.Projects.Open(new FileInfo(originalPath));
                    }
                    catch
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(sandboxPath) && File.Exists(sandboxPath))
                                _project = _tiaPortal.Projects.Open(new FileInfo(sandboxPath));
                        }
                        catch { }
                        throw;
                    }
                    _sandboxActive = false;
                    _sandboxProjectPath = "";
                    _sandboxOriginalProjectPath = "";
                    _sandboxArchivePath = "";
                    PortalService.ClearAllLadCaches();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "已退出沙盒并重新打开原工程。沙盒副本仍保留在磁盘中。",
                        originalProjectPath = originalPath,
                        sandboxProjectPath = sandboxPath
                    });
                }
                catch (Exception ex)
                {
                    return Err("退出沙盒失败：" + ex.Message);
                }
            }
        }

        /// <summary>为当前工程创建压缩安全快照，返回归档绝对路径；没有打开工程时返回空字符串。</summary>
        public string CreateSafetySnapshot(string outputDir, string operationLabel)
        {
            lock (_lock)
            {
                if (_project == null) return "";
                Directory.CreateDirectory(outputDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var projectName = SanitizeFileName(_project.Name);
                var label = SanitizeFileName(string.IsNullOrWhiteSpace(operationLabel) ? "变更前" : operationLabel);
                var archiveName = projectName + "_" + label + "_" + stamp + EnvironmentDiscoveryService.CurrentArchiveExtension();
                _project.Save();
                _project.Archive(new DirectoryInfo(outputDir), archiveName, ProjectArchivationMode.Compressed);
                return Path.Combine(outputDir, archiveName);
            }
        }
    }
}

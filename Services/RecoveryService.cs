using System;
using System.IO;
using Newtonsoft.Json;
using Siemens.Engineering;

namespace TiaMcpServer
{
    public partial class PortalService
    {
        /// <summary>
        /// 把安全快照恢复为独立工程副本并打开。不会覆盖原工程；恢复前会保存当前工程。
        /// </summary>
        public string RestoreSafetySnapshotAsCopy(string archivePath, string restoreRoot)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                        return Err("没有找到可恢复的安全快照。");

                    if (_sandboxActive)
                        return Err("当前正在使用沙盒工程。请先退出沙盒，再执行恢复操作。");

                    if (_tiaPortal == null)
                        _tiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface);

                    if (string.IsNullOrWhiteSpace(restoreRoot))
                        restoreRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "博途智能助手", "恢复工程");
                    Directory.CreateDirectory(restoreRoot);

                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var target = Path.Combine(restoreRoot, "安全恢复_" + stamp);
                    Directory.CreateDirectory(target);

                    var previousPath = _project?.Path?.FullName ?? "";
                    if (_project != null)
                    {
                        _project.Save();
                        _project.Close();
                        _project = null;
                    }
                    PortalService.ClearAllLadCaches();

                    try
                    {
                        _project = _tiaPortal.Projects.Retrieve(new FileInfo(archivePath), new DirectoryInfo(target));
                    }
                    catch
                    {
                        // 恢复失败时尽量回到恢复前工程。
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(previousPath) && File.Exists(previousPath))
                                _project = _tiaPortal.Projects.Open(new FileInfo(previousPath));
                        }
                        catch { }
                        throw;
                    }

                    PortalService.ClearAllLadCaches();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "安全快照已恢复为独立工程副本并打开。原工程没有被覆盖。",
                        restoredProjectPath = _project?.Path?.FullName ?? target,
                        sourceSnapshot = archivePath,
                        previousProjectPath = previousPath
                    });
                }
                catch (Exception ex)
                {
                    return Err("恢复安全快照失败：" + ex.Message);
                }
            }
        }
    }
}

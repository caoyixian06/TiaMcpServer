using System;
using System.Linq;
using System.Threading;
using Siemens.Engineering;

namespace TiaMcpServer;

public partial class PortalService
{
    /// <summary>
    /// 尝试自动附加到已有博途实例（带超时，不阻塞服务器启动）。
    /// 实际附加动作投递到 STA 调度线程执行，避免裸线程 + Thread.Abort 破坏 COM 状态。
    /// </summary>
    public string TryAutoAttach(int timeoutSeconds = 8)
    {
        try
        {
            string result = "{\"success\":false,\"message\":\"timeout\"}";
            using (var done = new ManualResetEventSlim(false))
            {
                TiaStaDispatcher.Instance.Post(() =>
                {
                    try
                    {
                        var processes = TiaPortal.GetProcesses();
                        if (processes == null || !processes.Any())
                        {
                            result = "{\"success\":false,\"message\":\"未发现运行中的博途实例\"}";
                            return;
                        }

                        var first = processes.First();
                        lock (_lock)
                        {
                            DisposeConnection();
                            _tiaPortal = first.Attach();
                            _project = _tiaPortal.Projects.FirstOrDefault();
                        }

                        var projName = _project?.Name ?? "无";
                        result = $"{{\"success\":true,\"message\":\"已自动附加到博途\",\"project\":\"{projName}\"}}";
                    }
                    catch (Exception ex)
                    {
                        result = $"{{\"success\":false,\"error\":\"{ex.Message}\"}}";
                    }
                    finally
                    {
                        done.Set();
                    }
                });

                if (!done.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                    return "{\"success\":false,\"message\":\"自动附加超时，跳过\"}";
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"{{\"success\":false,\"error\":\"{ex.Message}\"}}";
        }
    }
}

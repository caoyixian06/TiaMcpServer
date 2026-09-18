using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// 博途进程/连接管理工具注册（任务 6 / 工具入口）。
    /// </summary>
    public partial class McpServer
    {
        internal static void RegisterPortalTools(McpServer server, Lazy<PortalService> tia)
        {
            server.RegisterTool("list_tia_processes",
                "列出本机所有运行中的 TIA Portal 进程（ID、项目路径、模式）。",
                EmptySchema(),
                _ => tia.Value.ListTiaProcesses());

            server.RegisterTool("attach_to_process",
                "通过进程 ID 附加到已运行的 TIA Portal 进程。",
                Props(new Dictionary<string, object>
                {
                    ["processId"] = IntProp("要附加的博途进程 ID"),
                }, new[] { "processId" }),
                args => tia.Value.AttachToProcess(GetIntArg(args, "processId") ?? 0));

            server.RegisterTool("detach",
                "断开与 TIA Portal 的连接。",
                EmptySchema(),
                _ => tia.Value.Detach());

            server.RegisterTool("get_tia_status",
                "获取当前 TIA 连接状态、连接的项目以及运行中的进程列表。",
                EmptySchema(),
                _ => tia.Value.GetTiaStatus());
        }
    }
}

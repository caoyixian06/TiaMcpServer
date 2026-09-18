using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

public partial class McpServer
{
    internal static void RegisterMoreTools(McpServer server, Lazy<PortalService> tia)
    {
        server.RegisterTool("find_unused_blocks", "查找项目中所有未被引用的孤立程序块。", EmptySchema(), _ => tia.Value.FindUnusedBlocks());
        server.RegisterTool("find_block_references", "查找某个程序块被哪些其他块引用。", Props(new Dictionary<string, object> { ["blockName"] = StrProp("程序块名称") }, new[] { "blockName" }), args => tia.Value.FindBlockReferences(GetStringArg(args, "blockName") ?? ""));
        server.RegisterTool("list_watch_tables", "列出所有 PLC 的监视表和强制表。", EmptySchema(), _ => tia.Value.ListWatchAndForceTables());
        server.RegisterTool("scan_network_devices", "扫描 PROFINET 网络上的可访问设备。需先配置 PG/PC 接口。", EmptySchema(), _ => tia.Value.ScanNetworkDevices());
        server.RegisterTool("download_to_device", "将项目软件下载到连接的 PLC 设备。需先配置 PG/PC 接口。", EmptySchema(), _ => tia.Value.DownloadToDevice());
    }
}

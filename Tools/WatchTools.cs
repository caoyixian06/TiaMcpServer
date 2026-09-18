using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    public partial class McpServer
    {
        internal static void RegisterWatchTools(McpServer server, Lazy<PortalService> tia)
        {
            // 监视表
            server.RegisterTool("create_watch_table", "创建一个新的 PLC 监视表。",
                Props(new Dictionary<string, object> { ["name"] = StrProp("监视表名称"), ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）") }, new[] { "name" }),
                args => tia.Value.CreateWatchTable(GetStringArg(args, "name") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("delete_watch_table", "删除指定名称的监视表。",
                Props(new Dictionary<string, object> { ["name"] = StrProp("监视表名称"), ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）") }, new[] { "name" }),
                args => tia.Value.DeleteWatchTable(GetStringArg(args, "name") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("read_watch_table", "读取监视表中的所有条目。",
                Props(new Dictionary<string, object> { ["tableName"] = StrProp("监视表名称"), ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）") }, new[] { "tableName" }),
                args => tia.Value.ReadWatchTable(GetStringArg(args, "tableName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("add_watch_variable", "向监视表添加一个变量条目。",
                Props(new Dictionary<string, object>
                {
                    ["tableName"] = StrProp("监视表名称"),
                    ["varName"] = StrProp("变量名"),
                    ["address"] = StrProp("地址（可选）"),
                    ["dataType"] = StrProp("数据类型（可选）"),
                    ["comment"] = StrProp("注释（可选）"),
                    ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）")
                }, new[] { "tableName", "varName" }),
                args => tia.Value.AddWatchVariable(
                    GetStringArg(args, "tableName") ?? "",
                    GetStringArg(args, "varName") ?? "",
                    GetStringArg(args, "address"),
                    GetStringArg(args, "dataType"),
                    GetStringArg(args, "comment"),
                    GetStringArg(args, "plcName")));

            server.RegisterTool("dump_watch_table", "导出监视表到 XML 文件（同步；export_ 前缀会被归类为异步长任务故命名 dump_）。用于学习 V17 监视表结构/构造导入模板。",
                Props(new Dictionary<string, object> { ["tableName"] = StrProp("监视表名称"), ["outputPath"] = StrProp("输出 XML 路径"), ["plcName"] = StrProp("PLC 名称（可选）") }, new[] { "tableName", "outputPath" }),
                args => tia.Value.ExportWatchTable(GetStringArg(args, "tableName") ?? "", GetStringArg(args, "outputPath") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("apply_watch_table_xml", "从 XML 文件导入监视表（同步；import_ 前缀会被归类为异步长任务故命名 apply_）。ImportOptions.Override。",
                Props(new Dictionary<string, object> { ["filePath"] = StrProp("XML 文件路径"), ["plcName"] = StrProp("PLC 名称（可选）") }, new[] { "filePath" }),
                args => tia.Value.ImportWatchTable(GetStringArg(args, "filePath") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("probe_watch_api", "反射 WatchAndForceTableGroup/WatchTables/Entries 的方法面，诊断 V17 监视表条目创建路径。",
                Props(new Dictionary<string, object> { ["plcName"] = StrProp("PLC 名称（可选）") }, Array.Empty<string>()),
                args => tia.Value.ProbeWatchApi(GetStringArg(args, "plcName")));

            server.RegisterTool("modify_watch_variable", "修改监视表中条目的值（在线写值）。\n" +
                "SetAttribute(ModifyValue) + 反射触发 Modify/TriggerModify 等；返回 attempts 便于诊断。",
                Props(new Dictionary<string, object>
                {
                    ["tableName"] = StrProp("监视表名称"),
                    ["varName"] = StrProp("变量名"),
                    ["value"] = StrProp("要写入的值（如 1/0/true/false/数字）"),
                    ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）")
                }, new[] { "tableName", "varName", "value" }),
                args => tia.Value.ModifyWatchVariable(
                    GetStringArg(args, "tableName") ?? "",
                    GetStringArg(args, "varName") ?? "",
                    GetStringArg(args, "value") ?? "",
                    GetStringArg(args, "plcName")));

            server.RegisterTool("delete_watch_variable", "从监视表删除一个变量条目。",
                Props(new Dictionary<string, object>
                {
                    ["tableName"] = StrProp("监视表名称"),
                    ["varName"] = StrProp("变量名"),
                    ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）")
                }, new[] { "tableName", "varName" }),
                args => tia.Value.DeleteWatchVariable(
                    GetStringArg(args, "tableName") ?? "",
                    GetStringArg(args, "varName") ?? "",
                    GetStringArg(args, "plcName")));

            // 强制表
            server.RegisterTool("create_force_table", "创建一个新的 PLC 强制表。",
                Props(new Dictionary<string, object> { ["name"] = StrProp("强制表名称"), ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）") }, new[] { "name" }),
                args => tia.Value.CreateForceTable(GetStringArg(args, "name") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("delete_force_table", "删除指定名称的强制表。",
                Props(new Dictionary<string, object> { ["name"] = StrProp("强制表名称"), ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）") }, new[] { "name" }),
                args => tia.Value.DeleteForceTable(GetStringArg(args, "name") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("read_force_table", "读取强制表中的所有条目。",
                Props(new Dictionary<string, object> { ["tableName"] = StrProp("强制表名称"), ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）") }, new[] { "tableName" }),
                args => tia.Value.ReadForceTable(GetStringArg(args, "tableName") ?? "", GetStringArg(args, "plcName")));

            server.RegisterTool("add_force_variable", "向强制表添加一个变量条目。",
                Props(new Dictionary<string, object>
                {
                    ["tableName"] = StrProp("强制表名称"),
                    ["varName"] = StrProp("变量名"),
                    ["address"] = StrProp("地址（可选）"),
                    ["dataType"] = StrProp("数据类型（可选）"),
                    ["comment"] = StrProp("注释（可选）"),
                    ["forceValue"] = StrProp("强制值（可选）"),
                    ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）")
                }, new[] { "tableName", "varName" }),
                args => tia.Value.AddForceVariable(
                    GetStringArg(args, "tableName") ?? "",
                    GetStringArg(args, "varName") ?? "",
                    GetStringArg(args, "address"),
                    GetStringArg(args, "dataType"),
                    GetStringArg(args, "comment"),
                    GetStringArg(args, "forceValue"),
                    GetStringArg(args, "plcName")));

            server.RegisterTool("delete_force_variable", "从强制表删除一个变量条目。",
                Props(new Dictionary<string, object>
                {
                    ["tableName"] = StrProp("强制表名称"),
                    ["varName"] = StrProp("变量名"),
                    ["plcName"] = StrProp("PLC 名称（可选，为空时使用第一个 PLC）")
                }, new[] { "tableName", "varName" }),
                args => tia.Value.DeleteForceVariable(
                    GetStringArg(args, "tableName") ?? "",
                    GetStringArg(args, "varName") ?? "",
                    GetStringArg(args, "plcName")));
        }
    }
}

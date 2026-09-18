using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.WatchAndForceTables;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;

namespace TiaMcpServer;

public partial class PortalService
{
    // 交叉引用（V19 强类型 CrossReferenceService；V17 无此 API，反射探测，失败返回说明）
    private (object? service, string? err) GetCrossReferenceService()
    {
        try
        {
            var svcType = _project!.GetType().Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == "CrossReferenceService");
            if (svcType == null) return (null, "当前 TIA 版本的 Openness 不支持交叉引用服务（CrossReferenceService 不存在）");
            var getService = _project.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition);
            var svc = getService?.MakeGenericMethod(svcType).Invoke(_project, null);
            return (svc, null);
        }
        catch (Exception ex) { return (null, $"交叉引用服务解析失败: {ex.Message}"); }
    }

    private object? GetCrossReferences(object? cr)
    {
        try
        {
            if (cr == null) return null;
            var m = cr.GetType().GetMethod("GetCrossReferences");
            if (m == null) return null;
            // CrossReferenceFilter 无参构造
            var filter = m.GetParameters().Length > 0 ? Activator.CreateInstance(m.GetParameters()[0].ParameterType) : null;
            return m.Invoke(cr, filter != null ? new[] { filter } : null);
        }
        catch { return null; }
    }

    private static IEnumerable<object?> EnumerateRef(object? xref, string prop)
    {
        var list = new List<object?>();
        try
        {
            var v = xref?.GetType().GetProperty(prop)?.GetValue(xref);
            if (v is System.Collections.IEnumerable en)
                foreach (var item in en) list.Add(item);
        }
        catch { }
        return list;
    }

    public string FindUnusedBlocks(string? plcName = null)
    {
        lock (_lock) { try { RequireProject();
            var (cr, crErr) = GetCrossReferenceService();
            if (cr == null) return Err(crErr ?? "不支持交叉引用");
            var allBlocks = GetAllBlocks(ResolvePlcForDownload(plcName).BlockGroup);
            var xref = GetCrossReferences(cr);
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in EnumerateRef(xref, "Sources"))
            {
                var srcName = src?.GetType().GetProperty("Name")?.GetValue(src)?.ToString();
                if (srcName != null) referenced.Add(srcName);
                foreach (var r in EnumerateRef(src, "References"))
                {
                    var rName = r?.GetType().GetProperty("Name")?.GetValue(r)?.ToString();
                    if (rName != null) referenced.Add(rName);
                }
            }
            // 主循环 OB（OB1/Main）不应视为未使用，按名称与块号综合判断
            var unused = allBlocks.Where(b => !referenced.Contains(b.Name)
                && b.Name != "Main" && b.Name != "OB1" && b.Name != "Main_OB" && b.Number != 1)
                .Select(b => new { name = b.Name, type = GetBlockTypeName(b), number = b.Number }).ToList();
            return JsonConvert.SerializeObject(new { success = true, total = allBlocks.Count, unusedCount = unused.Count, unused }, Formatting.Indented);
        } catch (Exception ex) { return Err(ex.Message); } }
    }

    public string FindBlockReferences(string blockName)
    {
        lock (_lock) { try { RequireProject();
            var (cr, crErr) = GetCrossReferenceService();
            if (cr == null) return Err(crErr ?? "不支持交叉引用");
            var xref = GetCrossReferences(cr);
            var refs = new List<object>();
            foreach (var src in EnumerateRef(xref, "Sources"))
            {
                var srcName = src?.GetType().GetProperty("Name")?.GetValue(src)?.ToString() ?? "";
                var srcType = src?.GetType().GetProperty("TypeName")?.GetValue(src)?.ToString() ?? "";
                foreach (var r in EnumerateRef(src, "References"))
                {
                    var rName = r?.GetType().GetProperty("Name")?.GetValue(r)?.ToString();
                    if (rName != null && rName.Equals(blockName, StringComparison.OrdinalIgnoreCase))
                        refs.Add(new { sourceName = srcName, sourceType = srcType });
                }
            }
            return JsonConvert.SerializeObject(new { success = true, blockName, count = refs.Count, references = refs }, Formatting.Indented);
        } catch (Exception ex) { return Err(ex.Message); } }
    }

    // 监视表
    public string ListWatchAndForceTables()
    {
        lock (_lock) { try { RequireProject();
            var list = new List<object>();
            foreach (var plc in GetPlcSoftwareList()) {
                var wf = plc.WatchAndForceTableGroup;
                if (wf == null) continue;
                foreach (PlcWatchTable t in wf.WatchTables ?? Enumerable.Empty<PlcWatchTable>())
                    list.Add(new { plcName = plc.Name, name = t.Name, type = "WatchTable" });
                foreach (PlcForceTable t in wf.ForceTables ?? Enumerable.Empty<PlcForceTable>())
                    list.Add(new { plcName = plc.Name, name = t.Name, type = "ForceTable" });
            }
            return JsonConvert.SerializeObject(new { success = true, count = list.Count, tables = list }, Formatting.Indented);
        } catch (Exception ex) { return Err(ex.Message); } }
    }

    // 网络扫描（V19 GetAccessibleDevices；V17 无该 API，反射尝试）
    public string ScanNetworkDevices(string? plcName = null)
    {
        lock (_lock) { try { RequireProject();
            var plc = ResolvePlcForDownload(plcName);
            var dp = plc.GetService<DownloadProvider>();
            if (dp == null) return Err("不支持下载服务");
            var config = dp.Configuration;
            if (config == null) return Err("未配置连接");
            var firstMode = config.Modes?.FirstOrDefault();
            if (firstMode == null) return Err("未找到连接模式");
            var firstIf = firstMode.PcInterfaces?.FirstOrDefault();
            if (firstIf == null) return Err("未找到 PG/PC 接口");
            var getDevices = firstIf.GetType().GetMethod("GetAccessibleDevices");
            if (getDevices == null)
                return Err("当前 TIA 版本（V17）的 Openness 不支持 GetAccessibleDevices 网络扫描，请在博途 GUI 中扫描网络");
            var devices = getDevices.Invoke(firstIf, null) as System.Collections.IEnumerable;
            if (devices == null) return Err("扫描失败");
            var list = new List<object>();
            foreach (var d in devices)
            {
                try
                {
                    var dt = d.GetType();
                    list.Add(new
                    {
                        name = dt.GetProperty("Name")?.GetValue(d)?.ToString() ?? "",
                        address = dt.GetProperty("Address")?.GetValue(d)?.ToString() ?? "",
                        deviceSeries = dt.GetProperty("DeviceSeries")?.GetValue(d)?.ToString() ?? "",
                        macAddress = dt.GetProperty("MACAddress")?.GetValue(d)?.ToString() ?? ""
                    });
                }
                catch { }
            }
            return JsonConvert.SerializeObject(new { success = true, count = list.Count, devices = list }, Formatting.Indented);
        } catch (Exception ex) { return Err(ex.Message); } }
    }

    // 下载到设备（用反射绕过 IConfiguration 类型转换问题）
    public string DownloadToDevice(string? plcName = null)
    {
        SafeOnlineExecutor.DemandAuthorized("DownloadToDevice");
        lock (_lock) { try { RequireProject();
            var plc = ResolvePlcForDownload(plcName);
            var dp = plc.GetService<DownloadProvider>();
            if (dp == null) return Err("不支持下载服务");
            var config = dp.Configuration;
            if (config == null) return Err("未配置连接");

            var method = typeof(DownloadProvider).GetMethods()
                .First(m => m.Name == "Download" && m.GetParameters().Length == 4
                    && m.GetParameters()[0].ParameterType.Name == "IConfiguration");

            var result = method.Invoke(dp, new object?[] { config, null, null, new DownloadOptions() });
            if (result == null) return Err("下载失败");

            var errCount = (int)(result.GetType().GetProperty("ErrorCount")?.GetValue(result) ?? 0);
            var state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "";
            return JsonConvert.SerializeObject(new { success = errCount == 0, state, errorCount = errCount }, Formatting.Indented);
        } catch (Exception ex) { return Err(ex.Message); } }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace TiaMcpServer
{
    /// <summary>
    /// 项目 / 设备 / 子网 数据（任务 6）。
    /// </summary>
    public partial class PortalService
    {
        // ────────────────────────────────────────────────────────────
        // 项目级
        // ────────────────────────────────────────────────────────────

        public string GetProjectInfo()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        name = _project!.Name,
                        path = _project.Path?.FullName ?? "",
                        creationTime = SafeGetAttr(_project, "CreationTime") ?? "",
                        lastModified = SafeGetAttr(_project, "LastModified") ?? "",
                        author = SafeGetAttr(_project, "Author") ?? "",
                        comment = SafeGetAttr(_project, "Comment") ?? "",
                        family = SafeGetAttr(_project, "Family") ?? "",
                        typeIdentifier = SafeTypeIdentifier(_project)
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetProjectSummary()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var p = _project!;
                    // ★递归统计所有设备（含设备组），修复多站项目计数为 0
                    var allDevices = GetAllDevicesWithGroup();
                    int deviceCount = allDevices.Count;
                    int plcCount = GetPlcSoftwareList().Count;
                    int subnetCount = p.Subnets.Count();
                    int deviceGroupCount = p.DeviceGroups.Count();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        name = p.Name,
                        path = p.Path?.FullName ?? "",
                        deviceCount,
                        plcCount,
                        subnetCount,
                        deviceGroupCount,
                        isConnected = IsConnected
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateProject(string projectPath, string? projectName = null)
        {
            lock (_lock)
            {
                try
                {
                    // ★检查已有打开的项目，避免重复创建导致状态混乱
                    if (_project != null)
                    {
                        return Err("已有打开的项目，请先关闭");
                    }

                    if (string.IsNullOrEmpty(projectName))
                        projectName = new DirectoryInfo(projectPath).Name;

                    if (Directory.Exists(projectPath) && Directory.EnumerateFileSystemEntries(projectPath).Any())
                        return Err($"目标目录已存在且非空: {projectPath}");

                    // 如果已连接（attach），复用现有连接，不新开博途窗口
                    if (_tiaPortal == null)
                        _tiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface);
                    _project = _tiaPortal.Projects.Create(new DirectoryInfo(projectPath), projectName);
                    // ★ 创建新项目时清除所有 LAD 缓存，避免跨项目缓存污染
                    PortalService.ClearAllLadCaches();
                    // 防崩溃兜底：自动应答 TIA 确认弹窗
                    EnsureDialogSuppression();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建项目: {_project.Name}",
                        name = _project.Name,
                        path = _project.Path?.FullName ?? ""
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string OpenProject(string projectPath)
        {
            lock (_lock)
            {
                try
                {
                    // ★检查已有打开的项目，避免重复打开导致状态混乱
                    if (_project != null)
                    {
                        return Err("已有打开的项目，请先关闭");
                    }

                    if (_tiaPortal == null)
                        _tiaPortal = new TiaPortal(TiaPortalMode.WithUserInterface);
                    // TIA Portal Openness: Projects.Open 接受对应版本的 .apXX/.zapXX 文件
                    FileInfo fi;
                    if (EnvironmentDiscoveryService.IsTiaProjectFile(projectPath))
                    {
                        fi = new FileInfo(projectPath);
                    }
                    else
                    {
                        // 如果传入的是目录，优先查找当前 TIA 版本工程，再兼容其他 .apXX。
                        var dir = new DirectoryInfo(projectPath);
                        var preferred = dir.GetFiles("*" + EnvironmentDiscoveryService.CurrentProjectExtension()).FirstOrDefault();
                        var projectFile = preferred ?? dir.GetFiles("*.ap*").FirstOrDefault(f => EnvironmentDiscoveryService.IsTiaProjectFile(f.FullName, false));
                        if (projectFile == null) return Err($"目录中未找到 TIA 工程文件（.apXX）: {projectPath}");
                        // ★V17 修复★ 显式归一化为绝对路径，避免部分 Openness 版本对 FileInfo 的
                        // 原始路径形态敏感（目录分支实测出现过 "The argument 'path' cannot be a relative path"）。
                        fi = new FileInfo(projectFile.FullName);
                        if (string.IsNullOrWhiteSpace(fi.FullName) || !File.Exists(fi.FullName))
                            return Err($"工程文件不存在或路径无效: {projectFile.FullName}");
                    }
                    _project = _tiaPortal.Projects.Open(fi);
                    // ★ 打开项目时清除所有 LAD 缓存，避免跨项目缓存污染
                    PortalService.ClearAllLadCaches();
                    // 防崩溃兜底：自动应答 TIA 确认弹窗
                    EnsureDialogSuppression();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已打开项目: {_project.Name}",
                        name = _project.Name,
                        path = _project.Path?.FullName ?? ""
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CloseProject()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (_sandboxActive) return Err("当前处于沙盒工程，请使用沙盒退出功能返回原工程。");
                    _project!.Close();
                    _project = null;
                    // ★ 关闭项目时清除所有 LAD 缓存
                    PortalService.ClearAllLadCaches();
                    return Ok("项目已关闭");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string SaveProject()
        {
            lock (_lock)
            {
                try { RequireProject(); _project!.Save(); return Ok("项目已保存"); }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ArchiveProject(string outputDir, string archiveName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    Directory.CreateDirectory(outputDir);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(archiveName ?? "", @"\.zap\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        archiveName += EnvironmentDiscoveryService.CurrentArchiveExtension();
                    _project!.Archive(new DirectoryInfo(outputDir), archiveName, ProjectArchivationMode.Compressed);
                    var outputPath = Path.Combine(outputDir, archiveName);
                    return Ok($"项目已归档到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 设备
        // ────────────────────────────────────────────────────────────

        public string ListDevices()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★递归遍历所有设备（含设备组），返回设备所在的组路径
                    var list = GetAllDevicesWithGroup().Select(t => new
                    {
                        name = t.Device.Name,
                        typeId = t.Device.TypeIdentifier?.ToString() ?? "",
                        isGsd = t.Device.IsGsd,
                        groupPath = t.GroupPath
                    }).ToList();
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, devices = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetDeviceStructure(string deviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");
                    var tree = GetDeviceItemsRecursive(device.DeviceItems);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deviceName = device.Name,
                        typeId = device.TypeIdentifier?.ToString() ?? "",
                        children = tree
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetDeviceItemInfo(string deviceName, string? itemName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");

                    if (!string.IsNullOrEmpty(itemName))
                    {
                        var di = FindDeviceItemInDevice(device.DeviceItems, itemName!);
                        if (di == null) return Err($"未找到设备项: {itemName}");
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            name = di.Name,
                            typeId = di.TypeIdentifier?.ToString() ?? "",
                            isPlugged = di.IsPlugged,
                            positionNumber = di.PositionNumber
                        });
                    }
                    return GetDeviceStructure(deviceName);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string FindDeviceItem(string deviceItemName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★递归遍历所有设备（含设备组）
                    foreach (var device in GetAllDevices())
                    {
                        var di = FindDeviceItemInDevice(device.DeviceItems, deviceItemName);
                        if (di != null)
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                deviceName = device.Name,
                                deviceItemName = di.Name,
                                typeId = di.TypeIdentifier?.ToString() ?? ""
                            });
                    }
                    return Err($"未找到设备项: {deviceItemName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ListDeviceAttributes(string deviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");
                    var attrs = new Dictionary<string, string>();
                    var eo = (IEngineeringObject)device;
                    foreach (var info in eo.GetAttributeInfos())
                    {
                        try
                        {
                            var val = eo.GetAttribute(info.Name);
                            attrs[info.Name] = val?.ToString() ?? "";
                        }
                        catch { }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deviceName = device.Name,
                        attributes = attrs
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateDevice(string deviceItemTypeId, string deviceItemName, string deviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // HMI 设备需要 name/deviceName 为 null（空字符串自动转为 null）
                    // 参考：西门子官方文档 "TIA Portal Openness 使用技巧" 第6条
                    // CreateWithItem 创建 HMI 时 name 和 deviceName 必须为 null
                    var itemName = string.IsNullOrEmpty(deviceItemName) ? null : deviceItemName;
                    var devName = string.IsNullOrEmpty(deviceName) ? null : deviceName;
                    var device = _project!.Devices.CreateWithItem(deviceItemTypeId, itemName, devName);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建设备: {device.Name}",
                        device = new { name = device.Name, typeId = device.TypeIdentifier?.ToString() ?? "" }
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 按订单号创建设备（便捷方法）。内部自动查找 TypeIdentifier。
        /// 订单号格式：6ES7214-1AG40-0XB0（空格可选，版本号可选）
        /// </summary>
        public string CreateDeviceByOrder(string orderNumber, string deviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();

                    // 1. 按订单号查找 TypeIdentifier
                    var typeId = FindTypeIdentifierByOrder(orderNumber);
                    if (string.IsNullOrEmpty(typeId))
                        return Err($"未找到订单号 {orderNumber} 对应的硬件。请用 search_hardware_catalog 搜索型号关键词（如 1214C）确认。");

                    // 2. 用 TypeIdentifier 创建设备
                    // ★空字符串 deviceName 必须转为 null（HMI 设备创建时 name/deviceName 不能为空字符串）
                    var name = string.IsNullOrEmpty(deviceName) ? null : deviceName;
                    var device = CreateDeviceWithTypeIdFallback(typeId, name, out var matchedTypeId);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建设备: {device.Name}（订单号: {orderNumber}）",
                        device = new { name = device.Name, typeId = device.TypeIdentifier?.ToString() ?? "" },
                        matchedTypeIdentifier = matchedTypeId,
                        orderNumber = orderNumber,
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 以 TypeIdentifier 创建设备，失败时按固件版本回退重试。
        /// TypeIdentifierLibrary 条目为 V19 校准（如 /V4.6），V17 硬件目录中固件版本不同（如 /V4.1），
        /// 依次尝试替换版本号、去掉版本号后缀，兼容跨版本创建设备。
        /// </summary>
        private Device CreateDeviceWithTypeIdFallback(string typeId, string? deviceName, out string matchedTypeId)
        {
            var candidates = new List<string> { typeId };
            var m = System.Text.RegularExpressions.Regex.Match(typeId, @"^(.*/V)(\d+\.\d+)$");
            if (m.Success)
            {
                var prefix = m.Groups[1].Value;
                var version = m.Groups[2].Value;
                // ★V17 兼容★ 补充 4.5/2.9/4.6/2.8（V17 硬件目录常见 /V4.5、/V2.9），并按版本从新到旧排列
                var versions = new[] { "4.6", "4.5", "4.4", "4.2", "4.1", "4.0", "3.0", "2.9", "2.8", "2.2", "2.0" };
                foreach (var v in versions)
                    if (v != version && !candidates.Contains(prefix + v))
                        candidates.Add(prefix + v);
                candidates.Add(prefix.TrimEnd('/')); // 去掉版本号后缀
            }
            candidates.Add(typeId.Replace("/V" + (m.Success ? m.Groups[2].Value : ""), ""));

            Exception? lastEx = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var device = _project!.Devices.CreateWithItem(candidate, deviceName, deviceName);
                    matchedTypeId = candidate;
                    return device;
                }
                catch (Exception ex) { lastEx = ex; }
            }
            matchedTypeId = typeId;
            throw lastEx ?? new InvalidOperationException($"无法以 TypeIdentifier 创建设备: {typeId}");
        }

        public string PlugModule(string parentDeviceItemName, string typeIdentifier, string name, int positionNumber)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    DeviceItem? hwObj = null;
                    // ★递归遍历所有设备（含设备组）
                    foreach (var dev in GetAllDevices())
                    {
                        foreach (var di in EnumerateDeviceItems(dev.DeviceItems))
                        {
                            if (di.Name.Equals(parentDeviceItemName, StringComparison.OrdinalIgnoreCase))
                            { hwObj = di; break; }
                        }
                        if (hwObj != null) break;
                    }
                    if (hwObj == null) return Err($"未找到设备项: {parentDeviceItemName}");

                    var newItem = hwObj.PlugNew(typeIdentifier, name, positionNumber);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已插入模块: {name}",
                        deviceItem = new
                        {
                            name = newItem.Name,
                            typeId = newItem.TypeIdentifier?.ToString() ?? "",
                            position = positionNumber
                        }
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ShowDeviceInEditor(string deviceName, string viewName = "Device")
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");
                    // Device.ShowInEditor(View) 是直接方法（IShowable 接口）
                    var view = ParseView(viewName);
                    device.ShowInEditor(view);
                    return Ok($"已在编辑器中显示设备: {deviceName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 子网 / 网络接口
        // ────────────────────────────────────────────────────────────

        public string ListSubnets()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var list = _project!.Subnets
                        .Select(s => new { name = s.Name, typeId = s.TypeIdentifier?.ToString() ?? "" })
                        .ToList();
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, subnets = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateSubnet(string typeIdentifier, string name)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var sub = _project!.Subnets.Create(typeIdentifier, name);
                    return Ok($"已创建子网: {sub.Name} ({sub.TypeIdentifier})");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteSubnet(string name)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var sub = _project!.Subnets
                        .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (sub == null) return Err($"未找到子网: {name}");
                    sub.Delete();
                    return Ok($"已删除子网: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }
        
        public string DeleteDevice(string deviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var dev = FindDeviceByName(deviceName);
                    if (dev == null) return Err($"未找到设备: {deviceName}");
                    dev.Delete();
                    return Ok($"已删除设备: {deviceName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ConnectToSubnet(string deviceName, string subnetName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");
                    var subnet = _project!.Subnets
                        .FirstOrDefault(s => s.Name.Equals(subnetName, StringComparison.OrdinalIgnoreCase));
                    if (subnet == null) return Err($"未找到子网: {subnetName}");

                    var candidates = new List<(string ItemPath, NetworkInterface Interface)>();
                    foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                    {
                        try
                        {
                            var netIf = di.GetService<NetworkInterface>();
                            if (netIf != null && netIf.Nodes.Count > 0)
                                candidates.Add((di.Name, netIf));
                        }
                        catch { }
                    }
                    if (candidates.Count == 0)
                        return Err($"设备 {deviceName} 上未找到可用的网络接口节点");

                    // ★V17校准★ PROFINET/以太网接口优先：
                    // MPI/DP(PROFIBUS) 节点无法接入 PN/IE 子网，若其先被尝试会失败
                    // 并白占诊断轮次（KTP700 Basic 实测先枚举到 MPI/DP_CP_1 导致接入失败）。
                    candidates = candidates
                        .OrderBy(c => IsProfibusInterfaceName(c.ItemPath) ? 1 : 0)
                        .ThenBy(c => c.ItemPath, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var diagnostics = new List<object>();
                    foreach (var candidate in candidates)
                    {
                        for (var nodeIndex = 0; nodeIndex < candidate.Interface.Nodes.Count; nodeIndex++)
                        {
                            var node = candidate.Interface.Nodes[nodeIndex];
                            string connectedName = "";
                            try { connectedName = node.ConnectedSubnet?.Name ?? ""; } catch { }

                            if (connectedName.Equals(subnetName, StringComparison.OrdinalIgnoreCase))
                            {
                                return JsonConvert.SerializeObject(new
                                {
                                    success = true,
                                    message = $"{deviceName} 已连接到子网 {subnetName}",
                                    deviceName,
                                    subnetName,
                                    deviceItem = candidate.ItemPath,
                                    nodeIndex,
                                    skipped = true,
                                    verified = true
                                });
                            }

                            if (!string.IsNullOrWhiteSpace(connectedName))
                            {
                                diagnostics.Add(new
                                {
                                    deviceItem = candidate.ItemPath,
                                    nodeIndex,
                                    connectedSubnet = connectedName,
                                    skipped = true,
                                    reason = "节点已连接到其他子网；为避免破坏现有拓扑未自动断开"
                                });
                                continue;
                            }

                            try
                            {
                                node.ConnectToSubnet(subnet);
                                string verifiedName = "";
                                try { verifiedName = node.ConnectedSubnet?.Name ?? ""; } catch { }
                                if (verifiedName.Equals(subnetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return JsonConvert.SerializeObject(new
                                    {
                                        success = true,
                                        message = $"已将 {deviceName} 的网络接口连接到子网 {subnetName}",
                                        deviceName,
                                        subnetName,
                                        deviceItem = candidate.ItemPath,
                                        nodeIndex,
                                        verified = true
                                    });
                                }
                                diagnostics.Add(new
                                {
                                    deviceItem = candidate.ItemPath,
                                    nodeIndex,
                                    attempted = true,
                                    verified = false,
                                    connectedSubnetAfter = verifiedName,
                                    error = "ConnectToSubnet 未抛异常，但 ConnectedSubnet 未反查到目标子网"
                                });
                            }
                            catch (Exception ex)
                            {
                                diagnostics.Add(new
                                {
                                    deviceItem = candidate.ItemPath,
                                    nodeIndex,
                                    attempted = true,
                                    verified = false,
                                    error = ex.Message
                                });
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"未能把设备 {deviceName} 连接到子网 {subnetName}",
                        deviceName,
                        subnetName,
                        diagnostics
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err("连接子网失败: " + ex.Message); }
            }
        }

        /// <summary>按名称判断网络接口是否为 PROFIBUS/MPI 类（无法接入 PN/IE 子网）。</summary>
        private static bool IsProfibusInterfaceName(string itemName)
        {
            var n = (itemName ?? "").ToUpperInvariant();
            return n.Contains("MPI") || n.Contains("PROFIBUS") || n.Contains("DP_CP") || n.Contains("/DP");
        }

        public string ListNetworkInterfaces()
        {            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var list = new List<object>();
                    // ★递归遍历所有设备（含设备组）
                    foreach (var device in GetAllDevices())
                    {
                        foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                        {
                            NetworkInterface? netIf = null;
                            try { netIf = di.GetService<NetworkInterface>(); }
                            catch { }
                            if (netIf != null)
                            {
                                var connectedSubnet = "";
                                try
                                {
                                    if (netIf.Nodes.Count > 0)
                                        connectedSubnet = netIf.Nodes[0].ConnectedSubnet?.Name ?? "";
                                }
                                catch { }
                                list.Add(new
                                {
                                    deviceName = device.Name,
                                    deviceItemName = di.Name,
                                    nodeCount = netIf.Nodes.Count,
                                    connectedSubnet
                                });
                            }
                        }
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, networkInterfaces = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // PROFINET 拓扑 / IO 系统
        // ────────────────────────────────────────────────────────────

        /// <summary>在指定子网下创建 PROFINET IO 系统（IoController.CreateIoSystem）。</summary>
        public string CreateIoSystem(string plcName, string subnetName, string ioSystemName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plcDevice = FindDeviceByName(plcName);
                    if (plcDevice == null) return Err($"未找到 PLC 设备: {plcName}");
                    var subnet = _project!.Subnets
                        .FirstOrDefault(s => s.Name.Equals(subnetName, StringComparison.OrdinalIgnoreCase));
                    if (subnet == null) return Err($"未找到子网: {subnetName}");

                    // ★验证 PLC 网络接口是否已连接到指定子网，未连接则提前报错
                    // （CreateIoSystem 依赖 PLC 网络接口已 ConnectToSubnet，否则创建出的 IO 系统不在目标子网上）
                    bool connectedToSubnet = false;
                    foreach (var di in EnumerateDeviceItems(plcDevice.DeviceItems))
                    {
                        NetworkInterface? netIf = null;
                        try { netIf = di.GetService<NetworkInterface>(); }
                        catch { }
                        if (netIf == null) continue;
                        try
                        {
                            foreach (var node in netIf.Nodes)
                            {
                                if (node.ConnectedSubnet != null
                                    && string.Equals(node.ConnectedSubnet.Name, subnetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    connectedToSubnet = true;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (connectedToSubnet) break;
                    }
                    if (!connectedToSubnet)
                        return Err($"PLC {plcName} 的网络接口未连接到子网 {subnetName}，请先调用 connect_to_subnet 将 PLC 连接到该子网");

                    // IoController 不是 IEngineeringService，需通过 NetworkInterface.IoControllers 获取
                    IoController? ioController = null;
                    foreach (var di in EnumerateDeviceItems(plcDevice.DeviceItems))
                    {
                        NetworkInterface? netIf = null;
                        try { netIf = di.GetService<NetworkInterface>(); }
                        catch { }
                        if (netIf == null) continue;
                        try
                        {
                            foreach (var ioc in netIf.IoControllers)
                            {
                                ioController = ioc;
                                break;
                            }
                        }
                        catch { }
                        if (ioController != null) break;
                    }
                    if (ioController == null)
                        return Err($"在 PLC {plcName} 上未找到 IoController（确认该设备为 PROFINET 控制器且已连接子网）");

                    // CreateIoSystem 仅接受名称，子网由 PLC 网络接口连接决定
                    var ioSystem = ioController.CreateIoSystem(ioSystemName);
                    return Ok($"已在子网 {subnetName} 上创建 IO 系统: {ioSystem.Name}（PLC: {plcName}）");
                }
                catch (Exception ex) { return Err("创建 IO 系统失败: " + ex.Message); }
            }
        }

        /// <summary>将 IO 设备连接到指定 IO 系统（IoConnector.ConnectToIoSystem）。</summary>
        public string ConnectIoDevice(string plcName, string ioDeviceName, string ioSystemName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var ioDevice = FindDeviceByName(ioDeviceName);
                    if (ioDevice == null) return Err($"未找到 IO 设备: {ioDeviceName}");

                    // IoConnector 不是 IEngineeringService，需通过 NetworkInterface.IoConnectors 获取
                    IoConnector? ioConnector = null;
                    foreach (var di in EnumerateDeviceItems(ioDevice.DeviceItems))
                    {
                        NetworkInterface? netIf = null;
                        try { netIf = di.GetService<NetworkInterface>(); }
                        catch { }
                        if (netIf == null) continue;
                        try
                        {
                            foreach (var ioc in netIf.IoConnectors)
                            {
                                ioConnector = ioc;
                                break;
                            }
                        }
                        catch { }
                        if (ioConnector != null) break;
                    }
                    if (ioConnector == null)
                        return Err($"在 IO 设备 {ioDeviceName} 上未找到 IoConnector（确认该设备为 PROFINET IO 设备）");

                    IoSystem? ioSystem = null;
                    // ★plcName 非空时优先在该 PLC 连接的子网上查找 IO 系统
                    // （IoController 不直接暴露 IoSystems 集合，需通过子网枚举）
                    if (!string.IsNullOrEmpty(plcName))
                    {
                        var plcDevice = FindPlcDeviceByName(plcName);
                        if (plcDevice != null)
                        {
                            foreach (var sub in GetConnectedSubnets(plcDevice))
                            {
                                try
                                {
                                    foreach (var ios in sub.IoSystems)
                                    {
                                        if (ios.Name.Equals(ioSystemName, StringComparison.OrdinalIgnoreCase))
                                        { ioSystem = ios; break; }
                                    }
                                }
                                catch { }
                                if (ioSystem != null) break;
                            }
                        }
                    }
                    // 未指定 plcName 或在 PLC 子网上未找到时，回退到遍历所有子网
                    if (ioSystem == null)
                    {
                        foreach (var subnet in _project!.Subnets)
                        {
                            try
                            {
                                foreach (var ios in subnet.IoSystems)
                                {
                                    if (ios.Name.Equals(ioSystemName, StringComparison.OrdinalIgnoreCase))
                                    { ioSystem = ios; break; }
                                }
                            }
                            catch { }
                            if (ioSystem != null) break;
                        }
                    }
                    if (ioSystem == null) return Err($"未找到 IO 系统: {ioSystemName}");

                    ioConnector.ConnectToIoSystem(ioSystem);
                    return Ok($"已将 IO 设备 {ioDeviceName} 连接到 IO 系统 {ioSystemName}");
                }
                catch (Exception ex) { return Err("连接 IO 设备失败: " + ex.Message); }
            }
        }

        /// <summary>连接两个设备的网络端口（PROFINET 物理拓扑）。</summary>
        public string ConnectNetworkPorts(string plcName, string port1DeviceName, int port1Index, string port2DeviceName, int port2Index)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★plcName 非空时优先在该 PLC 设备子树中查找端口，未找到则回退全局查找
                    var port1 = FindNetworkPortScoped(plcName, port1DeviceName, port1Index);
                    if (port1 == null) return Err($"未找到设备 {port1DeviceName} 的网络端口 #{port1Index}");
                    var port2 = FindNetworkPortScoped(plcName, port2DeviceName, port2Index);
                    if (port2 == null) return Err($"未找到设备 {port2DeviceName} 的网络端口 #{port2Index}");

                    port1.ConnectToPort(port2);
                    return Ok($"已连接 {port1DeviceName}[{port1Index}] <-> {port2DeviceName}[{port2Index}]");
                }
                catch (Exception ex) { return Err("连接网络端口失败: " + ex.Message); }
            }
        }

        /// <summary>设置设备网络接口的 IP 地址。</summary>
        public string SetDeviceIp(string plcName, string deviceName, string ipAddress, string subnetMask)
        {
        SafeOnlineExecutor.DemandAuthorized("SetDeviceIp");
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★V17 修复★ 不再使用 System.Net.IPAddress.TryParse：
                    // 它会触发 Socket.InitializeSockets → ConfigurationManager，
                    // 在 App.config 存在非法节时会抛 "配置系统未能初始化"
                    // （ConfigurationErrorsException），与博途无关。
                    if (!TryValidateIpv4(ipAddress))
                        return Err($"IP 地址格式无效: {ipAddress}");
                    if (!TryValidateIpv4(subnetMask))
                        return Err($"子网掩码格式无效: {subnetMask}");

                    Device? device = null;
                    if (!string.IsNullOrEmpty(plcName))
                    {
                        var plcDevice = FindPlcDeviceByName(plcName);
                        if (plcDevice != null
                            && string.Equals(plcDevice.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                            device = plcDevice;
                    }
                    device ??= FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");

                    var engineeringObjects = new List<(string Path, IEngineeringObject Object)>();
                    try
                    {
                        foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                        {
                            NetworkInterface? netIf = null;
                            try { netIf = di.GetService<NetworkInterface>(); } catch { }
                            if (netIf == null) continue;

                            try
                            {
                                object netIfBoxed = netIf;
                                if (netIfBoxed is IEngineeringObject netIfObject)
                                    engineeringObjects.Add(($"{di.Name}/NetworkInterface", netIfObject));
                            }
                            catch { }

                            for (var i = 0; i < netIf.Nodes.Count; i++)
                            {
                                try
                                {
                                    object nodeBoxed = netIf.Nodes[i];
                                    if (nodeBoxed is IEngineeringObject nodeObject)
                                        engineeringObjects.Add(($"{di.Name}/Node[{i}]", nodeObject));
                                }
                                catch { }
                            }

                            try
                            {
                                object diBoxed = di;
                                if (diBoxed is IEngineeringObject diObject)
                                    engineeringObjects.Add(($"{di.Name}/DeviceItem", diObject));
                            }
                            catch { }
                        }
                    }
                    catch (Exception enumEx)
                    {
                        // ★V17 修复★ 枚举网络接口本身可能抛"配置系统未能初始化"（组态系统未就绪），
                        // 例如该工程硬件配置从未成功编译。返回结构化提示而不是裸异常。
                        var enumHint = enumEx.Message.IndexOf("配置系统未能初始化", StringComparison.OrdinalIgnoreCase) >= 0
                            || enumEx.Message.IndexOf("configuration system", StringComparison.OrdinalIgnoreCase) >= 0
                            ? "该工程硬件组态系统未初始化：请先在博途中成功完成一次硬件编译（若因保护密码等失败，需先解决），再设置 IP。"
                            : "";
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"枚举设备 {deviceName} 网络接口失败: {enumEx.Message}",
                            deviceName,
                            ipAddress,
                            subnetMask,
                            hint = enumHint,
                            diagnostics = new List<object>()
                        }, Formatting.Indented);
                    }
                    if (engineeringObjects.Count == 0)
                        return Err($"设备 {deviceName} 上未找到可配置 IP 的工程对象");

                    var ipCandidates = new[] { "IpAddress", "IPAddress", "IP", "Ip", "Address" };
                    var maskCandidates = new[] { "SubnetMask", "SubnetMaskAddress", "IPSubnetMask", "Mask" };
                    var diagnostics = new List<object>();

                    foreach (var candidate in engineeringObjects)
                    {
                        var infoNames = new List<string>();
                        // ★V17校准★ 新建设备的组态系统可能尚未就绪（GetAttributeInfos 抛
                        // "配置系统未能初始化"），延迟重试一次再放弃
                        Exception? attrEx = null;
                        for (int attempt = 0; attempt < 2; attempt++)
                        {
                            try
                            {
                                infoNames.Clear();
                                foreach (var info in candidate.Object.GetAttributeInfos())
                                {
                                    try
                                    {
                                        if (!string.IsNullOrWhiteSpace(info.Name)) infoNames.Add(info.Name);
                                    }
                                    catch { }
                                }
                                attrEx = null;
                                break;
                            }
                            catch (Exception ex)
                            {
                                attrEx = ex;
                                if (attempt == 0) System.Threading.Thread.Sleep(2000);
                            }
                        }
                        if (attrEx != null)
                        {
                            // ★V17 修复★ 增加可操作的提示：组态系统未初始化最常见的原因是
                            // 该工程硬件配置从未成功编译（如保护密码导致的编译失败）。
                            var hint = attrEx.Message.IndexOf("配置系统未能初始化", StringComparison.OrdinalIgnoreCase) >= 0
                                || attrEx.Message.IndexOf("configuration system", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "该工程硬件组态系统未初始化：请先在博途中成功完成一次硬件编译（若因保护密码等失败，需先解决），再设置 IP。"
                                : "";
                            diagnostics.Add(new { path = candidate.Path, success = false, stage = "GetAttributeInfos", error = attrEx.Message, hint });
                            continue;
                        }

                        var ipAttr = ipCandidates
                            .Select(wanted => infoNames.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                        var maskAttr = maskCandidates
                            .Select(wanted => infoNames.FirstOrDefault(n => n.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
                            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

                        // 只有同一对象同时暴露 IP 和掩码属性时才写入，避免把普通 Address 属性误当网络地址。
                        if (string.IsNullOrWhiteSpace(ipAttr) || string.IsNullOrWhiteSpace(maskAttr))
                        {
                            diagnostics.Add(new
                            {
                                path = candidate.Path,
                                success = false,
                                skipped = true,
                                reason = "未在同一工程对象上找到可识别的 IP/掩码属性组合",
                                availableAttributes = infoNames
                            });
                            continue;
                        }

                        try
                        {
                            candidate.Object.SetAttribute(ipAttr!, ipAddress);
                            candidate.Object.SetAttribute(maskAttr!, subnetMask);

                            string readIp = "";
                            string readMask = "";
                            string? verifyError = null;
                            try
                            {
                                readIp = candidate.Object.GetAttribute(ipAttr!)?.ToString() ?? "";
                                readMask = candidate.Object.GetAttribute(maskAttr!)?.ToString() ?? "";
                            }
                            catch (Exception ex) { verifyError = ex.Message; }

                            var verified = readIp.Equals(ipAddress, StringComparison.OrdinalIgnoreCase)
                                           && readMask.Equals(subnetMask, StringComparison.OrdinalIgnoreCase);
                            if (verified)
                            {
                                return JsonConvert.SerializeObject(new
                                {
                                    success = true,
                                    message = $"已设置 {deviceName} 的 IP 为 {ipAddress}/{subnetMask}",
                                    deviceName,
                                    ipAddress,
                                    subnetMask,
                                    objectPath = candidate.Path,
                                    ipAttribute = ipAttr,
                                    maskAttribute = maskAttr,
                                    verified = true
                                });
                            }

                            diagnostics.Add(new
                            {
                                path = candidate.Path,
                                success = false,
                                writeSucceeded = true,
                                verified = false,
                                ipAttribute = ipAttr,
                                maskAttribute = maskAttr,
                                readIp,
                                readMask,
                                verifyError,
                                error = "属性写入未抛异常，但反查值与请求值不一致"
                            });
                        }
                        catch (Exception ex)
                        {
                            diagnostics.Add(new
                            {
                                path = candidate.Path,
                                success = false,
                                ipAttribute = ipAttr,
                                maskAttribute = maskAttr,
                                availableAttributes = infoNames,
                                error = ex.Message
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = $"未能为设备 {deviceName} 设置并验证 IP 地址",
                        deviceName,
                        ipAddress,
                        subnetMask,
                        diagnostics
                    }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    // ★V17 修复★ 兜底提示:该异常已证实主要来源是 .NET 配置系统
                    // (App.config 非法节)而非博途;配置已修复,此处保留通用指引。
                    var msg = ex.Message;
                    var hint = msg.IndexOf("配置系统未能初始化", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("configuration system", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "底层配置系统异常：请确认 TiaMcpServer.exe.config 有效（V4.4.5 已修复非法 applicationSettings 节）；若仍失败，请先在博途中成功完成一次硬件编译后再试。"
                        : "";
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "设置设备 IP 失败: " + msg,
                        deviceName,
                        ipAddress,
                        subnetMask,
                        hint,
                        exceptionType = ex.GetType().FullName,
                        stackTrace = ex.StackTrace ?? ""
                    }, Formatting.Indented);
                }
            }
        }

        /// <summary>
        /// 不依赖 System.Net 的 IPv4 校验（避免 Socket.InitializeSockets 触发
        /// .NET 配置系统加载，App.config 异常时该调用会抛 ConfigurationErrorsException）。
        /// </summary>
        private static bool TryValidateIpv4(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parts = value.Trim().Split('.');
            if (parts.Length != 4) return false;
            foreach (var p in parts)
            {
                if (p.Length == 0 || p.Length > 3) return false;
                foreach (var c in p)
                    if (c < '0' || c > '9') return false;
                if (int.TryParse(p, out var n) && (n < 0 || n > 255)) return false;
                if (!int.TryParse(p, out _)) return false;
            }
            return true;
        }

        /// <summary>列出项目所有 IO 系统。</summary>
        public string ListIoSystems(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★plcName 非空时只返回该 PLC 连接子网上的 IO 系统
                    // （IoController 不直接暴露 IoSystems 集合，通过 PLC 连接的子网过滤）
                    HashSet<string>? plcSubnetNames = null;
                    if (!string.IsNullOrEmpty(plcName))
                    {
                        var plcDevice = FindPlcDeviceByName(plcName);
                        if (plcDevice != null)
                        {
                            plcSubnetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var sub in GetConnectedSubnets(plcDevice))
                                plcSubnetNames.Add(sub.Name);
                        }
                    }
                    var list = new List<object>();
                    foreach (var subnet in _project!.Subnets)
                    {
                        // 如果指定了 plcName，过滤非该 PLC 连接的子网
                        if (plcSubnetNames != null && !plcSubnetNames.Contains(subnet.Name))
                            continue;
                        try
                        {
                            foreach (var ios in subnet.IoSystems)
                            {
                                int devCount = 0;
                                try
                                {
                                    var ioDevsProp = ios.GetType().GetProperty("IoDevices");
                                    if (ioDevsProp?.GetValue(ios) is System.Collections.IEnumerable ioDevs)
                                    {
                                        foreach (var d in ioDevs) devCount++;
                                    }
                                }
                                catch { }
                                list.Add(new
                                {
                                    name = ios.Name,
                                    subnetName = subnet.Name,
                                    connectedDeviceCount = devCount
                                });
                            }
                        }
                        catch { }
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, ioSystems = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>列出设备所有网络端口。</summary>
        public string ListNetworkPorts(string plcName, string deviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    // ★plcName 非空且与 deviceName 匹配时，优先使用 PLC 设备（仅返回该 PLC 设备子树的端口）
                    Device? device = null;
                    if (!string.IsNullOrEmpty(plcName))
                    {
                        var plcDevice = FindPlcDeviceByName(plcName);
                        if (plcDevice != null
                            && string.Equals(plcDevice.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                            device = plcDevice;
                    }
                    if (device == null)
                        device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");
                    var list = new List<object>();
                    foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                    {
                        NetworkInterface? netIf = null;
                        try { netIf = di.GetService<NetworkInterface>(); }
                        catch { }
                        if (netIf == null) continue;
                        for (int i = 0; i < netIf.Ports.Count; i++)
                        {
                            var port = netIf.Ports[i];
                            string connectedTo = "";
                            try
                            {
                                var partnerProp = port.GetType().GetProperty("ConnectedTo");
                                var partner = partnerProp?.GetValue(port);
                                if (partner != null)
                                {
                                    var partnerNameProp = partner.GetType().GetProperty("Name");
                                    connectedTo = partnerNameProp?.GetValue(partner)?.ToString() ?? "";
                                }
                            }
                            catch { }
                            // NetworkPort 无 Name 属性，反射获取（可能为空）
                            string portName = "";
                            try
                            {
                                var nameProp = port.GetType().GetProperty("Name");
                                if (nameProp != null)
                                    portName = nameProp.GetValue(port)?.ToString() ?? "";
                            }
                            catch { }
                            list.Add(new
                            {
                                index = i,
                                name = portName,
                                deviceItem = di.Name,
                                connectedTo
                            });
                        }
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, ports = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>在指定设备上按端口索引查找网络端口。</summary>
        private NetworkPort? FindNetworkPort(string deviceName, int portIndex)
        {
            var device = FindDeviceByName(deviceName);
            if (device == null) return null;
            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
            {
                NetworkInterface? netIf = null;
                try { netIf = di.GetService<NetworkInterface>(); }
                catch { }
                if (netIf == null) continue;
                if (portIndex >= 0 && portIndex < netIf.Ports.Count)
                    return netIf.Ports[portIndex];
            }
            return null;
        }

        /// <summary>
        /// 深度诊断 HMI↔PLC 网络连接：反射 NetworkInterface.Nodes 的所有属性，
        /// 找出连接对象的真实存储位置和 PartnerDevice/PartnerInterface 等关系字段。
        /// 用于学习 TIA Portal Openness API 中 HMI 连接的内部结构。
        /// </summary>
        public string DiagnoseNetworkConnections()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var refl = new List<string>();

                    // ★先诊断 Project 和 DeviceGroup 结构（用于排查设备枚举为空的问题）
                    refl.Add("=== Project 对象所有属性 ===");
                    refl.Add($"Project 类型: {_project!.GetType().FullName}");
                    foreach (var prop in _project.GetType().GetProperties())
                    {
                        try
                        {
                            var pname = prop.Name;
                            var ptype = prop.PropertyType.Name;
                            var val = prop.GetValue(_project);
                            if (val == null) { refl.Add($"  {pname} ({ptype}) = null"); continue; }
                            var enumType = val.GetType().GetInterfaces()
                                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                            if (enumType != null)
                            {
                                var itemType = enumType.GetGenericArguments()[0];
                                var countProp = val.GetType().GetProperty("Count");
                                int cnt = countProp != null ? (int)countProp.GetValue(val)! : -1;
                                refl.Add($"  {pname} ({ptype}) -> IEnumerable<{itemType.Name}>, Count={cnt}");
                                if (itemType.Name.Contains("Device") && cnt > 0)
                                {
                                    int shown = 0;
                                    foreach (var item in (System.Collections.IEnumerable)val)
                                    {
                                        if (shown >= 5) { refl.Add($"    -> ... 共 {cnt} 个"); break; }
                                        var nameProp = item.GetType().GetProperty("Name");
                                        var nameVal = nameProp?.GetValue(item)?.ToString() ?? "?";
                                        refl.Add($"    -> [{shown}] {item.GetType().Name}: {nameVal}");
                                        shown++;
                                    }
                                }
                            }
                            else
                            {
                                var valStr = val.ToString() ?? "";
                                if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                                refl.Add($"  {pname} ({ptype}) = {valStr}");
                            }
                        }
                        catch (Exception ex) { refl.Add($"  {prop.Name} 读取异常: {ex.Message}"); }
                    }

                    refl.Add("");
                    refl.Add("=== DeviceGroups 详情 ===");
                    int gi = 0;
                    foreach (var g in _project.DeviceGroups)
                    {
                        refl.Add($"--- DeviceGroup[{gi}]: {g.Name} (类型: {g.GetType().FullName}) ---");
                        foreach (var prop in g.GetType().GetProperties())
                        {
                            try
                            {
                                var pname = prop.Name;
                                var ptype = prop.PropertyType.Name;
                                var val = prop.GetValue(g);
                                if (val == null) { refl.Add($"  {pname} ({ptype}) = null"); continue; }
                                var enumType = val.GetType().GetInterfaces()
                                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                                if (enumType != null)
                                {
                                    var itemType = enumType.GetGenericArguments()[0];
                                    var countProp = val.GetType().GetProperty("Count");
                                    int cnt = countProp != null ? (int)countProp.GetValue(val)! : -1;
                                    refl.Add($"  {pname} ({ptype}) -> IEnumerable<{itemType.Name}>, Count={cnt}");
                                    if (itemType.Name.Contains("Device") && cnt > 0)
                                    {
                                        int shown = 0;
                                        foreach (var item in (System.Collections.IEnumerable)val)
                                        {
                                            if (shown >= 5) { refl.Add($"    -> ... 共 {cnt} 个"); break; }
                                            var nameProp = item.GetType().GetProperty("Name");
                                            var nameVal = nameProp?.GetValue(item)?.ToString() ?? "?";
                                            refl.Add($"    -> [{shown}] {item.GetType().Name}: {nameVal}");
                                            shown++;
                                        }
                                    }
                                }
                                else
                                {
                                    var valStr = val.ToString() ?? "";
                                    if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                                    refl.Add($"  {pname} ({ptype}) = {valStr}");
                                }
                            }
                            catch (Exception ex) { refl.Add($"  {prop.Name} 读取异常: {ex.Message}"); }
                        }
                        gi++;
                    }

                    refl.Add("");
                    refl.Add("=== 所有设备的 NetworkInterface 诊断 ===");
                    // ★递归遍历所有设备（含设备组）
                    foreach (var device in GetAllDevices())
                    {
                        refl.Add($"--- 设备: {device.Name} (TypeIdentifier: {device.TypeIdentifier?.Substring(0, Math.Min(60, device.TypeIdentifier?.Length ?? 0))}) ---");
                        foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                        {
                            NetworkInterface? netIf = null;
                            try { netIf = di.GetService<NetworkInterface>(); }
                            catch { }
                            if (netIf == null) continue;

                            refl.Add($"  NetworkInterface 在 DeviceItem: {di.Name}");
                            refl.Add($"    Nodes.Count = {netIf.Nodes.Count}");
                            for (int i = 0; i < netIf.Nodes.Count; i++)
                            {
                                var node = netIf.Nodes[i];
                                refl.Add($"    --- Node[{i}] 类型: {node.GetType().FullName} ---");
                                // 反射 Node 的所有属性
                                foreach (var prop in node.GetType().GetProperties())
                                {
                                    try
                                    {
                                        var val = prop.GetValue(node);
                                        if (val == null) { refl.Add($"      {prop.Name} ({prop.PropertyType.Name}) = null"); continue; }
                                        var valType = val.GetType();
                                        var valName = valType.Name;
                                        // 简单类型
                                        if (prop.PropertyType == typeof(string) || prop.PropertyType.IsEnum || prop.PropertyType.IsPrimitive)
                                        {
                                            var valStr = val.ToString();
                                            if (valStr.Length > 80) valStr = valStr.Substring(0, 80) + "...";
                                            refl.Add($"      {prop.Name} ({valName}) = {valStr}");
                                        }
                                        else
                                        {
                                            // 复杂对象，尝试取 Name
                                            var nameProp = valType.GetProperty("Name");
                                            if (nameProp != null)
                                            {
                                                var n = nameProp.GetValue(val)?.ToString();
                                                refl.Add($"      {prop.Name} ({valName}).Name = {n}");
                                            }
                                            else
                                            {
                                                refl.Add($"      {prop.Name} ({valName}) = {val}");
                                            }
                                        }
                                    }
                                    catch (Exception ex) { refl.Add($"      {prop.Name} 读取异常: {ex.Message}"); }
                                }
                            }
                            // 反射 NetworkInterface 自身的属性（看是否有 Connection 相关）
                            refl.Add($"    --- NetworkInterface 类型: {netIf.GetType().FullName} ---");
                            foreach (var prop in netIf.GetType().GetProperties())
                            {
                                try
                                {
                                    var pname = prop.Name;
                                    var ptype = prop.PropertyType.Name;
                                    // 跳过 Nodes（已处理）
                                    if (pname == "Nodes") continue;
                                    var val = prop.GetValue(netIf);
                                    if (val == null) { refl.Add($"      {pname} ({ptype}) = null"); continue; }
                                    // 对 IoControllers/IoConnectors/TransferAreas 集合深入反射
                                    if (pname == "IoControllers" || pname == "IoConnectors" || pname == "TransferAreas" || pname == "MulticastableTransferAreas")
                                    {
                                        var enumType = val.GetType().GetInterfaces()
                                            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                                        if (enumType != null)
                                        {
                                            var itemType = enumType.GetGenericArguments()[0];
                                            var countProp = val.GetType().GetProperty("Count");
                                            int cnt = countProp != null ? (int)countProp.GetValue(val)! : -1;
                                            refl.Add($"      {pname} ({ptype}) -> IEnumerable<{itemType.Name}>, Count={cnt}");
                                            int shown = 0;
                                            foreach (var item in (System.Collections.IEnumerable)val)
                                            {
                                                if (shown >= 5) { refl.Add($"        -> ..."); break; }
                                                var itemTypeName = item.GetType().Name;
                                                // 反射 item 的所有简单属性
                                                var props2 = new List<string>();
                                                foreach (var p2 in item.GetType().GetProperties())
                                                {
                                                    try
                                                    {
                                                        var v2 = p2.GetValue(item);
                                                        if (v2 == null) { props2.Add($"{p2.Name}=null"); continue; }
                                                        var v2Type = v2.GetType();
                                                        if (p2.PropertyType == typeof(string) || p2.PropertyType.IsEnum || p2.PropertyType.IsPrimitive)
                                                        {
                                                            var s = v2.ToString(); if (s.Length > 50) s = s.Substring(0, 50) + "...";
                                                            props2.Add($"{p2.Name}={s}");
                                                        }
                                                        else
                                                        {
                                                            var np = v2Type.GetProperty("Name");
                                                            if (np != null) props2.Add($"{p2.Name}.{v2Type.Name}.Name={np.GetValue(v2)}");
                                                            else props2.Add($"{p2.Name}({v2Type.Name})");
                                                        }
                                                    }
                                                    catch { }
                                                }
                                                refl.Add($"        -> [{shown}] {itemTypeName}: {string.Join(", ", props2)}");
                                                shown++;
                                            }
                                        }
                                        else
                                        {
                                            refl.Add($"      {pname} ({ptype}) = {val}");
                                        }
                                    }
                                    else
                                    {
                                        refl.Add($"      {pname} ({ptype}) = {val}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    // 诊断 Subnet（HMI↔PLC 的 S7 连接可能存储在 Subnet 的 Nodes 关系中）
                    refl.Add("=== Subnet 诊断 ===");
                    try
                    {
                        var subnetComp = _project!.Subnets;
                        foreach (var subnet in subnetComp)
                        {
                            refl.Add($"--- Subnet: {subnet.Name} (类型: {subnet.GetType().FullName}) ---");
                            // 反射 Subnet 的所有属性
                            foreach (var prop in subnet.GetType().GetProperties())
                            {
                                try
                                {
                                    var pname = prop.Name;
                                    var ptype = prop.PropertyType.Name;
                                    var val = prop.GetValue(subnet);
                                    if (val == null) { refl.Add($"  {pname} ({ptype}) = null"); continue; }
                                    // Nodes 集合
                                    var enumType = val.GetType().GetInterfaces()
                                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                                    if (enumType != null && pname == "Nodes")
                                    {
                                        var itemType = enumType.GetGenericArguments()[0];
                                        var countProp = val.GetType().GetProperty("Count");
                                        int cnt = countProp != null ? (int)countProp.GetValue(val)! : -1;
                                        refl.Add($"  {pname} ({ptype}) -> IEnumerable<{itemType.Name}>, Count={cnt}");
                                        int shown = 0;
                                        foreach (var item in (System.Collections.IEnumerable)val)
                                        {
                                            if (shown >= 10) { refl.Add($"    -> ..."); break; }
                                            var itemTypeName = item.GetType().Name;
                                            // 反射 Node 的所有简单属性 + Name
                                            var props2 = new List<string>();
                                            foreach (var p2 in item.GetType().GetProperties())
                                            {
                                                try
                                                {
                                                    var v2 = p2.GetValue(item);
                                                    if (v2 == null) { props2.Add($"{p2.Name}=null"); continue; }
                                                    var v2Type = v2.GetType();
                                                    if (p2.PropertyType == typeof(string) || p2.PropertyType.IsEnum || p2.PropertyType.IsPrimitive)
                                                    {
                                                        var s = v2.ToString(); if (s.Length > 50) s = s.Substring(0, 50) + "...";
                                                        props2.Add($"{p2.Name}={s}");
                                                    }
                                                    else
                                                    {
                                                        var np = v2Type.GetProperty("Name");
                                                        if (np != null) props2.Add($"{p2.Name}.{v2Type.Name}.Name={np.GetValue(v2)}");
                                                        else props2.Add($"{p2.Name}({v2Type.Name})");
                                                    }
                                                }
                                                catch { }
                                            }
                                            refl.Add($"    -> [{shown}] {itemTypeName}: {string.Join(", ", props2)}");
                                            shown++;
                                        }
                                    }
                                    else if (enumType != null)
                                    {
                                        var itemType = enumType.GetGenericArguments()[0];
                                        var countProp = val.GetType().GetProperty("Count");
                                        int cnt = countProp != null ? (int)countProp.GetValue(val)! : -1;
                                        refl.Add($"  {pname} ({ptype}) -> IEnumerable<{itemType.Name}>, Count={cnt}");
                                    }
                                    else
                                    {
                                        refl.Add($"  {pname} ({ptype}) = {val}");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex) { refl.Add($"Subnet 诊断异常: {ex.Message}"); }
                    return JsonConvert.SerializeObject(new { success = true, diagnostics = refl });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 反射诊断项目结构 + 导出项目完整结构摘要。
        /// 输出两部分：
        ///   1. projectStructure: 设备组树 + 每个 PLC 的块/变量表/UDT 清单 + 子网（结构化 JSON）
        ///   2. diagnostics: Project 和 DeviceGroup 的反射属性（用于排查设备枚举问题）
        /// </summary>
        public string DiagnoseProjectStructure()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var p = _project!;

                    // ═══════════════════════════════════════════════════════════════
                    // 第 1 部分：项目结构摘要（结构化 JSON）
                    // ═══════════════════════════════════════════════════════════════
                    var projInfo = new
                    {
                        name = p.Name,
                        path = p.Path?.FullName ?? "",
                        creationTime = SafeGetAttr(p, "CreationTime") ?? "",
                        lastModified = SafeGetAttr(p, "LastModified") ?? "",
                        author = SafeGetAttr(p, "Author") ?? ""
                    };

                    // 设备组树
                    var deviceTree = BuildDeviceGroupTree(p.DeviceGroups);

                    // 根级设备
                    var rootDevices = p.Devices.Select(d => new
                    {
                        name = d.Name,
                        typeId = d.TypeIdentifier?.ToString() ?? "",
                        isGsd = d.IsGsd
                    }).ToList();

                    // 每个 PLC 的详细信息（list 模式：块名+类型）
                    var plcs = new List<object>();
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        var (devName, groupPath) = FindDeviceForPlcWithGroup(plc);
                        var blocks = GetAllBlocks(plc.BlockGroup);
                        var tagTables = GetAllTagTables(plc.TagTableGroup);
                        var udts = GetAllTypes(plc.TypeGroup);

                        plcs.Add(new
                        {
                            name = plc.Name,
                            deviceName = devName,
                            groupPath,
                            blockCount = blocks.Count,
                            tagTableCount = tagTables.Count,
                            udtCount = udts.Count,
                            blocks = blocks.Select(b => new
                            {
                                name = b.Name,
                                type = GetBlockTypeName(b),
                                number = b.Number,
                                language = b.ProgrammingLanguage.ToString()
                            }).ToList(),
                            tagTables = tagTables.Select(t => new
                            {
                                name = t.Name,
                                tagCount = SafeGetTagCount(t)
                            }).ToList(),
                            udts = udts.Select(u => new
                            {
                                name = u.Name,
                                comment = SafeGetAttr(u, "Comment") ?? ""
                            }).ToList()
                        });
                    }

                    var subnets = p.Subnets.Select(s => new
                    {
                        name = s.Name,
                        typeId = s.TypeIdentifier?.ToString() ?? ""
                    }).ToList();

                    var projectStructure = new
                    {
                        project = projInfo,
                        rootDevices,
                        deviceTree,
                        plcCount = plcs.Count,
                        plcs,
                        subnetCount = subnets.Count,
                        subnets
                    };

                    // ═══════════════════════════════════════════════════════════════
                    // 第 2 部分：反射诊断（简化版，只输出关键属性）
                    // ═══════════════════════════════════════════════════════════════
                    var refl = new List<string>();
                    refl.Add($"Project 类型: {p.GetType().FullName}");
                    refl.Add($"Devices.Count = {p.Devices.Count()}");
                    refl.Add($"DeviceGroups.Count = {p.DeviceGroups.Count()}");
                    refl.Add($"Subnets.Count = {p.Subnets.Count()}");
                    refl.Add($"GetAllDevices().Count = {GetAllDevices().Count}");
                    refl.Add($"GetPlcSoftwareList().Count = {GetPlcSoftwareList().Count}");

                    int gi = 0;
                    foreach (var g in p.DeviceGroups)
                    {
                        var gName = g.GetType().GetProperty("Name")?.GetValue(g)?.ToString() ?? "?";
                        var devsProp = g.GetType().GetProperty("Devices");
                        int devCnt = 0;
                        if (devsProp?.GetValue(g) is System.Collections.IEnumerable devs)
                        {
                            foreach (var d in devs) devCnt++;
                        }
                        var subGroupsProp = g.GetType().GetProperty("Groups");
                        int subCnt = 0;
                        if (subGroupsProp?.GetValue(g) is System.Collections.IEnumerable sgs)
                        {
                            foreach (var sg in sgs) subCnt++;
                        }
                        refl.Add($"DeviceGroup[{gi}]: {gName}, Devices.Count={devCnt}, Groups.Count={subCnt}");

                        // 遍历子组
                        if (subGroupsProp?.GetValue(g) is System.Collections.IEnumerable subGroupsEnum)
                        {
                            int si = 0;
                            foreach (var sg in subGroupsEnum)
                            {
                                var sgName = sg.GetType().GetProperty("Name")?.GetValue(sg)?.ToString() ?? "?";
                                var sgDevsProp = sg.GetType().GetProperty("Devices");
                                int sgDevCnt = 0;
                                if (sgDevsProp?.GetValue(sg) is System.Collections.IEnumerable sgDevs)
                                {
                                    foreach (var d in sgDevs) sgDevCnt++;
                                }
                                refl.Add($"  SubGroup[{si}]: {sgName}, Devices.Count={sgDevCnt}");
                                si++;
                            }
                        }
                        gi++;
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        projectStructure,
                        diagnostics = refl
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 巡检表 / 交叉引用
        // ────────────────────────────────────────────────────────────

        /// <summary>递归构建设备组树（含子组和设备）。</summary>
        private static List<object> BuildDeviceGroupTree(System.Collections.IEnumerable groups)
        {
            var result = new List<object>();
            foreach (var g in groups)
            {
                var gName = g.GetType().GetProperty("Name")?.GetValue(g)?.ToString() ?? "?";
                var devices = new List<object>();
                var devicesProp = g.GetType().GetProperty("Devices");
                if (devicesProp?.GetValue(g) is System.Collections.IEnumerable devs)
                {
                    foreach (var dev in devs)
                    {
                        var dName = dev.GetType().GetProperty("Name")?.GetValue(dev)?.ToString() ?? "?";
                        var dType = dev.GetType().GetProperty("TypeIdentifier")?.GetValue(dev)?.ToString() ?? "";
                        var dIsGsd = false;
                        try { dIsGsd = (bool)(dev.GetType().GetProperty("IsGsd")?.GetValue(dev) ?? false); } catch { }
                        devices.Add(new { name = dName, typeId = dType, isGsd = dIsGsd });
                    }
                }
                var subGroups = new List<object>();
                var subGroupsProp = g.GetType().GetProperty("Groups");
                if (subGroupsProp?.GetValue(g) is System.Collections.IEnumerable subGroupsEnum)
                {
                    subGroups = BuildDeviceGroupTree(subGroupsEnum);
                }
                result.Add(new
                {
                    name = gName,
                    deviceCount = devices.Count,
                    devices,
                    subGroupCount = subGroups.Count,
                    subGroups
                });
            }
            return result;
        }

        /// <summary>查找 PLC 软件所属的设备和组路径。</summary>
        private (string DeviceName, string GroupPath) FindDeviceForPlcWithGroup(PlcSoftware plc)
        {
            foreach (var (dev, groupPath) in GetAllDevicesWithGroup())
            {
                foreach (var di in EnumerateDeviceItems(dev.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        if (swContainer?.Software is PlcSoftware p
                            && (p == plc || p.Name == plc.Name))
                            return (dev.Name, groupPath);
                    }
                    catch { }
                }
            }
            return ("", "");
        }

        /// <summary>
        /// 查找 PLC 软件所属的 Device 对象。
        /// ★COM RCW 引用比对不可靠（每次 GetService 返回不同 RCW 实例），改用名称比对。
        /// </summary>
        private Device? FindDeviceForPlcObject(PlcSoftware plc)
        {
            foreach (var (dev, _) in GetAllDevicesWithGroup())
            {
                foreach (var di in EnumerateDeviceItems(dev.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        if (swContainer?.Software is PlcSoftware p
                            && string.Equals(p.Name, plc.Name, StringComparison.OrdinalIgnoreCase))
                            return dev;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 按 plcName 查找 PLC 所属的 Device 对象。plcName 为空或未找到返回 null。
        /// </summary>
        private Device? FindPlcDeviceByName(string? plcName)
        {
            if (string.IsNullOrEmpty(plcName)) return null;
            var plc = FindPlcByName(plcName);
            return plc == null ? null : FindDeviceForPlcObject(plc);
        }

        /// <summary>
        /// 获取设备网络接口已连接的所有子网。
        /// 用于按 PLC 作用域过滤 IO 系统（IO 系统挂在子网上，由该子网上的 IoController 创建）。
        /// </summary>
        private List<Subnet> GetConnectedSubnets(Device device)
        {
            var result = new List<Subnet>();
            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
            {
                NetworkInterface? netIf = null;
                try { netIf = di.GetService<NetworkInterface>(); }
                catch { }
                if (netIf == null) continue;
                try
                {
                    foreach (var node in netIf.Nodes)
                    {
                        if (node.ConnectedSubnet != null)
                            result.Add(node.ConnectedSubnet);
                    }
                }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// 在指定 PLC 设备子树中查找名为 deviceName 的网络端口；未找到则回退全局查找。
        /// 用于 ConnectNetworkPorts 等 plcName 作用域查询。
        /// </summary>
        private NetworkPort? FindNetworkPortScoped(string? plcName, string deviceName, int portIndex)
        {
            // 优先在指定 PLC 的设备子树中查找
            if (!string.IsNullOrEmpty(plcName))
            {
                var plcDevice = FindPlcDeviceByName(plcName);
                if (plcDevice != null)
                {
                    // 设备名匹配 PLC 设备本身：直接在其子树中查找端口
                    if (string.Equals(plcDevice.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        var port = FindNetworkPortInDevice(plcDevice, portIndex);
                        if (port != null) return port;
                    }
                    else
                    {
                        // 在 PLC 设备子树中查找名为 deviceName 的子设备项
                        foreach (var di in EnumerateDeviceItems(plcDevice.DeviceItems))
                        {
                            if (string.Equals(di.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                            {
                                var port = FindNetworkPortInDeviceItem(di, portIndex);
                                if (port != null) return port;
                            }
                        }
                    }
                }
            }
            // 回退到全局查找
            return FindNetworkPort(deviceName, portIndex);
        }

        /// <summary>在指定设备的子树中查找网络端口。</summary>
        private static NetworkPort? FindNetworkPortInDevice(Device device, int portIndex)
        {
            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
            {
                var port = FindNetworkPortInDeviceItem(di, portIndex);
                if (port != null) return port;
            }
            return null;
        }

        /// <summary>在指定设备项上查找网络端口。</summary>
        private static NetworkPort? FindNetworkPortInDeviceItem(DeviceItem di, int portIndex)
        {
            NetworkInterface? netIf = null;
            try { netIf = di.GetService<NetworkInterface>(); }
            catch { }
            if (netIf == null) return null;
            if (portIndex >= 0 && portIndex < netIf.Ports.Count)
                return netIf.Ports[portIndex];
            return null;
        }

        /// <summary>安全获取变量表的 Tag 数量。</summary>
        private static int SafeGetTagCount(PlcTagTable table)
        {
            try { return table.Tags.Count; }
            catch { return -1; }
        }

        public string ListWatchTables()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var list = new List<object>();
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        var wfGroup = plc.WatchAndForceTableGroup;
                        if (wfGroup == null) continue;
                        foreach (var wt in wfGroup.WatchTables)
                            list.Add(new { plcName = plc.Name, name = wt.Name });
                        // 递归子组
                        foreach (var g in wfGroup.Groups)
                        {
                            foreach (var wt in g.WatchTables)
                                list.Add(new { plcName = plc.Name, name = wt.Name, group = g.Name });
                        }
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, watchTables = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string RunCrossReference()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (cr, crErr) = GetCrossReferenceService();
                    if (cr == null) return Err(crErr ?? "该项目不支持交叉引用");
                    var result = GetCrossReferences(cr);
                    var sourceCount = 0;
                    try
                    {
                        var sources = result?.GetType().GetProperty("Sources")?.GetValue(result) as System.Collections.IEnumerable;
                        if (sources != null) { foreach (var _ in sources) sourceCount++; }
                    }
                    catch { }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "交叉引用已获取",
                        sourceCount
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 多语言 / 变量表分组（任务 7）
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 列出项目支持的所有语言（CultureInfo 列表）。
        /// 因不同 TIA 版本入口属性名差异（Languages / LanguageSettings.ActiveLanguages 等），
        /// 采用反射探测多个候选属性，返回每个语言的 Name/DisplayName/EnglishName。
        /// </summary>
        public string ListProjectLanguages()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var p = _project!;
                    var languages = new List<object>();
                    var triedPaths = new List<string>();
                    object? langCollection = null;
                    string entryProp = "";

                    // 候选属性路径（按优先级尝试）
                    var candidates = new[]
                    {
                        "Languages",
                        "LanguageSettings.ActiveLanguages",
                        "LanguageSettings.Languages",
                        "ActiveLanguages",
                        "AvailableLanguages"
                    };

                    foreach (var path in candidates)
                    {
                        triedPaths.Add(path);
                        try
                        {
                            object? cur = p;
                            foreach (var seg in path.Split('.'))
                            {
                                if (cur == null) break;
                                var pi = cur.GetType().GetProperty(seg,
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (pi == null) { cur = null; break; }
                                cur = pi.GetValue(cur);
                            }
                            if (cur is System.Collections.IEnumerable enum_ && !(cur is string))
                            {
                                // 放宽类型检查：只要元素有 Name/DisplayName/EnglishName 任一属性即视为语言条目
                                int n = 0;
                                foreach (var item in enum_)
                                {
                                    if (item == null) continue;
                                    var it = item.GetType();
                                    // 直接是 CultureInfo → 通过
                                    if (item is System.Globalization.CultureInfo) { n++; continue; }
                                    // Language 对象：有 Culture 属性（CultureInfo）→ 通过
                                    // TIA Openness 的 Language 类型只有 Culture 属性，没有直接返回语言字符串的 Name 属性
                                    try
                                    {
                                        var cultPi = it.GetProperty("Culture", BindingFlags.Public | BindingFlags.Instance);
                                        if (cultPi != null && cultPi.GetValue(item) is System.Globalization.CultureInfo)
                                        { n++; continue; }
                                    }
                                    catch { }
                                    // 有 Name 属性且是字符串 → 可能是 CultureInfo 的包装
                                    try
                                    {
                                        var namePi = it.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                                        if (namePi != null && namePi.GetValue(item) is string s && s.Length >= 2 && s.Length <= 10 && s.Contains("-"))
                                        { n++; continue; }
                                    }
                                    catch { }
                                    break;
                                }
                                if (n > 0)
                                {
                                    langCollection = cur;
                                    entryProp = path;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }

                    // 尝试路径6: GetService<LanguageSettings>()
                    if (langCollection == null)
                    {
                        triedPaths.Add("GetService<LanguageSettings>()");
                        try
                        {
                            var lsType = Type.GetType("Siemens.Engineering.LanguageSettings, Siemens.Engineering")
                                ?? Type.GetType("Siemens.Engineering.LanguageSettings");
                            if (lsType != null)
                            {
                                var getService = p.GetType().GetMethod("GetService");
                                if (getService != null)
                                {
                                    var gm = getService.MakeGenericMethod(lsType);
                                    var ls = gm.Invoke(p, null);
                                    if (ls != null)
                                    {
                                        // 尝试 ActiveLanguages / Languages 属性
                                        foreach (var prop in lsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                        {
                                            try
                                            {
                                                var val = prop.GetValue(ls);
                                                if (val is System.Collections.IEnumerable e && !(val is string))
                                                {
                                                    int n = 0;
                                                    foreach (var it in e) { if (it != null) n++; }
                                                    if (n > 0) { langCollection = val; entryProp = $"GetService<LanguageSettings>().{prop.Name}"; break; }
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (langCollection == null)
                    {
                        // 兜底：反射 Project 所有属性，找包含语言相关条目的集合
                        try
                        {
                            foreach (var prop in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                try
                                {
                                    var val = prop.GetValue(p);
                                    if (val is not System.Collections.IEnumerable e || val is string) continue;
                                    foreach (var item in e)
                                    {
                                        if (item == null) continue;
                                        // 放宽：CultureInfo 或任何有 Name 属性的类型
                                        if (item is System.Globalization.CultureInfo ||
                                            item.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance) != null)
                                        {
                                            langCollection = val;
                                            entryProp = prop.Name + " (反射兜底)";
                                            break;
                                        }
                                    }
                                    if (langCollection != null) break;
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    if (langCollection == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到项目语言集合（Project.Languages / LanguageSettings.ActiveLanguages 均不可用）",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    foreach (var item in (System.Collections.IEnumerable)langCollection)
                    {
                        try
                        {
                            if (item is System.Globalization.CultureInfo ci)
                            {
                                languages.Add(new
                                {
                                    name = ci.Name,
                                    displayName = ci.DisplayName,
                                    englishName = ci.EnglishName,
                                    lcid = ci.LCID
                                });
                            }
                            else
                            {
                                // TIA Openness Language 对象：通过 Culture 属性（CultureInfo）获取语言信息
                                // Language 类型没有直接的 Name 属性返回 "zh-CN"，需通过 Culture.Name 获取
                                var cultProp = item?.GetType().GetProperty("Culture");
                                var cultVal = cultProp?.GetValue(item);
                                if (cultVal is System.Globalization.CultureInfo lci)
                                {
                                    languages.Add(new
                                    {
                                        name = lci.Name,
                                        displayName = lci.DisplayName,
                                        englishName = lci.EnglishName,
                                        lcid = lci.LCID
                                    });
                                }
                                else
                                {
                                    // 反射兜底
                                    var nameProp = item?.GetType().GetProperty("Name");
                                    languages.Add(new
                                    {
                                        name = nameProp?.GetValue(item)?.ToString() ?? item?.ToString() ?? "",
                                        displayName = "",
                                        englishName = "",
                                        lcid = 0
                                    });
                                }
                            }
                        }
                        catch { }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        entryPoint = entryProp,
                        count = languages.Count,
                        languages
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 在指定 PLC 变量表下创建分组（PlcTagTableGroupComposition.Create）。
        /// parentGroupName 为空时在根组下创建；非空时先查找父组（仅一级）再在其下创建子组。
        /// 优先强类型 API；若编译期类型不可用，则反射兜底。
        /// </summary>
        public string CreateTagTableGroup(string plcName, string groupName, string? parentGroupName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(plcName)) return Err("plcName 不能为空");
                    if (string.IsNullOrEmpty(groupName)) return Err("groupName 不能为空");

                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var rootTagGroup = plc.TagTableGroup;  // PlcTagTableGroup
                    if (rootTagGroup == null) return Err($"PLC {plcName} 无 TagTableGroup");

                    // 反射获取 Groups 集合（PlcTagTableGroupComposition），
                    // 然后反射调用 Create(string name) 方法，避免不同 TIA 版本签名差异。
                    object? groupsColl;

                    // 若需要父组，先在根组的 Groups 中按名称查找
                    if (!string.IsNullOrEmpty(parentGroupName))
                    {
                        var rootGroupsProp = rootTagGroup.GetType().GetProperty("Groups");
                        var rootGroups = rootGroupsProp?.GetValue(rootTagGroup) as System.Collections.IEnumerable;
                        if (rootGroups == null) return Err("PLC 变量表根组无 Groups 集合");

                        object? parentGroup = null;
                        foreach (var g in rootGroups)
                        {
                            try
                            {
                                var gName = g?.GetType().GetProperty("Name")?.GetValue(g)?.ToString();
                                if (gName != null && gName.Equals(parentGroupName, StringComparison.OrdinalIgnoreCase))
                                {
                                    parentGroup = g;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (parentGroup == null)
                            return Err($"未找到父变量表组: {parentGroupName}（在 PLC {plcName} 根组下）");

                        // 父组的 Groups 集合
                        var pgGroupsProp = parentGroup.GetType().GetProperty("Groups");
                        groupsColl = pgGroupsProp?.GetValue(parentGroup);
                        if (groupsColl == null) return Err($"父组 {parentGroupName} 无 Groups 集合");
                    }
                    else
                    {
                        var rootGroupsProp = rootTagGroup.GetType().GetProperty("Groups");
                        groupsColl = rootGroupsProp?.GetValue(rootTagGroup);
                        if (groupsColl == null) return Err("PLC 变量表根组无 Groups 集合");
                    }

                    // 反射调用 Create(string name) 方法
                    var createMethod = groupsColl.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (createMethod == null)
                        return Err($"变量表组集合上未找到 Create(string) 方法（类型: {groupsColl.GetType().FullName}）");

                    object? newGroup;
                    try
                    {
                        newGroup = createMethod.Invoke(groupsColl, new object[] { groupName });
                    }
                    catch (TargetInvocationException tie)
                    {
                        var msg = tie.InnerException?.Message ?? tie.Message;
                        return Err($"创建变量表组失败: {msg}");
                    }

                    var newGroupName = "";
                    try
                    {
                        newGroupName = newGroup?.GetType().GetProperty("Name")?.GetValue(newGroup)?.ToString() ?? groupName;
                    }
                    catch { newGroupName = groupName; }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建变量表组: {newGroupName}",
                        plcName = plc.Name,
                        groupName = newGroupName,
                        parentGroupName = parentGroupName ?? ""
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 反射辅助
        // ────────────────────────────────────────────────────────────

        private static string? SafeGetAttr(object obj, string attrName)
        {
            try
            {
                if (obj is IEngineeringObject eo)
                {
                    var v = eo.GetAttribute(attrName);
                    return v?.ToString() ?? "";
                }
            }
            catch { }
            return null;
        }

        private static string SafeTypeIdentifier(object obj)
        {
            try
            {
                if (obj is IEngineeringObject eo)
                {
                    var v = eo.GetAttribute("TypeIdentifier");
                    return v?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        private static View ParseView(string viewName)
        {
            if (string.IsNullOrEmpty(viewName)) return View.Device;
            return viewName.ToLowerInvariant() switch
            {
                "device" => View.Device,
                "network" => View.Network,
                "topology" => View.Topology,
                _ => View.Device,
            };
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // OPC UA Server 接口 / 证书 / 项目文本 / 项目元信息 / 函数监督 / 项目历史
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 反射调用 source.GetService&lt;T&gt;() 获取服务提供者。
        /// 候选类型按顺序尝试，返回第一个非 null 的服务实例。
        /// triedPaths 记录所有尝试过的类型名。
        /// </summary>
        private (object? provider, Type? providerType, List<string> triedPaths) InvokeGetService(
            object source, IEnumerable<string> candidateTypeNames)
        {
            var triedPaths = new List<string>();

            var getServiceMethod = source.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod)
                ?? typeof(IEngineeringServiceProvider)
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);

            foreach (var tn in candidateTypeNames)
            {
                triedPaths.Add(tn);
                if (getServiceMethod == null) continue;
                try
                {
                    var providerType = typeof(TiaPortal).Assembly.GetType(tn);
                    if (providerType == null) continue;

                    var generic = getServiceMethod.MakeGenericMethod(providerType);
                    var provider = generic.Invoke(source, null);
                    if (provider != null)
                        return (provider, providerType, triedPaths);
                }
                catch { }
            }
            return (null, null, triedPaths);
        }

        // ────────────────────────────────────────────────────────────
        // OPC UA Server 接口
        // ────────────────────────────────────────────────────────────

        public string ListOpcUaServerInterfaces(string plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var (provider, providerType, triedPaths) = InvokeGetService(plc,
                        new[] { "Siemens.Engineering.OpcUa.OpcUaProvider" });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务（可能 PLC 不支持 OPC UA 或 TIA 版本不匹配）",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var serverInterfaces = new List<object>();
                    var siProp = providerType.GetProperty("ServerInterfaces");
                    var siColl = siProp?.GetValue(provider) as IEnumerable;
                    if (siColl != null)
                    {
                        foreach (var si in siColl)
                        {
                            var siType = si.GetType();
                            var nsList = new List<string>();
                            var nsProp = siType.GetProperty("Namespaces");
                            if (nsProp?.GetValue(si) is IEnumerable nsEnum)
                            {
                                foreach (var ns in nsEnum)
                                    nsList.Add(ns?.ToString() ?? "");
                            }
                            serverInterfaces.Add(new
                            {
                                name = siType.GetProperty("Name")?.GetValue(si)?.ToString() ?? "",
                                enabled = siType.GetProperty("Enabled")?.GetValue(si) ?? false,
                                namespaces = nsList
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        count = serverInterfaces.Count,
                        serverInterfaces
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateOpcUaServerInterface(string plcName, string interfaceName, string? xmlFilePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(interfaceName))
                        return Err("interfaceName 不能为空");

                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var (provider, providerType, triedPaths) = InvokeGetService(plc,
                        new[] { "Siemens.Engineering.OpcUa.OpcUaProvider" });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var siProp = providerType.GetProperty("ServerInterfaces");
                    var siColl = siProp?.GetValue(provider);
                    if (siColl == null) return Err("OpcUaProvider.ServerInterfaces 属性不可用");

                    var collType = siColl.GetType();

                    // 优先 Import 方式（xmlFilePath 非空时）
                    if (!string.IsNullOrEmpty(xmlFilePath) && File.Exists(xmlFilePath))
                    {
                        var importMethod = collType.GetMethod("Import");
                        if (importMethod != null)
                        {
                            var imported = importMethod.Invoke(siColl, new object[] { new FileInfo(xmlFilePath) });
                            var impName = imported?.GetType().GetProperty("Name")?.GetValue(imported)?.ToString() ?? interfaceName;
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = $"已从 XML 导入 OPC UA Server 接口: {impName}",
                                interfaceName = impName,
                                imported = true
                            });
                        }
                        return Err("ServerInterfaces 集合无 Import(FileInfo) 方法");
                    }

                    // 创建新接口：Create(string name)
                    var createMethod = collType.GetMethod("Create", new[] { typeof(string) });
                    if (createMethod == null)
                        return Err($"ServerInterfaces 集合上未找到 Create(string) 方法（类型: {collType.FullName}）");

                    object? newIf;
                    try
                    {
                        newIf = createMethod.Invoke(siColl, new object[] { interfaceName });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"创建 OPC UA Server 接口失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    var createdName = newIf?.GetType().GetProperty("Name")?.GetValue(newIf)?.ToString() ?? interfaceName;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 OPC UA Server 接口: {createdName}",
                        interfaceName = createdName
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportOpcUaServerInterface(string plcName, string interfaceName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(interfaceName)) return Err("interfaceName 不能为空");
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");

                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var (provider, providerType, triedPaths) = InvokeGetService(plc,
                        new[] { "Siemens.Engineering.OpcUa.OpcUaProvider" });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var siProp = providerType.GetProperty("ServerInterfaces");
                    var siColl = siProp?.GetValue(provider) as IEnumerable;
                    if (siColl == null) return Err("OpcUaProvider.ServerInterfaces 属性不可用");

                    object? target = null;
                    foreach (var si in siColl)
                    {
                        var n = si.GetType().GetProperty("Name")?.GetValue(si)?.ToString();
                        if (n != null && n.Equals(interfaceName, StringComparison.OrdinalIgnoreCase))
                        {
                            target = si;
                            break;
                        }
                    }
                    if (target == null) return Err($"未找到 OPC UA Server 接口: {interfaceName}");

                    var exportMethod = target.GetType().GetMethod("Export");
                    if (exportMethod == null)
                        return Err("OPC UA Server 接口无 Export 方法");

                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    try
                    {
                        exportMethod.Invoke(target, new object[] { new FileInfo(filePath) });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导出 OPC UA Server 接口失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出 OPC UA Server 接口到: {filePath}",
                        filePath
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportOpcUaServerInterface(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");

                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var (provider, providerType, triedPaths) = InvokeGetService(plc,
                        new[] { "Siemens.Engineering.OpcUa.OpcUaProvider" });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 OpcUaProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var siProp = providerType.GetProperty("ServerInterfaces");
                    var siColl = siProp?.GetValue(provider);
                    if (siColl == null) return Err("OpcUaProvider.ServerInterfaces 属性不可用");

                    var importMethod = siColl.GetType().GetMethod("Import");
                    if (importMethod == null)
                        return Err("ServerInterfaces 集合无 Import 方法");

                    object? imported;
                    try
                    {
                        imported = importMethod.Invoke(siColl, new object[] { new FileInfo(filePath) });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导入 OPC UA Server 接口失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    var importedName = imported?.GetType().GetProperty("Name")?.GetValue(imported)?.ToString() ?? "";
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导入 OPC UA Server 接口: {importedName}",
                        importedInterface = importedName
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 证书管理
        // ────────────────────────────────────────────────────────────

        public string ListCertificates()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var p = _project!;

                    var (provider, providerType, triedPaths) = InvokeGetService(p,
                        new[] {
                            "Siemens.Engineering.Security.SecurityProvider",
                            "Siemens.Engineering.SecurityProvider"
                        });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SecurityProvider 服务（项目可能不支持证书管理）",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var certProp = providerType.GetProperty("Certificates");
                    var certColl = certProp?.GetValue(provider) as IEnumerable;
                    if (certColl == null)
                        return Err("SecurityProvider.Certificates 属性不可用");

                    var certificates = new List<object>();
                    foreach (var cert in certColl)
                    {
                        var ct = cert.GetType();
                        certificates.Add(new
                        {
                            subject = ct.GetProperty("Subject")?.GetValue(cert)?.ToString() ?? "",
                            hasPrivateKey = ct.GetProperty("HasPrivateKey")?.GetValue(cert) ?? false,
                            validUntil = ct.GetProperty("ValidUntil")?.GetValue(cert)?.ToString() ?? "",
                            id = ct.GetProperty("Id")?.GetValue(cert)?.ToString() ?? ""
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        count = certificates.Count,
                        certificates
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportCertificate(string filePath, string? password)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");

                    var p = _project!;
                    var (provider, providerType, triedPaths) = InvokeGetService(p,
                        new[] {
                            "Siemens.Engineering.Security.SecurityProvider",
                            "Siemens.Engineering.SecurityProvider"
                        });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SecurityProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var certProp = providerType.GetProperty("Certificates");
                    var certColl = certProp?.GetValue(provider);
                    if (certColl == null) return Err("SecurityProvider.Certificates 属性不可用");

                    // Import(FileInfo, SecureString) 方法
                    var importMethod = certColl.GetType().GetMethod("Import",
                        new[] { typeof(FileInfo), typeof(SecureString) });
                    if (importMethod == null)
                    {
                        // 尝试 Import(FileInfo) 无密码重载
                        importMethod = certColl.GetType().GetMethod("Import", new[] { typeof(FileInfo) });
                        if (importMethod == null)
                            return Err("Certificates 集合无 Import(FileInfo, SecureString) 方法");
                    }

                    SecureString? secPwd = null;
                    if (!string.IsNullOrEmpty(password))
                    {
                        secPwd = new SecureString();
                        foreach (char c in password!) secPwd.AppendChar(c);
                        secPwd.MakeReadOnly();
                    }

                    object? imported;
                    try
                    {
                        if (importMethod.GetParameters().Length == 2)
                            imported = importMethod.Invoke(certColl, new object[] { new FileInfo(filePath), secPwd ?? new SecureString() });
                        else
                            imported = importMethod.Invoke(certColl, new object[] { new FileInfo(filePath) });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导入证书失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    var subject = imported?.GetType().GetProperty("Subject")?.GetValue(imported)?.ToString() ?? "";
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导入证书: {subject}",
                        certificateSubject = subject
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateCertificate(string subjectCommonName, string? validFrom, string? validUntil)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(subjectCommonName))
                        return Err("subjectCommonName 不能为空");

                    var p = _project!;
                    var (provider, providerType, triedPaths) = InvokeGetService(p,
                        new[] {
                            "Siemens.Engineering.Security.SecurityProvider",
                            "Siemens.Engineering.SecurityProvider"
                        });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SecurityProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var certProp = providerType.GetProperty("Certificates");
                    var certColl = certProp?.GetValue(provider);
                    if (certColl == null) return Err("SecurityProvider.Certificates 属性不可用");

                    // 创建 CertificateTemplate 实例（反射）
                    var templateType = typeof(TiaPortal).Assembly
                        .GetType("Siemens.Engineering.Security.CertificateTemplate");
                    if (templateType == null)
                        return Err("未找到 CertificateTemplate 类型");

                    var template = Activator.CreateInstance(templateType);

                    // 设置 SubjectCommonName
                    var cnProp = templateType.GetProperty("SubjectCommonName");
                    if (cnProp != null && cnProp.CanWrite)
                        cnProp.SetValue(template, subjectCommonName);

                    // 设置 ValidFrom
                    if (!string.IsNullOrEmpty(validFrom) && DateTime.TryParse(validFrom, out var vf))
                    {
                        var vfProp = templateType.GetProperty("ValidFrom");
                        if (vfProp != null && vfProp.CanWrite)
                            vfProp.SetValue(template, vf);
                    }

                    // 设置 ValidUntil
                    if (!string.IsNullOrEmpty(validUntil) && DateTime.TryParse(validUntil, out var vu))
                    {
                        var vuProp = templateType.GetProperty("ValidUntil");
                        if (vuProp != null && vuProp.CanWrite)
                            vuProp.SetValue(template, vu);
                    }

                    // 调用 Create(CertificateTemplate)
                    var createMethod = certColl.GetType().GetMethod("Create", new[] { templateType });
                    if (createMethod == null)
                        return Err("Certificates 集合无 Create(CertificateTemplate) 方法");

                    object? created;
                    try
                    {
                        created = createMethod.Invoke(certColl, new object[] { template });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"创建证书失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    var subject = created?.GetType().GetProperty("Subject")?.GetValue(created)?.ToString() ?? subjectCommonName;
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建自签名证书: {subject}",
                        certificateSubject = subject
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportCertificate(string certificateId, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(certificateId)) return Err("certificateId 不能为空");
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");

                    var p = _project!;
                    var (provider, providerType, triedPaths) = InvokeGetService(p,
                        new[] {
                            "Siemens.Engineering.Security.SecurityProvider",
                            "Siemens.Engineering.SecurityProvider"
                        });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SecurityProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    var certProp = providerType.GetProperty("Certificates");
                    var certColl = certProp?.GetValue(provider) as IEnumerable;
                    if (certColl == null) return Err("SecurityProvider.Certificates 属性不可用");

                    object? target = null;
                    foreach (var cert in certColl)
                    {
                        var id = cert.GetType().GetProperty("Id")?.GetValue(cert)?.ToString();
                        if (id != null && id.Equals(certificateId, StringComparison.OrdinalIgnoreCase))
                        {
                            target = cert;
                            break;
                        }
                    }
                    if (target == null) return Err($"未找到证书 ID: {certificateId}");

                    var exportMethod = target.GetType().GetMethod("Export");
                    if (exportMethod == null)
                        return Err("证书对象无 Export 方法");

                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    try
                    {
                        exportMethod.Invoke(target, new object[] { new FileInfo(filePath) });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导出证书失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出证书到: {filePath}",
                        filePath
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 项目文本翻译
        // ────────────────────────────────────────────────────────────

        public string ExportProjectTexts(string filePath, string? sourceLanguage, string? targetLanguage)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");

                    var p = _project!;
                    var projType = p.GetType();

                    // 优先尝试 ExportProjectTexts(FileInfo, CultureInfo, CultureInfo)
                    MethodInfo? exportMethod = null;
                    object?[]? args = null;

                    CultureInfo? srcCi = null;
                    CultureInfo? tgtCi = null;
                    if (!string.IsNullOrEmpty(sourceLanguage))
                    {
                        try { srcCi = new CultureInfo(sourceLanguage!); } catch { }
                    }
                    if (!string.IsNullOrEmpty(targetLanguage))
                    {
                        try { tgtCi = new CultureInfo(targetLanguage!); } catch { }
                    }

                    if (srcCi != null && tgtCi != null)
                    {
                        exportMethod = projType.GetMethod("ExportProjectTexts",
                            new[] { typeof(FileInfo), typeof(CultureInfo), typeof(CultureInfo) });
                        if (exportMethod != null)
                            args = new object[] { new FileInfo(filePath), srcCi, tgtCi };
                    }

                    // 回退：ExportProjectTexts(FileInfo)
                    if (exportMethod == null)
                    {
                        exportMethod = projType.GetMethod("ExportProjectTexts", new[] { typeof(FileInfo) });
                        if (exportMethod != null)
                            args = new object[] { new FileInfo(filePath) };
                    }

                    if (exportMethod == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Project 类型无 ExportProjectTexts 方法（可能 TIA 版本不支持）",
                            apiExplored = true,
                            triedPaths = new[] { "ExportProjectTexts(FileInfo, CultureInfo, CultureInfo)", "ExportProjectTexts(FileInfo)" }
                        });

                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    int textCount = -1;
                    try
                    {
                        var result = exportMethod.Invoke(p, args);
                        // 部分版本返回 int（文本数）
                        if (result is int n) textCount = n;
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导出项目文本失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出项目文本到: {filePath}",
                        filePath,
                        textCount,
                        sourceLanguage = sourceLanguage ?? "",
                        targetLanguage = targetLanguage ?? ""
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportProjectTexts(string filePath, bool updateSourceLanguage)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");

                    var p = _project!;
                    var projType = p.GetType();

                    // 优先 ImportProjectTexts(FileInfo, bool)
                    var importMethod = projType.GetMethod("ImportProjectTexts",
                        new[] { typeof(FileInfo), typeof(bool) });
                    object?[] args;
                    if (importMethod != null)
                    {
                        args = new object[] { new FileInfo(filePath), updateSourceLanguage };
                    }
                    else
                    {
                        // 回退 ImportProjectTexts(FileInfo)
                        importMethod = projType.GetMethod("ImportProjectTexts", new[] { typeof(FileInfo) });
                        if (importMethod == null)
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "Project 类型无 ImportProjectTexts 方法（可能 TIA 版本不支持）",
                                apiExplored = true,
                                triedPaths = new[] { "ImportProjectTexts(FileInfo, bool)", "ImportProjectTexts(FileInfo)" }
                            });
                        args = new object[] { new FileInfo(filePath) };
                    }

                    int updatedCount = -1;
                    try
                    {
                        var result = importMethod.Invoke(p, args);
                        if (result is int n) updatedCount = n;
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导入项目文本失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导入项目文本: {filePath}",
                        updatedTextCount = updatedCount,
                        updateSourceLanguage
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 项目元信息
        // ────────────────────────────────────────────────────────────

        public string GetProjectMetadata()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var p = _project!;
                    var projType = p.GetType();

                    var metadata = new Dictionary<string, object?>();

                    // 基本属性（直接或反射）
                    metadata["name"] = p.Name;
                    metadata["path"] = p.Path?.FullName ?? "";
                    metadata["typeIdentifier"] = SafeTypeIdentifier(p);

                    // 属性型字段（通过 GetAttribute）
                    metadata["author"] = SafeGetAttr(p, "Author") ?? "";
                    metadata["comment"] = SafeGetAttr(p, "Comment") ?? "";
                    metadata["copyright"] = SafeGetAttr(p, "Copyright") ?? "";
                    metadata["family"] = SafeGetAttr(p, "Family") ?? "";
                    metadata["version"] = SafeGetAttr(p, "Version") ?? "";
                    metadata["creationTime"] = SafeGetAttr(p, "CreationTime") ?? "";
                    metadata["lastModified"] = SafeGetAttr(p, "LastModified") ?? "";

                    // ProjectBase 属性（反射）
                    foreach (var propName in new[] { "IsModified", "IsPrimary", "LastModifiedBy", "Size" })
                    {
                        try
                        {
                            var prop = projType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                            if (prop != null)
                                metadata[propName.ToLowerInvariant()] = prop.GetValue(p);
                            else
                            {
                                // 尝试属性型
                                metadata[propName.ToLowerInvariant()] = SafeGetAttr(p, propName) ?? "";
                            }
                        }
                        catch { metadata[propName.ToLowerInvariant()] = ""; }
                    }

                    // UsedProducts 集合
                    try
                    {
                        var upProp = projType.GetProperty("UsedProducts", BindingFlags.Public | BindingFlags.Instance);
                        var upVal = upProp?.GetValue(p) as IEnumerable;
                        var upList = new List<string>();
                        if (upVal != null)
                        {
                            foreach (var item in upVal)
                            {
                                var itemStr = item?.ToString() ?? "";
                                // 尝试取 Name 属性
                                var np = item?.GetType().GetProperty("Name");
                                if (np != null) itemStr = np.GetValue(item)?.ToString() ?? itemStr;
                                upList.Add(itemStr);
                            }
                        }
                        metadata["usedProducts"] = upList;
                    }
                    catch { metadata["usedProducts"] = new List<string>(); }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        metadata
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 函数监督
        // ────────────────────────────────────────────────────────────

        public string ExportSupervisionXlsx(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");

                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var (provider, providerType, triedPaths) = InvokeGetService(plc,
                        new[] {
                            "Siemens.Engineering.SW.Supervision.SupervisionProvider",
                            "Siemens.Engineering.Supervision.SupervisionProvider"
                        });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SupervisionProvider 服务（可能 PLC 不支持函数监督）",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    // 查找 ExportSupervisionsToXlsx(FileInfo) 方法
                    var exportMethod = providerType.GetMethod("ExportSupervisionsToXlsx");
                    if (exportMethod == null)
                    {
                        // 模糊匹配
                        var methods = providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName
                                        && m.Name.IndexOf("Export", StringComparison.OrdinalIgnoreCase) >= 0
                                        && m.Name.IndexOf("Supervision", StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToList();
                        if (methods.Count == 0)
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "SupervisionProvider 无 ExportSupervisionsToXlsx 方法",
                                apiExplored = ExploreProviderMethods(providerType),
                                triedPaths
                            });
                        exportMethod = methods[0];
                    }

                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    try
                    {
                        exportMethod.Invoke(provider, new object[] { new FileInfo(filePath) });
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导出函数监督失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出函数监督到: {filePath}",
                        filePath
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportSupervisionXlsx(string plcName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrEmpty(filePath)) return Err("filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");

                    var plc = FindPlcByName(plcName);
                    if (plc == null) return Err($"未找到 PLC: {plcName}");

                    var (provider, providerType, triedPaths) = InvokeGetService(plc,
                        new[] {
                            "Siemens.Engineering.SW.Supervision.SupervisionProvider",
                            "Siemens.Engineering.Supervision.SupervisionProvider"
                        });

                    if (provider == null || providerType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "未找到 SupervisionProvider 服务",
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    // 查找 ImportSupervisionsFromXlsx(FileInfo) 方法
                    var importMethod = providerType.GetMethod("ImportSupervisionsFromXlsx");
                    if (importMethod == null)
                    {
                        var methods = providerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => !m.IsSpecialName
                                        && m.Name.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0
                                        && m.Name.IndexOf("Supervision", StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToList();
                        if (methods.Count == 0)
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = "SupervisionProvider 无 ImportSupervisionsFromXlsx 方法",
                                apiExplored = ExploreProviderMethods(providerType),
                                triedPaths
                            });
                        importMethod = methods[0];
                    }

                    int importedCount = -1;
                    try
                    {
                        var result = importMethod.Invoke(provider, new object[] { new FileInfo(filePath) });
                        if (result is int n) importedCount = n;
                    }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"导入函数监督失败: {tie.InnerException?.Message ?? tie.Message}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已从 {filePath} 导入函数监督",
                        importedCount
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 项目历史
        // ────────────────────────────────────────────────────────────

        public string ListProjectHistory()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var p = _project!;
                    var projType = p.GetType();

                    var historyProp = projType.GetProperty("HistoryEntries", BindingFlags.Public | BindingFlags.Instance);
                    if (historyProp == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "Project 类型无 HistoryEntries 属性（可能 TIA 版本不支持）",
                            apiExplored = true,
                            triedPaths = new[] { "Project.HistoryEntries" }
                        });
                    }

                    var historyColl = historyProp.GetValue(p) as IEnumerable;
                    var history = new List<object>();
                    if (historyColl != null)
                    {
                        foreach (var entry in historyColl)
                        {
                            var et = entry.GetType();
                            history.Add(new
                            {
                                dateTime = et.GetProperty("DateTime")?.GetValue(entry)?.ToString() ?? "",
                                text = et.GetProperty("Text")?.GetValue(entry)?.ToString() ?? ""
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        count = history.Count,
                        history
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }
    }
}

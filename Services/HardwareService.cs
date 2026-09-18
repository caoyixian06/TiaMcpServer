using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;

namespace TiaMcpServer
{
    /// <summary>
    /// 硬件与 IO 映射相关服务。
    /// </summary>
    public partial class PortalService
    {
        // ═════════════════════════════════════════════════════════════════════════════
        // 公共 API
        // ═════════════════════════════════════════════════════════════════════════════

        public string ListHardwareModules(string? plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var namedPlc = string.IsNullOrEmpty(plcName) ? null : GetPlcSoftwareByName(plcName!);
                    var plcs = namedPlc == null
                        ? GetPlcSoftwareList().Select(p => new { Plc = p, Device = FindDeviceForPlc(p) })
                        : new[] { new { Plc = namedPlc, Device = FindDeviceForPlc(namedPlc) } };

                    var result = new List<object>();
                    foreach (var entry in plcs)
                    {
                        if (entry.Device == null) continue;
                        foreach (var item in EnumerateDeviceItems(entry.Device.DeviceItems))
                        {
                            result.Add(new
                            {
                                deviceName = entry.Device.Name,
                                itemName = SafeGetDeviceItemString(item, "Name"),
                                itemType = SafeGetDeviceItemString(item, "TypeIdentifier"),
                                orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                                firmwareVersion = SafeGetDeviceItemString(item, "FirmwareVersion"),
                                positionNumber = SafeGetDeviceItemInt(item, "PositionNumber")
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plcName ?? "",
                        count = result.Count,
                        modules = result
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetModuleIoAddresses(string moduleName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrWhiteSpace(moduleName))
                        return Err("模块名称不能为空");

                    var item = FindDeviceItemAcrossProject(moduleName);
                    if (item == null)
                        return Err($"未找到模块: {moduleName}");

                    var addresses = ReadIoAddresses(item);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        moduleName = item.Name,
                        itemType = SafeGetDeviceItemString(item, "TypeIdentifier"),
                        orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                        count = addresses.Count,
                        addresses
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportIoMapping(string? plcName, string? outputPath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = string.IsNullOrEmpty(plcName) ? GetPlcSoftware() : GetPlcSoftwareByName(plcName!);
                    var device = FindDeviceForPlc(plc);
                    if (device == null)
                        return Err($"未找到 PLC 对应的硬件设备: {plc.Name}");

                    var allTags = GetAllTags(plc);
                    var modules = new List<object>();

                    foreach (var item in EnumerateDeviceItems(device.DeviceItems))
                    {
                        var addresses = ReadIoAddresses(item);
                        var matchedTags = MatchTagsForModule(allTags, addresses);
                        modules.Add(new
                        {
                            moduleName = item.Name,
                            itemType = SafeGetDeviceItemString(item, "TypeIdentifier"),
                            orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                            addresses,
                            tags = matchedTags
                        });
                    }

                    var payload = new
                    {
                        plcName = plc.Name,
                        deviceName = device.Name,
                        moduleCount = modules.Count,
                        modules
                    };

                    var json = JsonConvert.SerializeObject(payload, Formatting.Indented);

                    if (!string.IsNullOrEmpty(outputPath))
                    {
                        var dir = Path.GetDirectoryName(outputPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(outputPath, json);
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"已导出 IO 映射到: {outputPath}",
                            plcName = plc.Name,
                            moduleCount = modules.Count,
                            filePath = outputPath
                        });
                    }

                    return json;
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetIoDeviceStatus(string? plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = string.IsNullOrEmpty(plcName) ? GetPlcSoftware() : GetPlcSoftwareByName(plcName!);
                    var device = FindDeviceForPlc(plc);
                    if (device == null)
                        return Err($"未找到 PLC 对应的硬件设备: {plc.Name}");

                    var interfaces = new List<object>();
                    var ioDevices = new List<object>();

                    foreach (var item in EnumerateDeviceItems(device.DeviceItems))
                    {
                        var typeId = SafeGetDeviceItemString(item, "TypeIdentifier").ToLowerInvariant();
                        if (IsIoInterface(typeId))
                        {
                            var connected = TryGetConnectedDeviceNames(item);
                            interfaces.Add(new
                            {
                                name = item.Name,
                                type = typeId,
                                orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                                firmwareVersion = SafeGetDeviceItemString(item, "FirmwareVersion"),
                                connectedDeviceCount = connected.Count,
                                connectedDevices = connected
                            });
                        }
                    }

                    // ★构建 PLC 设备名称集合，用于排除 PLC（避免 PLC 的 PROFINET 接口被误判为 IO 设备）
                    var plcDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in GetPlcSoftwareList())
                    {
                        var plcDev = FindDeviceForPlc(p);
                        if (plcDev != null) plcDeviceNames.Add(plcDev.Name);
                    }

                    foreach (var dev in GetAllDevices())
                    {
                        // ★Bug 10: COM RCW 对象用 == 比较不可靠，改用名称比对
                        if (string.Equals(dev.Name, device.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        // ★Bug 8: 排除 PLC 设备，避免其 PROFINET 接口被误判为 IO 设备
                        if (plcDeviceNames.Contains(dev.Name)) continue;
                        var isIo = false;
                        foreach (var di in EnumerateDeviceItems(dev.DeviceItems))
                        {
                            var typeId = SafeGetDeviceItemString(di, "TypeIdentifier").ToLowerInvariant();
                            if (typeId.Contains("et200") || typeId.Contains("io device") ||
                                typeId.Contains("profinet") || typeId.Contains("profibus") ||
                                typeId.Contains("distributed"))
                            {
                                isIo = true;
                                break;
                            }
                        }
                        if (isIo)
                        {
                            ioDevices.Add(new
                            {
                                deviceName = dev.Name,
                                type = SafeTypeIdentifier(dev)
                            });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        deviceName = device.Name,
                        interfaceCount = interfaces.Count,
                        interfaces,
                        ioDeviceCount = ioDevices.Count,
                        ioDevices
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 导出硬件配置 XML 文件。
        /// 单个 PLC 且 outputPath 以 .xml 结尾时为文件模式；否则为目录模式（每个 PLC 一个 XML）。
        /// 可选附带 IO 映射 JSON 和网络配置信息。
        /// </summary>
        public string ExportHardware(string? plcName, string outputPath, bool includeIoMapping = true, bool includeNetworkConfig = false)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrWhiteSpace(outputPath))
                        return Err("输出路径不能为空");

                    // 解析要导出的 PLC 列表（指定 plcName 时只导一个，否则导出全部）
                    var namedPlc = string.IsNullOrEmpty(plcName) ? null : GetPlcSoftwareByName(plcName!);
                    var plcs = namedPlc == null
                        ? GetPlcSoftwareList().Select(p => new { Plc = p, Device = FindDeviceForPlc(p) }).ToList()
                        : new[] { new { Plc = namedPlc, Device = FindDeviceForPlc(namedPlc) } }.ToList();

                    if (plcs.Count == 0)
                        return Err("未找到任何 PLC 软件");

                    // 单个 PLC + 路径以 .xml 结尾 → 文件模式；否则 → 目录模式
                    bool singleFileMode = plcs.Count == 1
                        && outputPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
                    string outputDir = singleFileMode
                        ? (Path.GetDirectoryName(outputPath) ?? "")
                        : outputPath;
                    if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                        Directory.CreateDirectory(outputDir);

                    var exported = new List<object>();
                    int failed = 0;

                    foreach (var entry in plcs)
                    {
                        if (entry.Device == null)
                        {
                            failed++;
                            exported.Add(new { plcName = entry.Plc.Name, error = "未找到对应的硬件设备" });
                            continue;
                        }

                        string baseName = SanitizeFileName(entry.Plc.Name);
                        string filePath = singleFileMode
                            ? outputPath
                            : Path.Combine(outputDir, baseName + ".xml");

                        var info = new Dictionary<string, object?>
                        {
                            ["plcName"] = entry.Plc.Name,
                            ["deviceName"] = entry.Device.Name,
                            ["typeIdentifier"] = SafeTypeIdentifier(entry.Device)
                        };

                        string? exportedPath = null;
                        int moduleCount = 0;

                        try
                        {
                            // 优先：设备级导出（反射调用 Export，含编译重试）
                            // Device/DeviceItem 在编译时不可见 Export 方法，运行时 COM 对象可能支持
                            if (ExportDeviceWithRetry(entry.Device, filePath))
                            {
                                exportedPath = filePath;
                                moduleCount = EnumerateDeviceItems(entry.Device.DeviceItems).Count();
                                info["exportMode"] = "Device.Export（API 原生导出）";
                            }
                            else
                            {
                                // Device 不支持 Export → 回退：DeviceItem 级别导出
                                var itemDir = Path.Combine(outputDir, baseName + "_items");
                                if (!Directory.Exists(itemDir))
                                    Directory.CreateDirectory(itemDir);

                                var (itemCount, itemErrors) = ExportDeviceItemsLevel(entry.Device, itemDir);
                                if (itemCount > 0)
                                {
                                    exportedPath = itemDir;
                                    moduleCount = itemCount;
                                    info["exportMode"] = "DeviceItem.Export（逐模块导出）";
                                    info["itemErrors"] = itemErrors;
                                }
                                else
                                {
                                    // DeviceItem 也不支持 Export → 手动生成 XML
                                    GenerateHardwareXml(entry.Device, filePath);
                                    exportedPath = filePath;
                                    moduleCount = EnumerateDeviceItems(entry.Device.DeviceItems).Count();
                                    info["exportMode"] = "手动生成 XML（Device/DeviceItem 不支持 Export API）";
                                }
                            }
                        }
                        catch (Exception devEx)
                        {
                            // 导出过程抛异常 → 尝试手动生成 XML 作为最终回退
                            try
                            {
                                GenerateHardwareXml(entry.Device, filePath);
                                exportedPath = filePath;
                                moduleCount = EnumerateDeviceItems(entry.Device.DeviceItems).Count();
                                info["exportMode"] = "手动生成 XML（API 导出异常后的回退）";
                                info["deviceLevelError"] = devEx.Message;
                            }
                            catch (Exception xmlEx)
                            {
                                failed++;
                                info["error"] = $"设备级导出失败: {devEx.Message}; 手动 XML 生成也失败: {xmlEx.Message}";
                            }
                        }

                        if (exportedPath != null)
                        {
                            info["filePath"] = exportedPath;
                            info["moduleCount"] = moduleCount;

                            // 可选：导出 IO 映射 JSON
                            if (includeIoMapping)
                            {
                                var ioMapFile = ExportIoMappingForDevice(entry.Plc, entry.Device, outputDir, baseName);
                                if (ioMapFile != null)
                                    info["ioMappingFile"] = ioMapFile;
                            }

                            // 可选：收集网络配置
                            if (includeNetworkConfig)
                            {
                                var networkConfig = CollectNetworkConfig(entry.Device);
                                if (networkConfig != null)
                                    info["networkConfig"] = networkConfig;
                            }
                        }

                        exported.Add(info);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = failed == 0,
                        plcName = plcName ?? "all",
                        mode = singleFileMode ? "single file" : "directory",
                        outputDir,
                        exportedCount = exported.Count,
                        failedCount = failed,
                        exports = exported
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 使用 CaxProvider 将指定设备硬件配置导出为 XML 文件（用于跨项目迁移）。
        /// 与 ExportHardware 的区别：直接调用 CaxProvider.Export，返回 TransferResult 状态。
        /// </summary>
        public string ExportHardwareCax(string plcName, string deviceName, string filePath)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrWhiteSpace(filePath))
                        return Err("输出文件路径不能为空");
                    if (string.IsNullOrWhiteSpace(plcName))
                        return Err("PLC 名称不能为空");
                    if (string.IsNullOrWhiteSpace(deviceName))
                        return Err("设备名称不能为空");

                    var caxProvider = GetServiceByType(_project, "CaxProvider");
                    if (caxProvider == null)
                        return Err("当前项目不支持 CaxProvider 服务");

                    // 通过 PLC 名称定位 PlcSoftware，再找到对应的 Device
                    var plc = GetPlcSoftwareByName(plcName);
                    var device = FindDeviceForPlc(plc);
                    if (device == null || !device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        // PLC 对应的设备名不匹配 → 在整个项目中按名称查找
                        device = FindDeviceByName(deviceName);
                    }
                    if (device == null)
                        return Err("未找到设备: " + deviceName);

                    // 确保输出目录存在
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    var result = InvokeCaxExport(caxProvider, device, new FileInfo(filePath));
                    return SerializeTransferResult(result, "export", filePath, null, null);
                }
                catch (IOException ioEx)
                {
                    return Err("文件路径无效: " + ioEx.Message);
                }
                catch (Exception ex) { return Err("CaxProvider 导出失败: " + ex.Message); }
            }
        }

        /// <summary>
        /// 使用 CaxProvider 从 XML 文件导入硬件配置（支持跨项目设备迁移）。
        /// importOptions 支持 "MoveToParkingLot"/"OverwriteTiaDevice"/"RetainTiaDevice"
        /// 及其简写 "parking_lot"/"overwrite"/"retain"，默认 OverwriteTiaDevice。
        /// </summary>
        public string ImportHardwareCax(string filePath, string importOptions)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrWhiteSpace(filePath))
                        return Err("导入文件路径不能为空");
                    // ★防护★ 用户可控 XML 文件先做良构校验，坏 XML 曾直接终止 TIA 进程
                    var vErr = ValidateImportXmlFile(filePath, "导入硬件 CAx");
                    if (vErr != null) return Err(vErr);

                    var caxProvider = GetServiceByType(_project, "CaxProvider");
                    if (caxProvider == null)
                        return Err("当前项目不支持 CaxProvider 服务");

                    // 解析 importOptions 字符串为 CaxImportOptions 枚举
                    bool fallbackToDefault = false;
                    var options = ParseCaxImportOptions(importOptions, out fallbackToDefault);

                    var result = InvokeCaxImport(caxProvider, new FileInfo(filePath), options);
                    return SerializeTransferResult(result, "import", filePath, options.ToString(), !fallbackToDefault);
                }
                catch (Exception ex) { return Err("CaxProvider 导入失败: " + ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // IO 地址分配与硬件编译（新增）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 获取 PLC 的完整 IO 地址映射。通过 PlcSoftware 的 IAddressList 接口枚举所有已分配地址；
        /// 反射失败时回退到遍历 DeviceItem 的 IoAddresses + 标签匹配。
        /// 返回：地址列表（地址/变量名/数据类型/长度/区域 I/Q/M）。
        /// </summary>
        public string GetIoAddressMap(string? plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = string.IsNullOrEmpty(plcName) ? GetPlcSoftware() : GetPlcSoftwareByName(plcName!);
                    var device = FindDeviceForPlc(plc);
                    if (device == null)
                        return Err($"未找到 PLC 对应的硬件设备: {plc.Name}");

                    var triedPaths = new List<string>();
                    var addresses = new List<object>();
                    bool apiExplored = false;

                    // 优先：通过反射获取 IAddressList 并枚举
                    var addressList = TryGetAddressList(plc, triedPaths);
                    if (addressList == null && device != null)
                        addressList = TryGetAddressList(device, triedPaths);

                    if (addressList != null)
                    {
                        apiExplored = true;
                        addresses = EnumerateAddressListEntries(addressList, null);
                    }
                    else
                    {
                        // 回退：遍历 DeviceItem 收集 IoAddresses + 匹配标签
                        var allTags = GetAllTags(plc);
                        foreach (var item in EnumerateDeviceItems(device.DeviceItems))
                        {
                            var itemAddresses = ReadIoAddresses(item);
                            foreach (var addr in itemAddresses)
                            {
                                var areaChar = addr.Type == "Input" ? "I" : (addr.Type == "Output" ? "Q" : "M");
                                string varName = "", dataType = "";
                                foreach (var tag in allTags)
                                {
                                    var tagAddr = (tag.LogicalAddress ?? "").TrimStart('%').Trim();
                                    if (TryParseAddressString(tagAddr, out var tagInfo) && tagInfo != null
                                        && tagInfo.Type == addr.Type && tagInfo.StartByte == addr.StartByte)
                                    {
                                        varName = tag.Name;
                                        dataType = tag.DataTypeName;
                                        break;
                                    }
                                }
                                addresses.Add(new
                                {
                                    address = $"%{areaChar}{addr.StartByte}",
                                    area = areaChar,
                                    startByte = addr.StartByte,
                                    length = addr.Length,
                                    variableName = varName,
                                    dataType = dataType,
                                    moduleName = item.Name
                                });
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plc.Name,
                        deviceName = device.Name,
                        count = addresses.Count,
                        addresses,
                        apiExplored,
                        triedPaths = triedPaths.Count > 0 ? triedPaths : null
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 获取设备的地址分配列表。通过反射获取 PlcDevice.GetAddressList() 或类似方法。
        /// areaFilter 可选：I/Q/M/T/C（不区分大小写，支持全称如 Input/Output/Marker）。
        /// </summary>
        public string GetAssignmentList(string deviceName, string? areaFilter)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrWhiteSpace(deviceName))
                        return Err("设备名称不能为空");

                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");

                    var triedPaths = new List<string>();
                    var normalizedFilter = NormalizeAreaFilter(areaFilter);
                    var assignments = new List<object>();
                    bool apiExplored = false;

                    // 优先：通过反射获取地址列表
                    var addressList = TryGetAddressList(device, triedPaths);
                    if (addressList == null)
                    {
                        // 尝试在 DeviceItem 上获取
                        foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                        {
                            addressList = TryGetAddressList(di, triedPaths);
                            if (addressList != null) break;
                        }
                    }

                    if (addressList != null)
                    {
                        apiExplored = true;
                        assignments = EnumerateAddressListEntries(addressList, normalizedFilter);
                    }
                    else
                    {
                        // 回退：遍历 DeviceItem 的 IoAddresses
                        foreach (var item in EnumerateDeviceItems(device.DeviceItems))
                        {
                            var itemAddresses = ReadIoAddresses(item);
                            foreach (var addr in itemAddresses)
                            {
                                var areaChar = addr.Type == "Input" ? "I" : (addr.Type == "Output" ? "Q" : "M");
                                if (!string.IsNullOrEmpty(normalizedFilter) &&
                                    !areaChar.Equals(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                assignments.Add(new
                                {
                                    address = $"%{areaChar}{addr.StartByte}",
                                    area = areaChar,
                                    startByte = addr.StartByte,
                                    length = addr.Length,
                                    moduleName = item.Name,
                                    orderNumber = SafeGetDeviceItemString(item, "OrderNumber")
                                });
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        deviceName = device.Name,
                        areaFilter = areaFilter ?? "",
                        count = assignments.Count,
                        assignments,
                        apiExplored,
                        triedPaths = triedPaths.Count > 0 ? triedPaths : null
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 查找下一个空闲地址。通过 AddressList.FindNextFree() 反射调用。
        /// area: I/Q/M；dataType: Bool/Byte/Word/DWord/Real；mode: 可选（"aligned"/"any"）。
        /// 返回：空闲地址（起始字节）。
        /// </summary>
        public string FindNextFreeAddress(string plcName, string area, int startByte, string? dataType, string? mode)
        {
            lock (_lock)
            {
                try
                {
                RequireProject();
                    if (string.IsNullOrWhiteSpace(plcName))
                        return Err("PLC 名称不能为空");
                    if (string.IsNullOrWhiteSpace(area))
                        return Err("区域(area)不能为空，可选: I/Q/M");

                    var plc = GetPlcSoftwareByName(plcName);
                    var device = FindDeviceForPlc(plc);
                    if (device == null)
                        return Err($"未找到 PLC 对应的硬件设备: {plc.Name}");

                    var triedPaths = new List<string>();
                    var normalizedArea = NormalizeAreaFilter(area);

                    // 获取地址列表
                    object? addressList = TryGetAddressList(plc, triedPaths);
                    if (addressList == null)
                        addressList = TryGetAddressList(device, triedPaths);

                    if (addressList == null)
                    {
                        // 回退：遍历已分配地址，找出第一个空闲字节
                        return FindFreeAddressFallback(device, plc, normalizedArea, startByte, dataType, triedPaths);
                    }

                    // 反射调用 FindNextFree
                    var freeAddr = InvokeFindNextFree(addressList, normalizedArea, startByte, dataType, mode, triedPaths);
                    if (freeAddr.HasValue)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            plcName = plc.Name,
                            area = normalizedArea,
                            startByte,
                            dataType = dataType ?? "",
                            mode = mode ?? "",
                            freeAddress = freeAddr.Value,
                            apiExplored = true,
                            triedPaths
                        });
                    }

                    // FindNextFree 反射失败 → 回退
                    return FindFreeAddressFallback(device, plc, normalizedArea, startByte, dataType, triedPaths);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 获取 IO 地址冲突列表。通过 IAddressList.GetConflicts() 反射调用。
        /// areaFilter 可选：I/Q/M/T/C。
        /// 返回：冲突列表（地址/冲突变量1/冲突变量2）。
        /// </summary>
        public string GetIoAddressConflicts(string? areaFilter)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var triedPaths = new List<string>();
                    var normalizedFilter = NormalizeAreaFilter(areaFilter);
                    var conflicts = new List<object>();
                    bool apiExplored = false;

                    // 在所有 PLC/设备上查找地址列表并获取冲突
                    foreach (var plc in GetPlcSoftwareList())
                    {
                        var addressList = TryGetAddressList(plc, triedPaths);
                        if (addressList == null)
                        {
                            var dev = FindDeviceForPlc(plc);
                            if (dev != null) addressList = TryGetAddressList(dev, triedPaths);
                        }
                        if (addressList == null) continue;

                        apiExplored = true;
                        var listConflicts = GetAddressListConflicts(addressList);
                        foreach (var c in listConflicts)
                        {
                            // 应用区域过滤
                            if (!string.IsNullOrEmpty(normalizedFilter))
                            {
                                var areaProp = GetReflectedProperty(c, "area") ?? GetReflectedProperty(c, "Area") ?? "";
                                if (!areaProp.Equals(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }
                            conflicts.Add(c);
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        areaFilter = areaFilter ?? "",
                        count = conflicts.Count,
                        conflicts,
                        apiExplored,
                        triedPaths = triedPaths.Count > 0 ? triedPaths : null
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 编译硬件配置。通过反射获取 ICompilable 接口并调用 Compile()。
        /// plcName 为空时编译默认 PLC。
        /// 返回：编译结果（状态/错误数/警告数）。
        /// </summary>
        public string CompileHardware(string? plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var plc = string.IsNullOrEmpty(plcName) ? GetPlcSoftware() : GetPlcSoftwareByName(plcName!);
                    var device = FindDeviceForPlc(plc);
                    if (device == null)
                        return Err($"未找到 PLC 对应的硬件设备: {plc.Name}");

                    var result = CompileDeviceToObject(device);
                    // ★Bug 11: 安全访问反射属性，避免 ! 抑制 null 导致 NullReferenceException
                    var successProp = result.GetType().GetProperty("success");
                    var success = successProp != null && (bool)(successProp.GetValue(result) ?? false);
                    return JsonConvert.SerializeObject(new
                    {
                        success = success,
                        plcName = plc.Name,
                        deviceName = device.Name,
                        result
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 编译所有设备的硬件配置。
        /// 返回：每个设备的编译结果。
        /// </summary>
        public string CompileAllHardware()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var devices = GetAllDevices();
                    if (devices.Count == 0)
                        return Err("项目中未找到任何设备");

                    var results = new List<object>();
                    int totalErrors = 0, totalWarnings = 0;
                    bool allSuccess = true;

                    foreach (var device in devices)
                    {
                        // ★Bug 12: 用 try-catch 隔离单次设备编译，避免一个设备失败导致整体中断
                        try
                        {
                            var result = CompileDeviceToObject(device);
                            // ★Bug 11: 安全访问反射属性，避免 ! 抑制 null 导致 NullReferenceException
                            var successProp = result.GetType().GetProperty("success");
                            bool success = successProp != null && (bool)(successProp.GetValue(result) ?? false);
                            int errCount = (int)(result.GetType().GetProperty("errorCount")?.GetValue(result) ?? 0);
                            int warnCount = (int)(result.GetType().GetProperty("warningCount")?.GetValue(result) ?? 0);
                            totalErrors += errCount;
                            totalWarnings += warnCount;
                            if (!success || errCount > 0) allSuccess = false;
                            results.Add(result);
                        }
                        catch (Exception exDev)
                        {
                            allSuccess = false;
                            results.Add(new { deviceName = device.Name, success = false, error = exDev.Message });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = allSuccess,
                        deviceCount = devices.Count,
                        errorCount = totalErrors,
                        warningCount = totalWarnings,
                        results
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 完整网络配置（IP/掩码/网关/路由/IO系统）。通过 NetworkInterface 和 IoController 设置。
        /// deviceName: 设备名；subnetName: 子网名；ip/subnetMask/gateway: IP 配置；
        /// ioSystemName: 可选，IO 系统名（PLC 作为控制器时创建，IO 设备时连接）；
        /// useRouter: 是否启用路由；routerAddress: 路由地址。
        /// </summary>
        public string SetNetworkConfig(string deviceName, string subnetName, string ip, string subnetMask,
            string? gateway, string? ioSystemName, bool useRouter, string? routerAddress)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    if (string.IsNullOrWhiteSpace(deviceName))
                        return Err("设备名称不能为空");
                    if (string.IsNullOrWhiteSpace(ip))
                        return Err("IP 地址不能为空");
                    if (string.IsNullOrWhiteSpace(subnetMask))
                        return Err("子网掩码不能为空");

                    var device = FindDeviceByName(deviceName);
                    if (device == null) return Err($"未找到设备: {deviceName}");

                    Subnet? subnet = null;
                    if (!string.IsNullOrWhiteSpace(subnetName))
                    {
                        subnet = _project!.Subnets
                            .FirstOrDefault(s => s.Name.Equals(subnetName, StringComparison.OrdinalIgnoreCase));
                        if (subnet == null)
                            return Err($"未找到子网: {subnetName}");
                    }

                    // 获取设备的网络接口与节点
                    NetworkInterface? netIf = null;
                    object? nodeObj = null;
                    foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                    {
                        try { netIf = di.GetService<NetworkInterface>(); }
                        catch { }
                        if (netIf != null && netIf.Nodes.Count > 0)
                        {
                            nodeObj = netIf.Nodes[0];
                            break;
                        }
                    }
                    if (nodeObj == null)
                        return Err($"设备 {deviceName} 上未找到网络接口节点");

                    var eo = (IEngineeringObject)nodeObj;
                    var appliedAttrs = new List<string>();
                    var failedAttrs = new List<string>();

                    // 设置 IP 与子网掩码
                    void SetAttr(string name, string value)
                    {
                        try { eo.SetAttribute(name, value); appliedAttrs.Add(name); }
                        catch { failedAttrs.Add(name); }
                    }

                    SetAttr("Ip", ip);
                    SetAttr("SubnetMask", subnetMask);
                    if (!string.IsNullOrWhiteSpace(gateway))
                        SetAttr("Gateway", gateway);
                    if (useRouter)
                    {
                        SetAttr("UseRouter", "true");
                        if (!string.IsNullOrWhiteSpace(routerAddress))
                            SetAttr("RouterAddress", routerAddress!);
                    }

                    // 连接到子网
                    string connectMsg = "";
                    if (subnet != null)
                    {
                        try
                        {
                            var node = netIf!.Nodes[0];
                            node.ConnectToSubnet(subnet);
                            connectMsg = $"已连接到子网 {subnetName}";
                        }
                        catch (Exception cex)
                        {
                            connectMsg = $"连接子网失败: {cex.Message}";
                        }
                    }

                    // IO 系统配置
                    string ioSystemMsg = "";
                    if (!string.IsNullOrWhiteSpace(ioSystemName))
                    {
                        // 检查设备是 IO 控制器还是 IO 设备
                        IoController? ioController = null;
                        IoConnector? ioConnector = null;
                        try
                        {
                            if (netIf != null)
                            {
                                foreach (var ioc in netIf.IoControllers) { ioController = ioc; break; }
                            }
                        }
                        catch { }
                        try
                        {
                            if (netIf != null)
                            {
                                foreach (var ioc in netIf.IoConnectors) { ioConnector = ioc; break; }
                            }
                        }
                        catch { }

                        if (ioController != null)
                        {
                            // 作为 IO 控制器：创建 IO 系统
                            try
                            {
                                var ioSystem = ioController.CreateIoSystem(ioSystemName!);
                                ioSystemMsg = $"已创建 IO 系统: {ioSystem.Name}";
                            }
                            catch (Exception ex) { ioSystemMsg = $"创建 IO 系统失败: {ex.Message}"; }
                        }
                        else if (ioConnector != null)
                        {
                            // 作为 IO 设备：连接到已有 IO 系统
                            IoSystem? targetIoSystem = null;
                            foreach (var sub in _project!.Subnets)
                            {
                                try
                                {
                                    foreach (var ios in sub.IoSystems)
                                    {
                                        if (ios.Name.Equals(ioSystemName, StringComparison.OrdinalIgnoreCase))
                                        { targetIoSystem = ios; break; }
                                    }
                                }
                                catch { }
                                if (targetIoSystem != null) break;
                            }
                            if (targetIoSystem != null)
                            {
                                try
                                {
                                    ioConnector.ConnectToIoSystem(targetIoSystem);
                                    ioSystemMsg = $"已连接到 IO 系统: {ioSystemName}";
                                }
                                catch (Exception ex) { ioSystemMsg = $"连接 IO 系统失败: {ex.Message}"; }
                            }
                            else
                            {
                                ioSystemMsg = $"未找到 IO 系统: {ioSystemName}";
                            }
                        }
                        else
                        {
                            ioSystemMsg = "设备既非 IO 控制器也非 IO 设备，跳过 IO 系统配置";
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = failedAttrs.Count == 0,
                        deviceName,
                        ip,
                        subnetMask,
                        gateway = gateway ?? "",
                        subnetName = subnetName ?? "",
                        ioSystemName = ioSystemName ?? "",
                        useRouter,
                        routerAddress = routerAddress ?? "",
                        appliedAttributes = appliedAttrs,
                        failedAttributes = failedAttrs,
                        subnetConnection = connectMsg,
                        ioSystemConfig = ioSystemMsg
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 获取完整硬件配置信息（机架/槽位/模块/IO地址）。
        /// plcName 为空时返回所有 PLC 的硬件配置树。
        /// </summary>
        public string GetFullHardwareConfig(string? plcName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var namedPlc = string.IsNullOrEmpty(plcName) ? null : GetPlcSoftwareByName(plcName!);
                    var plcs = namedPlc == null
                        ? GetPlcSoftwareList().Select(p => new { Plc = p, Device = FindDeviceForPlc(p) }).ToList()
                        : new[] { new { Plc = namedPlc, Device = FindDeviceForPlc(namedPlc) } }.ToList();

                    if (plcs.Count == 0)
                        return Err("未找到任何 PLC 软件");

                    var plcConfigs = new List<object>();
                    foreach (var entry in plcs)
                    {
                        if (entry.Device == null) continue;
                        var modules = new List<object>();
                        foreach (var item in EnumerateDeviceItems(entry.Device.DeviceItems))
                        {
                            var addresses = ReadIoAddresses(item);
                            modules.Add(new
                            {
                                name = item.Name,
                                typeIdentifier = SafeGetDeviceItemString(item, "TypeIdentifier"),
                                orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                                firmwareVersion = SafeGetDeviceItemString(item, "FirmwareVersion"),
                                positionNumber = SafeGetDeviceItemInt(item, "PositionNumber"),
                                addresses = addresses.Select(a => new
                                {
                                    type = a.Type,
                                    startByte = a.StartByte,
                                    length = a.Length,
                                    area = a.Type == "Input" ? "I" : (a.Type == "Output" ? "Q" : "M")
                                }).ToList()
                            });
                        }
                        plcConfigs.Add(new
                        {
                            plcName = entry.Plc.Name,
                            deviceName = entry.Device.Name,
                            typeIdentifier = SafeTypeIdentifier(entry.Device),
                            moduleCount = modules.Count,
                            modules
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        plcName = plcName ?? "all",
                        plcCount = plcConfigs.Count,
                        plcs = plcConfigs
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 获取完整网络拓扑配置（所有子网/设备/接口/IO系统）。
        /// </summary>
        public string GetNetworkConfiguration()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var subnetList = new List<object>();

                    foreach (var subnet in _project!.Subnets)
                    {
                        var ioSystems = new List<object>();
                        try
                        {
                            foreach (var ios in subnet.IoSystems)
                            {
                                int devCount = 0;
                                var ioDeviceNames = new List<string>();
                                try
                                {
                                    var ioDevsProp = ios.GetType().GetProperty("IoDevices");
                                    if (ioDevsProp?.GetValue(ios) is IEnumerable ioDevs)
                                    {
                                        foreach (var d in ioDevs)
                                        {
                                            devCount++;
                                            var n = TryGetName(d);
                                            if (!string.IsNullOrEmpty(n)) ioDeviceNames.Add(n!);
                                        }
                                    }
                                }
                                catch { }
                                ioSystems.Add(new
                                {
                                    name = ios.Name,
                                    ioDeviceCount = devCount,
                                    ioDevices = ioDeviceNames
                                });
                            }
                        }
                        catch { }

                        // 收集连接到该子网的设备
                        var connectedDevices = new List<object>();
                        foreach (var device in GetAllDevices())
                        {
                            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                            {
                                NetworkInterface? netIf = null;
                                try { netIf = di.GetService<NetworkInterface>(); }
                                catch { }
                                if (netIf == null) continue;
                                try
                                {
                                    if (netIf.Nodes.Count > 0)
                                    {
                                        var connectedSubnet = netIf.Nodes[0].ConnectedSubnet;
                                        if (connectedSubnet != null &&
                                            connectedSubnet.Name.Equals(subnet.Name, StringComparison.OrdinalIgnoreCase))
                                        {
                                            string devIp = "", devSubnetMask = "", devGateway = "";
                                            var node = netIf.Nodes[0];
                                            // ★V17 修复★ 属性名随版本/固件变化（Ip/IpAddress/...），不能写死 "Ip"。
                                            // 与 SetDeviceIp 使用同一组候选名，先枚举属性再按候选匹配。
                                            try
                                            {
                                                var nodeEo = (IEngineeringObject)node;
                                                var infoNames = new List<string>();
                                                foreach (var info in nodeEo.GetAttributeInfos())
                                                {
                                                    try { if (!string.IsNullOrWhiteSpace(info.Name)) infoNames.Add(info.Name); }
                                                    catch { }
                                                }
                                                // ★V17 修复★ 以太网节点的 IP 存在 "Address" 属性中；
                                                // 但 DP 节点的 "Address" 是 PROFIBUS 站地址（实测为 "2"），
                                                // 因此 "Address" 仅在以太网子网上才作为 IP 候选。
                                                var ipCandidates = new[] { "IpAddress", "IPAddress", "IP", "Ip" };
                                                var isEthernet = subnet.TypeIdentifier?.ToString().IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0;
                                                if (isEthernet)
                                                    ipCandidates = ipCandidates.Concat(new[] { "Address" }).ToArray();
                                                var maskCandidates = new[] { "SubnetMask", "SubnetMaskAddress", "IPSubnetMask", "Mask" };
                                                var gatewayCandidates = new[] { "RouterAddress", "Gateway", "IPRouterAddress", "Router" };
                                                var ipAttr = ipCandidates.Select(w => infoNames.FirstOrDefault(n => n.Equals(w, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                                                var maskAttr = maskCandidates.Select(w => infoNames.FirstOrDefault(n => n.Equals(w, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                                                var gwAttr = gatewayCandidates.Select(w => infoNames.FirstOrDefault(n => n.Equals(w, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                                                if (ipAttr != null) devIp = nodeEo.GetAttribute(ipAttr)?.ToString() ?? "";
                                                if (maskAttr != null) devSubnetMask = nodeEo.GetAttribute(maskAttr)?.ToString() ?? "";
                                                if (gwAttr != null) devGateway = nodeEo.GetAttribute(gwAttr)?.ToString() ?? "";
                                            }
                                            catch { }
                                            connectedDevices.Add(new
                                            {
                                                deviceName = device.Name,
                                                deviceItem = di.Name,
                                                ip = devIp,
                                                subnetMask = devSubnetMask,
                                                gateway = devGateway,
                                                nodeCount = netIf.Nodes.Count
                                            });
                                            break;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        subnetList.Add(new
                        {
                            name = subnet.Name,
                            typeIdentifier = subnet.TypeIdentifier?.ToString() ?? "",
                            ioSystemCount = ioSystems.Count,
                            ioSystems,
                            connectedDeviceCount = connectedDevices.Count,
                            connectedDevices
                        });
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        subnetCount = subnetList.Count,
                        subnets = subnetList
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 配置模拟量模块（AI/AO）的通道类型（如 4-20mA、0-10V 等）。
        /// 定位流程：plcName → PLC 设备 → deviceName 或 moduleIndex → 目标 DeviceItem →
        /// DeviceItem.Channels → Channel.Number == channelNumber → IEngineeringObject.SetAttribute。
        /// 由于不同模块的通道类型属性名不一致，会依次尝试 SensorType / OutputType /
        /// MeasurementType / MeasuringType / InputRange / OutputRange 等候选属性名。
        /// 值类型也会做 best-effort 尝试：先按字符串（"4..20mA" 等），再按 long 索引。
        /// 失败时返回 GetAttributeInfos() 收集到的可用属性列表，便于排查。
        /// </summary>
        /// <param name="deviceName">模块设备名称（DeviceItem.Name）；为空时按 moduleIndex 定位</param>
        /// <param name="moduleIndex">模块索引（deviceName 为空时使用，默认 0）</param>
        /// <param name="channelNumber">通道号（0-7，需与模块实际通道范围匹配）</param>
        /// <param name="channelType">通道类型字符串，如 "4-20mA"、"0-10V"、"0-20mA"、"-10V到+10V"</param>
        /// <param name="plcName">PLC 名称（多 PLC 项目时指定，为空时使用默认 PLC）</param>
        public string SetAnalogChannelType(string deviceName, int moduleIndex, int channelNumber, string channelType, string? plcName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();

                    if (string.IsNullOrWhiteSpace(channelType))
                        return Err("通道类型(channelType)不能为空");
                    if (channelNumber < 0)
                        return Err("通道号(channelNumber)不能为负数");

                    // 1. 定位 PLC 设备
                    var plc = string.IsNullOrEmpty(plcName) ? GetPlcSoftware() : GetPlcSoftwareByName(plcName!);
                    var device = FindDeviceForPlc(plc);
                    if (device == null)
                        return Err($"未找到 PLC 对应的硬件设备: {plc.Name}");

                    // 2. 定位模块 DeviceItem
                    var triedPaths = new List<string>();
                    DeviceItem? moduleItem = null;

                    if (!string.IsNullOrWhiteSpace(deviceName))
                    {
                        // 2a. 按名称精确匹配
                        moduleItem = FindDeviceItemInDevice(device.DeviceItems, deviceName!);
                        triedPaths.Add($"按名称查找 DeviceItem: '{deviceName}' -> {(moduleItem != null ? "命中" : "未命中")}");

                        // 2b. 名称包含匹配（精确匹配失败时的回退）
                        if (moduleItem == null)
                        {
                            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                            {
                                if ((di.Name ?? "").IndexOf(deviceName!, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    moduleItem = di;
                                    triedPaths.Add($"按名称包含查找 DeviceItem: '{deviceName}' -> 命中 '{di.Name}'");
                                    break;
                                }
                            }
                        }
                    }

                    // 3. 按索引定位（deviceName 为空或未命中时）
                    if (moduleItem == null)
                    {
                        var allItems = EnumerateDeviceItems(device.DeviceItems).ToList();
                        // 优先在"看起来像 AI/AO 模块"的项中按索引选择
                        var analogCandidates = allItems.Where(i =>
                        {
                            var typeId = SafeGetDeviceItemString(i, "TypeIdentifier").ToLowerInvariant();
                            var name = (i.Name ?? "").ToLowerInvariant();
                            return typeId.Contains("ai") || typeId.Contains("ao")
                                   || typeId.Contains("analog") || typeId.Contains("signal")
                                   || name.Contains("ai") || name.Contains("ao")
                                   || name.Contains("analog");
                        }).ToList();

                        if (analogCandidates.Count > 0 && moduleIndex >= 0 && moduleIndex < analogCandidates.Count)
                        {
                            moduleItem = analogCandidates[moduleIndex];
                            triedPaths.Add($"按索引在模拟量候选项中查找: [{moduleIndex}] -> '{moduleItem.Name}' (共 {analogCandidates.Count} 个候选)");
                        }
                        else if (allItems.Count > 0 && moduleIndex >= 0 && moduleIndex < allItems.Count)
                        {
                            moduleItem = allItems[moduleIndex];
                            triedPaths.Add($"按索引在全部 DeviceItem 中查找: [{moduleIndex}] -> '{moduleItem.Name}' (共 {allItems.Count} 项)");
                        }
                    }

                    if (moduleItem == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            plcName = plc.Name,
                            deviceName = deviceName ?? "",
                            moduleIndex,
                            error = "未找到目标模块 DeviceItem",
                            triedPaths
                        });
                    }

                    // 4. 获取模块的 Channels 集合
                    ChannelComposition? channels = null;
                    try { channels = moduleItem.Channels; }
                    catch (Exception chEx)
                    {
                        triedPaths.Add($"读取 Channels 属性异常: {chEx.Message}");
                    }

                    if (channels == null || channels.Count == 0)
                    {
                        // 回退：在子 DeviceItem 中查找带通道的模块（如 ET200 的子模块）
                        foreach (var child in EnumerateDeviceItems(moduleItem.DeviceItems))
                        {
                            try
                            {
                                if (child.Channels != null && child.Channels.Count > 0)
                                {
                                    channels = child.Channels;
                                    moduleItem = child;
                                    triedPaths.Add($"在子 DeviceItem '{child.Name}' 中找到 {channels.Count} 个通道");
                                    break;
                                }
                            }
                            catch { }
                        }
                    }

                    if (channels == null || channels.Count == 0)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            plcName = plc.Name,
                            moduleName = moduleItem.Name,
                            error = "模块没有通道(Channels)，可能不是模拟量模块",
                            triedPaths
                        });
                    }

                    // 5. 定位指定通道号的 Channel
                    Channel? targetChannel = null;
                    var channelList = new List<object>();
                    foreach (Channel ch in channels)
                    {
                        channelList.Add(new
                        {
                            number = ch.Number,
                            ioType = ch.IoType.ToString(),
                            type = ch.Type.ToString()
                        });
                        if (ch.Number == channelNumber)
                            targetChannel = ch;
                    }

                    if (targetChannel == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            plcName = plc.Name,
                            moduleName = moduleItem.Name,
                            channelNumber,
                            error = $"未找到通道号 {channelNumber}",
                            availableChannels = channelList,
                            triedPaths
                        });
                    }

                    // 6. 映射通道类型字符串到 TIA Portal 内部格式
                    var mappedString = MapChannelTypeValue(channelType);
                    var mappedLong = MapChannelTypeToLong(channelType);

                    // 7. 尝试设置通道类型属性（多属性名 × 多值类型 best-effort）
                    var eo = (IEngineeringObject)targetChannel;
                    var attributeNameCandidates = new[]
                    {
                        "SensorType", "OutputType", "MeasurementType", "MeasuringType",
                        "InputRange", "OutputRange", "ChannelType", "Type"
                    };

                    var setResults = new List<object>();
                    bool setSuccess = false;
                    string? usedAttribute = null;
                    object? usedValue = null;

                    // 7a. 先尝试字符串值
                    foreach (var attrName in attributeNameCandidates)
                    {
                        try
                        {
                            eo.SetAttribute(attrName, mappedString);
                            setSuccess = true;
                            usedAttribute = attrName;
                            usedValue = mappedString;
                            setResults.Add(new { attribute = attrName, valueType = "string", status = "success", value = mappedString });
                            break;
                        }
                        catch (Exception ex)
                        {
                            setResults.Add(new { attribute = attrName, valueType = "string", status = "failed", error = ex.Message });
                        }
                    }

                    // 7b. 字符串失败且 long 映射可用时，尝试 long 值
                    if (!setSuccess && mappedLong.HasValue)
                    {
                        foreach (var attrName in attributeNameCandidates)
                        {
                            try
                            {
                                eo.SetAttribute(attrName, mappedLong.Value);
                                setSuccess = true;
                                usedAttribute = attrName;
                                usedValue = mappedLong.Value;
                                setResults.Add(new { attribute = attrName, valueType = "long", status = "success", value = mappedLong.Value });
                                break;
                            }
                            catch (Exception ex)
                            {
                                setResults.Add(new { attribute = attrName, valueType = "long", status = "failed", error = ex.Message });
                            }
                        }
                    }

                    // 8. 收集可用属性信息（便于排查失败原因）
                    var availableAttributes = new List<object>();
                    try
                    {
                        var attrInfos = eo.GetAttributeInfos();
                        foreach (var info in attrInfos)
                        {
                            try
                            {
                                var name = GetReflectedProperty(info, "Name") ?? "";
                                string currentValue = "";
                                try { currentValue = eo.GetAttribute(name)?.ToString() ?? ""; }
                                catch { }
                                availableAttributes.Add(new { name, currentValue });
                            }
                            catch { }
                        }
                    }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = setSuccess,
                        plcName = plc.Name,
                        deviceName = device.Name,
                        moduleName = moduleItem.Name,
                        moduleTypeIdentifier = SafeGetDeviceItemString(moduleItem, "TypeIdentifier"),
                        moduleOrderNumber = SafeGetDeviceItemString(moduleItem, "OrderNumber"),
                        channelNumber,
                        channelType,
                        mappedStringValue = mappedString,
                        mappedLongValue = mappedLong,
                        usedAttribute,
                        usedValue,
                        channelInfo = new
                        {
                            number = targetChannel.Number,
                            ioType = targetChannel.IoType.ToString(),
                            type = targetChannel.Type.ToString()
                        },
                        setResults,
                        availableAttributes = availableAttributes.Count > 0 ? availableAttributes : null,
                        availableChannels = channelList,
                        triedPaths
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 映射用户输入的通道类型字符串到 TIA Portal 内部字符串格式。
        /// 支持中文/英文/简写输入，统一输出 TIA Portal 使用的 ".." 范围格式。
        /// 未在映射表中的输入原样返回（让用户可以直接传入 TIA Portal 原生字符串）。
        /// </summary>
        private static string MapChannelTypeValue(string channelType)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 电流
                ["4-20mA"] = "4..20mA",
                ["4..20mA"] = "4..20mA",
                ["4~20mA"] = "4..20mA",
                ["4-20ma"] = "4..20mA",
                ["0-20mA"] = "0..20mA",
                ["0..20mA"] = "0..20mA",
                ["0~20mA"] = "0..20mA",
                ["0-20ma"] = "0..20mA",
                // 电压
                ["0-10V"] = "0..10V",
                ["0..10V"] = "0..10V",
                ["0~10V"] = "0..10V",
                ["0-10v"] = "0..10V",
                ["-10V到+10V"] = "-10..10V",
                ["-10..10V"] = "-10..10V",
                ["-10~10V"] = "-10..10V",
                ["±10V"] = "-10..10V",
                ["-10V到10V"] = "-10..10V",
                ["1-5V"] = "1..5V",
                ["1..5V"] = "1..5V",
                ["0-5V"] = "0..5V",
                ["0..5V"] = "0..5V",
                // 热电偶/热电阻（常见选项）
                ["热电偶"] = "Thermocouple",
                ["Thermocouple"] = "Thermocouple",
                ["热电阻"] = "RTD",
                ["RTD"] = "RTD",
                ["Pt100"] = "RTD",
                // 电阻
                ["电阻"] = "Resistance",
                ["Resistance"] = "Resistance",
            };
            return map.TryGetValue(channelType.Trim(), out var v) ? v : channelType;
        }

        /// <summary>
        /// 映射通道类型字符串到 long 值（部分模块的通道类型属性使用整数索引）。
        /// 这些索引值可能因模块型号/固件版本不同而不同，仅作为 best-effort 回退尝试。
        /// 返回 null 表示无可用 long 映射。
        /// </summary>
        private static long? MapChannelTypeToLong(string channelType)
        {
            var key = channelType.Trim().ToLowerInvariant();
            // 常见 S7-1200/1500 AI 模块的通道类型索引（best-effort，实际值可能因模块而异）
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["4..20ma"] = 7,
                ["4-20ma"] = 7,
                ["0..20ma"] = 6,
                ["0-20ma"] = 6,
                ["0..10v"] = 1,
                ["0-10v"] = 1,
                ["-10..10v"] = 2,
                ["-10v到+10v"] = 2,
                ["±10v"] = 2,
                ["1..5v"] = 5,
                ["1-5v"] = 5,
                ["0..5v"] = 4,
                ["0-5v"] = 4,
            };
            return map.TryGetValue(key, out var v) ? v : null;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 反射辅助方法（地址列表 / 类型发现 / 网络配置）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>在已加载程序集中查找 Siemens 类型（按短名或全名匹配）。</summary>
        private static Type? FindSiemensType(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types = null;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.Name == typeName) return t;
                    if (t.FullName != null && t.FullName.EndsWith(typeName, StringComparison.Ordinal)) return t;
                }
            }
            return null;
        }

        /// <summary>反射调用 GetService&lt;T&gt;() 泛型方法。</summary>
        private static object? InvokeGenericGetService(object obj, Type serviceType)
        {
            try
            {
                var getServiceMethod = obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod && m.GetParameters().Length == 0);
                if (getServiceMethod == null) return null;
                var typedMethod = getServiceMethod.MakeGenericMethod(serviceType);
                return typedMethod.Invoke(obj, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// 尝试从对象获取地址列表。按优先级尝试多种路径：
        /// GetService&lt;IAddressList&gt; / GetService&lt;AddressList&gt; / GetAddressList() / AddressList 属性。
        /// triedPaths 记录所有尝试过的路径。
        /// </summary>
        private static object? TryGetAddressList(object obj, List<string> triedPaths)
        {
            if (obj == null) return null;

            // 路径1: GetService<IAddressList>()
            var iAddressListType = FindSiemensType("IAddressList");
            if (iAddressListType != null)
            {
                var path = $"GetService<{iAddressListType.Name}>() on {obj.GetType().Name}";
                triedPaths.Add(path);
                var service = InvokeGenericGetService(obj, iAddressListType);
                if (service != null) return service;
            }

            // 路径2: GetService<AddressList>()
            var addressListType = FindSiemensType("AddressList");
            if (addressListType != null && addressListType != iAddressListType)
            {
                var path = $"GetService<{addressListType.Name}>() on {obj.GetType().Name}";
                triedPaths.Add(path);
                var service = InvokeGenericGetService(obj, addressListType);
                if (service != null) return service;
            }

            // 路径3: GetAddressList() 方法
            try
            {
                var method = obj.GetType().GetMethod("GetAddressList", Type.EmptyTypes);
                if (method != null)
                {
                    triedPaths.Add($"GetAddressList() on {obj.GetType().Name}");
                    return method.Invoke(obj, null);
                }
            }
            catch { }

            // 路径4: AddressList 属性
            try
            {
                var prop = obj.GetType().GetProperty("AddressList");
                if (prop != null)
                {
                    triedPaths.Add($"AddressList property on {obj.GetType().Name}");
                    return prop.GetValue(obj);
                }
            }
            catch { }

            // 路径5: Addresses 属性
            try
            {
                var prop = obj.GetType().GetProperty("Addresses");
                if (prop != null)
                {
                    triedPaths.Add($"Addresses property on {obj.GetType().Name}");
                    return prop.GetValue(obj);
                }
            }
            catch { }

            return null;
        }

        /// <summary>枚举地址列表中的所有条目，提取地址/变量名/数据类型/长度/区域。</summary>
        private static List<object> EnumerateAddressListEntries(object addressList, string? areaFilter)
        {
            var result = new List<object>();
            var normalizedFilter = NormalizeAreaFilter(areaFilter);

            if (addressList is IEnumerable enumerable and not string)
            {
                foreach (var entry in enumerable)
                {
                    if (entry == null) continue;

                    var area = GetAddressArea(entry);
                    if (!string.IsNullOrEmpty(normalizedFilter) &&
                        !area.Equals(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var addr = GetReflectedProperty(entry, "Address")
                        ?? GetReflectedProperty(entry, "LogicalAddress")
                        ?? GetReflectedProperty(entry, "AddressString") ?? "";
                    var varName = GetReflectedProperty(entry, "VariableName")
                        ?? GetReflectedProperty(entry, "TagName")
                        ?? GetReflectedProperty(entry, "Name") ?? "";
                    var dataType = GetReflectedProperty(entry, "DataType")
                        ?? GetReflectedProperty(entry, "DataTypeName") ?? "";
                    int length = GetReflectedInt(entry, "Length")
                        ?? GetReflectedInt(entry, "Size") ?? 0;
                    int startByte = GetReflectedInt(entry, "StartByte")
                        ?? GetReflectedInt(entry, "StartAddress") ?? 0;

                    result.Add(new
                    {
                        address = addr,
                        variableName = varName,
                        dataType,
                        length,
                        area,
                        startByte
                    });
                }
            }

            return result;
        }

        /// <summary>通过反射获取地址列表的冲突条目。</summary>
        private static List<object> GetAddressListConflicts(object addressList)
        {
            var result = new List<object>();

            // 尝试 GetConflicts() 方法
            try
            {
                var method = addressList.GetType().GetMethod("GetConflicts", Type.EmptyTypes);
                if (method != null)
                {
                    var conflicts = method.Invoke(addressList, null);
                    if (conflicts is IEnumerable enumerable and not string)
                    {
                        foreach (var entry in enumerable)
                        {
                            if (entry == null) continue;
                            result.Add(SerializeConflictEntry(entry));
                        }
                    }
                    return result;
                }
            }
            catch { }

            // 尝试 Conflicts 属性
            try
            {
                var prop = addressList.GetType().GetProperty("Conflicts");
                if (prop != null)
                {
                    var conflicts = prop.GetValue(addressList);
                    if (conflicts is IEnumerable enumerable and not string)
                    {
                        foreach (var entry in enumerable)
                        {
                            if (entry == null) continue;
                            result.Add(SerializeConflictEntry(entry));
                        }
                    }
                    return result;
                }
            }
            catch { }

            return result;
        }

        /// <summary>序列化单个冲突条目（地址/冲突变量1/冲突变量2）。</summary>
        private static object SerializeConflictEntry(object entry)
        {
            var addr = GetReflectedProperty(entry, "Address")
                ?? GetReflectedProperty(entry, "LogicalAddress") ?? "";
            var area = GetAddressArea(entry);
            var var1 = GetReflectedProperty(entry, "VariableName1")
                ?? GetReflectedProperty(entry, "TagName1")
                ?? GetReflectedProperty(entry, "FirstVariable") ?? "";
            var var2 = GetReflectedProperty(entry, "VariableName2")
                ?? GetReflectedProperty(entry, "TagName2")
                ?? GetReflectedProperty(entry, "SecondVariable") ?? "";

            // 回退：尝试从嵌套对象获取变量名
            if (string.IsNullOrEmpty(var1))
            {
                var v1Prop = entry.GetType().GetProperty("Variable1");
                if (v1Prop?.GetValue(entry) is { } v1)
                    var1 = GetReflectedProperty(v1, "Name") ?? v1.ToString() ?? "";
            }
            if (string.IsNullOrEmpty(var2))
            {
                var v2Prop = entry.GetType().GetProperty("Variable2");
                if (v2Prop?.GetValue(entry) is { } v2)
                    var2 = GetReflectedProperty(v2, "Name") ?? v2.ToString() ?? "";
            }

            return new
            {
                address = addr,
                area,
                variable1 = var1,
                variable2 = var2
            };
        }

        /// <summary>反射调用 FindNextFree 方法，尝试多种参数组合。</summary>
        private static int? InvokeFindNextFree(object addressList, string area, int startByte,
            string? dataType, string? mode, List<string> triedPaths)
        {
            var methods = addressList.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "FindNextFree")
                .ToList();

            foreach (var method in methods)
            {
                var parms = method.GetParameters();
                var path = $"FindNextFree({string.Join(", ", parms.Select(p => p.ParameterType.Name))})";
                triedPaths.Add(path);

                try
                {
                    object? result = null;
                    int areaNum = area switch
                    {
                        "I" => 1,  // Input
                        "Q" => 2,  // Output
                        "M" => 3,  // Marker
                        "T" => 4,  // Timer
                        "C" => 5,  // Counter
                        _ => 0
                    };

                    // 根据参数数量与类型构造调用
                    if (parms.Length == 2 && parms[0].ParameterType == typeof(int) && parms[1].ParameterType == typeof(int))
                    {
                        int len = GetDataTypeLength(dataType);
                        result = method.Invoke(addressList, new object[] { startByte, len });
                    }
                    else if (parms.Length == 3 && parms[0].ParameterType == typeof(int))
                    {
                        int len = GetDataTypeLength(dataType);
                        result = method.Invoke(addressList, new object[] { areaNum, startByte, len });
                    }
                    else if (parms.Length == 3 && parms[0].ParameterType == typeof(string))
                    {
                        int len = GetDataTypeLength(dataType);
                        result = method.Invoke(addressList, new object[] { area, startByte, len });
                    }
                    else if (parms.Length == 4)
                    {
                        int len = GetDataTypeLength(dataType);
                        result = method.Invoke(addressList, new object[] { areaNum, startByte, len, mode ?? "aligned" });
                    }

                    if (result != null)
                    {
                        if (result is int i) return i;
                        if (int.TryParse(result.ToString(), out var parsed)) return parsed;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>查找下一个空闲地址的回退方案：遍历已分配地址找出空闲字节。</summary>
        private string FindFreeAddressFallback(Device device, PlcSoftware plc, string area,
            int startByte, string? dataType, List<string> triedPaths)
        {
            triedPaths.Add("FindFreeAddressFallback (遍历已分配地址)");

            // 收集该区域所有已占用地址范围
            var occupied = new List<(int start, int len)>();
            foreach (var item in EnumerateDeviceItems(device.DeviceItems))
            {
                var addrs = ReadIoAddresses(item);
                foreach (var a in addrs)
                {
                    var aChar = a.Type == "Input" ? "I" : (a.Type == "Output" ? "Q" : "M");
                    if (!aChar.Equals(area, StringComparison.OrdinalIgnoreCase)) continue;
                    occupied.Add((a.StartByte, a.Length));
                }
            }

            // 收集标签表中该区域的地址
            foreach (var tag in GetAllTags(plc))
            {
                var tagAddr = (tag.LogicalAddress ?? "").TrimStart('%').Trim();
                if (tagAddr.Length == 0) continue;
                var tagChar = char.ToUpperInvariant(tagAddr[0]).ToString();
                if (!tagChar.Equals(area, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryParseAddressString(tagAddr, out var info) && info != null)
                    occupied.Add((info.StartByte, info.Length));
            }

            int needLen = GetDataTypeLength(dataType);
            int candidate = startByte;
            // 简单线性探测：找到第一个不与任何已占用范围重叠的地址
            while (true)
            {
                bool conflict = false;
                foreach (var (s, l) in occupied)
                {
                    if (candidate < s + l && candidate + needLen > s)
                    {
                        conflict = true;
                        candidate = s + l;
                        break;
                    }
                }
                if (!conflict) break;
                if (candidate > 65535) break;  // 防止无限循环
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                plcName = plc.Name,
                area,
                startByte,
                dataType = dataType ?? "",
                freeAddress = candidate,
                apiExplored = false,
                triedPaths,
                note = "通过回退方案（遍历已分配地址）计算空闲地址，结果可能不如原生 FindNextFree 精确"
            });
        }

        /// <summary>编译单个设备并返回结果对象（含状态/错误数/警告数/消息）。</summary>
        private object CompileDeviceToObject(Device device)
        {
            try
            {
                var compilable = device.GetService<ICompilable>();
                if (compilable != null)
                {
                    var result = compilable.Compile();
                    var errors = new List<object>();
                    var warnings = new List<object>();
                    foreach (var m in result.Messages)
                        FlattenCompilerMessages(m, errors, warnings);

                    return new
                    {
                        success = result.ErrorCount == 0,
                        deviceName = device.Name,
                        state = result.State.ToString(),
                        errorCount = result.ErrorCount,
                        warningCount = result.WarningCount,
                        errors,
                        warnings
                    };
                }
            }
            catch (Exception ex)
            {
                return new { success = false, deviceName = device.Name, error = ex.Message, errorCount = 1, warningCount = 0 };
            }

            // 回退：尝试编译 PLC 块组
            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
            {
                try
                {
                    var swContainer = di.GetService<SoftwareContainer>();
                    if (swContainer?.Software is PlcSoftware plc)
                    {
                        var blockCompilable = plc.BlockGroup.GetService<ICompilable>();
                        if (blockCompilable != null)
                        {
                            var result = blockCompilable.Compile();
                            var errors = new List<object>();
                            var warnings = new List<object>();
                            foreach (var m in result.Messages)
                                FlattenCompilerMessages(m, errors, warnings);

                            return new
                            {
                                success = result.ErrorCount == 0,
                                deviceName = device.Name,
                                state = result.State.ToString(),
                                errorCount = result.ErrorCount,
                                warningCount = result.WarningCount,
                                errors,
                                warnings,
                                compileMode = "PLC BlockGroup (fallback)"
                            };
                        }
                    }
                }
                catch { }
            }

            return new
            {
                success = false,
                deviceName = device.Name,
                error = "设备不支持 ICompilable 服务",
                errorCount = 1,
                warningCount = 0
            };
        }

        /// <summary>规范化区域过滤字符串（I/Input → "I"，Q/Output → "Q"，M/Marker → "M" 等）。</summary>
        private static string NormalizeAreaFilter(string? areaFilter)
        {
            if (string.IsNullOrWhiteSpace(areaFilter)) return "";
            var s = areaFilter.Trim().ToLowerInvariant();
            if (s == "i" || s == "input") return "I";
            if (s == "q" || s == "output" || s == "o") return "Q";
            if (s == "m" || s == "marker" || s == "memory") return "M";
            if (s == "t" || s == "timer") return "T";
            if (s == "c" || s == "counter") return "C";
            return areaFilter!.Trim().ToUpperInvariant();
        }

        /// <summary>从地址条目对象获取区域标识（I/Q/M/T/C）。</summary>
        private static string GetAddressArea(object entry)
        {
            try
            {
                var typeVal = GetReflectedProperty(entry, "AddressType")
                    ?? GetReflectedProperty(entry, "IoType")
                    ?? GetReflectedProperty(entry, "Area")
                    ?? GetReflectedProperty(entry, "Type");
                if (!string.IsNullOrEmpty(typeVal))
                {
                    var t = typeVal.ToLowerInvariant();
                    if (t.Contains("input") || t == "i") return "I";
                    if (t.Contains("output") || t == "q" || t == "o") return "Q";
                    if (t.Contains("marker") || t.Contains("memory") || t == "m") return "M";
                    if (t.Contains("timer") || t == "t") return "T";
                    if (t.Contains("counter") || t == "c") return "C";
                    return typeVal;
                }
            }
            catch { }

            // 从地址字符串推断
            var addr = GetReflectedProperty(entry, "Address")
                ?? GetReflectedProperty(entry, "LogicalAddress") ?? "";
            if (!string.IsNullOrEmpty(addr))
            {
                var c = addr.TrimStart('%').Trim();
                if (c.Length > 0)
                {
                    var ch = char.ToUpperInvariant(c[0]);
                    if (ch == 'I' || ch == 'Q' || ch == 'M' || ch == 'T' || ch == 'C')
                        return ch.ToString();
                }
            }
            return "";
        }

        /// <summary>反射获取对象的字符串属性（先尝试属性，再尝试 IEngineeringObject.GetAttribute）。</summary>
        private static string? GetReflectedProperty(object obj, string propName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj)?.ToString();
                if (obj is IEngineeringObject eo) return eo.GetAttribute(propName)?.ToString();
            }
            catch { }
            return null;
        }

        /// <summary>反射获取对象的整数属性。</summary>
        private static int? GetReflectedInt(object obj, string propName)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(obj);
                    if (v is int i) return i;
                    if (v != null && int.TryParse(v.ToString(), out var parsed)) return parsed;
                }
                if (obj is IEngineeringObject eo)
                {
                    var v = eo.GetAttribute(propName);
                    if (v is int i2) return i2;
                    if (v != null && int.TryParse(v.ToString(), out var parsed2)) return parsed2;
                }
            }
            catch { }
            return null;
        }

        /// <summary>根据数据类型名获取字节长度（Bool=1, Byte=1, Word=2, DWord=4, Real=4, LReal=8）。</summary>
        private static int GetDataTypeLength(string? dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return 1;
            return dataType!.Trim().ToLowerInvariant() switch
            {
                "bool" => 1,
                "byte" => 1,
                "char" => 1,
                "word" => 2,
                "int" => 2,
                "dword" => 4,
                "dint" => 4,
                "real" => 4,
                "lreal" => 8,
                "lint" => 8,
                "ulint" => 8,
                "udint" => 4,
                "uint" => 2,
                "usint" => 1,
                "wchar" => 2,
                "string" => 2,
                "wstring" => 4,
                _ => 1
            };
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 辅助方法
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>反射调用 IProject.GetService&lt;T&gt;（类型名匹配，跨版本）。</summary>
        private static object? GetServiceByType(object target, string typeName)
        {
            try
            {
                var svcType = target.GetType().Assembly.GetTypes()
                    .FirstOrDefault(t => t.Name == typeName);
                if (svcType == null) return null;
                var getService = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition);
                return getService?.MakeGenericMethod(svcType).Invoke(target, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// 反射调用 CaxProvider.Export。
        /// V19: Export(Device, FileInfo) 返回 TransferResult；
        /// V17: Export(Device, FileInfo, FileInfo) 返回 void。
        /// </summary>
        private static object? InvokeCaxExport(object caxProvider, Device device, FileInfo file)
        {
            try
            {
                var t = caxProvider.GetType();
                var m = t.GetMethod("Export", new[] { typeof(Device), typeof(FileInfo) });
                if (m == null)
                    m = t.GetMethod("Export", new[] { typeof(Device), typeof(FileInfo), typeof(FileInfo) });
                if (m == null) return null;
                if (m.GetParameters().Length == 2)
                    return m.Invoke(caxProvider, new object[] { device, file });

                // ★V17 修复★ 3 参 Export 的第 3 参是日志文件（FileInfo），传 null 会被 V17 拒绝；
                // 改用临时日志文件路径，调用后立即删除。
                var logPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".log");
                try
                {
                    return m.Invoke(caxProvider, new object[] { device, file, new FileInfo(logPath) });
                }
                finally
                {
                    try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
                }
            }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        /// <summary>
        /// 反射调用 CaxProvider.Import。
        /// V19: Import(FileInfo, CaxImportOptions) 返回 TransferResult；
        /// V17: Import(FileInfo, FileInfo, CaxImportOptions) 返回 void。
        /// </summary>
        private static object? InvokeCaxImport(object caxProvider, FileInfo file, object options)
        {
            try
            {
                var t = caxProvider.GetType();
                var m = t.GetMethod("Import", new[] { typeof(FileInfo), typeof(CaxImportOptions) });
                if (m == null)
                    m = t.GetMethod("Import", new[] { typeof(FileInfo), typeof(FileInfo), typeof(CaxImportOptions) });
                return m?.Invoke(caxProvider,
                    m != null && m.GetParameters().Length == 2
                        ? new object[] { file, options }
                        : new object[] { file, null!, options });
            }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        /// <summary>
        /// 序列化 CaxProvider 的 TransferResult 为 JSON 字符串。
        /// operation: "export" 或 "import"；filePath: 关联的文件路径；
        /// importOptions: 导入模式（仅 import 有值）；optionsParsed: 导入选项是否被成功解析（仅 import 有值）。
        /// V17 的 CaxProvider.Export/Import 返回 void，此时 result 为 null，视为成功。
        /// </summary>
        private static string SerializeTransferResult(object? result, string operation,
            string filePath, string? importOptions, bool? optionsParsed)
        {
            var messages = new List<object>();
            int errors = 0, warnings = 0;
            string stateText = "";
            if (result != null)
            {
                try
                {
                    var rt = result.GetType();
                    errors = Convert.ToInt32(rt.GetProperty("ErrorCount")?.GetValue(result) ?? 0);
                    warnings = Convert.ToInt32(rt.GetProperty("WarningCount")?.GetValue(result) ?? 0);
                    var msgs = rt.GetProperty("Messages")?.GetValue(result) as System.Collections.IEnumerable;
                    if (msgs != null)
                        foreach (var msg in msgs)
                            messages.Add(SerializeTransferResultMessage(msg));
                }
                catch { }
                try { stateText = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? ""; } catch { }
            }
            else
            {
                stateText = "Success";
            }

            bool success = string.Equals(stateText, "Success", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stateText, "Information", StringComparison.OrdinalIgnoreCase)
                || (result == null && File.Exists(filePath));

            var payload = new Dictionary<string, object?>
            {
                ["success"] = success,
                ["operation"] = operation,
                ["state"] = stateText,
                ["errorCount"] = errors,
                ["warningCount"] = warnings,
                ["filePath"] = filePath,
                ["messages"] = messages
            };
            if (importOptions != null)
            {
                payload["importOptions"] = importOptions;
                payload["optionsParsed"] = optionsParsed ?? true;
            }

            return JsonConvert.SerializeObject(payload);
        }

        /// <summary>递归序列化 TransferResultMessage（与 CompilerResultMessage 结构类似）。</summary>
        private static object SerializeTransferResultMessage(object msg)
        {
            var children = new List<object>();
            var stateText = "";
            var messageText = "";
            var dateTime = "";
            int errors = 0, warnings = 0;
            try
            {
                var mt = msg.GetType();
                stateText = mt.GetProperty("State")?.GetValue(msg)?.ToString() ?? "";
                messageText = FixComEncoding(mt.GetProperty("Message")?.GetValue(msg)?.ToString() ?? "");
                dateTime = mt.GetProperty("DateTime")?.GetValue(msg)?.ToString() ?? "";
                errors = Convert.ToInt32(mt.GetProperty("ErrorCount")?.GetValue(msg) ?? 0);
                warnings = Convert.ToInt32(mt.GetProperty("WarningCount")?.GetValue(msg) ?? 0);
                var childMsgs = mt.GetProperty("Messages")?.GetValue(msg) as System.Collections.IEnumerable;
                if (childMsgs != null)
                    foreach (var child in childMsgs)
                        children.Add(SerializeTransferResultMessage(child));
            }
            catch { }

            return new
            {
                message = messageText,
                state = stateText,
                dateTime,
                errorCount = errors,
                warningCount = warnings,
                messages = children
            };
        }

        /// <summary>
        /// 解析 CaxImportOptions 字符串（不区分大小写）。
        /// 支持 "MoveToParkingLot"/"parking_lot"、"OverwriteTiaDevice"/"overwrite"、"RetainTiaDevice"/"retain"。
        /// 未识别或为空时返回 OverwriteTiaDevice 并设置 fallbackToDefault=true。
        /// </summary>
        private static CaxImportOptions ParseCaxImportOptions(string? input, out bool fallbackToDefault)
        {
            fallbackToDefault = false;
            if (string.IsNullOrWhiteSpace(input))
            {
                fallbackToDefault = true;
                return CaxImportOptions.OverwriteTiaDevice;
            }

            var s = input.Trim().ToLowerInvariant();
            if (s == "movetoparkinglot" || s == "parking_lot" || s == "parkinglot" || s == "parking")
                return CaxImportOptions.MoveToParkingLot;
            if (s == "overwritetiadevice" || s == "overwrite" || s == "over")
                return CaxImportOptions.OverwriteTiaDevice;
            if (s == "retaintiadevice" || s == "retain" || s == "keep")
                return CaxImportOptions.RetainTiaDevice;

            // 尝试 Enum.Parse 兜底
            if (Enum.TryParse<CaxImportOptions>(input, true, out var parsed))
                return parsed;

            fallbackToDefault = true;
            return CaxImportOptions.OverwriteTiaDevice;
        }

        private PlcSoftware GetPlcSoftwareByName(string plcName)
        {
            RequireProject();
            var plc = GetPlcSoftwareList()
                .FirstOrDefault(p => p.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase));
            if (plc == null) throw new InvalidOperationException($"未找到 PLC: {plcName}");
            return plc;
        }

        private Device? FindDeviceForPlc(PlcSoftware plc)
        {
            try
            {
                // ★递归遍历所有设备（含设备组）
                foreach (var device in GetAllDevices())
                {
                    foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                    {
                        try
                        {
                            var swContainer = di.GetService<SoftwareContainer>();
                            // ★COM RCW 引用比对不可靠（每次 GetService 返回不同 RCW 实例），
                            //   改用名称比对（与 ProjectService.FindDeviceForPlcWithGroup 一致）
                            if (swContainer?.Software is PlcSoftware p
                                && (p == plc || string.Equals(p.Name, plc.Name, StringComparison.OrdinalIgnoreCase)))
                                return device;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private DeviceItem? FindDeviceItemAcrossProject(string name)
        {
            // ★递归遍历所有设备（含设备组）
            foreach (var device in GetAllDevices())
            {
                var found = FindDeviceItemInDevice(device.DeviceItems, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// 设备级 XML 导出（反射调用 Export，遇到"不一致"异常时先编译再重试）。
        /// 返回值：true=方法存在并已调用；false=Device 不支持 Export 方法。
        /// 如果方法存在但抛异常（非 Inconsistent），异常向上传播。
        /// </summary>
        private bool ExportDeviceWithRetry(Device device, string filePath)
        {
            var method = FindExportMethod(device);
            if (method == null) return false;

            try
            {
                method.Invoke(device, new object[] { new FileInfo(filePath), ExportOptions.WithDefaults });
                return true;
            }
            catch (TargetInvocationException tex)
            {
                var inner = tex.InnerException ?? tex;
                if (inner.Message.Contains("Inconsistent") || inner.Message.Contains("cannot be exported"))
                {
                    // 设备不一致时，先编译再重试
                    CompileDevice(device);
                    method.Invoke(device, new object[] { new FileInfo(filePath), ExportOptions.WithDefaults });
                    return true;
                }
                throw inner;
            }
        }

        /// <summary>
        /// DeviceItem 级别导出（设备级导出失败时的回退方案）。
        /// 遍历所有 DeviceItem，通过反射逐个调用 Export。
        /// </summary>
        private (int exportedCount, int errorCount) ExportDeviceItemsLevel(Device device, string outputDir)
        {
            int exported = 0, errors = 0;
            foreach (var item in EnumerateDeviceItems(device.DeviceItems))
            {
                var method = FindExportMethod(item);
                if (method == null) { errors++; continue; }

                try
                {
                    var rawName = string.IsNullOrEmpty(item.Name) ? $"item_{exported}" : item.Name;
                    var itemPath = Path.Combine(outputDir, SanitizeFileName(rawName) + ".xml");
                    method.Invoke(item, new object[] { new FileInfo(itemPath), ExportOptions.WithDefaults });
                    exported++;
                }
                catch
                {
                    errors++;
                }
            }
            return (exported, errors);
        }

        /// <summary>
        /// 通过反射查找对象的 Export(FileInfo, ExportOptions) 方法。
        /// Device/DeviceItem 在编译时不可见 Export 方法，但运行时 COM 对象可能支持。
        /// </summary>
        private static MethodInfo? FindExportMethod(object obj)
        {
            return obj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Export"
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[0].ParameterType == typeof(FileInfo)
                    && m.GetParameters()[1].ParameterType == typeof(ExportOptions));
        }

        /// <summary>
        /// 编译设备（优先设备级编译服务，回退到 PLC 块组编译）。
        /// </summary>
        private void CompileDevice(Device device)
        {
            try
            {
                var deviceCompilable = device.GetService<ICompilable>();
                if (deviceCompilable != null)
                {
                    deviceCompilable.Compile();
                    return;
                }
            }
            catch { }

            // 回退：找到设备下的 PLC 软件并编译块组
            foreach (var di in EnumerateDeviceItems(device.DeviceItems))
            {
                try
                {
                    var swContainer = di.GetService<SoftwareContainer>();
                    if (swContainer?.Software is PlcSoftware plc)
                        plc.BlockGroup.GetService<ICompilable>()?.Compile();
                }
                catch { }
            }
        }

        /// <summary>
        /// 手动生成硬件配置 XML（当 Device/DeviceItem 不支持 Export API 时的最终回退方案）。
        /// 遍历所有 DeviceItem，收集属性生成结构化 XML。
        /// </summary>
        private void GenerateHardwareXml(Device device, string filePath)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                BuildDeviceElement(device)
            );
            doc.Save(filePath);
        }

        private XElement BuildDeviceElement(Device device)
        {
            var element = new XElement("Device",
                new XAttribute("name", device.Name ?? ""),
                new XAttribute("typeIdentifier", SafeTypeIdentifier(device))
            );

            foreach (var item in device.DeviceItems)
                element.Add(BuildDeviceItemElement(item));

            return element;
        }

        private XElement BuildDeviceItemElement(DeviceItem item)
        {
            var element = new XElement("DeviceItem",
                new XAttribute("name", item.Name ?? ""),
                new XAttribute("typeIdentifier", SafeGetDeviceItemString(item, "TypeIdentifier")),
                new XAttribute("orderNumber", SafeGetDeviceItemString(item, "OrderNumber")),
                new XAttribute("firmwareVersion", SafeGetDeviceItemString(item, "FirmwareVersion")),
                new XAttribute("positionNumber", SafeGetDeviceItemInt(item, "PositionNumber").ToString())
            );

            // IO 地址
            var addresses = ReadIoAddresses(item);
            if (addresses.Count > 0)
            {
                var addrElement = new XElement("IoAddresses");
                foreach (var addr in addresses)
                {
                    addrElement.Add(new XElement("Address",
                        new XAttribute("type", addr.Type),
                        new XAttribute("startByte", addr.StartByte),
                        new XAttribute("length", addr.Length)
                    ));
                }
                element.Add(addrElement);
            }

            // 子项递归
            if (item.DeviceItems != null)
            {
                foreach (var child in item.DeviceItems)
                    element.Add(BuildDeviceItemElement(child));
            }

            return element;
        }

        /// <summary>
        /// 为指定设备导出 IO 映射 JSON（复用 ReadIoAddresses + MatchTagsForModule 逻辑）。
        /// 文件名为 {baseName}_iomap.json，放在 outputDir 中。
        /// </summary>
        private string? ExportIoMappingForDevice(PlcSoftware plc, Device device, string outputDir, string baseName)
        {
            try
            {
                var allTags = GetAllTags(plc);
                var modules = new List<object>();
                foreach (var item in EnumerateDeviceItems(device.DeviceItems))
                {
                    var addresses = ReadIoAddresses(item);
                    var matchedTags = MatchTagsForModule(allTags, addresses);
                    modules.Add(new
                    {
                        moduleName = item.Name,
                        itemType = SafeGetDeviceItemString(item, "TypeIdentifier"),
                        orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                        addresses,
                        tags = matchedTags
                    });
                }

                var payload = new
                {
                    plcName = plc.Name,
                    deviceName = device.Name,
                    moduleCount = modules.Count,
                    modules
                };
                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                var ioMapPath = Path.Combine(outputDir, baseName + "_iomap.json");
                File.WriteAllText(ioMapPath, json);
                return ioMapPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// 收集设备的网络配置信息（PROFINET/PROFIBUS 接口及连接的 IO 设备）。
        /// </summary>
        private object? CollectNetworkConfig(Device device)
        {
            try
            {
                var interfaces = new List<object>();
                foreach (var item in EnumerateDeviceItems(device.DeviceItems))
                {
                    var typeId = SafeGetDeviceItemString(item, "TypeIdentifier").ToLowerInvariant();
                    if (IsIoInterface(typeId))
                    {
                        var connected = TryGetConnectedDeviceNames(item);
                        interfaces.Add(new
                        {
                            name = item.Name,
                            type = typeId,
                            orderNumber = SafeGetDeviceItemString(item, "OrderNumber"),
                            firmwareVersion = SafeGetDeviceItemString(item, "FirmwareVersion"),
                            connectedDeviceCount = connected.Count,
                            connectedDevices = connected
                        });
                    }
                }
                return new { interfaceCount = interfaces.Count, interfaces };
            }
            catch { return null; }
        }

        /// <summary>将字符串中的非法文件名字符替换为下划线。</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        private static string SafeGetDeviceItemString(DeviceItem item, string attrName)
        {
            try
            {
                var prop = typeof(DeviceItem).GetProperty(attrName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                    return prop.GetValue(item)?.ToString() ?? "";

                if (item is IEngineeringObject eo)
                    return eo.GetAttribute(attrName)?.ToString() ?? "";
            }
            catch { }
            return "";
        }

        private static int SafeGetDeviceItemInt(DeviceItem item, string attrName)
        {
            try
            {
                var prop = typeof(DeviceItem).GetProperty(attrName, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(item);
                    if (v is int i) return i;
                    if (v != null && int.TryParse(v.ToString(), out var parsed)) return parsed;
                }

                if (item is IEngineeringObject eo)
                {
                    var v = eo.GetAttribute(attrName);
                    if (v is int i2) return i2;
                    if (v != null && int.TryParse(v.ToString(), out var parsed2)) return parsed2;
                }
            }
            catch { }
            return -1;
        }

        private static List<AddressInfo> ReadIoAddresses(DeviceItem item)
        {
            var result = new List<AddressInfo>();
            try
            {
                if (item is IEngineeringObject eo)
                {
                    var attrNames = new[] { "IoAddresses", "Addresses", "InputAddress", "OutputAddress" };
                    foreach (var attrName in attrNames)
                    {
                        try
                        {
                            var value = eo.GetAttribute(attrName);
                            if (value == null) continue;
                            result.AddRange(ParseAddressValue(value, attrName));
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (result.Count == 0)
            {
                result.AddRange(TryGetAddressProperty(item, "IoAddresses"));
                result.AddRange(TryGetAddressProperty(item, "Addresses"));
                result.AddRange(TryGetAddressProperty(item, "InputAddress", "Input"));
                result.AddRange(TryGetAddressProperty(item, "OutputAddress", "Output"));
            }

            return result;
        }

        private static List<AddressInfo> TryGetAddressProperty(DeviceItem item, string propName, string? defaultType = null)
        {
            var result = new List<AddressInfo>();
            try
            {
                var prop = typeof(DeviceItem).GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return result;
                var value = prop.GetValue(item);
                if (value == null) return result;
                result.AddRange(ParseAddressValue(value, defaultType ?? propName));
            }
            catch { }
            return result;
        }

        private static List<AddressInfo> ParseAddressValue(object value, string context)
        {
            var result = new List<AddressInfo>();
            if (value == null) return result;

            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                if (TryParseAddressString(s, out var info) && info != null)
                    result.Add(info);
                return result;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var element in enumerable)
                {
                    if (element == null) continue;
                    result.AddRange(ParseAddressObject(element, context));
                }
                return result;
            }

            result.AddRange(ParseAddressObject(value, context));
            return result;
        }

        private static List<AddressInfo> ParseAddressObject(object obj, string context)
        {
            var result = new List<AddressInfo>();
            try
            {
                var type = InferAddressType(obj, context);
                int? start = null;
                int? length = null;

                var startProp = obj.GetType().GetProperty("StartByte", BindingFlags.Public | BindingFlags.Instance)
                    ?? obj.GetType().GetProperty("StartAddress", BindingFlags.Public | BindingFlags.Instance)
                    ?? obj.GetType().GetProperty("Address", BindingFlags.Public | BindingFlags.Instance);
                if (startProp != null && startProp.GetValue(obj) is { } sv)
                {
                    if (sv is int si) start = si;
                    else if (int.TryParse(sv.ToString(), out var sip)) start = sip;
                }

                var lengthProp = obj.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance)
                    ?? obj.GetType().GetProperty("Size", BindingFlags.Public | BindingFlags.Instance);
                if (lengthProp != null && lengthProp.GetValue(obj) is { } lv)
                {
                    if (lv is int li) length = li;
                    else if (int.TryParse(lv.ToString(), out var lip)) length = lip;
                }

                if (start.HasValue && length.HasValue)
                {
                    result.Add(new AddressInfo { Type = type, StartByte = start.Value, Length = length.Value });
                    return result;
                }

                var text = obj.ToString();
                if (!string.IsNullOrWhiteSpace(text) && TryParseAddressString(text, out var info) && info != null)
                {
                    info.Type = type;
                    result.Add(info);
                }
            }
            catch { }
            return result;
        }

        private static string InferAddressType(object obj, string context)
        {
            var ctx = context.ToLowerInvariant();
            if (ctx.Contains("input") || ctx.Contains("iaddress")) return "Input";
            if (ctx.Contains("output") || ctx.Contains("oaddress")) return "Output";

            try
            {
                var typeProp = obj.GetType().GetProperty("AddressType", BindingFlags.Public | BindingFlags.Instance)
                    ?? obj.GetType().GetProperty("IoType", BindingFlags.Public | BindingFlags.Instance)
                    ?? obj.GetType().GetProperty("Type", BindingFlags.Public | BindingFlags.Instance);
                if (typeProp != null && typeProp.GetValue(obj)?.ToString() is { } ts)
                {
                    var t = ts.ToLowerInvariant();
                    if (t.Contains("input")) return "Input";
                    if (t.Contains("output")) return "Output";
                }
            }
            catch { }
            return "Unknown";
        }

        private static bool TryParseAddressString(string? text, out AddressInfo? info)
        {
            info = null;
            if (text == null) return false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim().TrimStart('%').Trim();
            if (string.IsNullOrEmpty(s)) return false;

            char ioType = char.ToUpperInvariant(s[0]);
            if (ioType != 'I' && ioType != 'Q') return false;

            string type = ioType == 'I' ? "Input" : "Output";

            var rest = s.Substring(1);
            if (string.IsNullOrEmpty(rest)) return false;

            int byteSize = 1;
            if (char.ToUpperInvariant(rest[0]) == 'B') { byteSize = 1; rest = rest.Substring(1); }
            else if (char.ToUpperInvariant(rest[0]) == 'W') { byteSize = 2; rest = rest.Substring(1); }
            else if (char.ToUpperInvariant(rest[0]) == 'D') { byteSize = 4; rest = rest.Substring(1); }

            if (!int.TryParse(rest.Split('.')[0], out var byteAddr)) return false;

            info = new AddressInfo { Type = type, StartByte = byteAddr, Length = byteSize };
            return true;
        }

        private static List<PlcTag> GetAllTags(PlcSoftware plc)
        {
            var result = new List<PlcTag>();
            try
            {
                foreach (var table in GetAllTagTables(plc.TagTableGroup))
                {
                    if (table.Tags != null)
                        result.AddRange(table.Tags);
                }
            }
            catch { }
            return result;
        }

        private static List<object> MatchTagsForModule(List<PlcTag> allTags, List<AddressInfo> ranges)
        {
            var result = new List<object>();
            if (ranges.Count == 0) return result;

            foreach (var tag in allTags)
            {
                var addr = tag.LogicalAddress;
                if (string.IsNullOrWhiteSpace(addr)) continue;
                var trimmed = addr.TrimStart('%').Trim();
                if (!trimmed.StartsWith("I", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("Q", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryParseAddressString(trimmed, out var info) && info != null &&
                    ranges.Any(r => r.Type == info.Type && info.StartByte >= r.StartByte && info.StartByte < r.StartByte + r.Length))
                {
                    result.Add(new
                    {
                        name = tag.Name,
                        dataType = tag.DataTypeName,
                        address = "%" + tag.LogicalAddress
                    });
                }
            }
            return result;
        }

        private static bool IsIoInterface(string typeId)
        {
            // ★移除过短的 "pn"/"dp" 子串匹配（会误匹配 "input"、"adapter" 等无关 typeId），
            //   改用全词 "profinet"/"profibus" 匹配
            return typeId.Contains("profinet") || typeId.Contains("profibus") ||
                   typeId.Contains("ethernet") || typeId.Contains("io system");
        }

        private static List<string> TryGetConnectedDeviceNames(DeviceItem item)
        {
            var result = new List<string>();
            try
            {
                if (item is IEngineeringObject eo)
                {
                    foreach (var attrName in new[] { "ConnectedDevices", "IoDevices", "Nodes", "IoConnectors" })
                    {
                        try
                        {
                            var value = eo.GetAttribute(attrName);
                            if (value is IEnumerable enumerable and not string)
                            {
                                foreach (var element in enumerable)
                                {
                                    if (element == null) continue;
                                    var name = TryGetName(element);
                                    if (!string.IsNullOrEmpty(name) && !result.Contains(name))
                                        result.Add(name);
                                }
                            }
                        }
                        catch { }
                    }
                }

                var prop = typeof(DeviceItem).GetProperty("Nodes", BindingFlags.Public | BindingFlags.Instance)
                    ?? typeof(DeviceItem).GetProperty("IoConnectors", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.GetValue(item) is IEnumerable coll)
                {
                    foreach (var element in coll)
                    {
                        if (element == null) continue;
                        var name = TryGetName(element);
                        if (!string.IsNullOrEmpty(name) && !result.Contains(name))
                            result.Add(name);
                    }
                }
            }
            catch { }
            return result;
        }

        private static string? TryGetName(object obj)
        {
            try
            {
                var prop = obj.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj)?.ToString();
                if (obj is IEngineeringObject eo) return eo.GetAttribute("Name")?.ToString();
            }
            catch { }
            return null;
        }

        private class AddressInfo
        {
            public string Type { get; set; } = "Unknown";
            public int StartByte { get; set; }
            public int Length { get; set; }
        }
    }
}

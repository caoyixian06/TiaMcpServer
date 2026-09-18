using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.RuntimeScripting;
using Siemens.Engineering.Hmi.Faceplate;
using Siemens.Engineering.Hmi.Cycle;
using Siemens.Engineering.Hmi.Globalization;
using Siemens.Engineering.Hmi.TextGraphicList;

namespace TiaMcpServer
{
    /// <summary>
    /// 传统 HMI 扩展模块（VBScript/Faceplate/Cycle/Globalization/TextGraphicList）。
    /// <para>本文件为 partial class PortalService，与 HmiService.cs 共享状态与辅助方法。</para>
    /// <para>复用现有辅助：<see cref="RequireClassicHmi"/>、<see cref="Err"/>、<see cref="Ok"/>、
    /// <see cref="RequireProject"/>、<see cref="GetAllDevices"/>、<see cref="EnumerateDeviceItems"/>、
    /// <see cref="TryCreateViaReflection"/>、<see cref="UxGetProp"/>、<see cref="UxGetPropStr"/>、
    /// <see cref="UxListCollection"/>、<see cref="UxFindInCollection"/>、<see cref="UxDeleteObject"/>、
    /// <see cref="UxExploreProps"/>、<see cref="UxGetServiceOrProperty"/>、<see cref="TryDelete3"/>。</para>
    /// <para>所有 HMI 相关 API 大量使用反射，因为传统 HMI API 在不同版本差异较大，
    /// 且部分集合（CycleComposition/TextListComposition 等）仅有 Import/Find 方法，无 Create(name)，
    /// 需采用“导出模板→改名称→导入”的方式创建。</para>
    /// </summary>
    public partial class PortalService
    {
        // ═════════════════════════════════════════════════════════════════════════════
        // 通用反射辅助（Hx 前缀，Hmi Extended Classic）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 定位传统 HMI（HmiTarget）。hmiDeviceName 为空时取第一个找到的传统 HMI。
        /// 返回 (hmi, err)；err 非空表示未找到。
        /// </summary>
        private (HmiTarget? hmi, string err) HxRequireClassicHmi(string? hmiDeviceName)
        {
            HmiTarget? hmiTarget = null;
            if (!string.IsNullOrEmpty(hmiDeviceName))
            {
                foreach (var device in GetAllDevices())
                {
                    if (!device.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                    {
                        try
                        {
                            var swContainer = di.GetService<SoftwareContainer>();
                            if (swContainer?.Software is HmiTarget t) { hmiTarget = t; break; }
                        }
                        catch { }
                    }
                    if (hmiTarget != null) break;
                }
                // fix#18 兜底: 设备树名(HMI_1)匹配失败时，再按 HmiTarget.Name（站内软件名，如 HMI_RT_1）匹配
                if (hmiTarget == null)
                    hmiTarget = FindHmiTargetBySoftwareName(hmiDeviceName);
            }
            else
            {
                hmiTarget = GetClassicHmi();
            }

            if (hmiTarget == null)
            {
                return (null, "未找到传统 HMI 设备，此工具仅支持经典 HMI（WinCC Advanced/Comfort）" +
                    (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));
            }
            return (hmiTarget, "");
        }

        /// <summary>fix#18: 按 HmiTarget.Name（站内软件名，如 HMI_RT_1）遍历全部设备查找传统 HMI。
        /// 设备树名(device.Name) 与软件名(HmiTarget.Name) 不一致时提供双端匹配。</summary>
        private HmiTarget? FindHmiTargetBySoftwareName(string softwareName)
        {
            foreach (var device in GetAllDevices())
                foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                {
                    try
                    {
                        if (di.GetService<SoftwareContainer>()?.Software is HmiTarget t &&
                            string.Equals(t.Name, softwareName, StringComparison.OrdinalIgnoreCase))
                            return t;
                    }
                    catch { }
                }
            return null;
        }

        /// <summary>
        /// 在集合上反射尝试多种 Create 签名创建对象；若全部失败再尝试“导出模板→改名称→导入”。
        /// </summary>
        /// <param name="collection">集合对象（如 Cycles/TextLists/GraphicLists/MultilingualGraphics）</param>
        /// <param name="name">要创建的对象名称</param>
        /// <param name="debugInfo">用于错误信息的上下文描述</param>
        /// <returns>创建的对象；失败抛异常</returns>
        private object HxCreateWithImportFallback(object collection, string name, string debugInfo)
        {
            // 1) 先尝试 Create(name) / Create(string, string) / CreateFrom(name) / Create()
            try
            {
                return TryCreateViaReflection(collection, name, debugInfo);
            }
            catch
            {
                // 继续走导入路径
            }

            // 2) 尝试“导出模板→改名称→导入”
            var collType = collection.GetType();
            var importMethod = collType.GetMethod("Import",
                new[] { typeof(FileInfo), typeof(ImportOptions) });
            if (importMethod == null)
                throw new Exception($"集合 {debugInfo} 既无 Create(name) 也无 Import(FileInfo, ImportOptions) 方法");

            // 在现有集合中找一个对象作为模板导出
            object? templateObj = null;
            if (collection is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    templateObj = item;
                    break;
                }
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hx_{Guid.NewGuid():N}.xml");
            try
            {
                if (templateObj != null)
                {
                    // 用现有对象作模板
                    var exportMethod = templateObj.GetType().GetMethod("Export",
                        new[] { typeof(FileInfo), typeof(ExportOptions) });
                    try
                    {
                        exportMethod?.Invoke(templateObj, new object[] { new FileInfo(tempFile), ExportOptions.WithDefaults });
                    }
                    catch (Exception expEx)
                    {
                        var expInner = expEx is System.Reflection.TargetInvocationException tie ? tie.InnerException : expEx;
                        throw new Exception($"模板导出失败: {expInner?.Message}");
                    }

                    var xml = File.ReadAllText(tempFile, Encoding.UTF8);
                    var newXml = HxRenameInExportedXml(xml, name);
                    File.WriteAllText(tempFile, newXml, new UTF8Encoding(true));
                }
                else
                {
                    // 无模板，直接返回明确错误
                    throw new Exception(
                        $"集合 {debugInfo} 无 Create(name) 方法，且当前无现有对象可作模板导出。" +
                        $"请先在博途 GUI 手动创建一个 '{name}'，或先用其它方式建立模板对象。");
                }

                try
                {
                    var imported = importMethod.Invoke(collection,
                        new object[] { new FileInfo(tempFile), ImportOptions.Override });
                    var first = (imported as IEnumerable)?.Cast<object>().FirstOrDefault();
                    return first ?? throw new Exception($"导入返回空集合（{debugInfo}）");
                }
                catch (Exception impEx)
                {
                    var impInner = impEx is System.Reflection.TargetInvocationException tie2 ? tie2.InnerException : impEx;
                    throw new Exception($"模板导入失败: {impInner?.Message}");
                }
            }
            finally
            {
                TryDelete3(tempFile);
            }
        }

        /// <summary>
        /// 导出文本列表 XML 到指定文件（学习类型与条目结构用）。
        /// </summary>
        public string ExportHmiTextListFull(string name, string outputPath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    if (string.IsNullOrWhiteSpace(outputPath)) return Err("参数 outputPath 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var target = hmi.TextLists.Find(name);
                    if (target == null) return Err($"未找到文本列表: {name}");

                    var exportMethod = target.GetType().GetMethod("Export",
                        new[] { typeof(FileInfo), typeof(ExportOptions) });
                    if (exportMethod == null) return Err("文本列表对象未暴露 Export(FileInfo, ExportOptions) 方法");
                    try
                    {
                        exportMethod.Invoke(target, new object[] { new FileInfo(outputPath), ExportOptions.WithDefaults });
                    }
                    catch (Exception iex)
                    {
                        var inner = iex is System.Reflection.TargetInvocationException tie ? tie.InnerException : iex;
                        return Err($"导出失败: {inner?.Message}");
                    }
                    return Ok($"已导出文本列表 {name} 到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 从 XML 文件导入文本列表（覆盖同名）。
        /// </summary>
        public string ImportHmiTextListFull(string filePath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(filePath)) return Err("参数 filePath 不能为空");
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var imported = hmi.TextLists.Import(new FileInfo(filePath), ImportOptions.Override);
                    var first = (imported as IEnumerable)?.Cast<object>().FirstOrDefault();
                    return Ok(first != null ? $"已导入文本列表: {UxGetPropStr(first, "Name")}" : "文本列表导入完成");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 在导出的 HMI 对象 XML 中替换名称节点为 newName。
        /// </summary>
        private static string HxRenameInExportedXml(string xml, string newName)
        {
            try
            {
                var doc = new XmlDocument();
                doc.PreserveWhitespace = true;
                doc.LoadXml(xml);

                var escName = System.Security.SecurityElement.Escape(newName);

                // 通用替换：查找 <Name> 节点
                var nameNodes = doc.SelectNodes("//*[local-name()='Name']");
                if (nameNodes != null && nameNodes.Count > 0)
                {
                    // 通常第一个 Name 节点是对象自身的名称
                    nameNodes[0]!.InnerText = escName;
                }

                // 同时替换 Number 为 5000（与 HmiService 保持一致，避免与现有对象冲突）
                var numberNodes = doc.SelectNodes("//*[local-name()='Number']");
                if (numberNodes != null && numberNodes.Count > 0)
                {
                    numberNodes[0]!.InnerText = "5000";
                }

                // ★修复★ 必须返回 OuterXml（无 XML 声明），由调用方以 UTF-8+BOM 写盘。
                // 之前用 XmlWriter+StringWriter 会产生与文件实际编码不一致的声明，
                // TIA 读取时报"没有 Unicode 字节顺序标记"。
                return doc.OuterXml;
            }
            catch
            {
                // XML 解析失败，原样返回
                return xml;
            }
        }

        /// <summary>
        /// 反射调用对象的 Export(FileInfo, ExportOptions) 方法导出到临时文件并返回内容。
        /// </summary>
        private string? HxExportToTempString(object obj)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hx_exp_{Guid.NewGuid():N}.xml");
            try
            {
                var exportMethod = obj.GetType().GetMethod("Export",
                    new[] { typeof(FileInfo), typeof(ExportOptions) });
                if (exportMethod == null) return null;
                exportMethod.Invoke(obj, new object[] { new FileInfo(tempFile), ExportOptions.WithDefaults });
                if (!File.Exists(tempFile)) return null;
                return File.ReadAllText(tempFile, Encoding.UTF8);
            }
            catch { return null; }
            finally { TryDelete3(tempFile); }
        }

        /// <summary>
        /// 递归收集 VBScript 文件夹树下的所有脚本，返回 [{name, folder, typeName}]。
        /// </summary>
        private List<object> HxCollectVbScripts(object folder, string folderPath)
        {
            var result = new List<object>();
            try
            {
                // 当前文件夹下的脚本
                var scriptsProp = folder.GetType().GetProperty("VBScripts");
                var scripts = scriptsProp?.GetValue(folder) as IEnumerable;
                if (scripts != null)
                {
                    foreach (var s in scripts)
                    {
                        result.Add(new
                        {
                            name = UxGetPropStr(s, "Name") ?? "(无名)",
                            folder = folderPath,
                            typeName = s.GetType().Name
                        });
                    }
                }

                // 递归子文件夹
                var foldersProp = folder.GetType().GetProperty("Folders");
                var subFolders = foldersProp?.GetValue(folder) as IEnumerable;
                if (subFolders != null)
                {
                    foreach (var sf in subFolders)
                    {
                        var sfName = UxGetPropStr(sf, "Name") ?? "?";
                        var subPath = string.IsNullOrEmpty(folderPath) ? sfName : folderPath + "/" + sfName;
                        result.AddRange(HxCollectVbScripts(sf, subPath));
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 递归在 VBScript 文件夹树中查找名为 scriptName 的 VBScript 对象。
        /// </summary>
        private object? HxFindVbScriptRecursive(object folder, string scriptName)
        {
            try
            {
                var scriptsProp = folder.GetType().GetProperty("VBScripts");
                var scripts = scriptsProp?.GetValue(folder);
                var findM = scripts?.GetType().GetMethod("Find", new[] { typeof(string) });
                if (findM != null)
                {
                    var found = findM.Invoke(scripts, new object[] { scriptName });
                    if (found != null) return found;
                }
                // 也尝试手动遍历
                if (scripts is IEnumerable enumScripts)
                {
                    foreach (var s in enumScripts)
                    {
                        var n = UxGetPropStr(s, "Name");
                        if (n != null && n.Equals(scriptName, StringComparison.OrdinalIgnoreCase))
                            return s;
                    }
                }

                // 递归子文件夹
                var foldersProp = folder.GetType().GetProperty("Folders");
                var subFolders = foldersProp?.GetValue(folder) as IEnumerable;
                if (subFolders != null)
                {
                    foreach (var sf in subFolders)
                    {
                        var found = HxFindVbScriptRecursive(sf, scriptName);
                        if (found != null) return found;
                    }
                }
            }
            catch { }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 1. VBScript 脚本管理（Siemens.Engineering.Hmi.RuntimeScripting）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>1.1 列出 HMI 中所有 VBScript（递归遍历 VBScriptFolder 子文件夹）。</summary>
        public string ListHmiVbScripts(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var list = HxCollectVbScripts(rootFolder, "");
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        vbScripts = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.2 列出 VBScript 根文件夹下的子文件夹。</summary>
        public string ListVbScriptFolders(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var list = new List<object>();
                    var foldersProp = rootFolder.GetType().GetProperty("Folders");
                    var folders = foldersProp?.GetValue(rootFolder) as IEnumerable;
                    if (folders != null)
                    {
                        foreach (var f in folders)
                        {
                            list.Add(new
                            {
                                name = UxGetPropStr(f, "Name") ?? "(无名)",
                                typeName = f.GetType().Name
                            });
                        }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        folders = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.3 创建 VBScript 子文件夹（在 VBScriptFolder.Folders 中 Create(name)）。</summary>
        public string CreateVbScriptFolder(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var foldersProp = rootFolder.GetType().GetProperty("Folders");
                    var folders = foldersProp?.GetValue(rootFolder);
                    if (folders == null) return Err("VBScriptFolder.Folders 属性未找到");

                    // 同名检查
                    if (UxFindInCollection(folders, name) != null)
                        return Ok($"VBScript 文件夹已存在: {name}");

                    var createM = folders.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (createM == null)
                        return Err("VBScriptUserFolderComposition.Create(string) 方法未找到");
                    createM.Invoke(folders, new object[] { name });
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 VBScript 文件夹: {name}",
                        folderName = name,
                        hmiDeviceName = hmi.Name
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (TargetInvocationException tie)
                {
                    return Err($"创建 VBScript 文件夹失败: {tie.InnerException?.Message ?? tie.Message}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.4 删除 VBScript 子文件夹。</summary>
        public string DeleteVbScriptFolder(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var foldersProp = rootFolder.GetType().GetProperty("Folders");
                    var folders = foldersProp?.GetValue(rootFolder);
                    if (folders == null) return Err("VBScriptFolder.Folders 属性未找到");

                    var target = UxFindInCollection(folders, name);
                    if (target == null) return Err($"未找到 VBScript 文件夹: {name}");
                    UxDeleteObject(target);
                    return Ok($"已删除 VBScript 文件夹: {name}");
                }
                catch (TargetInvocationException tie)
                {
                    return Err($"删除 VBScript 文件夹失败: {tie.InnerException?.Message ?? tie.Message}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.5 读取 VBScript 内容（通过 Export 导出 XML 后提取内容）。</summary>
        public string ReadVbScriptContent(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var script = HxFindVbScriptRecursive(rootFolder, name);
                    if (script == null) return Err($"未找到 VBScript: {name}");

                    // 尝试直接读 Content / Source 属性
                    var content = UxGetPropStr(script, "Content")
                                ?? UxGetPropStr(script, "Source")
                                ?? UxGetPropStr(script, "Script");
                    string? exportXml = null;
                    if (string.IsNullOrEmpty(content))
                    {
                        // 退而求其次：导出 XML
                        exportXml = HxExportToTempString(script);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        name,
                        hmiDeviceName = hmi.Name,
                        content = content ?? "",
                        exportXml,  // 原始导出 XML（调试用）
                        note = string.IsNullOrEmpty(content) && string.IsNullOrEmpty(exportXml)
                            ? "无法读取 VBScript 内容（Openness API 未暴露 Content 属性，且 Export 失败）"
                            : null
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.6 创建 VBScript（在 VBScriptFolder.VBScripts 中创建）。
        /// VBScriptComposition 仅有 CreateFrom(libraryTypeVersion/masterCopy)，无 Create(name)，
        /// 故先尝试反射 Create(name)，失败则尝试导入模板。</summary>
        public string CreateVbScript(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var scriptsProp = rootFolder.GetType().GetProperty("VBScripts");
                    var scripts = scriptsProp?.GetValue(rootFolder);
                    if (scripts == null) return Err("VBScriptFolder.VBScripts 属性未找到");

                    // 同名检查
                    if (HxFindVbScriptRecursive(rootFolder, name) != null)
                        return Ok($"VBScript 已存在: {name}");

                    // 尝试 Create(name) / Create(string, string) / Create() 反射
                    object created;
                    try
                    {
                        created = HxCreateWithImportFallback(scripts, name, "VBScriptComposition");
                    }
                    catch (Exception cex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"无法通过 API 创建 VBScript: {cex.Message}",
                            name,
                            hmiDeviceName = hmi.Name,
                            note = "VBScriptComposition 仅有 CreateFrom(libraryTypeVersion/masterCopy)，" +
                                   "Openness API 不支持直接 Create(name)。请改用：1) 在博途 GUI 手动创建；" +
                                   "2) 从全局库/项目库导入 MasterCopy；3) 使用 XML Import（需先准备 .xml 文件）。"
                        }, Newtonsoft.Json.Formatting.Indented);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 VBScript: {UxGetPropStr(created, "Name") ?? name}",
                        name = UxGetPropStr(created, "Name") ?? name,
                        hmiDeviceName = hmi.Name
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>1.7 删除 VBScript（递归查找后 Delete）。</summary>
        public string DeleteVbScript(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var rootFolder = hmi.VBScriptFolder;
                    var script = HxFindVbScriptRecursive(rootFolder, name);
                    if (script == null) return Err($"未找到 VBScript: {name}");
                    UxDeleteObject(script);
                    return Ok($"已删除 VBScript: {name}");
                }
                catch (TargetInvocationException tie)
                {
                    return Err($"删除 VBScript 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 2. Faceplate 面板库（Siemens.Engineering.Hmi.Faceplate）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 反射获取 HmiTarget 上的 FaceplateLibraryTypes 集合。
        /// 注意：V19 Openness API 中 HmiTarget 可能未直接暴露此属性，返回 null 时上层会报错。
        /// </summary>
        private object? HxGetFaceplateLibraryTypes(HmiTarget hmi)
        {
            // 尝试直接属性
            var propNames = new[] { "FaceplateLibraryTypes", "FaceplateTypes", "Faceplates" };
            foreach (var pn in propNames)
            {
                try
                {
                    var val = hmi.GetType().GetProperty(pn)?.GetValue(hmi);
                    if (val != null) return val;
                }
                catch { }
            }
            // 尝试 GetService
            var svcTypeNames = new[]
            {
                "Siemens.Engineering.Hmi.Faceplate.FaceplateLibraryTypeComposition",
                "Siemens.Engineering.Hmi.Faceplate.FaceplateTypeComposition"
            };
            foreach (var tn in svcTypeNames)
            {
                var svc = UxGetService(hmi, tn);
                if (svc != null) return svc;
            }
            return null;
        }

        /// <summary>2.1 列出 HMI 面板库类型。</summary>
        public string ListHmiFaceplateTypes(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var coll = HxGetFaceplateLibraryTypes(hmi);
                    if (coll == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "无法获取 FaceplateLibraryTypes 集合（当前 Openness 版本可能不支持，或 HMI 类型不匹配）",
                            hmiDeviceName = hmi.Name,
                            exploredProperties = UxExploreProps(hmi)
                        }, Newtonsoft.Json.Formatting.Indented);

                    var list = new List<object>();
                    if (coll is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            list.Add(new
                            {
                                name = UxGetPropStr(item, "Name") ?? "(无名)",
                                author = UxGetPropStr(item, "Author"),
                                guid = UxGetPropStr(item, "Guid"),
                                namespaceName = UxGetPropStr(item, "Namespace"),
                                typeName = item.GetType().Name
                            });
                        }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        faceplateTypes = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>2.2 获取面板类型信息（属性详情）。</summary>
        public string GetFaceplateTypeInfo(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var coll = HxGetFaceplateLibraryTypes(hmi);
                    if (coll == null) return Err("无法获取 FaceplateLibraryTypes 集合");

                    var target = UxFindInCollection(coll, name);
                    if (target == null) return Err($"未找到面板类型: {name}");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        faceplateType = new
                        {
                            name = UxGetPropStr(target, "Name"),
                            author = UxGetPropStr(target, "Author"),
                            guid = UxGetPropStr(target, "Guid"),
                            namespaceName = UxGetPropStr(target, "Namespace"),
                            status = UxGetPropStr(target, "Status"),
                            minimumTargetDeviceVersion = UxGetPropStr(target, "MinimumTargetDeviceVersion"),
                            typeName = target.GetType().Name
                        },
                        allProperties = UxExploreProps(target)
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>2.3 获取面板类型版本列表。</summary>
        public string GetFaceplateTypeVersions(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var coll = HxGetFaceplateLibraryTypes(hmi);
                    if (coll == null) return Err("无法获取 FaceplateLibraryTypes 集合");

                    var target = UxFindInCollection(coll, name);
                    if (target == null) return Err($"未找到面板类型: {name}");

                    var versionsProp = target.GetType().GetProperty("Versions");
                    var versions = versionsProp?.GetValue(target);
                    var list = new List<object>();
                    if (versions is IEnumerable enumVer)
                    {
                        foreach (var v in enumVer)
                        {
                            list.Add(new
                            {
                                versionNumber = UxGetPropStr(v, "VersionNumber"),
                                author = UxGetPropStr(v, "Author"),
                                guid = UxGetPropStr(v, "Guid"),
                                isDefault = UxGetPropStr(v, "IsDefault"),
                                modifiedDate = UxGetPropStr(v, "ModifiedDate"),
                                state = UxGetPropStr(v, "State"),
                                typeName = v.GetType().Name
                            });
                        }
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        faceplateTypeName = name,
                        count = list.Count,
                        versions = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 3. Cycle 采集周期（Siemens.Engineering.Hmi.Cycle）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>3.1 列出 HMI 采集周期。</summary>
        public string ListHmiCycles(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var cycles = hmi.Cycles;
                    var list = new List<object>();
                    foreach (var c in cycles)
                    {
                        list.Add(new
                        {
                            name = c.Name,
                            isSystemObject = UxGetPropStr(c, "IsSystemObject"),
                            typeName = c.GetType().Name
                        });
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        cycles = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.2 创建采集周期。CycleComposition 仅有 Import/Find，无 Create(name)，故用模板导入方式。</summary>
        public string CreateHmiCycle(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var cycles = hmi.Cycles;

                    // 同名检查
                    if (cycles.Find(name) != null)
                        return Ok($"采集周期已存在: {name}");

                    object created;
                    try
                    {
                        created = HxCreateWithImportFallback(cycles, name, "CycleComposition");
                    }
                    catch (Exception cex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"无法创建采集周期: {cex.Message}",
                            name,
                            hmiDeviceName = hmi.Name,
                            note = "CycleComposition 仅有 Import(FileInfo, ImportOptions) 和 Find(string)，" +
                                   "无 Create(name)。需先有现有 Cycle 作模板导出，或从 XML 文件导入。"
                        }, Newtonsoft.Json.Formatting.Indented);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建采集周期: {UxGetPropStr(created, "Name") ?? name}",
                        name = UxGetPropStr(created, "Name") ?? name,
                        hmiDeviceName = hmi.Name
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>3.3 删除采集周期。</summary>
        public string DeleteHmiCycle(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var cycles = hmi.Cycles;
                    var target = cycles.Find(name);
                    if (target == null) return Err($"未找到采集周期: {name}");
                    target.Delete();
                    return Ok($"已删除采集周期: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 4. Globalization 多语言图形（Siemens.Engineering.Hmi.Globalization）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 反射获取 HmiTarget 上的 MultilingualGraphics 集合。
        /// 注意：V19 Openness API 中 HmiTarget 可能未直接暴露此属性，需用反射尝试。
        /// </summary>
        private object? HxGetMultilingualGraphics(HmiTarget hmi)
        {
            var propNames = new[] { "MultilingualGraphics", "MultiLingualGraphics", "MultilingualGraphic" };
            foreach (var pn in propNames)
            {
                try
                {
                    var val = hmi.GetType().GetProperty(pn)?.GetValue(hmi);
                    if (val != null) return val;
                }
                catch { }
            }
            var svcTypeNames = new[]
            {
                "Siemens.Engineering.Hmi.Globalization.MultiLingualGraphicComposition",
                "Siemens.Engineering.Hmi.Globalization.MultilingualGraphicComposition"
            };
            foreach (var tn in svcTypeNames)
            {
                var svc = UxGetService(hmi, tn);
                if (svc != null) return svc;
            }
            return null;
        }

        /// <summary>4.1 列出多语言图形。</summary>
        public string ListMultilingualGraphics(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var coll = HxGetMultilingualGraphics(hmi);
                    if (coll == null)
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "无法获取 MultilingualGraphics 集合（当前 Openness 版本可能不支持）",
                            hmiDeviceName = hmi.Name,
                            exploredProperties = UxExploreProps(hmi)
                        }, Newtonsoft.Json.Formatting.Indented);

                    var list = UxListCollection(coll as IEnumerable);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        multilingualGraphics = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.2 创建多语言图形。MultiLingualGraphicComposition 仅有 Import/Find，无 Create(name)。</summary>
        public string CreateMultilingualGraphic(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var coll = HxGetMultilingualGraphics(hmi);
                    if (coll == null) return Err("无法获取 MultilingualGraphics 集合");

                    // 同名检查
                    if (UxFindInCollection(coll, name) != null)
                        return Ok($"多语言图形已存在: {name}");

                    object created;
                    try
                    {
                        created = HxCreateWithImportFallback(coll, name, "MultiLingualGraphicComposition");
                    }
                    catch (Exception cex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"无法创建多语言图形: {cex.Message}",
                            name,
                            hmiDeviceName = hmi.Name,
                            note = "MultiLingualGraphicComposition 仅有 Import(FileInfo, ImportOptions) 和 Find(string)，" +
                                   "无 Create(name)。需先有现有对象作模板导出，或从 XML 文件导入。"
                        }, Newtonsoft.Json.Formatting.Indented);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建多语言图形: {UxGetPropStr(created, "Name") ?? name}",
                        name = UxGetPropStr(created, "Name") ?? name,
                        hmiDeviceName = hmi.Name
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>4.3 删除多语言图形。</summary>
        public string DeleteMultilingualGraphic(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var coll = HxGetMultilingualGraphics(hmi);
                    if (coll == null) return Err("无法获取 MultilingualGraphics 集合");

                    var target = UxFindInCollection(coll, name);
                    if (target == null) return Err($"未找到多语言图形: {name}");
                    UxDeleteObject(target);
                    return Ok($"已删除多语言图形: {name}");
                }
                catch (TargetInvocationException tie)
                {
                    return Err($"删除多语言图形失败: {tie.InnerException?.Message ?? tie.Message}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 5. TextGraphicList 文本图形列表（Siemens.Engineering.Hmi.TextGraphicList）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>5.1 列出文本列表（完整版：含条目数）。</summary>
        public string ListHmiTextListsFull(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var textLists = hmi.TextLists;
                    var list = new List<object>();
                    foreach (var tl in textLists)
                    {
                        // 尝试获取条目数
                        int? entryCount = null;
                        try
                        {
                            var entriesProp = tl.GetType().GetProperty("Entries")
                                        ?? tl.GetType().GetProperty("Items");
                            var entries = entriesProp?.GetValue(tl);
                            if (entries is IEnumerable enumEntries)
                            {
                                int cnt = 0;
                                foreach (var _ in enumEntries) cnt++;
                                entryCount = cnt;
                            }
                        }
                        catch { }

                        list.Add(new
                        {
                            name = tl.Name,
                            entryCount,
                            typeName = tl.GetType().Name
                        });
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        textLists = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.2 创建文本列表（完整版：支持 entries 数组参数）。
        /// TextListComposition 仅有 Import/Find，无 Create(name)，故用模板导入方式。</summary>
        /// <param name="name">文本列表名称</param>
        /// <param name="entriesJson">条目 JSON 数组，每项 {value, text}（可选）</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选）</param>
        public string CreateHmiTextListFull(string name, string? entriesJson = null, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var textLists = hmi.TextLists;

                    // 同名检查
                    if (textLists.Find(name) != null)
                        return Ok($"文本列表已存在: {name}");

                    object created;
                    try
                    {
                        created = HxCreateWithImportFallback(textLists, name, "TextListComposition");
                    }
                    catch (Exception cex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"无法创建文本列表: {cex.Message}",
                            name,
                            hmiDeviceName = hmi.Name,
                            note = "TextListComposition 仅有 Import(FileInfo, ImportOptions) 和 Find(string)，" +
                                   "无 Create(name)。需先有现有 TextList 作模板导出，或从 XML 文件导入。"
                        }, Newtonsoft.Json.Formatting.Indented);
                    }

                    // 尝试添加条目（entries）
                    var entriesAdded = 0;
                    var entriesErrors = new List<string>();
                    if (!string.IsNullOrWhiteSpace(entriesJson))
                    {
                        try
                        {
                            var entries = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(
                                entriesJson ?? "[]");
                            if (entries != null && entries.Count > 0)
                            {
                                // 尝试通过反射获取 Entries 集合并 Add
                                var entriesProp = created.GetType().GetProperty("Entries")
                                            ?? created.GetType().GetProperty("Items");
                                var entriesColl = entriesProp?.GetValue(created);
                                if (entriesColl != null)
                                {
                                    var addMethod = entriesColl.GetType().GetMethods()
                                        .FirstOrDefault(m => m.Name == "Create" || m.Name == "Add");
                                    if (addMethod != null)
                                    {
                                        foreach (var e in entries)
                                        {
                                            try
                                            {
                                                var value = e.TryGetValue("value", out var v) ? v : "";
                                                var text = e.TryGetValue("text", out var t) ? t : "";
                                                // 尝试 Create(text) 或 Add(value, text) 多种签名
                                                var pms = addMethod.GetParameters();
                                                if (pms.Length == 1)
                                                    addMethod.Invoke(entriesColl, new object[] { text });
                                                else if (pms.Length == 2)
                                                    addMethod.Invoke(entriesColl, new object[] { value, text });
                                                entriesAdded++;
                                            }
                                            catch (Exception ee)
                                            {
                                                entriesErrors.Add(ee.InnerException?.Message ?? ee.Message);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception parseEx)
                        {
                            entriesErrors.Add("解析 entriesJson 失败: " + parseEx.Message);
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建文本列表: {UxGetPropStr(created, "Name") ?? name}",
                        name = UxGetPropStr(created, "Name") ?? name,
                        hmiDeviceName = hmi.Name,
                        entriesAdded,
                        entriesErrors = entriesErrors.Count > 0 ? entriesErrors : null,
                        note = entriesAdded == 0 && !string.IsNullOrWhiteSpace(entriesJson)
                            ? "条目未能添加（Openness API 可能不支持 Entries 集合操作），请在博途 GUI 手动添加条目"
                            : null
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.3 删除文本列表。</summary>
        public string DeleteHmiTextListFull(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var textLists = hmi.TextLists;
                    var target = textLists.Find(name);
                    if (target == null) return Err($"未找到文本列表: {name}");
                    target.Delete();
                    return Ok($"已删除文本列表: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.4 列出图形列表（完整版：含条目数）。</summary>
        public string ListHmiGraphicListsFull(string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var graphicLists = hmi.GraphicLists;
                    var list = new List<object>();
                    foreach (var gl in graphicLists)
                    {
                        int? entryCount = null;
                        try
                        {
                            var entriesProp = gl.GetType().GetProperty("Entries")
                                        ?? gl.GetType().GetProperty("Items");
                            var entries = entriesProp?.GetValue(gl);
                            if (entries is IEnumerable enumEntries)
                            {
                                int cnt = 0;
                                foreach (var _ in enumEntries) cnt++;
                                entryCount = cnt;
                            }
                        }
                        catch { }

                        list.Add(new
                        {
                            name = gl.Name,
                            entryCount,
                            typeName = gl.GetType().Name
                        });
                    }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        count = list.Count,
                        graphicLists = list
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.5 创建图形列表（完整版：支持 entries 数组参数）。
        /// GraphicListComposition 仅有 Import/Find，无 Create(name)。</summary>
        /// <param name="name">图形列表名称</param>
        /// <param name="entriesJson">条目 JSON 数组，每项 {value, graphicName}（可选）</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选）</param>
        public string CreateHmiGraphicListFull(string name, string? entriesJson = null, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var graphicLists = hmi.GraphicLists;

                    // 同名检查
                    if (graphicLists.Find(name) != null)
                        return Ok($"图形列表已存在: {name}");

                    object created;
                    try
                    {
                        created = HxCreateWithImportFallback(graphicLists, name, "GraphicListComposition");
                    }
                    catch (Exception cex)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"无法创建图形列表: {cex.Message}",
                            name,
                            hmiDeviceName = hmi.Name,
                            note = "GraphicListComposition 仅有 Import(FileInfo, ImportOptions) 和 Find(string)，" +
                                   "无 Create(name)。需先有现有 GraphicList 作模板导出，或从 XML 文件导入。"
                        }, Newtonsoft.Json.Formatting.Indented);
                    }

                    // 尝试添加条目（entries）
                    var entriesAdded = 0;
                    var entriesErrors = new List<string>();
                    if (!string.IsNullOrWhiteSpace(entriesJson))
                    {
                        try
                        {
                            var entries = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(
                                entriesJson ?? "[]");
                            if (entries != null && entries.Count > 0)
                            {
                                var entriesProp = created.GetType().GetProperty("Entries")
                                            ?? created.GetType().GetProperty("Items");
                                var entriesColl = entriesProp?.GetValue(created);
                                if (entriesColl != null)
                                {
                                    var addMethod = entriesColl.GetType().GetMethods()
                                        .FirstOrDefault(m => m.Name == "Create" || m.Name == "Add");
                                    if (addMethod != null)
                                    {
                                        foreach (var e in entries)
                                        {
                                            try
                                            {
                                                var value = e.TryGetValue("value", out var v) ? v : "";
                                                var graphicName = e.TryGetValue("graphicName", out var g) ? g
                                                            : (e.TryGetValue("text", out var t) ? t : "");
                                                var pms = addMethod.GetParameters();
                                                if (pms.Length == 1)
                                                    addMethod.Invoke(entriesColl, new object[] { graphicName });
                                                else if (pms.Length == 2)
                                                    addMethod.Invoke(entriesColl, new object[] { value, graphicName });
                                                entriesAdded++;
                                            }
                                            catch (Exception ee)
                                            {
                                                entriesErrors.Add(ee.InnerException?.Message ?? ee.Message);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception parseEx)
                        {
                            entriesErrors.Add("解析 entriesJson 失败: " + parseEx.Message);
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建图形列表: {UxGetPropStr(created, "Name") ?? name}",
                        name = UxGetPropStr(created, "Name") ?? name,
                        hmiDeviceName = hmi.Name,
                        entriesAdded,
                        entriesErrors = entriesErrors.Count > 0 ? entriesErrors : null,
                        note = entriesAdded == 0 && !string.IsNullOrWhiteSpace(entriesJson)
                            ? "条目未能添加（Openness API 可能不支持 Entries 集合操作），请在博途 GUI 手动添加条目"
                            : null
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.6 删除图形列表。</summary>
        public string DeleteHmiGraphicListFull(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var graphicLists = hmi.GraphicLists;
                    var target = graphicLists.Find(name);
                    if (target == null) return Err($"未找到图形列表: {name}");
                    target.Delete();
                    return Ok($"已删除图形列表: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>5.7 获取文本列表条目（反射访问 Entries/Items 集合）。</summary>
        public string GetHmiTextListEntries(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return Err("参数 name 不能为空");
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);

                    var textLists = hmi.TextLists;
                    var target = textLists.Find(name);
                    if (target == null) return Err($"未找到文本列表: {name}");

                    // 反射获取条目集合
                    var entriesProp = target.GetType().GetProperty("Entries")
                                ?? target.GetType().GetProperty("Items");
                    var entries = entriesProp?.GetValue(target);

                    var list = new List<object>();
                    if (entries is IEnumerable enumEntries)
                    {
                        foreach (var e in enumEntries)
                        {
                            var et = e.GetType();
                            list.Add(new
                            {
                                value = UxGetPropStr(e, "Value"),
                                text = UxGetPropStr(e, "Text"),
                                name = UxGetPropStr(e, "Name"),
                                typeName = et.Name
                            });
                        }
                    }

                    // ★学习增强★ Openness API 不暴露 Entries 时，改用"导出 XML 解析"：
                    // 可读取 ValueListMode（Numeric/Text/Bit）与全部条目（Index/Value + ItemText）。
                    string? valueListMode = null;
                    if (list.Count == 0)
                    {
                        var xml = HxExportToTempString(target);
                        if (!string.IsNullOrEmpty(xml))
                        {
                            try
                            {
                                var xdoc = System.Xml.Linq.XDocument.Parse(xml);
                                var ns = xdoc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                                var modeElem = xdoc.Descendants(ns + "ValueListMode").FirstOrDefault();
                                valueListMode = modeElem?.Value?.Trim();
                                foreach (var item in xdoc.Descendants(ns + "TextListItem"))
                                {
                                    var itemAttr = item.Element(ns + "AttributeList");
                                    var idx = itemAttr?.Element(ns + "Index")?.Value?.Trim();
                                    var val = itemAttr?.Element(ns + "Value")?.Value?.Trim();
                                    var itemText = item.Descendants(ns + "Text")
                                        .Select(t => t.Value).FirstOrDefault()?.Trim();
                                    list.Add(new
                                    {
                                        value = val ?? idx ?? "",
                                        text = string.IsNullOrEmpty(itemText) ? "" : itemText,
                                        name = "",
                                        typeName = "TextListItem"
                                    });
                                }
                            }
                            catch { /* 导出解析失败忽略 */ }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiDeviceName = hmi.Name,
                        textListName = name,
                        valueListMode = valueListMode ?? (list.Count > 0 ? "" : "unknown"),
                        count = list.Count,
                        entries = list,
                        note = list.Count == 0
                            ? "未获取到条目（Openness API 可能未暴露 Entries 集合，建议在博途 GUI 查看）"
                            : null
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═══         // ═══ 通用 HMI 对象导航与创建（2026-08-30 新增）═══

        /// <summary>按 '/' 分段解析对象路径：每段依次尝试 GetComposition(段) → Find(段) → 属性(段)。'=名称' 等价于 Find(名称)。</summary>
        private object? HxResolveHmiPath(HmiTarget hmi, string path)
        {
            object? cur = hmi;
            foreach (var rawSeg in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (cur == null) return null;
                var seg = rawSeg.StartsWith("=") ? rawSeg.Substring(1) : rawSeg;
                try
                {
                    var gco = cur.GetType().GetMethods().FirstOrDefault(m =>
                        m.Name == "GetComposition" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));
                    var next = gco?.Invoke(cur, new object[] { seg });
                    if (next != null) { cur = next; continue; }
                }
                catch { }
                try
                {
                    var find = cur.GetType().GetMethods().FirstOrDefault(m =>
                        m.Name == "Find" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(string));
                    var next = find?.Invoke(cur, new object[] { seg });
                    if (next != null) { cur = next; continue; }
                }
                catch { }
                try
                {
                    var pr = cur.GetType().GetProperty(seg);
                    var next = pr?.GetValue(cur);
                    if (next != null) { cur = next; continue; }
                }
                catch { }
                return null;
            }
            return cur;
        }

        private static Type? HxResolveType(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var nm = asm.GetName().Name ?? "";
                if (!nm.StartsWith("Siemens.Engineering")) continue;
                try
                {
                    var t = asm.GetType(typeName, false);
                    if (t != null) return t;
                    if (!typeName.Contains('.'))
                    {
                        Type? found = null;
                        try
                        {
                            foreach (var tt in asm.GetTypes())
                            {
                                if (tt.Name == typeName || tt.Name == typeName + "Facade") { found = tt; break; }
                            }
                        }
                        catch (System.Reflection.ReflectionTypeLoadException ex)
                        {
                            foreach (var tt in ex.Types)
                            {
                                if (tt != null && (tt.Name == typeName || tt.Name == typeName + "Facade")) { found = tt; break; }
                            }
                        }
                        if (found != null) return found;
                    }
                }
                catch { }
            }
            return null;
        }

        public string HmiObjectCreationInfos(string path, string? compositionName, string? hmiDeviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);
                    var obj = HxResolveHmiPath(hmi, path);
                    if (obj == null) return Err($"路径解析失败: {path}");
                    object? infos = null, compInfos = null;
                    if (!string.IsNullOrWhiteSpace(compositionName))
                    {
                        infos = obj.GetType().GetMethod("GetCreationInfos", new[] { typeof(string) })
                            ?.Invoke(obj, new object[] { compositionName });
                        compInfos = obj.GetType().GetMethod("GetCompositionInfos", new[] { typeof(string) })
                            ?.Invoke(obj, new object[] { compositionName });
                    }
                    else
                        infos = obj.GetType().GetMethod("GetCreationInfos", Type.EmptyTypes)?.Invoke(obj, null);
                    var props = UxExploreProps(obj);
                    var methods = obj.GetType().GetMethods()
                        .Where(m => !m.IsSpecialName && (m.Name.Contains("Create") || m.Name.Contains("Composition") || m.Name.Contains("Info")))
                        .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(pp => pp.ParameterType.Name)) + ")")
                        .Distinct().ToList();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        path,
                        compositionName,
                        typeName = obj.GetType().FullName,
                        properties = props,
                        relevantMethods = methods,
                        creationInfos = infos?.ToString() ?? "(null)",
                        compositionInfos = compInfos?.ToString() ?? "(null)"
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string HmiObjectCreate(string path, string compositionName, string name,
            string? typeName, string? attrsJson, string? hmiDeviceName)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var (hmi, err) = HxRequireClassicHmi(hmiDeviceName);
                    if (hmi == null) return Err(err);
                    var parent = HxResolveHmiPath(hmi, path);
                    if (parent == null) return Err($"路径解析失败: {path}");
                    var comp = parent;
                    if (!string.IsNullOrWhiteSpace(compositionName))
                    {
                        var gco = parent.GetType().GetMethods().FirstOrDefault(m =>
                            m.Name == "GetComposition" && m.GetParameters().Length == 1 &&
                            m.GetParameters()[0].ParameterType == typeof(string));
                        comp = gco?.Invoke(parent, new object[] { compositionName });
                        if (comp == null)
                        {
                            // 回退1：同名属性（如 TextList.Entries / .Items）
                            try { comp = parent.GetType().GetProperty(compositionName)?.GetValue(parent); } catch { }
                        }
                        if (comp == null)
                        {
                            // 回退2：常见别名属性
                            foreach (var alias in new[] { "Entries", "Items", compositionName })
                            {
                                try { comp = parent.GetType().GetProperty(alias)?.GetValue(parent); } catch { }
                                if (comp != null) break;
                            }
                        }
                        if (comp == null) return Err($"组合不存在: {compositionName}");
                    }
                    Type? type = null;
                    string creationInfo = "";
                    if (!string.IsNullOrWhiteSpace(typeName))
                        type = HxResolveType(typeName);
                    var creationInfos = comp.GetType().GetMethod("GetCreationInfos", new[] { typeof(string) })
                        ?.Invoke(comp, new object[] { name });
                    creationInfo = creationInfos?.ToString() ?? "";
                    if (type == null)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(creationInfo ?? "", "Siemens\\.Engineering[A-Za-z0-9.]*");
                        if (m.Success) type = HxResolveType(m.Value);
                    }
                    if (type == null)
                        type = HxResolveType("Siemens.Engineering.Hmi.Texts.MultilingualText");
                    var attrs = new Dictionary<string, object>();
                    if (!string.IsNullOrWhiteSpace(attrsJson))
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, object>>(attrsJson);
                        if (dict != null) foreach (var kv in dict) attrs[kv.Key] = kv.Value;
                    }
                    var createM = comp.GetType().GetMethods().FirstOrDefault(m =>
                        !m.IsGenericMethod && m.Name == "Create" &&
                        m.GetParameters().Length == 3 &&
                        m.GetParameters()[0].ParameterType == typeof(string) &&
                        m.GetParameters()[1].ParameterType == typeof(Type));
                    if (createM == null)
                        return Err("组合不支持 Create(name, type, attrs): " + comp.GetType().FullName + " | creationInfos=" + creationInfo);
                    var attrsParam = attrs.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value)).ToList();
                    var created = createM.Invoke(comp, new object[] { name, type, attrsParam });
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        createdName = name,
                        typeName = type.FullName,
                        createdType = created?.GetType().FullName,
                        creationInfo
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }




        // ═══ Ux 反射助手（2026-08-30 从 HmiUnifiedExtendedService.cs 归档捞回）═══
        /// <summary>反射获取对象属性值并转字符串。</summary>
        private static string? UxGetPropStr(object obj, string propName)
        {
            try { return obj?.GetType().GetProperty(propName)?.GetValue(obj)?.ToString(); }
            catch { return null; }
        }

        /// <summary>反射设置对象属性值（字符串自动转换 bool/int/double）。</summary>
        private static bool UxSetProp(object obj, string propName, string value)
        {
            try
            {
                var prop = obj.GetType().GetProperty(propName);
                if (prop == null || !prop.CanWrite) return false;
                object? converted = UxConvertValue(value, prop.PropertyType);
                if (converted == null) return false;
                prop.SetValue(obj, converted);
                return true;
            }
            catch { return false; }
        }

        /// <summary>把字符串转换为目标类型。</summary>
        private static object? UxConvertValue(string value, Type targetType)
        {
            try
            {
                if (targetType == typeof(string)) return value;
                if (targetType == typeof(bool))
                    return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                           value == "1" || value == "True";
                if (targetType == typeof(int)) return int.Parse(value);
                if (targetType == typeof(double)) return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(float)) return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(long)) return long.Parse(value);
                if (targetType.IsEnum) return Enum.Parse(targetType, value, true);
                // 尝试 ChangeType 兜底
                return Convert.ChangeType(value, targetType);
            }
            catch { return value; }
        }

        /// <summary>
        /// 枚举工程对象集合，返回 [{ name, typeName }] 列表。
        /// </summary>
        private static List<object> UxListCollection(IEnumerable? collection)
        {
            var result = new List<object>();
            if (collection == null) return result;
            foreach (var item in collection)
            {
                try
                {
                    result.Add(new
                    {
                        name = UxGetPropStr(item, "Name") ?? "(无名)",
                        typeName = item.GetType().Name
                    });
                }
                catch { }
            }
            return result;
        }

        /// <summary>反射调用集合的 Find(name) 方法。</summary>
        private static object? UxFindInCollection(object collection, string name)
        {
            try
            {
                var findMethod = collection.GetType().GetMethod("Find", new[] { typeof(string) });
                if (findMethod == null) return null;
                return findMethod.Invoke(collection, new object[] { name });
            }
            catch { return null; }
        }

        /// <summary>反射调用对象的 Delete() 方法。</summary>
        private static void UxDeleteObject(object obj)
        {
            try { obj.GetType().GetMethod("Delete", Type.EmptyTypes)?.Invoke(obj, null); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }

        /// <summary>反射列出对象可读属性及当前值（调试用）。</summary>
        private static List<object> UxExploreProps(object obj)
        {
            var result = new List<object>();
            try
            {
                foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        var val = p.GetValue(obj);
                        result.Add(new { name = p.Name, type = p.PropertyType.Name, value = val?.ToString() ?? "(null)" });
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }


        /// 通过反射调用 owner.GetService&lt;T&gt;()，T 由 serviceTypeFullName 指定。
        /// </summary>
        private object? UxGetService(object owner, string serviceTypeFullName)
        {
            try
            {
                var svcType = UxFindType(serviceTypeFullName);
                if (svcType == null) return null;
                var getServiceMethod = owner.GetType()
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod && m.GetParameters().Length == 0);
                if (getServiceMethod == null) return null;
                var generic = getServiceMethod.MakeGenericMethod(svcType);
                return generic.Invoke(owner, null);
            }
            catch { return null; }
        }

        private static Type? UxFindType(string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName)) return null;
            // 先尝试完整名
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(typeFullName);
                    if (t != null) return t;
                }
                catch { }
            }
            // 再按短名（取最后一段）匹配
            var shortName = typeFullName.Contains('.')
                ? typeFullName.Substring(typeFullName.LastIndexOf('.') + 1)
                : typeFullName;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == shortName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}

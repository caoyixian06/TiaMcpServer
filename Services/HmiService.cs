using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;

namespace TiaMcpServer
{
    /// <summary>
    /// 经典 HMI 全操作（任务 5.4）。
    /// 经典 HMI 的部分 API 在 Openness 中不稳定，统一用反射兼容访问。
    /// </summary>
    public partial class PortalService
    {
        // ────────────────────────────────────────────────────────────
        // 反射创建辅助方法
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 通过反射尝试多种 Create 方法签名在集合上创建对象。
        /// 依次尝试：
        ///   1. Create(string name, string typeIdentifier)（如果提供 typeIdentifier）
        ///   2. Create(string name)
        ///   3. Create(string name, string dataType)
        ///   4. CreateFrom(string name)
        ///   5. Create() 无参，然后设置 Name 属性
        /// 如果全部失败，列出所有可用的 Create 方法供调试。
        /// </summary>
        /// <param name="collection">集合对象（如 Tags、Screens、Connections）</param>
        /// <param name="name">要创建的对象名称</param>
        /// <param name="debugInfo">用于错误信息的上下文描述</param>
        /// <param name="typeIdentifier">可选的类型标识符</param>
        /// <returns>创建的对象，失败则抛出异常</returns>
        private object TryCreateViaReflection(object collection, string name, string debugInfo = "", string? typeIdentifier = null)
        {
            var collType = collection.GetType();

            // 尝试 0: Create(string name, string typeIdentifier)
            if (!string.IsNullOrEmpty(typeIdentifier))
            {
                var create0 = collType.GetMethod("Create", new[] { typeof(string), typeof(string) });
                if (create0 != null)
                {
                    try
                    {
                        var result = create0.Invoke(collection, new object[] { name, typeIdentifier });
                        if (result != null) return result;
                    }
                    catch (TargetInvocationException tie)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[TryCreateViaReflection] Create(string, string) 失败: {tie.InnerException?.Message ?? tie.Message}");
                    }
                }
            }

            // 尝试 1: Create(string name)
            var create1 = collType.GetMethod("Create", new[] { typeof(string) });
            if (create1 != null)
            {
                try
                {
                    var result = create1.Invoke(collection, new object[] { name });
                    if (result != null) return result;
                }
                catch (TargetInvocationException tie)
                {
                    // 记录但继续尝试其他签名
                    System.Diagnostics.Debug.WriteLine(
                        $"[TryCreateViaReflection] Create(string) 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
            }

            // 尝试 2: Create(string name, string dataType)
            var create2 = collType.GetMethod("Create", new[] { typeof(string), typeof(string) });
            if (create2 != null)
            {
                try
                {
                    var result = create2.Invoke(collection, new object[] { name, "" });
                    if (result != null) return result;
                }
                catch (TargetInvocationException tie)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TryCreateViaReflection] Create(string, string) 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
            }

            // 尝试 3: CreateFrom(string name)
            var createFrom = collType.GetMethod("CreateFrom", new[] { typeof(string) });
            if (createFrom != null)
            {
                try
                {
                    var result = createFrom.Invoke(collection, new object[] { name });
                    if (result != null) return result;
                }
                catch (TargetInvocationException tie)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TryCreateViaReflection] CreateFrom(string) 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
            }

            // 尝试 4: Create() 无参，然后设置 Name 属性
            var createNoArgs = collType.GetMethod("Create", Type.EmptyTypes);
            if (createNoArgs != null)
            {
                try
                {
                    var result = createNoArgs.Invoke(collection, null);
                    if (result != null)
                    {
                        var nameProp = result.GetType().GetProperty("Name");
                        if (nameProp != null && nameProp.CanWrite)
                        {
                            nameProp.SetValue(result, name);
                            return result;
                        }
                        // 无 Name 属性但创建成功，也返回
                        return result;
                    }
                }
                catch (TargetInvocationException tie)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TryCreateViaReflection] Create() 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
            }

            // 全部失败 — 枚举所有 Create 方法供调试
            var allCreateMethods = collType.GetMethods()
                .Where(m => m.Name == "Create" || m.Name == "CreateFrom")
                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                .ToList();

            var methodList = allCreateMethods.Count > 0
                ? string.Join("; ", allCreateMethods)
                : "(无任何 Create/CreateFrom 方法)";

            var typeIdInfo = !string.IsNullOrEmpty(typeIdentifier)
                ? $" 请求的类型标识符: {typeIdentifier}。"
                : "";

            throw new Exception(
                $"无法通过反射在 {debugInfo} 上创建对象 '{name}'。{typeIdInfo}集合类型: {collType.FullName}。" +
                $"可用的 Create/CreateFrom 方法: {methodList}");
        }

        // ────────────────────────────────────────────────────────────
        // HMI 设备型号检测与画面尺寸映射
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// HMI 设备型号 → 画面分辨率映射表。
        /// Key 为不含后缀的型号（如 TP700、TP1200），Value 为 (宽, 高)。
        /// </summary>
        private static readonly Dictionary<string, (int W, int H)> HmiScreenSizes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["KTP400"]  = (320, 240),
                ["KTP700"]  = (800, 480),
                ["KTP900"]  = (1024, 600),
                ["KP300"]   = (480, 320),
                ["KP400"]   = (320, 240),
                ["KP700"]   = (800, 480),
                ["KP900"]   = (1024, 600),
                ["TP700"]   = (800, 480),
                ["TP900"]   = (1024, 600),
                ["TP1200"]  = (1280, 800),
                ["TP1500"]  = (1920, 1080),
                ["TP1900"]  = (1920, 1080),
                ["TP2200"]  = (1920, 1080),
            };

        /// <summary>
        /// 从 TypeIdentifier（如 "HMI:TP1200COMFORTPN"）提取设备型号（如 "TP1200"）。
        /// </summary>
        private static string ExtractHmiDeviceType(string typeIdentifier)
        {
            if (string.IsNullOrEmpty(typeIdentifier)) return "Unknown";
            var part = typeIdentifier.StartsWith("HMI:", StringComparison.OrdinalIgnoreCase)
                ? typeIdentifier.Substring(4)
                : typeIdentifier;
            // 移除后缀：COMFORTPN / BASICPN / COMFORT / BASIC / PN
            var suffixes = new[] { "COMFORTPN", "BASICPN", "COMFORT", "BASIC", "PN" };
            foreach (var suffix in suffixes)
            {
                if (part.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    part = part.Substring(0, part.Length - suffix.Length);
                    break;
                }
            }
            part = part.ToUpperInvariant();
            // 部分设备 TypeIdentifier 会包含额外硬件/版本后缀，不能只做 EndsWith 剥离。
            // 优先从已知型号表中做最长匹配，避免 TP1200 被错误回退到 800x480。
            var matched = HmiScreenSizes.Keys
                .OrderByDescending(k => k.Length)
                .FirstOrDefault(k => part.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
            if (matched != null) return matched;

            // ★修复★ OrderNumber:6AV2 124-0GC01-0AX0/17.0.0.0 形式的标识（按订单号创建的
            // 面板，型号标识挂在硬件子项上）：字符串不含 TP700 等型号词，直接匹配必然落空、
            // 分辨率退回 800x480 默认值。从已验证类型库按订单号反查型号，再对尺寸表最长匹配。
            // 订单号分组不规则（6AV2+三位-五-四），固定分段正则匹配不上——直接截取 6AV2
            // 起的字符流、剔除空格/连字符、到版本号 '/' 截止后与类型库比对。
            var orderIdx = typeIdentifier.IndexOf("6AV2", StringComparison.OrdinalIgnoreCase);
            if (orderIdx >= 0)
            {
                var orderSb = new StringBuilder();
                foreach (var ch in typeIdentifier.Substring(orderIdx))
                {
                    if (ch == '/') break;
                    if (ch == ' ' || ch == '-') continue;
                    if (!char.IsLetterOrDigit(ch)) break;
                    orderSb.Append(char.ToUpperInvariant(ch));
                }
                var order = orderSb.ToString();
                if (order.Length >= 10)
                {
                    foreach (var entry in TypeIdentifierLibrary.VerifiedEntries)
                    {
                        var entryOrder = (entry.OrderNumber ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant();
                        if (entryOrder.Length == 0) continue;
                        if (!entryOrder.Contains(order) && !order.StartsWith(entryOrder, StringComparison.Ordinal)) continue;
                        var modelMatched = HmiScreenSizes.Keys
                            .OrderByDescending(k => k.Length)
                            .FirstOrDefault(k => entry.ModelName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (modelMatched != null) return modelMatched;
                    }
                }
            }
            return part;
        }

        /// <summary>
        /// HMI 设备信息（含 HmiTarget 引用、型号、分辨率）。
        /// </summary>
        private class HmiDeviceInfo
        {
            public HmiTarget Target = null!;
            public string DeviceType = "Unknown";   // "TP1200", "TP700" 等
            public string TypeIdentifier = "";       // "HMI:TP1200COMFORTPN"
            public int ScreenWidth = 800;
            public int ScreenHeight = 480;
        }

        /// <summary>
        /// 获取项目中第一个 HMI 设备的完整信息（型号 + 分辨率）。
        /// </summary>
        private HmiDeviceInfo? GetHmiDeviceInfo()
        {
            if (_project == null) return null;
            // ★递归遍历所有设备（含设备组），修复多站项目找不到 HMI 的问题
            foreach (var device in GetAllDevices())
            {
                HmiTarget? found = null;
                var typeId = "";
                foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        if (swContainer?.Software is HmiTarget target)
                        {
                            found = target;
                            typeId = di.TypeIdentifier ?? "";
                            break;
                        }
                    }
                    catch { }
                }
                if (found == null) continue;
                // ★修复★ 承载 HmiTarget 的子项 TypeIdentifier 可能为空（如按订单号
                // 创建的面板），型号识别落空、分辨率退回 800x480 默认值。回退扫描
                // 同一设备的其他子项，取第一个非空标识。
                if (string.IsNullOrWhiteSpace(typeId))
                {
                    foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                    {
                        try
                        {
                            var t = di.TypeIdentifier;
                            if (!string.IsNullOrWhiteSpace(t)) { typeId = t; break; }
                        }
                        catch { }
                    }
                }
                var deviceType = ExtractHmiDeviceType(typeId);
                var (w, h) = HmiScreenSizes.TryGetValue(deviceType, out var size)
                    ? size
                    : (800, 480);
                return new HmiDeviceInfo
                {
                    Target = found,
                    DeviceType = deviceType,
                    TypeIdentifier = typeId,
                    ScreenWidth = w,
                    ScreenHeight = h
                };
            }
            return null;
        }

        /// <summary>
        /// 从现有画面导出 XML 中提取 Engineering 版本号（如 "V17"）。
        /// 若无现有画面则回退到当前绑定的 TIA 版本。
        /// </summary>
        private string GetEngineeringVersion(HmiTarget hmi)
        {
            try
            {
                var existingScreens = GetAllScreens(hmi.ScreenFolder).ToList();
                if (existingScreens.Count > 0)
                {
                    var exportFile = Path.Combine(Path.GetTempPath(), $"tia_ver_{Guid.NewGuid():N}.xml");
                    try
                    {
                        existingScreens[0].Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                        var xml = File.ReadAllText(exportFile, Encoding.UTF8);
                        var match = System.Text.RegularExpressions.Regex.Match(
                            xml, @"<Engineering\s+version=""(V[\d.]+)""");
                        if (match.Success) return match.Groups[1].Value;
                    }
                    finally { TryDelete3(exportFile); }
                }
            }
            catch { }
            return EnvironmentDiscoveryService.CurrentEngineeringVersion();
        }

        // ────────────────────────────────────────────────────────────
        // HMI 定位
        // ────────────────────────────────────────────────────────────

        private HmiTarget? GetClassicHmi()
        {
            var hmi = GetHmiDeviceInfo()?.Target;
            if (hmi == null)
            {
                // ★修复★ 首次设备枚举可能因 TIA COM 对象未完全就绪而返回空（attach 后立即调用时复现），
                // 重试一次可稳定命中（实测：先调 get_hmi_targets 预热后必成功）。
                try { System.Threading.Thread.Sleep(300); } catch { }
                hmi = GetHmiDeviceInfo()?.Target;
            }
            return hmi;
        }

        private HmiTarget RequireClassicHmi(string? hmiDeviceName = null)
        {
            HmiTarget? hmi;
            if (string.IsNullOrEmpty(hmiDeviceName))
            {
                // 未指定设备名：保持原逻辑，返回第一个 HMI 设备
                hmi = GetClassicHmi();
            }
            else
            {
                // 按名称查找指定 HMI 设备
                hmi = null;
                if (_project != null)
                {
                    foreach (var device in GetAllDevices())
                    {
                        if (!device.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                        {
                            try
                            {
                                var swContainer = di.GetService<SoftwareContainer>();
                                if (swContainer?.Software is HmiTarget target)
                                {
                                    hmi = target;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (hmi != null) break;
                    }
                }
            }
            if (hmi == null) throw new InvalidOperationException("未找到经典 HMI 设备");
            return hmi;
        }

        public string GetHmiTargets()
        {
            lock (_lock)
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
                            try
                            {
                                var swContainer = di.GetService<SoftwareContainer>();
                                if (swContainer?.Software is HmiTarget target)
                                {
                                    var typeId = di.TypeIdentifier ?? "";
                                    var deviceType = ExtractHmiDeviceType(typeId);
                                    var (w, h) = HmiScreenSizes.TryGetValue(deviceType, out var size)
                                        ? size
                                        : (800, 480);
                                    list.Add(new
                                    {
                                        name = target.Name,
                                        deviceType,
                                        typeIdentifier = typeId,
                                        screenWidth = w,
                                        screenHeight = h
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, hmiTargets = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 画面递归查找
        // ────────────────────────────────────────────────────────────

        private static IEnumerable<Screen> GetAllScreens(ScreenFolder folder)
        {
            foreach (var screen in folder.Screens) yield return screen;
            foreach (var sub in folder.Folders)
                foreach (var s in GetAllScreens(sub)) yield return s;
        }

        private Screen? FindScreenRecursive(ScreenFolder folder, string screenName)
        {
            var screen = folder.Screens.Find(screenName);
            if (screen != null) return screen;
            foreach (var sub in folder.Folders)
            {
                var found = FindScreenRecursive(sub, screenName);
                if (found != null) return found;
            }
            return null;
        }

        // ── fix#18: 模板/弹出/滑入画面触达 ──────────────────────────────
        // 原有工具只递归 HmiTarget.ScreenFolder，模板(ScreenTemplateFolder)、
        // 弹出(ScreenPopupFolder)、滑入(ScreenSlideinFolder) 三类系统文件夹完全
        // 不可触达，导致模板内悬空事件引用无法用工具定位和清洗。以下方法用反射
        // 统一处理 4 类文件夹（集合属性名：常规/弹出/滑入=Screens，模板=ScreenTemplates）。
        // fix#19: 模板区组合 ScreenTemplates 的元素类型是 ScreenTemplate（不是 Screen 的子类），
        // 原 `is Screen` 判定会把模板命中全部丢弃；改为返回 object，由调用方反射处理。

        /// <summary>反射取对象 Name 属性（Screen 与 ScreenTemplate 通用）。</summary>
        private static string? GetNameOf(object obj)
        {
            try { return obj.GetType().GetProperty("Name")?.GetValue(obj)?.ToString(); }
            catch { return null; }
        }

        /// <summary>反射调用 Export(FileInfo[, ExportOptions])（Screen 与 ScreenTemplate 通用）。</summary>
        private static void ExportViaReflection(object screen, string filePath)
        {
            var m = screen.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) })
                ?? screen.GetType().GetMethod("Export", new[] { typeof(FileInfo) });
            if (m == null)
                throw new InvalidOperationException($"{screen.GetType().Name} 不支持 Export 方法");
            var args = m.GetParameters().Length == 2
                ? new object[] { new FileInfo(filePath), ExportOptions.WithDefaults }
                : new object[] { new FileInfo(filePath) };
            m.Invoke(screen, args);
        }

        /// <summary>在单个画面文件夹对象（4 类之一）及其子文件夹中递归查找画面。
        /// folderKind 标识顶层归属（Screen/Template/Popup/Slidein）。
        /// fix#19: 返回 object 以兼容 Screen 与 ScreenTemplate。</summary>
        private (object? screen, string folderKind) FindScreenInFolderTree(object folder, string screenName, string folderKind)
        {
            foreach (var collName in new[] { "Screens", "ScreenTemplates" })
            {
                try
                {
                    var coll = folder.GetType().GetProperty(collName)?.GetValue(folder);
                    if (coll == null) continue;
                    var findM = coll.GetType().GetMethod("Find", new[] { typeof(string) });
                    if (findM == null) continue;
                    // fix#19: Find 精确按名匹配，非空即命中（Screen 或 ScreenTemplate）
                    var found = findM.Invoke(coll, new object[] { screenName });
                    if (found != null) return (found, folderKind);
                }
                catch { }
            }
            try
            {
                var folders = folder.GetType().GetProperty("Folders")?.GetValue(folder);
                if (folders != null)
                {
                    var enumM = folders.GetType().GetMethod("GetEnumerator");
                    if (enumM?.Invoke(folders, null) is System.Collections.IEnumerable seq)
                        foreach (var sub in seq)
                        {
                            var (found, kind) = FindScreenInFolderTree(sub, screenName, folderKind);
                            if (found != null) return (found, kind);
                        }
                }
            }
            catch { }
            return (null, folderKind);
        }

        /// <summary>跨 4 类画面文件夹查找画面：常规 → 模板 → 弹出 → 滑入。
        /// 常规区优先，保证既有行为的查找顺序不变。
        /// fix#19: 返回 object 以兼容 Screen 与 ScreenTemplate。</summary>
        private (object? screen, string folderKind) FindScreenAny(HmiTarget hmi, string screenName)
        {
            var r = FindScreenInFolderTree(hmi.ScreenFolder, screenName, "Screen");
            if (r.screen != null) return r;
            r = FindScreenInFolderTree(hmi.ScreenTemplateFolder, screenName, "Template");
            if (r.screen != null) return r;
            r = FindScreenInFolderTree(hmi.ScreenPopupFolder, screenName, "Popup");
            if (r.screen != null) return r;
            return FindScreenInFolderTree(hmi.ScreenSlideinFolder, screenName, "Slidein");
        }

        /// <summary>按 target（screen/template/popup/slidein）选择目标画面组合执行 XML 导入（Override）。
        /// 反射取组合：常规与弹出/滑入为 Screens，模板为 ScreenTemplates。</summary>
        private object? GetScreenCompositionForImport(HmiTarget hmi, string target, out string kind)
        {
            kind = string.IsNullOrWhiteSpace(target) ? "Screen" : target.Trim();
            object folder;
            if (kind.Equals("Template", StringComparison.OrdinalIgnoreCase))
            { folder = hmi.ScreenTemplateFolder; kind = "Template"; }
            else if (kind.Equals("Popup", StringComparison.OrdinalIgnoreCase))
            { folder = hmi.ScreenPopupFolder; kind = "Popup"; }
            else if (kind.Equals("Slidein", StringComparison.OrdinalIgnoreCase))
            { folder = hmi.ScreenSlideinFolder; kind = "Slidein"; }
            else if (kind.Equals("Screen", StringComparison.OrdinalIgnoreCase))
            { folder = hmi.ScreenFolder; kind = "Screen"; }
            else
                throw new InvalidOperationException($"不支持的导入目标 target: {target}（支持: screen/template/popup/slidein）");

            foreach (var collName in new[] { "Screens", "ScreenTemplates" })
            {
                var coll = folder.GetType().GetProperty(collName)?.GetValue(folder);
                if (coll != null && coll.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) }) != null)
                    return coll;
            }
            throw new InvalidOperationException($"{kind} 文件夹上未找到可用的画面导入组合（Screens/ScreenTemplates + Import）");
        }

        // ────────────────────────────────────────────────────────────
        // 画面管理
        // ────────────────────────────────────────────────────────────

        public string ListHmiScreens()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = GetAllScreens(hmi.ScreenFolder).Select(s => new { name = s.Name }).ToList();
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, screens = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateHmiScreen(string name)
        {
            lock (_lock)
            {
                try
                {
                    var devInfo = GetHmiDeviceInfo() ?? throw new InvalidOperationException("未找到经典 HMI 设备");
                    var hmi = devInfo.Target;
                    // ★修复★ 导入前检查同名画面：ImportOptions.Override 会静默覆盖同名画面
                    if (FindScreenRecursive(hmi.ScreenFolder, name) != null)
                        return Err($"画面已存在: {name}，请先删除该画面或改换名称后再创建");
                    // ScreenComposition 没有 Create(string name)，只能用 XML Import
                    var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_screen_{Guid.NewGuid():N}.xml");
                    var exportFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_ref_{Guid.NewGuid():N}.xml");
                    try
                    {
                        // 从现有画面导出 XML 获取正确的版本号和格式模板
                        Screen? first = null;
                        var usedTemplate = "";
                        var existingScreens = GetAllScreens(hmi.ScreenFolder).ToList();
                        if (existingScreens.Count > 0)
                        {
                            // 导出第一个画面作为模板
                            existingScreens[0].Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                            var templateXml = File.ReadAllText(exportFile, Encoding.UTF8);
                            var newXml = BuildScreenXmlFromTemplate(templateXml, name);
                            var bom = new UTF8Encoding(true);
                            File.WriteAllText(tempFile, newXml, bom);
                            var imported = hmi.ScreenFolder.Screens.Import(new FileInfo(tempFile), ImportOptions.Override);
                            first = imported.FirstOrDefault();
                        }
                        else
                        {
                            // 没有现有画面：枚举设备的画面模板文件夹（ScreenTemplateFolder），
                            // 用设备实际存在的模板名生成画面，避免硬编码模板名导致
                            // V17 编译报 "Invalid template for screen"。
                            var engVer = GetEngineeringVersion(hmi);
                            var bom = new UTF8Encoding(true);
                            var tplNames = new List<string>();
                            try
                            {
                                // ScreenTemplateFolder 类型为 ScreenTemplateSystemFolder，
                                // 与 ScreenFolder 不同，通过反射枚举其中的 Screens 集合。
                                var tplFolder = hmi.ScreenTemplateFolder;
                                var scrProp = tplFolder.GetType().GetProperty("Screens");
                                if (scrProp?.GetValue(tplFolder) is System.Collections.IEnumerable scrList)
                                {
                                    foreach (var s in scrList)
                                    {
                                        try
                                        {
                                            var nProp = s.GetType().GetProperty("Name");
                                            var n = nProp?.GetValue(s)?.ToString();
                                            if (!string.IsNullOrWhiteSpace(n)) tplNames.Add(n!);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                            var templateCandidates = new List<string> { "" };
                            templateCandidates.AddRange(tplNames.Distinct(StringComparer.OrdinalIgnoreCase));
                            templateCandidates.AddRange(new[] { "模板_1", "模板_2", "模板_3", "Template_1", "Template_2" });
                            var attemptErrors = new List<string>();
                            foreach (var tpl in templateCandidates)
                            {
                                var xml = GenerateEmptyScreenXml(name, devInfo.ScreenWidth, devInfo.ScreenHeight, engVer, tpl);
                                File.WriteAllText(tempFile, xml, bom);
                                try
                                {
                                    var imported = hmi.ScreenFolder.Screens.Import(new FileInfo(tempFile), ImportOptions.Override);
                                    first = imported.FirstOrDefault();
                                    if (first != null) { usedTemplate = tpl; break; }
                                }
                                catch (Exception ex) { attemptErrors.Add((tpl.Length == 0 ? "<无模板链接,由 TIA 分配默认>" : tpl) + ": " + ex.Message); first = null; }
                            }
                            if (first == null)
                                return Err("画面导入失败：所有候选模板均未能导入" + (attemptErrors.Count > 0 ? $"（{string.Join("; ", attemptErrors)}）" : "") + "。可在博途中手动创建同名画面后重试。");
                            if (string.IsNullOrWhiteSpace(usedTemplate) && tplNames.Count > 0)
                                usedTemplate = tplNames[0];
                        }

                        if (first == null) return Err("画面导入失败：返回空列表");
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"已创建画面: {name}（设备: {devInfo.DeviceType}, 分辨率: {devInfo.ScreenWidth}x{devInfo.ScreenHeight}, 模板: {usedTemplate}）",
                            screen = new { name = first.Name },
                            template = usedTemplate
                        });
                    }
                    finally
                    {
                        TryDelete3(tempFile);
                        TryDelete3(exportFile);
                    }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 从导出的画面 XML 模板创建新画面 XML：替换名称、清空画面项、删除 Number 交由导入器自动分配。
        /// 这样可以自动适配博途版本（V18/V19/V20 等）。
        /// </summary>
        private static string BuildScreenXmlFromTemplate(string templateXml, string newName)
        {
            var doc = new System.Xml.XmlDocument();
            doc.PreserveWhitespace = true;
            doc.LoadXml(templateXml);

            // 替换画面名称（InnerText 赋值自动转义，勿再用 SecurityElement.Escape，否则双重转义）
            var nameNode = doc.SelectSingleNode("//Hmi.Screen.Screen/AttributeList/Name");
            if (nameNode != null) nameNode.InnerText = newName;

            // ★修复★ 删除 Number 节点，交由导入器自动分配画面号；
            // 原硬编码 5000 在连续创建多个画面时互相冲突。
            var numberNode = doc.SelectSingleNode("//Hmi.Screen.Screen/AttributeList/Number");
            numberNode?.ParentNode?.RemoveChild(numberNode);

            // ★修复★ 清空所有 ScreenLayer 的 ObjectList（原实现只清空第一个层）
            var layerObjectLists = doc.SelectNodes("//Hmi.Screen.ScreenLayer/ObjectList");
            if (layerObjectLists != null)
            {
                foreach (System.Xml.XmlNode node in layerObjectLists)
                    node.InnerXml = "";
            }

            // 移除 HelpText 和 ScreenLayer 以外的 Screen 直接子 ObjectList 中的节点
            var screenObjectList = doc.SelectSingleNode("//Hmi.Screen.Screen/ObjectList");
            if (screenObjectList != null)
            {
                var toRemove = new List<System.Xml.XmlNode>();
                foreach (System.Xml.XmlNode child in screenObjectList.ChildNodes)
                {
                    if (child.Name == "MultilingualText" || child.Name == "Hmi.Screen.ScreenLayer")
                        continue;
                    toRemove.Add(child);
                }
                foreach (var node in toRemove) screenObjectList.RemoveChild(node);
            }

            // 直接写入文件，带 UTF-8 BOM
            return doc.OuterXml;
        }

        public string DeleteHmiScreen(string name)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // fix#19: 跨 4 类画面文件夹查找（含模板），Screen/ScreenTemplate 反射 Delete
                    var (screen, folderKind) = FindScreenAny(hmi, name);
                    if (screen == null) return Err($"未找到画面: {name}（已检索常规/模板/弹出/滑入画面区）");
                    var deleteM = screen.GetType().GetMethod("Delete", Type.EmptyTypes);
                    if (deleteM == null) return Err($"{folderKind} 区对象 {screen.GetType().Name} 不支持 Delete");
                    deleteM.Invoke(screen, null);
                    return Ok($"已删除画面: {name}（{folderKind}）");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExportHmiScreen(string screenName, string outputPath)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // fix#18/19: 跨 4 类画面文件夹查找（常规/模板/弹出/滑入），Screen 与 ScreenTemplate 兼容
                    var (screen, screenFolder) = FindScreenAny(hmi, screenName);
                    if (screen == null) return Err($"未找到画面: {screenName}（已检索常规/模板/弹出/滑入画面区）");
                    EnsureDir3(outputPath);
                    ExportViaReflection(screen, outputPath);
                    return Ok($"画面 {screenName}（{screenFolder}）已导出到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportHmiScreen(string filePath, string? target = null)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // ★防护★ 用户可控画面 XML 先做良构校验，坏 XML 曾直接终止 TIA 进程
                    var vErr = ValidateImportXmlFile(filePath, "导入 HMI 画面");
                    if (vErr != null) return Err(vErr);
                    // fix#18: target 支持 screen（默认，向后兼容）/template/popup/slidein
                    var composition = GetScreenCompositionForImport(hmi, target ?? "screen", out var kind);
                    var importM = composition.GetType().GetMethod("Import", new[] { typeof(FileInfo), typeof(ImportOptions) });
                    if (importM == null) return Err($"{kind} 画面组合缺少 Import(FileInfo, ImportOptions) 方法");
                    var imported = importM.Invoke(composition, new object[] { new FileInfo(filePath), ImportOptions.Override });
                    // fix#19: ScreenTemplates.Import 返回 ScreenTemplate 集合，非 IEnumerable<Screen>，按非泛型处理
                    string? firstName = null;
                    if (imported is IEnumerable<Screen> typedList) firstName = typedList.FirstOrDefault()?.Name;
                    else if (imported is IEnumerable rawList)
                        foreach (var it in rawList) { firstName = GetNameOf(it); break; }
                    return Ok(firstName != null ? $"已导入画面到 {kind}: {firstName}" : $"画面导入完成（{kind}）");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ReadHmiScreen(string screenName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // fix#18/19: 跨 4 类画面文件夹查找，Screen 与 ScreenTemplate 兼容
                    var (screen, screenFolder) = FindScreenAny(hmi, screenName);
                    if (screen == null) return Err($"未找到画面: {screenName}（已检索常规/模板/弹出/滑入画面区）");
                    var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_screen_{Guid.NewGuid():N}.xml");
                    try
                    {
                        ExportViaReflection(screen, tempFile);
                        var xml = File.ReadAllText(tempFile);
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            screenName = GetNameOf(screen) ?? screenName,
                            screenFolder,
                            xmlContent = xml
                        });
                    }
                    finally { TryDelete3(tempFile); }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 画面组
        // ────────────────────────────────────────────────────────────

        public string ListHmiScreenGroups()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = new List<object>();
                    foreach (var f in hmi.ScreenFolder.Folders)
                        list.Add(new { name = f.Name });
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, screenGroups = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateHmiScreenGroup(string groupName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    hmi.ScreenFolder.Folders.Create(groupName);
                    return Ok($"已创建画面组: {groupName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteHmiScreenGroup(string groupName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var g = hmi.ScreenFolder.Folders.Find(groupName);
                    if (g == null) return Err($"未找到画面组: {groupName}");
                    g.Delete();
                    return Ok($"已删除画面组: {groupName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 画面项管理（反射访问 ScreenItems）
        // ────────────────────────────────────────────────────────────

        public string ListHmiScreenItems(string screenName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // fix#18/19: 跨 4 类画面文件夹查找，Screen 与 ScreenTemplate 兼容
                    var (screen, screenFolder) = FindScreenAny(hmi, screenName);
                    if (screen == null) return Err($"未找到画面: {screenName}（已检索常规/模板/弹出/滑入画面区）");

                    // 经典 HMI 的 Screen 对象无法通过 API 直接访问画面项，
                    // 通过导出 XML 后解析 ScreenLayer.ObjectList 中的画面项
                    var exportFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_list_{Guid.NewGuid():N}.xml");
                    try
                    {
                        ExportViaReflection(screen, exportFile);
                        var xml = File.ReadAllText(exportFile, Encoding.UTF8);
                        var items = ParseScreenItemsFromXml(xml);
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            screenName = GetNameOf(screen) ?? screenName,
                            screenFolder,
                            count = items.Count,
                            screenItems = items
                        });
                    }
                    finally { TryDelete3(exportFile); }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 从画面 XML 中解析画面项列表（从 ScreenLayer.ObjectList 中提取）。
        /// </summary>
        private static List<object> ParseScreenItemsFromXml(string xml)
        {
            var items = new List<object>();
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);

            // 查找所有 CompositionName="ScreenItems" 的节点（即画面项）
            var itemNodes = doc.SelectNodes("//*[@CompositionName='ScreenItems']");
            if (itemNodes == null) return items;

            foreach (System.Xml.XmlNode node in itemNodes)
            {
                var typeName = node.Name; // 如 Hmi.Screen.Button
                var shortType = typeName.StartsWith("Hmi.Screen.") ? typeName.Substring(11) : typeName;

                string? objName = null;
                var objNameNode = node.SelectSingleNode("AttributeList/ObjectName");
                if (objNameNode != null) objName = objNameNode.InnerText;

                int left = 0, top = 0, width = 0, height = 0;
                var leftNode = node.SelectSingleNode("AttributeList/Left");
                if (leftNode != null) int.TryParse(leftNode.InnerText, out left);
                var topNode = node.SelectSingleNode("AttributeList/Top");
                if (topNode != null) int.TryParse(topNode.InnerText, out top);
                var widthNode = node.SelectSingleNode("AttributeList/Width");
                if (widthNode != null) int.TryParse(widthNode.InnerText, out width);
                var heightNode = node.SelectSingleNode("AttributeList/Height");
                if (heightNode != null) int.TryParse(heightNode.InnerText, out height);

                items.Add(new
                {
                    name = objName ?? "(unnamed)",
                    type = shortType,
                    typeIdentifier = typeName,
                    x = left,
                    y = top,
                    width = width,
                    height = height
                });
            }
            return items;
        }

        private static object DescribeScreenItem(object item)
        {
            var t = item.GetType();
            var name = t.GetProperty("Name")?.GetValue(item)?.ToString() ?? "?";
            var type = t.GetProperty("TypeIdentifier")?.GetValue(item)?.ToString() ?? t.Name;
            var typeIdentifier = t.GetProperty("TypeIdentifier")?.GetValue(item)?.ToString();
            var posProp = t.GetProperty("Position")?.GetValue(item);
            var sizeProp = t.GetProperty("Size")?.GetValue(item);
            var x = posProp?.GetType().GetProperty("X")?.GetValue(posProp) ?? 0;
            var y = posProp?.GetType().GetProperty("Y")?.GetValue(posProp) ?? 0;
            var w = sizeProp?.GetType().GetProperty("Width")?.GetValue(sizeProp) ?? 0;
            var h = sizeProp?.GetType().GetProperty("Height")?.GetValue(sizeProp) ?? 0;
            return new { name, type, typeIdentifier, x, y, width = w, height = h };
        }

        private static string? MapHmiItemType(string itemType)
        {
            // 已验证通过的类型（通过 XML 往返方式测试，参考画面_1.xml 导出结构）：
            //   button, textfield, iofield, rectangle, switch, circle
            //   gauge, bar, graphicview
            // 未验证/有问题的类型：
            //   line - Import 失败导致博途崩溃，需进一步排查
            //   trendview, ellipse - 需要用户手动导出参考 XML
            return itemType.ToLowerInvariant() switch
            {
                "button" => "Hmi.Screen.Button",
                "iofield" or "io_field" => "Hmi.Screen.IOField",
                "textfield" or "text_field" => "Hmi.Screen.TextField",
                "rectangle" => "Hmi.Screen.Rectangle",
                "switch" => "Hmi.Screen.Switch",
                "circle" => "Hmi.Screen.Circle",
                "gauge" => "Hmi.Screen.Gauge",
                "bar" => "Hmi.Screen.Bar",
                "graphicview" or "graphic_view" => "Hmi.Screen.GraphicView",
                _ => null
            };
        }

        public string CreateHmiScreenItem(string screenName, string itemType, string name,
            string? tagName = null, int? left = null, int? top = null, int? width = null, int? height = null)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var screen = FindScreenRecursive(hmi.ScreenFolder, screenName);
                    if (screen == null) return Err($"未找到画面: {screenName}");

                    // 经典 HMI 的 Screen 对象无法通过 Openness API 直接访问画面项，
                    // 只能通过 XML 往返方式：导出 → 添加画面项 XML → 重新导入
                    var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_item_{Guid.NewGuid():N}.xml");
                    var exportFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_export_{Guid.NewGuid():N}.xml");
                    try
                    {
                        // 1. 导出当前画面 XML
                        screen.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                        var xml = File.ReadAllText(exportFile, Encoding.UTF8);

                        // 2. 在 XML 中添加画面项并直接写入文件（带 UTF-8 BOM）
                        //    坐标/尺寸未传时使用各类型默认值（保持向后兼容）
                        var itemXml = BuildScreenItemXml(itemType, name, tagName, left, top, width, height);
                        InjectScreenItemXmlToFile(xml, itemXml, tempFile);

                        // 3. 重新导入
                        hmi.ScreenFolder.Screens.Import(new FileInfo(tempFile), ImportOptions.Override);

                        // 如果指定了 tagName，导入后绑定变量
                        if (!string.IsNullOrEmpty(tagName))
                        {
                            try
                            {
                                var createdItem = FindScreenItem(hmi, screenName, name);
                                if (createdItem != null)
                                    BindItemTag(hmi, createdItem, tagName);
                            }
                            catch { /* 记录警告但不失败 */ }
                        }

                        return Ok($"已创建 {itemType}: {name}（经典 HMI，XML 往返方式）");
                    }
                    finally
                    {
                        TryDelete3(tempFile);
                        TryDelete3(exportFile);
                    }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 生成画面项的最小化 XML 片段（包含在 ScreenLayer.ObjectList 中）。
        /// 对照根画面.xml 和立库管理.xml 的导出结构。
        /// 坐标/尺寸参数为 null 时使用各类型默认值（保持向后兼容）。
        /// </summary>
        private static string BuildScreenItemXml(string itemType, string name, string? tagName,
            int? left, int? top, int? width, int? height)
        {
            var escName = System.Security.SecurityElement.Escape(name);
            var xmlTag = itemType.ToLowerInvariant() switch
            {
                "button" => "Hmi.Screen.Button",
                "iofield" or "io_field" => "Hmi.Screen.IOField",
                "textfield" or "text_field" => "Hmi.Screen.TextField",
                "rectangle" => "Hmi.Screen.Rectangle",
                "switch" => "Hmi.Screen.Switch",
                "circle" => "Hmi.Screen.Circle",
                "gauge" => "Hmi.Screen.Gauge",
                "bar" => "Hmi.Screen.Bar",
                "graphicview" or "graphic_view" => "Hmi.Screen.GraphicView",
                _ => throw new ArgumentException($"不支持的画面项类型: {itemType}")
            };

            // 各类型默认尺寸（坐标未传时统一用 50,50；宽高按类型给出合理默认）
            int defLeft = 50, defTop = 50;
            int defW, defH;
            switch (itemType.ToLowerInvariant())
            {
                case "circle":      defW = 80;  defH = 80;  break;
                case "gauge":       defW = 150; defH = 150; break;
                case "bar":         defW = 150; defH = 200; break;
                case "graphicview":
                case "graphic_view": defW = 100; defH = 100; break;
                default:            defW = 100; defH = 30;  break;  // button/textfield/iofield/rectangle/switch
            }
            int L = left ?? defLeft;
            int T = top ?? defTop;
            int W = width ?? defW;
            int H = height ?? defH;

            var sb = new StringBuilder();
            var id = GenerateHexId();

            switch (itemType.ToLowerInvariant())
            {
                case "circle":
                    // Circle 需要 Radius 属性
                    sb.AppendLine($"          <{xmlTag} ID=\"{id}\" CompositionName=\"ScreenItems\">");
                    sb.AppendLine("            <AttributeList>");
                    sb.AppendLine("              <BackColor>217, 217, 217</BackColor>");
                    sb.AppendLine("              <BackFillStyle>Solid</BackFillStyle>");
                    sb.AppendLine("              <BorderColor>24, 28, 49</BorderColor>");
                    sb.AppendLine("              <BorderWidth>1</BorderWidth>");
                    sb.AppendLine("              <EdgeStyle>Solid</EdgeStyle>");
                    sb.AppendLine("              <Flashing>None</Flashing>");
                    sb.AppendLine($"              <Height>{H}</Height>");
                    sb.AppendLine($"              <Left>{L}</Left>");
                    sb.AppendLine($"              <ObjectName>{escName}</ObjectName>");
                    // ★修复★ Radius 必须 ≤ 宽高的一半（Radius*2 超过 Width/Height 时 TIA 校验失败，
                    // 导入失败会导致博途进程退出）。按实际尺寸计算半径。
                    sb.AppendLine($"              <Radius>{Math.Max(1, Math.Min(W, H) / 2)}</Radius>");
                    sb.AppendLine("              <TabIndex>-1</TabIndex>");
                    sb.AppendLine($"              <Top>{T}</Top>");
                    sb.AppendLine("              <UseDesignColorSchema>false</UseDesignColorSchema>");
                    sb.AppendLine($"              <Width>{W}</Width>");
                    sb.AppendLine("            </AttributeList>");
                    sb.AppendLine($"          </{xmlTag}>");
                    break;

                case "gauge":
                    // Gauge 是复杂控件，需要子对象（字体、文本等）
                    sb.AppendLine($"          <{xmlTag} ID=\"{id}\" CompositionName=\"ScreenItems\">");
                    sb.AppendLine("            <AttributeList>");
                    sb.AppendLine("              <AngleMax>45</AngleMax>");
                    sb.AppendLine("              <AngleMin>-225</AngleMin>");
                    sb.AppendLine("              <BackColor>0, 255, 255</BackColor>");
                    sb.AppendLine("              <BackFillStyle>FrameTransparent</BackFillStyle>");
                    sb.AppendLine("              <BorderBackColor>255, 255, 255</BorderBackColor>");
                    sb.AppendLine("              <BorderColor>56, 58, 72</BorderColor>");
                    sb.AppendLine("              <BorderWidth>0</BorderWidth>");
                    sb.AppendLine("              <CaptionColor>194, 193, 193</CaptionColor>");
                    sb.AppendLine("              <CaptionTop>0.88</CaptionTop>");
                    sb.AppendLine("              <CenterColor>101, 103, 115</CenterColor>");
                    sb.AppendLine("              <CenterSize>0.12</CenterSize>");
                    sb.AppendLine("              <CompatibilityMode>false</CompatibilityMode>");
                    sb.AppendLine("              <CornerRadius>0</CornerRadius>");
                    sb.AppendLine("              <DialColor>101, 103, 115</DialColor>");
                    sb.AppendLine("              <DialFillStyle>Solid</DialFillStyle>");
                    sb.AppendLine("              <DialSize>1</DialSize>");
                    sb.AppendLine("              <EdgeStyle>Solid</EdgeStyle>");
                    sb.AppendLine("              <Gradation>10</Gradation>");
                    sb.AppendLine($"              <Height>{H}</Height>");
                    sb.AppendLine("              <InnerDialColor>241, 241, 242</InnerDialColor>");
                    sb.AppendLine("              <InnerDialInnerDistance>0.08</InnerDialInnerDistance>");
                    sb.AppendLine("              <InnerDialOuterDistance>0.9</InnerDialOuterDistance>");
                    sb.AppendLine($"              <Left>{L}</Left>");
                    sb.AppendLine("              <LockSquaredExtent>true</LockSquaredExtent>");
                    sb.AppendLine("              <MaximumValue>100</MaximumValue>");
                    sb.AppendLine("              <MinimumValue>0</MinimumValue>");
                    sb.AppendLine("              <NeedleHeight>0.66</NeedleHeight>");
                    sb.AppendLine($"              <ObjectName>{escName}</ObjectName>");
                    sb.AppendLine("              <PointerColor>49, 52, 74</PointerColor>");
                    sb.AppendLine("              <RangeLower1Color>241, 161, 44</RangeLower1Color>");
                    sb.AppendLine("              <RangeLower1Enabled>true</RangeLower1Enabled>");
                    sb.AppendLine("              <RangeLower2Color>237, 88, 97</RangeLower2Color>");
                    sb.AppendLine("              <RangeLower2Enabled>true</RangeLower2Enabled>");
                    sb.AppendLine("              <RangeNormalColor>56, 195, 70</RangeNormalColor>");
                    sb.AppendLine("              <RangeNormalEnabled>true</RangeNormalEnabled>");
                    sb.AppendLine("              <RangeUpper1Color>241, 161, 44</RangeUpper1Color>");
                    sb.AppendLine("              <RangeUpper1Enabled>true</RangeUpper1Enabled>");
                    sb.AppendLine("              <RangeUpper1Start>70</RangeUpper1Start>");
                    sb.AppendLine("              <RangeUpper2Color>237, 88, 97</RangeUpper2Color>");
                    sb.AppendLine("              <RangeUpper2Enabled>true</RangeUpper2Enabled>");
                    sb.AppendLine("              <RangeUpper2Start>85</RangeUpper2Start>");
                    sb.AppendLine("              <ScaleLabelColor>49, 52, 74</ScaleLabelColor>");
                    sb.AppendLine("              <ScaleTickColor>24, 28, 49</ScaleTickColor>");
                    sb.AppendLine("              <ScaleTickLabelPosition>0.72</ScaleTickLabelPosition>");
                    sb.AppendLine("              <ScaleTickLength>0.29</ScaleTickLength>");
                    sb.AppendLine("              <ScaleTickPosition>0.64</ScaleTickPosition>");
                    sb.AppendLine("              <ShowDecimalPoint>false</ShowDecimalPoint>");
                    sb.AppendLine("              <ShowInnerDial>true</ShowInnerDial>");
                    sb.AppendLine("              <ShowLimitRanges>false</ShowLimitRanges>");
                    sb.AppendLine("              <ShowPeakValuePointer>true</ShowPeakValuePointer>");
                    sb.AppendLine("              <TabIndex>-1</TabIndex>");
                    sb.AppendLine($"              <Top>{T}</Top>");
                    sb.AppendLine("              <UnitColor>194, 193, 193</UnitColor>");
                    sb.AppendLine("              <UnitTop>0.8</UnitTop>");
                    sb.AppendLine("              <UseDesignColorSchema>false</UseDesignColorSchema>");
                    sb.AppendLine($"              <Width>{W}</Width>");
                    sb.AppendLine("            </AttributeList>");
                    sb.AppendLine("            <ObjectList>");
                    // CaptionFont
                    var cfId = GenerateHexId();
                    sb.AppendLine($"              <Hmi.Globalization.MultiLingualFont ID=\"{cfId}\" CompositionName=\"CaptionFont\">");
                    sb.AppendLine("                <ObjectList>");
                    var cfiId = GenerateHexId();
                    sb.AppendLine($"                  <Hmi.Globalization.FontItem ID=\"{cfiId}\" CompositionName=\"Items\">");
                    sb.AppendLine("                    <AttributeList>");
                    sb.AppendLine("                      <Culture>zh-CN</Culture>");
                    sb.AppendLine("                      <FontFamily>宋体</FontFamily>");
                    sb.AppendLine("                      <FontSize>13</FontSize>");
                    sb.AppendLine("                      <FontStyle>Regular</FontStyle>");
                    sb.AppendLine("                    </AttributeList>");
                    sb.AppendLine("                  </Hmi.Globalization.FontItem>");
                    sb.AppendLine("                </ObjectList>");
                    sb.AppendLine("              </Hmi.Globalization.MultiLingualFont>");
                    // CaptionText
                    var ctId = GenerateHexId();
                    sb.AppendLine($"              <MultilingualText ID=\"{ctId}\" CompositionName=\"CaptionText\">");
                    sb.AppendLine("                <ObjectList>");
                    var ctiId = GenerateHexId();
                    sb.AppendLine($"                  <MultilingualTextItem ID=\"{ctiId}\" CompositionName=\"Items\">");
                    sb.AppendLine("                    <AttributeList>");
                    sb.AppendLine("                      <Culture>zh-CN</Culture>");
                    sb.AppendLine($"                      <Text>{escName}</Text>");
                    sb.AppendLine("                    </AttributeList>");
                    sb.AppendLine("                  </MultilingualTextItem>");
                    sb.AppendLine("                </ObjectList>");
                    sb.AppendLine("              </MultilingualText>");
                    // ScaleLabelFont
                    var sfId = GenerateHexId();
                    sb.AppendLine($"              <Hmi.Globalization.MultiLingualFont ID=\"{sfId}\" CompositionName=\"ScaleLabelFont\">");
                    sb.AppendLine("                <ObjectList>");
                    var sfiId = GenerateHexId();
                    sb.AppendLine($"                  <Hmi.Globalization.FontItem ID=\"{sfiId}\" CompositionName=\"Items\">");
                    sb.AppendLine("                    <AttributeList>");
                    sb.AppendLine("                      <Culture>zh-CN</Culture>");
                    sb.AppendLine("                      <FontFamily>宋体</FontFamily>");
                    sb.AppendLine("                      <FontSize>15</FontSize>");
                    sb.AppendLine("                      <FontStyle>Bold</FontStyle>");
                    sb.AppendLine("                    </AttributeList>");
                    sb.AppendLine("                  </Hmi.Globalization.FontItem>");
                    sb.AppendLine("                </ObjectList>");
                    sb.AppendLine("              </Hmi.Globalization.MultiLingualFont>");
                    // UnitFont
                    var ufId = GenerateHexId();
                    sb.AppendLine($"              <Hmi.Globalization.MultiLingualFont ID=\"{ufId}\" CompositionName=\"UnitFont\">");
                    sb.AppendLine("                <ObjectList>");
                    var ufiId = GenerateHexId();
                    sb.AppendLine($"                  <Hmi.Globalization.FontItem ID=\"{ufiId}\" CompositionName=\"Items\">");
                    sb.AppendLine("                    <AttributeList>");
                    sb.AppendLine("                      <Culture>zh-CN</Culture>");
                    sb.AppendLine("                      <FontFamily>宋体</FontFamily>");
                    sb.AppendLine("                      <FontSize>15</FontSize>");
                    sb.AppendLine("                      <FontStyle>Bold</FontStyle>");
                    sb.AppendLine("                    </AttributeList>");
                    sb.AppendLine("                  </Hmi.Globalization.FontItem>");
                    sb.AppendLine("                </ObjectList>");
                    sb.AppendLine("              </Hmi.Globalization.MultiLingualFont>");
                    // UnitText
                    var utId = GenerateHexId();
                    sb.AppendLine($"              <MultilingualText ID=\"{utId}\" CompositionName=\"UnitText\">");
                    sb.AppendLine("                <ObjectList>");
                    var utiId = GenerateHexId();
                    sb.AppendLine($"                  <MultilingualTextItem ID=\"{utiId}\" CompositionName=\"Items\">");
                    sb.AppendLine("                    <AttributeList>");
                    sb.AppendLine("                      <Culture>zh-CN</Culture>");
                    sb.AppendLine("                      <Text>Unit</Text>");
                    sb.AppendLine("                    </AttributeList>");
                    sb.AppendLine("                  </MultilingualTextItem>");
                    sb.AppendLine("                </ObjectList>");
                    sb.AppendLine("              </MultilingualText>");
                    sb.AppendLine("            </ObjectList>");
                    sb.AppendLine($"          </{xmlTag}>");
                    break;

                case "bar":
                    // Bar 是复杂控件，需要字体子对象
                    sb.AppendLine($"          <{xmlTag} ID=\"{id}\" CompositionName=\"ScreenItems\">");
                    sb.AppendLine("            <AttributeList>");
                    sb.AppendLine("              <BackColor>241, 241, 242</BackColor>");
                    sb.AppendLine("              <BarBackColor>0, 255, 255</BarBackColor>");
                    sb.AppendLine("              <BarEdgeStyle>Solid</BarEdgeStyle>");
                    sb.AppendLine("              <BarOrientation>Up</BarOrientation>");
                    sb.AppendLine("              <BorderBackColor>101, 103, 115</BorderBackColor>");
                    sb.AppendLine("              <BorderColor>71, 73, 87</BorderColor>");
                    sb.AppendLine("              <BorderWidth>7</BorderWidth>");
                    sb.AppendLine("              <CompatibilityMode>false</CompatibilityMode>");
                    sb.AppendLine("              <CornerRadius>4</CornerRadius>");
                    sb.AppendLine("              <CountSubDivisions>5</CountSubDivisions>");
                    sb.AppendLine("              <EdgeStyle>Double</EdgeStyle>");
                    sb.AppendLine("              <Flashing>None</Flashing>");
                    sb.AppendLine("              <FlashingOnLimitViolation>false</FlashingOnLimitViolation>");
                    sb.AppendLine("              <ForeColor>0, 122, 204</ForeColor>");
                    sb.AppendLine($"              <Height>{H}</Height>");
                    sb.AppendLine("              <IntegerDigits>3</IntegerDigits>");
                    sb.AppendLine("              <LargeTickLabelingStep>2</LargeTickLabelingStep>");
                    sb.AppendLine($"              <Left>{L}</Left>");
                    sb.AppendLine("              <MaximumValue>100</MaximumValue>");
                    sb.AppendLine("              <MinimumValue>0</MinimumValue>");
                    sb.AppendLine($"              <ObjectName>{escName}</ObjectName>");
                    sb.AppendLine("              <Precision>0</Precision>");
                    sb.AppendLine("              <RangeLower1Color>241, 161, 44</RangeLower1Color>");
                    sb.AppendLine("              <RangeLower1Enabled>true</RangeLower1Enabled>");
                    sb.AppendLine("              <RangeLower2Color>241, 161, 44</RangeLower2Color>");
                    sb.AppendLine("              <RangeLower2Enabled>true</RangeLower2Enabled>");
                    sb.AppendLine("              <RangeNormalColor>56, 195, 70</RangeNormalColor>");
                    sb.AppendLine("              <RangeNormalEnabled>true</RangeNormalEnabled>");
                    sb.AppendLine("              <RangeUpper1Color>241, 161, 44</RangeUpper1Color>");
                    sb.AppendLine("              <RangeUpper1Enabled>true</RangeUpper1Enabled>");
                    sb.AppendLine("              <RangeUpper2Color>237, 88, 97</RangeUpper2Color>");
                    sb.AppendLine("              <RangeUpper2Enabled>true</RangeUpper2Enabled>");
                    sb.AppendLine("              <ScaleColor>49, 52, 74</ScaleColor>");
                    sb.AppendLine("              <ScaleGradation>10</ScaleGradation>");
                    sb.AppendLine("              <ScaleLabelingDoubleLined>false</ScaleLabelingDoubleLined>");
                    sb.AppendLine("              <ScalePosition>LeftUp</ScalePosition>");
                    sb.AppendLine("              <SegmentColoring>Entire</SegmentColoring>");
                    sb.AppendLine("              <ShowLimitLines>false</ShowLimitLines>");
                    sb.AppendLine("              <ShowLimitMarkers>true</ShowLimitMarkers>");
                    sb.AppendLine("              <ShowLimitRanges>false</ShowLimitRanges>");
                    sb.AppendLine("              <ShowProcessValue>true</ShowProcessValue>");
                    sb.AppendLine("              <ShowScale>true</ShowScale>");
                    sb.AppendLine("              <ShowTickLabels>true</ShowTickLabels>");
                    sb.AppendLine("              <TabIndex>-1</TabIndex>");
                    sb.AppendLine($"              <Top>{T}</Top>");
                    sb.AppendLine("              <Unit />");
                    sb.AppendLine("              <UseAutoScaling>false</UseAutoScaling>");
                    sb.AppendLine("              <UseDesignColorSchema>false</UseDesignColorSchema>");
                    sb.AppendLine("              <UseExponentialFormat>false</UseExponentialFormat>");
                    sb.AppendLine($"              <Width>{W}</Width>");
                    sb.AppendLine("            </AttributeList>");
                    sb.AppendLine("            <ObjectList>");
                    // Font
                    var bfId = GenerateHexId();
                    sb.AppendLine($"              <Hmi.Globalization.MultiLingualFont ID=\"{bfId}\" CompositionName=\"Font\">");
                    sb.AppendLine("                <ObjectList>");
                    var bfiId = GenerateHexId();
                    sb.AppendLine($"                  <Hmi.Globalization.FontItem ID=\"{bfiId}\" CompositionName=\"Items\">");
                    sb.AppendLine("                    <AttributeList>");
                    sb.AppendLine("                      <Culture>zh-CN</Culture>");
                    sb.AppendLine("                      <FontFamily>宋体</FontFamily>");
                    sb.AppendLine("                      <FontSize>15</FontSize>");
                    sb.AppendLine("                      <FontStyle>Bold</FontStyle>");
                    sb.AppendLine("                    </AttributeList>");
                    sb.AppendLine("                  </Hmi.Globalization.FontItem>");
                    sb.AppendLine("                </ObjectList>");
                    sb.AppendLine("              </Hmi.Globalization.MultiLingualFont>");
                    sb.AppendLine("            </ObjectList>");
                    sb.AppendLine($"          </{xmlTag}>");
                    break;

                case "graphicview":
                    // GraphicView 简单控件
                    sb.AppendLine($"          <{xmlTag} ID=\"{id}\" CompositionName=\"ScreenItems\">");
                    sb.AppendLine("            <AttributeList>");
                    sb.AppendLine("              <AutoSizing>StretchPicture</AutoSizing>");
                    sb.AppendLine("              <BackColor>173, 174, 181</BackColor>");
                    sb.AppendLine("              <BackFillStyle>Solid</BackFillStyle>");
                    sb.AppendLine("              <BorderColor>0, 0, 0</BorderColor>");
                    sb.AppendLine("              <BorderWidth>0</BorderWidth>");
                    sb.AppendLine("              <EdgeStyle>Solid</EdgeStyle>");
                    sb.AppendLine("              <FitToLargest>false</FitToLargest>");
                    sb.AppendLine("              <Flashing>None</Flashing>");
                    sb.AppendLine($"              <Height>{H}</Height>");
                    sb.AppendLine($"              <Left>{L}</Left>");
                    sb.AppendLine($"              <ObjectName>{escName}</ObjectName>");
                    sb.AppendLine("              <TabIndex>-1</TabIndex>");
                    sb.AppendLine($"              <Top>{T}</Top>");
                    sb.AppendLine("              <TransparentColor>255, 0, 255</TransparentColor>");
                    sb.AppendLine("              <UseTransparentColor>false</UseTransparentColor>");
                    sb.AppendLine($"              <Width>{W}</Width>");
                    sb.AppendLine("            </AttributeList>");
                    sb.AppendLine($"          </{xmlTag}>");
                    break;

                default:
                    // 通用简单控件（button, textfield, iofield, rectangle, switch）
                    sb.AppendLine($"          <{xmlTag} ID=\"{id}\" CompositionName=\"ScreenItems\">");
                    sb.AppendLine("            <AttributeList>");
                    sb.AppendLine($"              <ObjectName>{escName}</ObjectName>");
                    sb.AppendLine($"              <Left>{L}</Left>");
                    sb.AppendLine($"              <Top>{T}</Top>");
                    sb.AppendLine($"              <Width>{W}</Width>");
                    sb.AppendLine($"              <Height>{H}</Height>");
                    sb.AppendLine("            </AttributeList>");
                    sb.AppendLine("            <ObjectList />");
                    sb.AppendLine($"          </{xmlTag}>");
                    break;
            }

            return sb.ToString();
        }

        /// <summary>
        /// 生成唯一的十六进制 ID（用于画面项 XML）。
        /// 使用大数值避免与导出 XML 中已有的小 ID 冲突。
        /// </summary>
        private static int _nextItemId = 0x1000;
        private static string GenerateHexId()
        {
            var id = System.Threading.Interlocked.Increment(ref _nextItemId);
            return id.ToString("X");
        }

        /// <summary>
        /// 将画面项 XML 注入到画面 XML 中，并直接写入文件（带 UTF-8 BOM）。
        /// 如果画面没有 ScreenLayer，则创建一个。
        /// 自动检测已有 ID 并避免冲突。
        /// </summary>
        private static void InjectScreenItemXmlToFile(string screenXml, string itemXml, string outputPath)
        {
            var doc = new System.Xml.XmlDocument();
            doc.PreserveWhitespace = true;
            doc.LoadXml(screenXml);

            // 扫描已有 XML 中所有 ID，避免冲突
            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Xml.XmlNode node in doc.SelectNodes("//*[@ID]"))
            {
                var idVal = node.Attributes?["ID"]?.Value;
                if (!string.IsNullOrEmpty(idVal)) existingIds.Add(idVal);
            }

            // 找到 Hmi.Screen.Screen 节点
            var screenNode = doc.SelectSingleNode("//Hmi.Screen.Screen");
            if (screenNode == null) throw new Exception("画面 XML 中未找到 Hmi.Screen.Screen 节点");

            // 找到 ObjectList 节点（Screen 的直接子节点）
            var screenObjectList = screenNode.SelectSingleNode("ObjectList");
            if (screenObjectList == null)
            {
                screenObjectList = doc.CreateElement("ObjectList");
                screenNode.AppendChild(screenObjectList);
            }

            // 在 ObjectList 中查找 ScreenLayer
            var screenLayer = screenObjectList.SelectSingleNode("Hmi.Screen.ScreenLayer");
            if (screenLayer == null)
            {
                // 创建 ScreenLayer
                screenLayer = doc.CreateElement("Hmi.Screen.ScreenLayer");
                var idAttr = doc.CreateAttribute("ID");
                idAttr.Value = GetUniqueId(existingIds);
                screenLayer.Attributes.Append(idAttr);
                var compAttr = doc.CreateAttribute("CompositionName");
                compAttr.Value = "Layers";
                screenLayer.Attributes.Append(compAttr);

                var layerAttrList = doc.CreateElement("AttributeList");
                var indexElem = doc.CreateElement("Index");
                indexElem.InnerText = "0";
                layerAttrList.AppendChild(indexElem);
                var layerNameElem = doc.CreateElement("Name");
                layerAttrList.AppendChild(layerNameElem);
                var visibleElem = doc.CreateElement("VisibleES");
                visibleElem.InnerText = "true";
                layerAttrList.AppendChild(visibleElem);
                screenLayer.AppendChild(layerAttrList);

                var layerObjectList = doc.CreateElement("ObjectList");
                screenLayer.AppendChild(layerObjectList);

                screenObjectList.AppendChild(screenLayer);
            }

            // 找到 ScreenLayer 的 ObjectList
            var layerObjectListNode = screenLayer.SelectSingleNode("ObjectList");
            if (layerObjectListNode == null)
            {
                layerObjectListNode = doc.CreateElement("ObjectList");
                screenLayer.AppendChild(layerObjectListNode);
            }

            // 将画面项 XML 解析为节点并添加，同时替换 ID 避免冲突
            var itemFragment = doc.CreateDocumentFragment();
            itemFragment.InnerXml = itemXml;
            // 替换新节点中所有 ID 属性
            foreach (System.Xml.XmlNode newNode in itemFragment.SelectNodes("//*[@ID]"))
            {
                if (newNode.Attributes?["ID"] != null)
                {
                    var oldId = newNode.Attributes["ID"].Value;
                    if (existingIds.Contains(oldId))
                    {
                        newNode.Attributes["ID"].Value = GetUniqueId(existingIds);
                    }
                    existingIds.Add(newNode.Attributes["ID"].Value);
                }
            }
            layerObjectListNode.AppendChild(itemFragment);

            // 直接写入文件，带 UTF-8 BOM
            var bom = new UTF8Encoding(true);
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            using (var xw = System.Xml.XmlWriter.Create(fs, new System.Xml.XmlWriterSettings
            {
                Encoding = bom,
                Indent = true,
                OmitXmlDeclaration = false
            }))
            {
                doc.Save(xw);
            }
        }

        /// <summary>
        /// 生成不与已有 ID 冲突的唯一 ID。
        /// </summary>
        private static string GetUniqueId(HashSet<string> existingIds)
        {
            while (true)
            {
                var id = GenerateHexId();
                if (!existingIds.Contains(id)) return id;
            }
        }

        public string DeleteHmiScreenItem(string screenName, string itemName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var item = FindScreenItem(hmi, screenName, itemName);
                    if (item == null) return Err($"未找到画面项: {itemName}");
                    var deleteM = item.GetType().GetMethod("Delete");
                    if (deleteM == null) return Err("画面项不支持 Delete 方法");
                    deleteM.Invoke(item, null);
                    return Ok($"已删除画面项: {screenName}.{itemName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string SetHmiScreenItemProperty(string screenName, string itemName, string propertyName, string value)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var item = FindScreenItem(hmi, screenName, itemName);
                    if (item == null) return Err($"未找到画面项: {itemName}");

                    var prop = item.GetType().GetProperty(propertyName);
                    if (prop == null)
                    {
                        var avail = string.Join(", ", item.GetType().GetProperties().Select(p => p.Name));
                        return Err($"属性 '{propertyName}' 不存在。可用属性: {avail}");
                    }
                    if (!prop.CanWrite) return Err($"属性 '{propertyName}' 不可写");

                    var targetType = prop.PropertyType;
                    object converted;
                    try
                    {
                        if (targetType == typeof(int) || targetType == typeof(short) || targetType == typeof(long))
                            converted = Convert.ChangeType(value, targetType);
                        else if (targetType == typeof(bool))
                            converted = bool.Parse(value);
                        else if (targetType == typeof(double) || targetType == typeof(float))
                            converted = Convert.ChangeType(value, targetType);
                        else
                            converted = value;
                    }
                    catch { return Err($"无法将 '{value}' 转换为 {targetType.Name}"); }

                    prop.SetValue(item, converted);
                    return Ok($"{screenName}.{itemName}.{propertyName} = {value}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string BindHmiScreenItemTag(string screenName, string itemName, string tagName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var item = FindScreenItem(hmi, screenName, itemName);
                    if (item == null) return Err($"未找到画面项: {itemName}");
                    BindItemTag(hmi, item, tagName);
                    return Ok($"已绑定 {screenName}.{itemName} → {tagName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ExploreHmiScreenItem(string screenName, string itemName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var item = FindScreenItem(hmi, screenName, itemName);
                    if (item == null) return Err($"未找到画面项: {itemName}");

                    var props = item.GetType()
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => new
                        {
                            name = p.Name,
                            type = p.PropertyType.Name,
                            canRead = p.CanRead,
                            canWrite = p.CanWrite,
                            value = TryGetProp(p, item)
                        }).ToList();

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        screenName,
                        itemName,
                        properties = props,
                        itemType = item.GetType().FullName
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private void BindItemTag(HmiTarget hmi, object item, string tagName)
        {
            // 尝试用多种属性名绑定 ProcessValue / Tag
            var candidates = new[] { "ProcessValue", "Tag", "TagName", "OutputValue", "InputValue" };
            var tag = FindTagRecursive(hmi.TagFolder, tagName);
            if (tag == null)
            {
                throw new InvalidOperationException($"未找到变量: {tagName}");
            }
            foreach (var cand in candidates)
            {
                var prop = item.GetType().GetProperty(cand);
                if (prop != null && prop.CanWrite)
                {
                    try { prop.SetValue(item, tag); return; }
                    catch { }
                }
            }
            // 兜底：写入 TagName 字符串属性
            var tnProp = item.GetType().GetProperty("TagName");
            if (tnProp != null && tnProp.CanWrite)
                tnProp.SetValue(item, tagName);
        }

        // ── ScreenItems 辅助：通过 Layers[0] 访问（对照导出 XML：ScreenLayer → ObjectList → ScreenItems）──
        // fix#19: 参数放宽为 object 以兼容 Screen 与 ScreenTemplate（内部本为反射实现）
        private static object? GetScreenItemsCollection(object screen)
        {
            var screenType = screen.GetType();

            // 优先尝试 Layers[0].ScreenItems
            var layersProp = screenType.GetProperty("Layers");
            if (layersProp != null)
            {
                var layers = layersProp.GetValue(screen) as IEnumerable;
                if (layers != null)
                {
                    foreach (var layer in layers)
                    {
                        var layerType = layer.GetType();
                        var siProp = layerType.GetProperty("ScreenItems")
                            ?? layerType.GetProperty("Items")
                            ?? layerType.GetProperty("ObjectList");
                        if (siProp != null)
                        {
                            var val = siProp.GetValue(layer);
                            if (val != null) return val;
                        }
                    }
                }
            }

            // 回退：直接尝试 Screen.ScreenItems / Screen.Items
            var directProp = screenType.GetProperty("ScreenItems")
                ?? screenType.GetProperty("Items");
            if (directProp != null)
            {
                var val = directProp.GetValue(screen);
                if (val != null) return val;
            }

            return null;
        }

        /// <summary>
        /// 诊断：返回 Screen 对象的所有属性、方法和 GetService 接口（用于调试）。
        /// </summary>
        public string DiagnoseScreen(string screenName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // fix#18: 跨 4 类画面文件夹查找
                    var (screen, screenFolder) = FindScreenAny(hmi, screenName);
                    if (screen == null) return Err($"未找到画面: {screenName}（已检索常规/模板/弹出/滑入画面区）");

                    var screenType = screen.GetType();
                    var props = screenType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => new { name = p.Name, type = p.PropertyType.Name, value = SafeGetProp(p, screen) })
                        .ToList();

                    var methods = screenType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object))
                        .Select(m => new
                        {
                            name = m.Name,
                            returnType = m.ReturnType.Name,
                            parameters = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))
                        })
                        .ToList();

                    // 尝试常见的服务接口
                    var serviceTests = new List<object>();

                    // 枚举 Siemens.Engineering 程序集中所有与 Screen/Item/Layer 相关的类型
                    var candidateTypes = new List<Type>();
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (!asm.GetName().Name.StartsWith("Siemens.Engineering")) continue;
                        try
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                var tn = t.FullName ?? "";
                                if (tn.Contains("Screen") || tn.Contains("ScreenItem") || tn.Contains("ScreenLayer"))
                                    candidateTypes.Add(t);
                            }
                        }
                        catch { }
                    }

                    // GetService<T>() 是泛型方法
                    var getServiceGeneric = screenType.GetMethods()
                        .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethod);

                    foreach (var st in candidateTypes)
                    {
                        try
                        {
                            if (getServiceGeneric != null && st.IsClass)
                            {
                                var getService = getServiceGeneric.MakeGenericMethod(st);
                                var svc = getService.Invoke(screen, null);
                                if (svc != null)
                                {
                                    serviceTests.Add(new { serviceType = st.FullName, found = true, svcType = svc.GetType().Name });
                                }
                            }
                        }
                        catch (TargetInvocationException tie)
                        {
                            // 预期大部分会失败，只记录非 "not supported" 的错误
                            var msg = tie.InnerException?.Message ?? tie.Message;
                            if (!msg.Contains("not supported") && !msg.Contains("不支持"))
                                serviceTests.Add(new { serviceType = st.FullName, found = false, svcType = msg });
                        }
                        catch (Exception)
                        {
                            // 忽略
                        }
                    }

                    // 如果没有找到任何服务，记录候选类型列表
                    if (serviceTests.Count == 0)
                    {
                        serviceTests.Add(new { serviceType = $"(no services found; {candidateTypes.Count} candidate types tested)", found = false, svcType = "" });
                        // 列出所有候选类型
                        foreach (var ct in candidateTypes)
                        {
                            serviceTests.Add(new { serviceType = ct.FullName, found = false, svcType = "tested" });
                        }
                    }

                    // 调用 GetAttributeInfos() 列出所有可用属性
                    var attrInfos = new List<object>();
                    try
                    {
                        var getAttrInfos = screenType.GetMethod("GetAttributeInfos");
                        if (getAttrInfos != null)
                        {
                            var infos = getAttrInfos.Invoke(screen, null) as IEnumerable;
                            if (infos != null)
                            {
                                foreach (var info in infos)
                                {
                                    var it = info.GetType();
                                    attrInfos.Add(new
                                    {
                                        name = it.GetProperty("Name")?.GetValue(info)?.ToString(),
                                        type = it.GetProperty("Type")?.GetValue(info)?.ToString() ?? it.GetProperty("ValueType")?.GetValue(info)?.ToString()
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex) { attrInfos.Add(new { name = "ERROR", type = ex.Message }); }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        screenName = GetNameOf(screen) ?? screenName,
                        screenFolder,
                        screenType = screenType.FullName,
                        baseType = screenType.BaseType?.FullName,
                        interfaces = screenType.GetInterfaces().Select(i => i.Name).ToList(),
                        properties = props,
                        methods = methods,
                        services = serviceTests,
                        attributeInfos = attrInfos
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private static string SafeGetProp(PropertyInfo p, object obj)
        {
            try
            {
                var v = p.GetValue(obj);
                if (v == null) return "null";
                if (v is IEnumerable ie && !(v is string))
                {
                    var count = 0;
                    foreach (var _ in ie) count++;
                    return $"[{count} items]";
                }
                return v.ToString() ?? "null";
            }
            catch (Exception ex) { return $"ERR: {ex.Message}"; }
        }

        // ── 空画面 XML 模板（用于 CreateHmiScreen 的 XML Import 方式）──
        private static string GenerateEmptyScreenXml(string name, int width = 800, int height = 480, string engVersion = "", string templateName = "模板_1")
        {
            if (string.IsNullOrWhiteSpace(engVersion)) engVersion = EnvironmentDiscoveryService.CurrentEngineeringVersion();
            // ★V17 校准★ Created 时间格式与 TIA 导出样本一致（7 位小数 + Z，参考 HmiScreenXmlBuilder）
            var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
            var escName = System.Security.SecurityElement.Escape(name);
            // ★V17 修复★ templateName 为空时不输出模板链接，由 TIA 导入时为画面分配设备默认模板
            // （部分 V17 面板的 ScreenTemplateFolder 为空，硬编码模板名会在编译时报
            // "Invalid template for screen"）。
            var templateLink = string.IsNullOrWhiteSpace(templateName)
                ? ""
                : $@"    <LinkList>
      <Template TargetID=""@OpenLink"">
        <Name>{System.Security.SecurityElement.Escape(templateName)}</Name>
      </Template>
    </LinkList>
";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""{engVersion}"" />
  <DocumentInfo>
    <Created>{created}</Created>
    <ExportSetting>WithDefaults</ExportSetting>
    <InstalledProducts>
      <Product>
        <DisplayName>Totally Integrated Automation Portal</DisplayName>
        <DisplayVersion>{engVersion}</DisplayVersion>
      </Product>
      <OptionPackage>
        <DisplayName>TIA Portal Openness</DisplayName>
        <DisplayVersion>{engVersion}</DisplayVersion>
      </OptionPackage>
      <OptionPackage>
        <DisplayName>TIA Portal Version Control Interface</DisplayName>
        <DisplayVersion>{engVersion}</DisplayVersion>
      </OptionPackage>
      <Product>
        <DisplayName>STEP 7 Professional</DisplayName>
        <DisplayVersion>{engVersion}</DisplayVersion>
      </Product>
      <OptionPackage>
        <DisplayName>STEP 7 Safety</DisplayName>
        <DisplayVersion>{engVersion}</DisplayVersion>
      </OptionPackage>
      <Product>
        <DisplayName>WinCC Professional</DisplayName>
        <DisplayVersion>{engVersion}</DisplayVersion>
      </Product>
    </InstalledProducts>
  </DocumentInfo>
  <Hmi.Screen.Screen ID=""0"">
    <AttributeList>
      <ActiveLayer>0</ActiveLayer>
      <BackColor>182, 182, 182</BackColor>
      <GridColor>0, 0, 0</GridColor>
      <Height>{height}</Height>
      <Name>{escName}</Name>
      <Visible>true</Visible>
      <Width>{width}</Width>
    </AttributeList>
{templateLink}    <ObjectList>
      <MultilingualText ID=""100"" CompositionName=""HelpText"">
        <ObjectList>
          <MultilingualTextItem ID=""101"" CompositionName=""Items"">
            <AttributeList>
              <Culture>zh-CN</Culture>
              <Text />
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
      <Hmi.Screen.ScreenLayer ID=""102"" CompositionName=""Layers"">
        <AttributeList>
          <Index>0</Index>
          <Name />
          <VisibleES>true</VisibleES>
        </AttributeList>
        <ObjectList />
      </Hmi.Screen.ScreenLayer>
    </ObjectList>
  </Hmi.Screen.Screen>
</Document>";
        }

        private object? FindScreenItem(HmiTarget hmi, string screenName, string itemName)
        {
            // fix#18: 跨 4 类画面文件夹查找（Delete/SetProp/Explore 等画面项工具共用此入口）
            var (screen, _) = FindScreenAny(hmi, screenName);
            if (screen == null) return null;
            // 画面项嵌套在 Screen.Layers[0].ScreenItems 中
            var screenItems = GetScreenItemsCollection(screen);
            if (screenItems is IEnumerable items)
            {
                foreach (var i in items)
                {
                    var np = i.GetType().GetProperty("Name");
                    var name = np?.GetValue(i)?.ToString();
                    // V17 may expose a generic Name and the engineering name separately.
                    var objectName = i.GetType().GetProperty("ObjectName")?.GetValue(i)?.ToString();
                    if (string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(objectName, itemName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            return null;
        }

        private static string TryGetProp(PropertyInfo p, object obj)
        {
            try { return p.GetValue(obj)?.ToString() ?? "null"; }
            catch { return "<error>"; }
        }

        // ────────────────────────────────────────────────────────────
        // HMI 变量（反射访问 TagFolder）
        // ────────────────────────────────────────────────────────────

        private static IEnumerable<Tag> GetAllTags(object folder)
        {
            var ttProp = folder.GetType().GetProperty("TagTables");
            if (ttProp != null && ttProp.GetValue(folder) is IEnumerable tables)
            {
                foreach (var table in tables)
                {
                    var tgProp = table.GetType().GetProperty("Tags");
                    if (tgProp?.GetValue(table) is IEnumerable tags)
                        foreach (Tag tag in tags) yield return tag;
                }
            }
            var fProp = folder.GetType().GetProperty("Folders");
            if (fProp != null && fProp.GetValue(folder) is IEnumerable subFolders)
            {
                foreach (var sf in subFolders)
                    foreach (var t in GetAllTags(sf)) yield return t;
            }
        }

        private static IEnumerable<TagTable> GetAllTagTables(object folder)
        {
            var ttProp = folder.GetType().GetProperty("TagTables");
            if (ttProp != null && ttProp.GetValue(folder) is IEnumerable tables)
                foreach (TagTable t in tables) yield return t;
            var fProp = folder.GetType().GetProperty("Folders");
            if (fProp != null && fProp.GetValue(folder) is IEnumerable subFolders)
                foreach (var sf in subFolders)
                    foreach (var t in GetAllTagTables(sf)) yield return t;
        }

        private TagTable? FindTagTableRecursive(object folder, string name)
        {
            var ttProp = folder.GetType().GetProperty("TagTables");
            if (ttProp != null && ttProp.GetValue(folder) is IEnumerable tables)
            {
                foreach (TagTable t in tables)
                    if (t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return t;
            }
            var fProp = folder.GetType().GetProperty("Folders");
            if (fProp != null && fProp.GetValue(folder) is IEnumerable subFolders)
            {
                foreach (var sf in subFolders)
                {
                    var found = FindTagTableRecursive(sf, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private Tag? FindTagRecursive(object folder, string name)
        {
            var ttProp = folder.GetType().GetProperty("TagTables");
            if (ttProp != null && ttProp.GetValue(folder) is IEnumerable tables)
            {
                foreach (var table in tables)
                {
                    var tgProp = table.GetType().GetProperty("Tags");
                    var tags = tgProp?.GetValue(table);
                    var findM = tags?.GetType().GetMethod("Find", new[] { typeof(string) });
                    if (findM != null && tags != null)
                    {
                        if (findM.Invoke(tags, new object[] { name }) is Tag found)
                            return found;
                    }
                }
            }
            var fProp = folder.GetType().GetProperty("Folders");
            if (fProp != null && fProp.GetValue(folder) is IEnumerable subFolders)
            {
                foreach (var sf in subFolders)
                {
                    var found = FindTagRecursive(sf, name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public string ListHmiTags()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = GetAllTags(hmi.TagFolder).Select(t => new { name = t.Name }).ToList();
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, tags = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ListHmiTagTables()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = GetAllTagTables(hmi.TagFolder).Select(t => new { name = t.Name }).ToList();
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, tagTables = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateHmiTag(string tagName, string? tagTableName = null, string? connection = null,
            string? plcTag = null, string? address = null, string? dataType = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(tagName)) return Err("HMI 变量名不能为空");
                    var type = string.IsNullOrWhiteSpace(dataType) ? "Bool" : dataType!.Trim();

                    // Classic HMI Tag 的公开对象模型在不同面板/版本间并不稳定。
                    // 统一走 Siemens 支持的 XML Import 路径，并在导入后验证对象确实存在，禁止反射假成功。
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        var payload = JsonConvert.SerializeObject(new[]
                        {
                            new { name = tagName, dataType = type, address, connection }
                        });
                        return ImportHmiTagsAbsolute(payload, connection, tagTableName);
                    }

                    var symbolicPayload = JsonConvert.SerializeObject(new[]
                    {
                        new { tagName, dataType = type, plcTag, connection }
                    });
                    if (!string.IsNullOrWhiteSpace(tagTableName))
                    {
                        var hmi = RequireClassicHmi();
                        var requested = FindTagTableRecursive(hmi.TagFolder, tagTableName!);
                        if (requested == null) return Err($"未找到变量表: {tagTableName}");
                        if (!ReferenceEquals(requested, hmi.TagFolder.DefaultTagTable))
                            return Err("符号变量当前只能安全导入默认变量表；指定变量表请使用 import_hmi_tags_absolute，或先导出该表作为模板后再导入");
                    }
                    return BatchCreateHmiTags(symbolicPayload);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteHmiTag(string tagName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var tag = FindTagRecursive(hmi.TagFolder, tagName);
                    if (tag == null) return Err($"未找到 HMI 变量: {tagName}");
                    tag.Delete();
                    return Ok($"已删除 HMI 变量: {tagName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// ★V17探测★ 反射 HmiTarget.Connections 组合对象：列出可用方法、尝试 Create(name)。
        /// 用于判定 V17 经典 HMI 能否通过 Openness 直接创建集成连接。
        /// </summary>
        public string ProbeHmiConnectionApi(string? createName = null)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var conns = hmi.Connections;
                    var t = conns.GetType();
                    var methods = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")
                        .Distinct()
                        .Take(50)
                        .ToList();

                    // ★接口契约探测★ IEngineeringComposition.Create(Type, IEnumerable) / GetCreationInfos()
                    var engComp = conns as Siemens.Engineering.IEngineeringComposition;
                    var creationInfos = new List<object>();
                    string? creationInfosError = null;
                    if (engComp != null)
                    {
                        try
                        {
                            var cis = engComp.GetCreationInfos();
                            if (cis != null)
                            {
                                foreach (var ci in cis)
                                {
                                    var dict = new Dictionary<string, object?>();
                                    foreach (var p in ci.GetType().GetProperties())
                                    {
                                        try
                                        {
                                            var v = p.GetValue(ci);
                                            if (v is string || v == null || v.GetType().IsPrimitive || v.GetType().IsEnum)
                                                dict[p.Name] = v?.ToString();
                                            else
                                                dict[p.Name] = v.GetType().FullName + "(" + (v as System.Collections.IEnumerable)?.GetType().FullName + ")";
                                        }
                                        catch { }
                                    }
                                    creationInfos.Add(dict);
                                }
                            }
                        }
                        catch (Exception ex) { creationInfosError = ex.InnerException?.Message ?? ex.Message; }
                    }

                    var name = string.IsNullOrWhiteSpace(createName) ? "HMI_连接_1" : createName!.Trim();
                    var attemptResults = new List<object>();
                    if (engComp != null)
                    {
                        var elemType = typeof(Siemens.Engineering.Hmi.Communication.Connection);
                        // 尝试 1：空初始化参数
                        try
                        {
                            var init = new List<KeyValuePair<string, object>>();
                            var obj = engComp.Create(elemType, init);
                            attemptResults.Add(new
                            {
                                mode = "empty-init",
                                ok = true,
                                createdType = obj?.GetType().FullName,
                                createdName = SafeName(obj)
                            });
                        }
                        catch (Exception ex)
                        {
                            attemptResults.Add(new { mode = "empty-init", ok = false, err = ex.InnerException?.Message ?? ex.Message, errType = ex.InnerException?.GetType().FullName ?? ex.GetType().FullName });
                        }
                        // 尝试 2：Name 初始化参数
                        try
                        {
                            var init = new List<KeyValuePair<string, object>>
                            {
                                new KeyValuePair<string, object>("Name", name)
                            };
                            var obj = engComp.Create(elemType, init);
                            attemptResults.Add(new
                            {
                                mode = "init-name",
                                ok = true,
                                createdType = obj?.GetType().FullName,
                                createdName = SafeName(obj)
                            });
                        }
                        catch (Exception ex)
                        {
                            attemptResults.Add(new { mode = "init-name", ok = false, err = ex.InnerException?.Message ?? ex.Message, errType = ex.InnerException?.GetType().FullName ?? ex.GetType().FullName });
                        }
                    }

                    var afterCount = 0;
                    try { afterCount = conns.Count; } catch { }
                    var afterNames = new List<string>();
                    if (afterCount > 0 && afterCount <= 50)
                    {
                        try { foreach (var c in conns) afterNames.Add(c.Name); } catch { }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        compositionType = t.FullName,
                        methods,
                        creationInfos,
                        creationInfosError,
                        createAttempts = attemptResults,
                        afterCount,
                        afterNames
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private static string? SafeName(object? obj)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty("Name")?.GetValue(obj)?.ToString(); }
            catch { return null; }
        }

        /// <summary>
        /// ★V17探测★ 反射 Siemens.Engineering.Hmi 程序集：列出所有名字含 Connection 的类型及其公开方法；
        /// 同时 dump HmiTarget 自身与连接相关的方法/属性，寻找 Create 之外的连接创建入口。
        /// </summary>
        public string ProbeHmiAssemblies()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // HmiTarget 上所有 PropertyType 含 Connection 的公开属性（不限名字）
                    var connProps = new List<object>();
                    foreach (var p in hmi.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        try
                        {
                            var pt = p.PropertyType;
                            if ((pt.FullName ?? "").IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                object? val = null;
                                string? valType = null;
                                var intf = new List<string>();
                                try
                                {
                                    val = p.GetValue(hmi);
                                    valType = val?.GetType().FullName;
                                    intf = (val?.GetType().GetInterfaces().Select(i => i.FullName).ToList()) ?? intf;
                                }
                                catch { }
                                connProps.Add(new { property = p.Name, declaredType = pt.FullName, valueType = valType, valueInterfaces = intf });
                            }
                        }
                        catch { }
                    }
                    // 当前 Connections 对象的接口
                    var connsIfaces = hmi.Connections.GetType().GetInterfaces().Select(i => i.FullName).ToList();
                    // ★深挖★ HmiTarget 的 GetCompositionInfos（全部组合名）与非公开成员中连接相关项
                    var compositionInfos = new List<string>();
                    try
                    {
                        var eo = hmi as Siemens.Engineering.IEngineeringObject;
                        var cis = eo?.GetCompositionInfos();
                        if (cis != null)
                        {
                            foreach (var ci in cis)
                            {
                                try
                                {
                                    var dict = new List<string>();
                                    foreach (var p in ci.GetType().GetProperties())
                                    {
                                        try { dict.Add(p.Name + "=" + p.GetValue(ci)?.ToString()); } catch { }
                                    }
                                    compositionInfos.Add(string.Join(" | ", dict));
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    var nonPublicConnProps = new List<string>();
                    try
                    {
                        foreach (var p in hmi.GetType().GetProperties(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        {
                            try
                            {
                                if ((p.PropertyType.FullName ?? "").IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0)
                                    nonPublicConnProps.Add(p.Name + " : " + p.PropertyType.FullName);
                            }
                            catch { }
                        }
                        foreach (var f in hmi.GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                        {
                            try
                            {
                                if ((f.FieldType.FullName ?? "").IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0)
                                    nonPublicConnProps.Add("FIELD " + f.Name + " : " + f.FieldType.FullName);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    var asms = AppDomain.CurrentDomain.GetAssemblies()
                        .Where(a => (a.GetName().Name ?? "").Contains("Siemens.Engineering") || (a.GetName().Name ?? "").Contains("Siemens.Automation"))
                        .ToList();
                    var asmNames = asms.Select(a => a.GetName().Name).ToList();
                    var connTypes = new List<object>();
                    foreach (var asm in asms)
                    {
                        try
                        {
                            foreach (var tp in asm.GetTypes())
                            {
                                if (tp.Name.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0
                                    || tp.Name.IndexOf("Connections", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    connTypes.Add(new
                                    {
                                        assembly = asm.GetName().Name,
                                        typeName = tp.FullName,
                                        isComposition = typeof(System.Collections.IEnumerable).IsAssignableFrom(tp),
                                        methods = tp.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                                            .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")")
                                            .Distinct().Take(25).ToList(),
                                        interfaceMethods = tp.GetInterfaces()
                                            .SelectMany(i => i.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                                .Select(m => i.Name + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"))
                                            .Distinct().Take(40).ToList()
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                    // HmiTarget 自身公开成员（与连接相关）
                    var hmiMembers = hmi.GetType()
                        .GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                        .Select(m => m.MemberType.ToString() + " " + m.Name)
                        .Where(n => n.IndexOf("onnect", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("Partner", StringComparison.OrdinalIgnoreCase) >= 0
                                 || n.IndexOf("Station", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        hmiTargetType = hmi.GetType().FullName,
                        hmiTargetConnectionMembers = hmiMembers,
                        hmiTargetConnectionTypedProperties = connProps,
                        connectionsCompositionInterfaces = connsIfaces,
                        hmiTargetCompositionInfos = compositionInfos,
                        hmiTargetNonPublicConnectionMembers = nonPublicConnProps,
                        connectionTypes = connTypes,
                        assembliesScanned = asmNames
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// ★V17实验★ 用最小 XML 向 Connections.Import 导入一条非集成连接。
        /// interfaceTypeMode: Ethernet(默认,含 InterfaceType=Ethernet + Online=true) / None(不含 InterfaceType)。
        /// 成功后返回连接对象的全部可写属性元数据，供后续 SetAttribute 配置伙伴地址。
        /// </summary>
        public string ImportHmiConnectionMinimal(string connectionName, string? driver = null, string? interfaceTypeMode = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(connectionName)) return Err("连接名不能为空");
                    var hmi = RequireClassicHmi();
                    var conns = hmi.Connections;
                    var actualDriver = string.IsNullOrWhiteSpace(driver) ? "SIMATIC S7 1200" : driver!.Trim();
                    var mode = string.IsNullOrWhiteSpace(interfaceTypeMode) ? "None" : interfaceTypeMode!.Trim();
                    if (!mode.Equals("Ethernet", StringComparison.OrdinalIgnoreCase)
                        && !mode.Equals("None", StringComparison.OrdinalIgnoreCase)
                        && !mode.Equals("PNIE", StringComparison.OrdinalIgnoreCase))
                        return Err("interfaceTypeMode 仅支持 Ethernet / None / PNIE");

                    var escapedName = System.Security.SecurityElement.Escape(connectionName);
                    var sb = new StringBuilder();
                    sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
                    sb.AppendLine("<Document>");
                    sb.AppendLine("  <Engineering version=\"V17\" />");
                    sb.AppendLine("  <DocumentInfo>");
                    sb.AppendLine("    <Created>2026-08-14T00:00:00.0000000Z</Created>");
                    sb.AppendLine("    <ExportSetting>WithDefaults</ExportSetting>");
                    sb.AppendLine("    <InstalledProducts>");
                    sb.AppendLine("      <Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>V17</DisplayVersion></Product>");
                    sb.AppendLine("      <OptionPackage><DisplayName>TIA Portal Openness</DisplayName><DisplayVersion>V17</DisplayVersion></OptionPackage>");
                    sb.AppendLine("      <Product><DisplayName>STEP 7 Professional</DisplayName><DisplayVersion>V17</DisplayVersion></Product>");
                    sb.AppendLine("      <Product><DisplayName>WinCC Professional</DisplayName><DisplayVersion>V17</DisplayVersion></Product>");
                    sb.AppendLine("    </InstalledProducts>");
                    sb.AppendLine("  </DocumentInfo>");
                    sb.AppendLine("  <Hmi.Communication.Connection ID=\"0\">");
                    sb.AppendLine("    <AttributeList>");
                    sb.AppendLine($"      <Name>{escapedName}</Name>");
                    sb.AppendLine($"      <Driver>{System.Security.SecurityElement.Escape(actualDriver)}</Driver>");
                    if (mode.Equals("Ethernet", StringComparison.OrdinalIgnoreCase)
                        || mode.Equals("PNIE", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"      <InterfaceType>{(mode.Equals("PNIE", StringComparison.OrdinalIgnoreCase) ? "PN/IE" : "Ethernet")}</InterfaceType>");
                        sb.AppendLine("      <Online>true</Online>");
                    }
                    sb.AppendLine("    </AttributeList>");
                    sb.AppendLine("    <ObjectList>");
                    string[] areaPtrTypes = { "Coordination", "DateTime", "DateTimereturn", "EventId",
                        "FieldbusReadmailbox", "FieldbusWritemailbox", "HmiIdentification", "Jobmailbox",
                        "ProjectId", "ScreenNumber", "TagManagement" };
                    int nextId = 1;
                    for (int i = 0; i < areaPtrTypes.Length; i++)
                    {
                        var c1 = (nextId++).ToString("X"); var c2 = (nextId++).ToString("X"); var c3 = (nextId++).ToString("X");
                        sb.AppendLine($"      <Hmi.Communication.AreaPointer ID=\"{c1}\" CompositionName=\"AreaPointers\">");
                        sb.AppendLine("        <AttributeList>");
                        sb.AppendLine($"          <AreaPointerType>{areaPtrTypes[i]}</AreaPointerType>");
                        sb.AppendLine("          <LogicalAddress />");
                        sb.AppendLine($"          <NameIndex>{i + 1}</NameIndex>");
                        sb.AppendLine("        </AttributeList>");
                        sb.AppendLine("        <ObjectList>");
                        sb.AppendLine($"          <MultilingualText ID=\"{c2}\" CompositionName=\"Comment\">");
                        sb.AppendLine("            <ObjectList>");
                        sb.AppendLine($"              <MultilingualTextItem ID=\"{c3}\" CompositionName=\"Items\">");
                        sb.AppendLine("                <AttributeList>");
                        sb.AppendLine("                  <Culture>zh-CN</Culture>");
                        sb.AppendLine("                  <Text />");
                        sb.AppendLine("                </AttributeList>");
                        sb.AppendLine("              </MultilingualTextItem>");
                        sb.AppendLine("            </ObjectList>");
                        sb.AppendLine("          </MultilingualText>");
                        sb.AppendLine("        </ObjectList>");
                        sb.AppendLine("      </Hmi.Communication.AreaPointer>");
                    }
                    sb.AppendLine("    </ObjectList>");
                    sb.AppendLine("  </Hmi.Communication.Connection>");
                    sb.AppendLine("</Document>");

                    var tempFile = Path.Combine(Path.GetTempPath(), $"tia_minconn_{Guid.NewGuid():N}.xml");
                    Connection? createdConn = null;
                    string importError = "";
                    try
                    {
                        File.WriteAllText(tempFile, sb.ToString(), new UTF8Encoding(true));
                        var imported = conns.Import(new FileInfo(tempFile), ImportOptions.Override);
                        createdConn = imported.FirstOrDefault() ?? conns.Find(connectionName);
                    }
                    catch (Exception ex)
                    {
                        importError = (ex.InnerException?.Message ?? ex.Message) + " | " + (ex.InnerException?.GetType().FullName ?? ex.GetType().FullName);
                    }
                    finally { try { File.Delete(tempFile); } catch { } }

                    if (createdConn == null)
                        return JsonConvert.SerializeObject(new { success = false, error = "导入未产生连接对象。", importError }, Formatting.Indented);

                    var attrInfos = new List<object>();
                    try
                    {
                        foreach (var ai in createdConn.GetAttributeInfos())
                        {
                            var dict = new Dictionary<string, object?>();
                            foreach (var p in ai.GetType().GetProperties())
                            {
                                try
                                {
                                    var v = p.GetValue(ai);
                                    dict[p.Name] = v == null ? null : v.ToString();
                                }
                                catch { }
                            }
                            attrInfos.Add(dict);
                        }
                    }
                    catch { }

                    var exportVerified = false;
                    string? verifyError = null;
                    try
                    {
                        var vf = Path.Combine(Path.GetTempPath(), $"tia_minconn_v_{Guid.NewGuid():N}.xml");
                        try
                        {
                            createdConn.Export(new FileInfo(vf), ExportOptions.WithDefaults);
                            exportVerified = File.Exists(vf) && new FileInfo(vf).Length > 0;
                        }
                        finally { try { File.Delete(vf); } catch { } }
                    }
                    catch (Exception ex) { verifyError = ex.Message; }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        connectionName = createdConn.Name,
                        mode,
                        driver = actualDriver,
                        exportVerified,
                        verifyError,
                        attributeInfos = attrInfos,
                        importError
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private static string? SafeStr(object? v)
        {
            try { return v?.ToString(); } catch { return null; }
        }

        /// <summary>
        /// 同步导出默认 HMI 变量表并返回 XML 文本（get_ 前缀保持同步执行，export_ 前缀会被归类为异步长任务）。
        /// </summary>
        public string GetHmiTagTableXml()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var table = hmi.TagFolder.DefaultTagTable;
                    var tmp = Path.Combine(Path.GetTempPath(), $"tia_tblget_{Guid.NewGuid():N}.xml");
                    try
                    {
                        table.Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                        var xml = File.ReadAllText(tmp, Encoding.UTF8);
                        return JsonConvert.SerializeObject(new { success = true, tableName = table.Name, xml }, Formatting.Indented);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string BindHmiTagToPlc(string hmiTagName, string plcTagName, string? connectionName = null)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var tag = FindTagRecursive(hmi.TagFolder, hmiTagName);
                    if (tag == null) return Err($"未找到 HMI 变量: {hmiTagName}");
                    if (!string.IsNullOrEmpty(connectionName)) SetHmiTagProperty(tag, "Connection", connectionName);
                    SetHmiTagProperty(tag, "PlcTag", plcTagName);
                    return Ok($"已绑定 HMI 变量 {hmiTagName} → PLC 变量 {plcTagName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private void SetHmiTagProperty(Tag tag, string propName, string? value)
        {
            var altNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["Address"] = new[] { "Address", "PlcAddress", "TagAddress", "AddressString" },
                ["PlcTag"] = new[] { "PlcTag", "PlcTagName", "TagName", "Symbol" },
                ["Connection"] = new[] { "Connection", "ConnectionName" }
            };

            if (!altNames.TryGetValue(propName, out var candidates))
                throw new ArgumentException($"不支持的 HMI Tag 属性: {propName}");

            var failures = new List<string>();
            foreach (var candidate in candidates)
            {
                var prop = tag.GetType().GetProperty(candidate);
                if (prop != null && prop.CanWrite)
                {
                    try { prop.SetValue(tag, value); return; }
                    catch (TargetInvocationException tie)
                    {
                        failures.Add($"{candidate}: {tie.InnerException?.Message ?? tie.Message}");
                    }
                    catch (Exception ex) { failures.Add($"{candidate}: {ex.Message}"); }
                }

                try
                {
                    ((IEngineeringObject)tag).SetAttribute(candidate, value ?? "");
                    return;
                }
                catch (Exception ex) { failures.Add($"SetAttribute({candidate}): {ex.Message}"); }
            }

            throw new Exception($"无法设置 HMI 变量属性 '{propName}'。该面板/版本未暴露可写属性；请改用 XML 导入。尝试结果: {string.Join(" | ", failures)}");
        }

        // ────────────────────────────────────────────────────────────
        // HMI 变量表组 / 连接
        // ────────────────────────────────────────────────────────────

        public string ListHmiTagTableGroups()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = new List<object>();
                    foreach (var f in hmi.TagFolder.Folders)
                        list.Add(new { name = f.Name });
                    return JsonConvert.SerializeObject(new { success = true, count = list.Count, tagTableGroups = list });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateHmiTagTableGroup(string groupName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    hmi.TagFolder.Folders.Create(groupName);
                    return Ok($"已创建 HMI 变量表组: {groupName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteHmiTagTableGroup(string groupName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var g = hmi.TagFolder.Folders.Find(groupName);
                    if (g == null) return Err($"未找到 HMI 变量表组: {groupName}");
                    g.Delete();
                    return Ok($"已删除 HMI 变量表组: {groupName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateHmiConnection(string connectionName,
            string? plcName = null, string? hmiIp = null, string? plcIp = null,
            string? subnetMask = null, string? subnetName = null,
            string? driver = null, string? templateFilePath = null,
            string? hmiDeviceName = null, string? connectionMode = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(connectionName)) return Err("连接名不能为空");
                    var hmi = RequireClassicHmi(hmiDeviceName);
                    var conns = hmi.Connections;
                    var mode = string.IsNullOrWhiteSpace(connectionMode)
                        ? (!string.IsNullOrWhiteSpace(plcName) ? "integrated" : "nonIntegrated")
                        : connectionMode!.Trim();

                    if (mode.Equals("integrated", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(plcName))
                            return Err("integrated 模式必须指定 plcName，以便将 PLC 与 HMI 接入同一 PN/IE 子网");
                        // Classic 集成连接由网络组态生成。不要导入连接XML，也不要要求连接出现在 HmiTarget.Connections。
                        return SetupNetworkAndHmiConnection(
                            subnetName, plcName, hmiDeviceName, plcIp, hmiIp, subnetMask,
                            connectionName, driver, null, "integrated");
                    }

                    if (!mode.Equals("nonIntegrated", StringComparison.OrdinalIgnoreCase))
                        return Err("connectionMode 仅支持 integrated 或 nonIntegrated");

                    if (!string.IsNullOrWhiteSpace(plcName))
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            errorCode = "NON_INTEGRATED_CONNECTION_USES_ADDRESS_NOT_PROJECT_PLC",
                            message = "非集成连接不能用项目内 PLC 对象名建立伙伴关系。请去掉 plcName，并提供 plcIp/driver；变量使用绝对地址。",
                            connectionMode = "nonIntegrated",
                            requestedPlc = plcName
                        }, Formatting.Indented);
                    }

                    var actualDriver = string.IsNullOrWhiteSpace(driver) ? "SIMATIC S7 1200" : driver!.Trim();
                    var existing = conns.Find(connectionName);
                    Connection? createdConn = existing;
                    var templateUsed = false;
                    var importedNewObject = false;
                    var diagnostics = new List<string>();

                    if (createdConn == null || !string.IsNullOrWhiteSpace(templateFilePath))
                    {
                        string connXml;
                        if (!string.IsNullOrWhiteSpace(templateFilePath))
                        {
                            if (!File.Exists(templateFilePath))
                                return Err($"HMI 非集成连接模板不存在: {templateFilePath}");
                            connXml = File.ReadAllText(templateFilePath!, Encoding.UTF8)
                                .Replace("{{CONNECTION_NAME}}", connectionName)
                                .Replace("{{DRIVER}}", actualDriver)
                                .Replace("{{HMI_IP}}", hmiIp ?? "")
                                .Replace("{{PLC_IP}}", plcIp ?? "")
                                .Replace("{{SUBNET_MASK}}", subnetMask ?? "")
                                .Replace("{{SUBNET_NAME}}", subnetName ?? "");

                            var templateDoc = XDocument.Parse(connXml, LoadOptions.PreserveWhitespace);
                            var connElement = templateDoc.Descendants()
                                .FirstOrDefault(e => e.Name.LocalName == "Hmi.Communication.Connection");
                            if (connElement == null)
                                return Err("模板中未找到 Hmi.Communication.Connection 元素。该模板必须来自非集成连接。");
                            var attrs = connElement.Elements()
                                .FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                            var nameElement = attrs?.Elements()
                                .FirstOrDefault(e => e.Name.LocalName == "Name");
                            if (nameElement == null)
                                return Err("模板连接缺少 AttributeList/Name");
                            nameElement.Value = connectionName;
                            var driverElement = attrs?.Elements()
                                .FirstOrDefault(e => e.Name.LocalName == "Driver");
                            if (driverElement != null && !string.IsNullOrWhiteSpace(actualDriver))
                                driverElement.Value = actualDriver;
                            connXml = templateDoc.ToString(SaveOptions.DisableFormatting);
                            templateUsed = true;
                        }
                        else
                        {
                            // ★修复★ 不再生成 Minimal 连接 XML。
                            // 实测：InterfaceType 缺失（missing）、PN/IE（incompatible）、Ethernet（incompatible）
                            // 三种形态均被 V19 拒绝，且 Import 失败会直接终止 TIA 进程（disposed）。
                            // 非集成连接必须提供模板（GUI 手工创建后 export_hmi_connection 导出），
                            // 或改用 integrated 模式（由子网组态自动生成连接）。
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                errorCode = "TEMPLATE_REQUIRED_FOR_NON_INTEGRATED_CONNECTION",
                                message = "nonIntegrated 模式必须提供 connectionTemplateFilePath（从现有项目导出的连接 XML）：" +
                                          "请先用 export_hmi_connection 从现有项目导出非集成连接 XML，再把它通过 connectionTemplateFilePath 参数传入。" +
                                          "服务器不再自动生成最小连接 XML——实测 InterfaceType（PN/IE/Ethernet 均不兼容）" +
                                          "会导致导入失败并终止 TIA 进程。" +
                                          "或者改用 integrated 模式（connectionMode=integrated 并指定 plcName），由子网组态自动生成连接。",
                                connectionMode = "nonIntegrated",
                                requiresTemplate = true
                            }, Formatting.Indented);
                        }

                        var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_conn_{Guid.NewGuid():N}.xml");
                        try
                        {
                            File.WriteAllText(tempFile, connXml, new UTF8Encoding(true));
                            var imported = conns.Import(new FileInfo(tempFile), ImportOptions.Override);
                            createdConn = imported.FirstOrDefault() ?? conns.Find(connectionName);
                            importedNewObject = createdConn != null;
                        }
                        finally { TryDelete3(tempFile); }
                    }

                    if (createdConn == null)
                        return Err("非集成连接导入后未在 HMI Connections 集合中找到，已拒绝返回成功");

                    var driverAttempts = new List<object>();
                    var driverSet = TrySetConnectionAttribute(createdConn, "Driver", actualDriver, driverAttempts);
                    var exportVerified = false;
                    string? verificationError = null;
                    var verifyFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_conn_verify_{Guid.NewGuid():N}.xml");
                    try
                    {
                        createdConn.Export(new FileInfo(verifyFile), ExportOptions.WithDefaults);
                        exportVerified = File.Exists(verifyFile) && new FileInfo(verifyFile).Length > 0;
                    }
                    catch (Exception ex) { verificationError = ex.Message; }
                    finally { TryDelete3(verifyFile); }

                    var success = exportVerified;
                    if (!exportVerified)
                        diagnostics.Add("连接对象存在，但无法按非集成连接导出验证，因此返回 success=false。");
                    if (!driverSet)
                        diagnostics.Add("Driver 属性未能通过 SetAttribute 验证；模板中的驱动信息可能仍已随 XML 导入。");

                    return JsonConvert.SerializeObject(new
                    {
                        success,
                        message = success
                            ? $"HMI 非集成连接 '{connectionName}' 已创建并通过导出验证"
                            : $"HMI 非集成连接 '{connectionName}' 未通过导出验证",
                        connectionMode = "nonIntegrated",
                        connectionName = createdConn.Name,
                        hmiDevice = hmiDeviceName ?? hmi.Name,
                        plcIp,
                        driver = actualDriver,
                        importedNewObject,
                        templateUsed,
                        driverSet,
                        exportVerified,
                        verificationError,
                        driverAttempts,
                        availableAttributes = GetConnectionAttributeInfos(createdConn),
                        diagnostics
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // Unified HMI 连接（WinCC Unified）
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 为 WinCC Unified HMI 设备创建连接（HMI ↔ PLC）。
        ///
        /// API 探测结论（2026-06-27，TIA Portal V19 Openness）：
        ///   - Unified HMI 软件类为 Siemens.Engineering.HmiUnified.HmiSoftware
        ///     （与传统 Siemens.Engineering.Hmi.HmiTarget 不同，后者仅能通过 Import 创建连接）
        ///   - HmiSoftware.Connections 返回 HmiConnectionComposition
        ///   - HmiConnectionComposition.Create(string name) ✅ 可用，返回 HmiConnection
        ///     （与传统 ConnectionComposition 不同，后者无 Create 方法）
        ///   - HmiConnection 可读写属性：Name/Partner/Station/Node/CommunicationDriver/
        ///     InitialAddress/DisabledAtStartup/Comment/DriverProperties
        ///   - Partner/Station/Node 关联 PLC 的属性在 Openness API 中通常不可直接写字符串，
        ///     本方法用 SetAttribute 尝试设置并捕获异常，最终关联需在博途 GUI 完成
        ///
        /// 已知限制：
        ///   - Partner/Station/Node 属性通常因 Openness API 限制不可直接写，
        ///     需在博途 GUI 中手动选择 PLC 设备
        ///   - CommunicationDriver 通常可设置（如 "SIMATIC S7 1200"）
        /// </summary>
        /// <param name="hmiDeviceName">Unified HMI 设备名（必填）</param>
        /// <param name="connectionName">连接名（必填）</param>
        /// <param name="plcDeviceName">关联的 PLC 设备名（必填，用于设置 Partner）</param>
        /// <param name="connectionType">通信驱动类型（默认 SIMATIC S7 1200）</param>
        /// <returns>JSON: { success, connectionName, hmiDeviceName, plcDeviceName, properties, apiExplored, warning? }</returns>
        public string CreateUnifiedHmiConnection(string hmiDeviceName, string connectionName,
            string plcDeviceName, string? connectionType = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();

                    if (string.IsNullOrWhiteSpace(hmiDeviceName))
                        return Err("参数 hmiDeviceName 不能为空");
                    if (string.IsNullOrWhiteSpace(connectionName))
                        return Err("参数 connectionName 不能为空");
                    if (string.IsNullOrWhiteSpace(plcDeviceName))
                        return Err("参数 plcDeviceName 不能为空");

                    var driver = string.IsNullOrEmpty(connectionType) ? "SIMATIC S7 1200" : connectionType!;

                    // ── 1. 查找 Unified HMI 设备（按名称匹配，避免 COM RCW 引用比对） ──
                    object? unifiedHmi = null;
                    Device? hmiDevice = null;
                    bool foundAsClassicHmi = false;

                    foreach (var device in GetAllDevices())
                    {
                        if (!device.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        hmiDevice = device;
                        foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                        {
                            try
                            {
                                var swContainer = di.GetService<SoftwareContainer>();
                                if (swContainer?.Software == null) continue;

                                // Unified HMI：HmiSoftware
                                if (IsUnifiedHmiSoftware(swContainer.Software))
                                {
                                    unifiedHmi = swContainer.Software;
                                    break;
                                }
                                // 传统 HMI：HmiTarget（用于友好报错）
                                if (swContainer.Software is HmiTarget)
                                {
                                    foundAsClassicHmi = true;
                                }
                            }
                            catch { }
                        }
                        if (unifiedHmi != null) break;
                    }

                    if (unifiedHmi == null)
                    {
                        if (foundAsClassicHmi)
                            return Err($"设备 {hmiDeviceName} 不是 WinCC Unified HMI，不支持此操作。传统 HMI 请使用 create_hmi_connection");
                        if (hmiDevice == null)
                            return Err($"未找到 HMI 设备: {hmiDeviceName}（请用 list_devices 确认设备名称）");
                        return Err($"设备 {hmiDeviceName} 未包含 HMI 软件容器，无法创建连接");
                    }

                    // ── 2. 验证 PLC 设备存在（不阻断，仅做存在性校验） ──
                    var plcDevice = FindDeviceByName(plcDeviceName);
                    if (plcDevice == null)
                        return Err($"未找到 PLC 设备: {plcDeviceName}（请用 list_devices 确认 PLC 名称）");

                    // ── 3. 获取 HmiConnectionComposition 并调用 Create ──
                    var conns = GetHmiProperty(unifiedHmi, "Connections"); // HmiConnectionComposition

                    // 同名连接已存在则跳过
                    var existing = TryInvoke(conns, "Find", new object[] { connectionName });
                    if (existing != null)
                        return Ok($"Unified HMI 连接已存在: {connectionName}");

                    object? createdConn;
                    try
                    {
                        createdConn = TryInvoke(conns, "Create", new object[] { connectionName });
                    }
                    catch (Exception exCreate)
                    {
                        var inner = exCreate is TargetInvocationException tie
                            ? (tie.InnerException?.Message ?? tie.Message)
                            : exCreate.Message;
                        return Err($"HmiConnectionComposition.Create 调用失败: {inner}");
                    }

                    // ── 4. 设置连接属性（SetAttribute 强类型 API） ──
                    var propResults = new List<object>();

                    // 4.1 CommunicationDriver（通信驱动，Openness 通常可写）
                    TrySetUnifiedConnectionAttr(createdConn, "CommunicationDriver", driver, propResults);

                    // 4.2 Partner / Station / Node（关联 PLC，Openness 通常限制为只读）
                    bool partnerOk = TrySetUnifiedConnectionAttr(createdConn, "Partner", plcDeviceName, propResults);
                    TrySetUnifiedConnectionAttr(createdConn, "Station", plcDeviceName, propResults);
                    TrySetUnifiedConnectionAttr(createdConn, "Node", plcDeviceName, propResults);

                    // 4.3 InitialAddress（S7 连接参数占位，真实 IP 需用户在 GUI 调整）
                    TrySetUnifiedConnectionAttr(createdConn, "InitialAddress",
                        $"PlcAddress={plcDeviceName};Rack=0;Slot=0", propResults);

                    // ── 5. 反射枚举所有可用属性 + GetAttributeInfos（API 探测） ──
                    var apiExplored = ExploreUnifiedConnectionApi(createdConn);

                    // ── 6. 警告信息 ──
                    var warnings = new List<string>();
                    if (!partnerOk)
                    {
                        warnings.Add("Partner 属性设置失败：Openness API 限制，Unified HMI 连接的 Partner/Station/Node " +
                                     "通常不可直接写字符串，需在博途 GUI 中选择 PLC 设备完成关联");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建 Unified HMI 连接: {GetHmiProperty(createdConn, "Name")}（HMI: {hmiDeviceName}，目标 PLC: {plcDeviceName}）",
                        connectionName = GetHmiProperty(createdConn, "Name"),
                        hmiDeviceName,
                        plcDeviceName,
                        connectionType = driver,
                        properties = propResults,
                        apiExplored,
                        warning = warnings.Count > 0 ? string.Join(" | ", warnings) : null
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 用强类型 SetAttribute 设置 Unified HmiConnection 属性，捕获异常以避免单属性失败中断整个流程。
        /// 返回 true 表示设置成功；结果同时追加到 results 列表用于诊断返回。
        /// </summary>
        private bool TrySetUnifiedConnectionAttr(object? conn, string attrName, object value,
            List<object> results)
        {
            try
            {
                conn?.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) })?.Invoke(conn, new[] { (object)attrName, value });
                results.Add(new { attribute = attrName, value = value.ToString(), success = true });
                return true;
            }
            catch (Exception ex)
            {
                var err = ex is TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? tie.Message)
                    : ex.Message;
                results.Add(new { attribute = attrName, value = value.ToString(), success = false, error = err });
                return false;
            }
        }

        /// <summary>
        /// 反射枚举 Unified HmiConnection 的所有可读属性、可用方法和 GetAttributeInfos，
        /// 用于在 API 限制时返回诊断信息，便于用户判断后续 GUI 操作。
        /// </summary>
        private object ExploreUnifiedConnectionApi(object? conn)
        {
            var properties = new List<object>();
            var methods = new List<string>();
            if (conn == null) return new { properties, methods, attrInfos = new List<object>() };
            var connType = conn.GetType();

            // 公共属性
            foreach (var prop in connType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    object? val = null;
                    try { val = prop.GetValue(conn); } catch { }
                    properties.Add(new
                    {
                        name = prop.Name,
                        type = prop.PropertyType.Name,
                        canRead = prop.CanRead,
                        canWrite = prop.CanWrite,
                        currentValue = val?.ToString() ?? "(null)"
                    });
                }
                catch { }
            }

            // 公共方法（仅签名，跳过 get_/set_ 特殊方法）
            foreach (var m in connType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                methods.Add($"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
            }

            // GetAttributeInfos（HmiConnection 强类型 API，暴露所有属性的元数据）
            var attrInfos = new List<object>();
            try
            {
                var infos = connType.GetMethod("GetAttributeInfos")?.Invoke(conn, null) as System.Collections.IEnumerable;
                if (infos != null)
                {
                    foreach (var info in infos)
                    {
                        try
                        {
                            var infoType = info.GetType();
                            var name = infoType.GetProperty("Name")?.GetValue(info)?.ToString() ?? "";
                            var accessMode = infoType.GetProperty("AccessMode")?.GetValue(info)?.ToString() ?? "";
                            var supportedTypes = "";
                            try
                            {
                                var stVal = infoType.GetProperty("SupportedTypes")?.GetValue(info);
                                if (stVal is System.Collections.IEnumerable stEnum)
                                {
                                    var typeNames = new List<string>();
                                    foreach (var t in stEnum)
                                    {
                                        try { typeNames.Add(t?.ToString() ?? "?"); } catch { }
                                    }
                                    supportedTypes = string.Join(", ", typeNames);
                                }
                            }
                            catch { }
                            attrInfos.Add(new { name, accessMode, supportedTypes });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return new { properties, methods, attributeInfos = attrInfos };
        }

        /// <summary>
        /// 用强类型 SetAttribute 设置连接属性，捕获异常以避免单属性失败中断整个流程。
        /// 结果追加到 results 列表用于诊断返回。
        /// </summary>
        private bool TrySetConnectionAttribute(Connection conn, string attrName, object value,
            List<object> results)
        {
            try
            {
                conn.SetAttribute(attrName, value);
                string readBack = "";
                string? verifyError = null;
                try { readBack = conn.GetAttribute(attrName)?.ToString() ?? ""; }
                catch (Exception ex) { verifyError = ex.Message; }

                var expected = value?.ToString() ?? "";
                var verified = readBack.Equals(expected, StringComparison.OrdinalIgnoreCase)
                               || (!string.IsNullOrWhiteSpace(expected)
                                   && readBack.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
                results.Add(new
                {
                    attribute = attrName,
                    value = expected,
                    writeSucceeded = true,
                    verified,
                    readBack,
                    verifyError,
                    success = verified
                });
                return verified;
            }
            catch (Exception ex)
            {
                var err = ex is TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? tie.Message)
                    : ex.Message;
                results.Add(new { attribute = attrName, value = value?.ToString(), writeSucceeded = false, verified = false, success = false, error = err });
                return false;
            }
        }

        /// <summary>
        /// 用强类型 GetAttributeInfos 枚举连接所有可用属性及其元数据。
        /// 返回每个属性的 Name、AccessMode（读/写）、SupportedTypes，用于诊断和后续操作。
        /// </summary>
        private List<object> GetConnectionAttributeInfos(Connection conn)
        {
            var result = new List<object>();
            try
            {
                var infos = conn.GetAttributeInfos();
                if (infos == null) return result;
                foreach (var info in infos)
                {
                    try
                    {
                        var infoType = info.GetType();
                        var name = infoType.GetProperty("Name")?.GetValue(info)?.ToString() ?? "";
                        var accessMode = infoType.GetProperty("AccessMode")?.GetValue(info)?.ToString() ?? "";
                        var supportedTypes = "";
                        try
                        {
                            var stVal = infoType.GetProperty("SupportedTypes")?.GetValue(info);
                            if (stVal is System.Collections.IEnumerable stEnum)
                            {
                                var typeNames = new List<string>();
                                foreach (var t in stEnum)
                                {
                                    try { typeNames.Add(t?.ToString() ?? "?"); } catch { }
                                }
                                supportedTypes = string.Join(", ", typeNames);
                            }
                        }
                        catch { }
                        result.Add(new { name, accessMode, supportedTypes });
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>按工程版本返回 InstalledProducts 中 WinCC 产品名（V17=Professional，V18+=Basic/Comfort/Advanced）。</summary>
        private static string WinccProductName(string engVersion)
            => engVersion.StartsWith("V17", StringComparison.OrdinalIgnoreCase)
                ? "WinCC Professional"
                : "WinCC Basic/Comfort/Advanced";

        /// <summary>
        /// 生成仅含 Name 的最小 HMI 连接 XML。
        /// 项目记忆：ConnectionComposition.Import() 仅接受最简单的 <Name> 属性，
        /// 其他属性（Driver、PLC 引用等）必须通过强类型 SetAttribute 配置。
        /// </summary>
        private string GenerateHmiConnectionXml(string connectionName, string engVersion = "")
        {
            if (string.IsNullOrWhiteSpace(engVersion)) engVersion = EnvironmentDiscoveryService.CurrentEngineeringVersion();
            var sb = new StringBuilder();
            sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
            sb.AppendLine("<Document>");
            sb.AppendLine($"  <Engineering version=\"{engVersion}\" />");
            sb.AppendLine("  <DocumentInfo>");
            sb.AppendLine("    <Created>2026-06-27T08:00:00.0000000Z</Created>");
            sb.AppendLine("    <ExportSetting>WithDefaults</ExportSetting>");
            sb.AppendLine("    <InstalledProducts>");
            sb.AppendLine($"      <Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></Product>");
            sb.AppendLine($"      <OptionPackage><DisplayName>TIA Portal Openness</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></OptionPackage>");
            sb.AppendLine($"      <Product><DisplayName>STEP 7 Professional</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></Product>");
            sb.AppendLine($"      <Product><DisplayName>{WinccProductName(engVersion)}</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></Product>");
            sb.AppendLine("    </InstalledProducts>");
            sb.AppendLine("  </DocumentInfo>");
            sb.AppendLine("  <Hmi.Communication.Connection ID=\"0\">");
            sb.AppendLine("    <AttributeList>");
            sb.AppendLine($"      <Name>{System.Security.SecurityElement.Escape(connectionName)}</Name>");
            sb.AppendLine("      <Driver>SIMATIC S7 1200</Driver>");
            sb.AppendLine("      <InterfaceType>PN/IE</InterfaceType>");
            sb.AppendLine("    </AttributeList>");
            sb.AppendLine("  </Hmi.Communication.Connection>");
            sb.AppendLine("</Document>");
            return sb.ToString();
        }

        /// <summary>
        /// 导出 HmiTarget（整个 HMI 设备配置），用于分析连接等 XML 格式
        /// </summary>
        public string ExportHmiTarget(string outputPath)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    EnsureDir3(outputPath);
                    // HmiTarget 本身可能不支持 Export，尝试通过 DeviceItem 导出
                    // 先尝试直接 Export
                    try
                    {
                        // HmiTarget 不支持直接 Export
                        return Err("HmiTarget 不支持直接 Export。请尝试导出变量表或画面。");
                    }
                    catch
                    {
                        // 如果 HmiTarget 不支持 Export，尝试导出其子对象
                        return Err("HmiTarget 不支持直接 Export。请尝试导出变量表或画面。");
                    }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 设置 HMI 起始画面（运行系统设置 → 常规 → 画面选项 → 起始画面）
        /// </summary>
        public string SetHmiStartScreen(string screenName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();

                    // 查找画面
                    var screen = FindScreenRecursive(hmi.ScreenFolder, screenName)
                                 ?? throw new Exception($"未找到画面: {screenName}");

                    // 方法1: 通过 ScreenOverview 设置起始画面
                    var soProp = hmi.GetType().GetProperty("ScreenOverview");
                    if (soProp != null)
                    {
                        var so = soProp.GetValue(hmi);
                        if (so != null)
                        {
                            // 尝试直接设置 StartScreen
                            var ssProp = so.GetType().GetProperty("StartScreen");
                            if (ssProp != null && ssProp.CanWrite)
                            {
                                ssProp.SetValue(so, screen);
                                return Ok($"已设置起始画面: {screenName}（通过 ScreenOverview.StartScreen）");
                            }
                        }
                    }

                    // 方法2: 通过 DeviceItem 属性设置
                    // HmiTarget 继承自 IEngineeringObject → IEngineeringServiceProvider
                    // 尝试获取 DeviceItem 并设置属性
                    try
                    {
                        var deviceItemProp = hmi.GetType().GetProperty("DeviceItem");
                        if (deviceItemProp != null)
                        {
                            var deviceItem = deviceItemProp.GetValue(hmi);
                            if (deviceItem != null)
                            {
                                // 尝试通过 IEngineeringServiceProvider 获取属性
                                var sp = deviceItem as Siemens.Engineering.IEngineeringServiceProvider;
                                if (sp != null)
                                {
                                    // 查找 StartScreen 相关属性
                                    var attrs = deviceItem.GetType().GetProperty("Attributes")?.GetValue(deviceItem);
                                    if (attrs != null)
                                    {
                                        foreach (var attr in (System.Collections.IEnumerable)attrs)
                                        {
                                            var attrType = attr.GetType();
                                            var attrName = attrType.Name;
                                            if (attrName.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0
                                                || attrName.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0
                                                || attrName.IndexOf("Runtime", StringComparison.OrdinalIgnoreCase) >= 0)
                                            {
                                                // 列出此属性的子属性
                                                var subProps = attrType.GetProperties();
                                                foreach (var sp2 in subProps)
                                                {
                                                    if (sp2.Name.IndexOf("StartScreen", StringComparison.OrdinalIgnoreCase) >= 0 && sp2.CanWrite)
                                                    {
                                                        sp2.SetValue(attr, screen);
                                                        return Ok($"已设置起始画面: {screenName}（通过 {attrName}.{sp2.Name}）");
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { /* 忽略 */ }

                    // 方法3: 递归搜索
                    var found = TrySetStartScreenRecursive(hmi, screen, "HmiTarget", 0);
                    if (found != null)
                        return Ok(found);

                    return Err($"经典 HMI 的起始画面设置不在 Openness API 中暴露。" +
                               $"\n请在博途中手动设置：HMI 设备 → 运行系统设置 → 常规 → 画面选项 → 起始画面 → 选择 \"{screenName}\"");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 递归搜索 HMI 对象树中的 StartScreen 属性
        /// </summary>
        private string? TrySetStartScreenRecursive(object obj, object screen, string path, int depth)
        {
            if (obj == null || depth > 3) return null;

            var type = obj.GetType();

            // 直接查找 StartScreen 属性
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name.IndexOf("StartScreen", StringComparison.OrdinalIgnoreCase) >= 0 && prop.CanWrite)
                {
                    try
                    {
                        prop.SetValue(obj, screen);
                        return $"已设置起始画面（路径: {path}.{prop.Name}）";
                    }
                    catch { /* 设置失败，继续搜索 */ }
                }
            }

            // 递归搜索子属性
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string)
                    || prop.PropertyType.IsEnum || prop.PropertyType.IsArray)
                    continue;

                try
                {
                    var child = prop.GetValue(obj);
                    if (child == null || ReferenceEquals(child, obj)) continue;
                    var result = TrySetStartScreenRecursive(child, screen, $"{path}.{prop.Name}", depth + 1);
                    if (result != null) return result;
                }
                catch { /* 忽略访问异常 */ }
            }

            return null;
        }

        public string ListHmiConnections()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var conns = hmi.Connections; // ConnectionComposition（强类型）
                    var list = new List<object>();

                    // 用强类型 IEnumerable<Connection> 遍历，并通过 GetAttributeInfos/GetAttribute
                    // 暴露每个连接的所有可读属性（Driver、PLC 关联等）
                    foreach (var conn in conns)
                    {
                        var name = conn.Name;

                        // 枚举所有可用属性及其元数据
                        var attrInfos = GetConnectionAttributeInfos(conn);

                        // 用强类型 GetAttribute 读取每个属性的当前值
                        var attrValues = new Dictionary<string, string>();
                        foreach (var info in attrInfos)
                        {
                            // info 是匿名对象 { name, accessMode, supportedTypes }
                            // 通过反射取出 name 字段（匿名对象属性访问）
                            string? attrName = null;
                            try
                            {
                                var nameField = info.GetType().GetField("name");
                                if (nameField != null) attrName = nameField.GetValue(info)?.ToString();
                                else
                                {
                                    var nameProp = info.GetType().GetProperty("name");
                                    if (nameProp != null) attrName = nameProp.GetValue(info)?.ToString();
                                }
                            }
                            catch { }

                            if (string.IsNullOrEmpty(attrName)) continue;
                            try
                            {
                                var val = conn.GetAttribute(attrName);
                                attrValues[attrName] = val?.ToString() ?? "null";
                            }
                            catch (Exception ex)
                            {
                                // 读取失败（可能是 Write-only 属性），记录错误
                                attrValues[attrName] = $"<读取失败: {ex.Message}>";
                            }
                        }

                        list.Add(new
                        {
                            name,
                            attributes = attrValues,
                            attributeInfos = attrInfos
                        });
                    }

                    // HmiTarget.Connections 仅公开非集成连接。
                    var limitation = list.Count == 0
                        ? "HmiTarget.Connections 中没有非集成连接。同一项目内 HMI↔PLC 的集成连接不会出现在该集合中，也不支持导出；集合为空不代表工程没有集成连接。"
                        : "仅列出非集成连接。集成连接不会出现在 HmiTarget.Connections 中。";

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        count = list.Count,
                        connections = list,
                        limitation
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteHmiConnection(string connectionName)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var conns = hmi.Connections;
                    var conn = conns.Find(connectionName);
                    if (conn == null) return Err($"未找到 HMI 连接: {connectionName}");
                    conn.Delete();
                    return Ok($"已删除 HMI 连接: {connectionName}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 仅导出非集成 HMI 连接。
        /// 集成连接不在 HmiTarget.Connections 中暴露，并且不支持导出。
        /// </summary>
        public string ExportHmiConnection(string connectionName, string outputPath)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var conn = hmi.Connections.Find(connectionName);
                    if (conn == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            errorCode = "HMI_CONNECTION_NOT_EXPORTABLE",
                            message = $"未在 HmiTarget.Connections 中找到非集成连接: {connectionName}",
                            likelyReason = "如果该连接是同一项目内 HMI↔PLC 的集成连接，这是正常现象：集成连接不在该集合中暴露，也不支持导出。",
                            supportedAction = "只有非集成连接可以使用 export_hmi_connection。集成连接请保留在种子工程中或在 TIA GUI 中手工创建。"
                        }, Formatting.Indented);
                    }

                    EnsureDir3(outputPath);
                    conn.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                    if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                        return Err("连接 Export 调用完成，但输出文件不存在或为空");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        connectionMode = "nonIntegrated",
                        connectionName,
                        outputPath,
                        bytes = new FileInfo(outputPath).Length,
                        message = "HMI 非集成连接已导出"
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 用反射在 HmiTarget 对象上递归查找名为 connectionName 的连接对象。
        /// 遍历所有属性，寻找 IEnumerable 集合，逐项匹配 Name。
        /// </summary>
        private object? FindConnectionByReflection(object root, string connectionName)
        {
            var visited = new HashSet<object>();
            return FindConnRecurse(root, connectionName, visited, 0);
        }

        private object? FindConnRecurse(object obj, string connectionName, HashSet<object> visited, int depth)
        {
            if (obj == null || depth > 4) return null;
            if (visited.Contains(obj)) return null;
            visited.Add(obj);

            var t = obj.GetType();
            // 若对象本身是 Connection 类型，检查 Name
            if (t.Name.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0
                && t.Namespace?.Contains("Communication") == true)
            {
                var nameProp = t.GetProperty("Name");
                if (nameProp != null)
                {
                    var n = nameProp.GetValue(obj)?.ToString();
                    if (n == connectionName) return obj;
                }
            }

            // 遍历属性
            foreach (var prop in t.GetProperties())
            {
                try
                {
                    var getter = prop.GetMethod;
                    if (getter == null) continue;
                    var ptype = prop.PropertyType;
                    // 跳过字符串（避免误判）
                    if (ptype == typeof(string)) continue;
                    // 若是 IEnumerable<> 泛型集合
                    var enumInterface = ptype.GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                    if (enumInterface != null)
                    {
                        var itemTypeName = enumInterface.GetGenericArguments()[0].Name;
                        // 只遍历名字含 Connection 的集合，或属性名含 Connection
                        if (itemTypeName.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0
                            || prop.Name.IndexOf("Connection", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foreach (var item in (System.Collections.IEnumerable)prop.GetValue(obj)!)
                            {
                                if (item == null) continue;
                                var itemName = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                                if (itemName == connectionName) return item;
                                // 递归查找（限深）
                                var found = FindConnRecurse(item, connectionName, visited, depth + 1);
                                if (found != null) return found;
                            }
                        }
                    }
                }
                catch { /* skip */ }
            }
            return null;
        }

        /// <summary>
        /// 用 IEngineeringObject.Children 递归遍历 HmiTarget 整个对象树，
        /// 查找名为 objectName 的对象，返回其类型、路径、所有简单属性。
        /// 用于定位 HMI 连接等在 Openness API 中隐藏的对象。
        /// </summary>
        public string FindHmiObjectByName(string objectName, string? pattern = null, int maxDepth = 10, int maxResults = 500)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var results = new List<object>();
                    var visited = new HashSet<object>();
                    var usePattern = !string.IsNullOrWhiteSpace(pattern);
                    var match = usePattern ? pattern!.Trim() : objectName;
                    FindObjRecurse(hmi, match, usePattern, visited, results, "HmiTarget", 0,
                        Math.Min(24, Math.Max(1, maxDepth)), maxResults);
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        matchMode = usePattern ? "contains-ignorecase" : "exact",
                        targetName = match,
                        count = results.Count,
                        truncated = results.Count >= maxResults,
                        results
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private void FindObjRecurse(object obj, string targetName, bool usePattern, HashSet<object> visited,
            List<object> results, string path, int depth, int maxDepth, int maxResults)
        {
            if (obj == null || depth > maxDepth || visited.Contains(obj) || results.Count >= maxResults) return;
            visited.Add(obj);

            var t = obj.GetType();
            // 检查当前对象的 Name 属性
            try
            {
                var nameProp = t.GetProperty("Name");
                if (nameProp != null)
                {
                    var n = nameProp.GetValue(obj)?.ToString();
                    bool hit = usePattern
                        ? (n != null && n.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0)
                        : (n == targetName);
                    if (hit)
                    {
                        // 收集所有简单属性
                        var attrs = new Dictionary<string, string>();
                        foreach (var p in t.GetProperties())
                        {
                            try
                            {
                                if (p.PropertyType == typeof(string) || p.PropertyType.IsEnum || p.PropertyType.IsPrimitive)
                                {
                                    var v = p.GetValue(obj);
                                    attrs[p.Name] = v?.ToString() ?? "null";
                                }
                            }
                            catch { }
                        }
                        results.Add(new
                        {
                            path,
                            typeName = t.FullName,
                            attributes = attrs
                        });
                    }
                }
            }
            catch { }

            if (depth >= maxDepth) return;

            // 尝试通过 IEngineeringObject.Children 递归
            try
            {
                var childrenProp = t.GetProperty("Children");
                if (childrenProp != null)
                {
                    var children = childrenProp.GetValue(obj) as System.Collections.IEnumerable;
                    if (children != null)
                    {
                        int idx = 0;
                        foreach (var child in children)
                        {
                            if (child == null) continue;
                            var childName = child.GetType().GetProperty("Name")?.GetValue(child)?.ToString() ?? $"[{idx}]";
                            FindObjRecurse(child, targetName, usePattern, visited, results, $"{path}/{childName}", depth + 1, maxDepth, maxResults);
                            idx++;
                        }
                    }
                }
            }
            catch { }

            // 也尝试通过 IEnumerable<> 属性递归（如 Connections、Tags、Screens 等）
            try
            {
                foreach (var prop in t.GetProperties())
                {
                    try
                    {
                        var ptype = prop.PropertyType;
                        if (ptype == typeof(string)) continue;
                        var enumInterface = ptype.GetInterfaces()
                            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
                        if (enumInterface != null)
                        {
                            var val = prop.GetValue(obj);
                            if (val == null) continue;
                            // 跳过已通过 Children 访问的
                            if (prop.Name == "Children") continue;
                            var idx = 0;
                            foreach (var item in (System.Collections.IEnumerable)val)
                            {
                                if (item == null) continue;
                                var itemName = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString() ?? $"[{idx}]";
                                FindObjRecurse(item, targetName, usePattern, visited, results, $"{path}/{prop.Name}[{itemName}]", depth + 1, maxDepth, maxResults);
                                idx++;
                                if (idx > 200) break; // 防止超大集合
                            }
                        }
                        // fix#18: 单对象组合属性递归——传统 HMI 的对象树主要经"单对象组合属性"
                        // 暴露（如 HmiTarget.ScreenFolder / AlarmFolder），Children 对经典 HMI
                        // 基本为空，仅枚举集合属性会导致树根都进不去（find_hmi_object 恒 0）。
                        // 只递归 Siemens.Engineering 命名空间的对象类型，排除基元/枚举/字符串。
                        else if (!ptype.IsEnum && !ptype.IsPrimitive &&
                                 ptype.Namespace != null &&
                                 ptype.Namespace.StartsWith("Siemens.Engineering", StringComparison.Ordinal))
                        {
                            var val = prop.GetValue(obj);
                            if (val == null) return;
                            FindObjRecurse(val, targetName, usePattern, visited, results,
                                $"{path}/{prop.Name}", depth + 1, maxDepth, maxResults);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ────────────────────────────────────────────────────────────
        // 报警 / 数据日志 / 文本列表 / 图形列表（反射）
        // ────────────────────────────────────────────────────────────

        private object? GetAlarmFolder(HmiTarget hmi)
        {
            var afProp = hmi.GetType().GetProperty("AlarmFolder") ?? hmi.GetType().GetProperty("Alarms");
            return afProp?.GetValue(hmi);
        }

        public string ListAlarmClasses()
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = new List<object>();
                    var af = GetAlarmFolder(hmi);
                    var ac = af?.GetType().GetProperty("AlarmClasses")?.GetValue(af);
                    if (ac is IEnumerable enumerable)
                        foreach (var a in enumerable)
                        {
                            var np = a.GetType().GetProperty("Name");
                            list.Add(new { name = np?.GetValue(a)?.ToString() ?? "?" });
                        }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        count = list.Count,
                        alarmClasses = list,
                        note = list.Count == 0 ? "经典 HMI 报警 API 路径未确认" : null
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateAlarmClass(string name)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var af = GetAlarmFolder(hmi);
                    if (af == null) return Err("经典 HMI 报警 API 未找到");
                    var ac = af.GetType().GetProperty("AlarmClasses")?.GetValue(af);
                    if (ac == null) return Err("AlarmClasses 属性未找到");
                    var created = TryCreateViaReflection(ac, name, "AlarmClasses");
                    var np = created?.GetType().GetProperty("Name");
                    return Ok($"已创建报警类别: {np?.GetValue(created)?.ToString() ?? name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteAlarmClass(string name)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var af = GetAlarmFolder(hmi);
                    if (af == null) return Err("经典 HMI 报警 API 未找到");
                    var ac = af.GetType().GetProperty("AlarmClasses")?.GetValue(af);
                    if (ac == null) return Err("AlarmClasses 属性未找到");
                    var findM = ac.GetType().GetMethod("Find", new[] { typeof(string) });
                    if (findM == null) return Err("AlarmClasses.Find 方法未找到");
                    var found = findM.Invoke(ac, new object[] { name });
                    if (found == null) return Err($"未找到报警类别: {name}");
                    found.GetType().GetMethod("Delete")?.Invoke(found, null);
                    return Ok($"已删除报警类别: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string CreateHmiDiscreteAlarm(string name) => CreateViaReflection(name, "DiscreteAlarmClass");
        public string CreateHmiAnalogAlarm(string name) => CreateViaReflection(name, "AnalogAlarmClass");
        public string CreateHmiTextList(string name) => CreateViaReflection(name, "TextLists");
        public string CreateHmiGraphicList(string name) => CreateViaReflection(name, "GraphicLists");

        /// <summary>
        /// 创建 HMI 数据日志。
        /// 修复点（任务 3）：
        ///   - DataLogs 集合不在经典 HmiTarget 上公开（GetProperty 返回 null，
        ///     GetAlarmFolder 也返回 null），原实现必然返回 "经典 HMI 未找到集合: DataLogs"。
        ///   - 新实现：优先在 Unified HMI 上调用 HmiSoftware.DataLogs.Create(name)，
        ///     这是 V19 Openness 公开支持的强类型 API。
        ///   - 经典 HMI 无对应 Openness API，返回明确错误并提示用户。
        /// </summary>
        public string CreateHmiDataLog(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return Err("参数 name 不能为空");

                    // 优先 Unified HMI（DataLogs 在 HmiSoftware 上强类型公开）
                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi != null)
                    {
                        var dataLogs = GetHmiProperty(unifiedHmi, "DataLogs");
                        if (dataLogs == null)
                            return Err("unifiedHmi.DataLogs 返回 null（集合不可用）");

                        // 同名检查
                        try
                        {
                            var existing = TryInvoke(dataLogs, "Find", new object[] { name });
                            if (existing != null)
                                return JsonConvert.SerializeObject(new
                                {
                                    success = true,
                                    message = $"数据日志已存在: {name}",
                                    dataLogName = GetHmiProperty(existing, "Name"),
                                    alreadyExisted = true,
                                    hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                                    hmiKind = "Unified"
                                }, Formatting.Indented);
                        }
                        catch { /* Find 失败不中断创建 */ }

                        object? created;
                        try { created = TryInvoke(dataLogs, "Create", new object[] { name }); }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException?.Message ?? tie.Message;
                            return JsonConvert.SerializeObject(new
                            {
                                success = false,
                                error = $"DataLogs.Create(\"{name}\") 失败: {inner}",
                                innerExceptionType = tie.InnerException?.GetType().Name ?? "(unknown)",
                                hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                                collectionType = dataLogs.GetType().FullName,
                                hmiKind = "Unified"
                            }, Formatting.Indented);
                        }

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"已创建数据日志: {GetHmiProperty(created, "Name")}",
                            dataLogName = GetHmiProperty(created, "Name"),
                            alreadyExisted = false,
                            hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                            collectionType = dataLogs.GetType().Name,
                            hmiKind = "Unified"
                        }, Formatting.Indented);
                    }

                    // 经典 HMI 回退：DataLogs 不在 HmiTarget 公开 API 上
                    // 原实现的 CreateViaReflection 会返回 "经典 HMI 未找到集合: DataLogs"
                    // 改进：返回更明确的诊断信息
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "经典 HMI 的 DataLogs 集合未在 Openness API 公开",
                        dataLogName = name,
                        note = "经典 HMI（WinCC Comfort/Advanced）的数据日志无法通过 Openness API 程序化创建。" +
                               "请在博途 GUI 中手动创建，或使用 Unified HMI 设备（WinCC Unified）。",
                        hmiKind = "Classic",
                        hint = "若项目已有 Unified HMI 设备，请通过 hmiDeviceName 参数指定设备名"
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private string CreateViaReflection(string name, string collectionProperty)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    // 尝试在 HmiTarget 上直接找集合属性，否则深入 AlarmFolder 等
                    var prop = hmi.GetType().GetProperty(collectionProperty);
                    object? collection = prop?.GetValue(hmi);
                    if (collection == null)
                    {
                        var af = GetAlarmFolder(hmi);
                        collection = af?.GetType().GetProperty(collectionProperty)?.GetValue(af);
                    }
                    if (collection == null)
                    {
                        // 改进：列出 HmiTarget 上所有可用属性，便于诊断
                        var availableProps = hmi.GetType().GetProperties()
                            .Select(p => p.Name)
                            .ToList();
                        return Err($"经典 HMI 未找到集合: {collectionProperty}。" +
                            $"HmiTarget 可用属性: {string.Join(", ", availableProps)}");
                    }
                    TryCreateViaReflection(collection, name, collectionProperty);
                    return Ok($"已创建 {collectionProperty}: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string DeleteHmiDiscreteAlarm(string name) => DeleteViaReflection(name, "DiscreteAlarmClass");
        public string DeleteHmiAnalogAlarm(string name) => DeleteViaReflection(name, "AnalogAlarmClass");
        public string DeleteHmiTextList(string name) => DeleteViaReflection(name, "TextLists");
        public string DeleteHmiGraphicList(string name) => DeleteViaReflection(name, "GraphicLists");
        public string DeleteHmiDataLog(string name) => DeleteViaReflection(name, "DataLogs");

        private string DeleteViaReflection(string name, string collectionProperty)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var prop = hmi.GetType().GetProperty(collectionProperty);
                    object? collection = prop?.GetValue(hmi);
                    if (collection == null)
                    {
                        var af = GetAlarmFolder(hmi);
                        collection = af?.GetType().GetProperty(collectionProperty)?.GetValue(af);
                    }
                    if (collection == null) return Err($"经典 HMI 未找到集合: {collectionProperty}");
                    var findM = collection.GetType().GetMethod("Find", new[] { typeof(string) });
                    if (findM == null) return Err($"{collectionProperty}.Find 方法未找到");
                    var found = findM.Invoke(collection, new object[] { name });
                    if (found == null) return Err($"未找到: {name}");
                    found.GetType().GetMethod("Delete")?.Invoke(found, null);
                    return Ok($"已删除 {collectionProperty}: {name}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ListHmiDiscreteAlarms() => ListViaReflection("DiscreteAlarms", "DiscreteAlarmClass");
        public string ListHmiAnalogAlarms() => ListViaReflection("AnalogAlarms", "AnalogAlarmClass");
        public string ListHmiTextLists() => ListViaReflection("TextLists", "TextLists");
        public string ListHmiGraphicLists() => ListViaReflection("GraphicLists", "GraphicLists");
        public string ListHmiDataLogs() => ListViaReflection("DataLogs", "DataLogs");

        private string ListViaReflection(string resultName, string collectionProperty)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var list = new List<object>();
                    var prop = hmi.GetType().GetProperty(collectionProperty);
                    object? collection = prop?.GetValue(hmi);
                    if (collection == null)
                    {
                        var af = GetAlarmFolder(hmi);
                        collection = af?.GetType().GetProperty(collectionProperty)?.GetValue(af);
                    }
                    if (collection is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            var np = item.GetType().GetProperty("Name");
                            list.Add(new { name = np?.GetValue(item)?.ToString() ?? "?" });
                        }
                    }
                    // 注意：匿名对象不能用计算属性名 [resultName]，故统一用 "items"
                    var payload = new Dictionary<string, object?>
                    {
                        ["success"] = true,
                        ["count"] = list.Count,
                        ["collection"] = collectionProperty,
                        [resultName] = list
                    };
                    return JsonConvert.SerializeObject(payload);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // HMI 变量表导出/导入
        // ────────────────────────────────────────────────────────────

        public string ExportHmiTagTable(string tagTableName, string outputPath)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    TagTable table;
                    if (string.IsNullOrEmpty(tagTableName))
                        table = hmi.TagFolder.DefaultTagTable;
                    else
                        table = FindTagTableRecursive(hmi.TagFolder, tagTableName)
                                ?? throw new Exception($"未找到变量表: {tagTableName}");
                    EnsureDir3(outputPath);
                    table.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                    return Ok($"变量表 {table.Name} 已导出到: {outputPath}");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string ImportHmiTagTable(string filePath)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    if (!File.Exists(filePath)) return Err($"文件不存在: {filePath}");

                    // 预验证：解析 XML 检查连接和 ControllerTag 引用
                    var doc = System.Xml.Linq.XDocument.Load(filePath);
                    var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;

                    // 检查连接引用
                    var connNames = doc.Descendants(ns + "Connection")
                        .Where(e => e.Attribute("TargetID")?.Value == "@OpenLink")
                        .Select(e => e.Value.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Distinct()
                        .ToList();

                    if (connNames.Count > 0)
                    {
                        // 验证 HMI 连接是否存在
                        // 注意：Openness API 的 hmi.Connections 在已配置好连接的真实项目中也可能返回 0
                        // （连接对象不在该集合中暴露），因此必须从现有 Tag 的 LinkList 中提取连接名来验证
                        var existingConns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var c in hmi.Connections)
                            if (!string.IsNullOrEmpty(c.Name)) existingConns.Add(c.Name);

                        // 如果 hmi.Connections 找不到，从现有 Tag 的导出 XML 中提取连接名
                        if (existingConns.Count == 0)
                        {
                            try
                            {
                                var tempExport = Path.Combine(Path.GetTempPath(), $"tia_conn_check_{Guid.NewGuid():N}.xml");
                                hmi.TagFolder.DefaultTagTable.Export(new FileInfo(tempExport), ExportOptions.WithDefaults);
                                var tdoc = System.Xml.Linq.XDocument.Load(tempExport);
                                var tns = tdoc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                                foreach (var connElem in tdoc.Descendants(tns + "Connection"))
                                {
                                    var cn = connElem.Value?.Trim();
                                    if (!string.IsNullOrEmpty(cn)) existingConns.Add(cn);
                                }
                                TryDelete3(tempExport);
                            }
                            catch { /* 忽略导出失败 */ }
                        }

                        var missingConns = connNames.Where(cn => !existingConns.Contains(cn)).ToList();
                        if (missingConns.Count > 0 && existingConns.Count > 0)
                            return Err($"XML 中引用的 HMI 连接不存在: {string.Join(", ", missingConns)}。" +
                                       $"\n现有连接: {(existingConns.Count > 0 ? string.Join(", ", existingConns) : "无")}" +
                                       $"\n请先在博途中创建 HMI 连接。");
                        // ★修复★ existingConns 为空（hmi.Connections 集合空 + 现有变量表无连接）时：
                        // 连接可能是"集成连接"（同一 PN/IE 子网由 TIA 自动生成，Openness 的
                        // HmiTarget.Connections 集合不暴露集成连接）。此时无法校验连接存在性，
                        // 降级为警告继续导入（TIA 导入时按真实连接解析；连接确实不存在时会
                        // 在导入结果中体现，且本方法外层有 catch 兜底）。
                    }

                    var imported = hmi.TagFolder.TagTables.Import(new FileInfo(filePath), ImportOptions.Override);
                    var first = imported.FirstOrDefault();
                    return Ok(first != null ? $"已导入变量表: {first.Name}" : "变量表导入完成");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 批量创建 HMI 变量并绑定到 PLC 变量。
        /// 使用从博途导出的正确 XML 格式，通过 TagTable.Import 导入。
        /// </summary>
        public string BatchCreateHmiTags(string tagsJson)
        {
            lock (_lock)
            {
                try
                {
                    var hmi = RequireClassicHmi();
                    var tagDefs = JsonConvert.DeserializeObject<List<HmiTagDef>>(tagsJson)
                                  ?? throw new Exception("无法解析 tagsJson");
                    if (tagDefs.Count == 0) return Err("tagsJson 为空");

                    // ★崩溃修复★ Real 类型前置拒绝：XML 导入对 Real 的任何 Coding 值都会被博途拒绝，
                    // 且导入失败可导致博途进程退出。Real 请用 Int+kPa 镜像方案（FC/FB 做换算）。
                    var realTags = tagDefs.Where(td =>
                        string.Equals(td.dataType, "Real", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(td.dataType, "Float", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (realTags.Count > 0)
                        return Err("HMI 变量不支持 Real/Float 类型（博途 XML 导入对 Real 的任何 Coding 值都会拒绝，且导入失败可导致博途进程退出）。" +
                                   "请改用 Int+kPa 镜像方案：PLC 侧用 Int 变量存储千帕值，FC/FB 中做 Real↔Int 换算，HMI 只绑定 Int 变量。受影响变量: " +
                                   string.Join(", ", realTags.Select(td => td.tagName ?? "<unnamed>")));

                    // 兼容工具旧说明中的 address 字段：存在绝对地址时统一转交 Absolute 导入器，
                    // 禁止在 Symbolic XML 中静默忽略 address。
                    var absoluteTags = tagDefs.Where(td => !string.IsNullOrWhiteSpace(td.address)).ToList();
                    if (absoluteTags.Count > 0)
                    {
                        if (absoluteTags.Count != tagDefs.Count)
                            return Err("同一次 batch_create_hmi_tags 不能混合 absolute address 与 symbolic plcTag；请拆成两次调用");
                        var distinctConnections = absoluteTags.Select(td => td.connection)
                            .Where(c => !string.IsNullOrWhiteSpace(c))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (distinctConnections.Count > 1)
                            return Err("Absolute 标签包含多个不同 connection，请按连接拆分批次");
                        var absolutePayload = JsonConvert.SerializeObject(absoluteTags.Select(td => new
                        {
                            name = td.tagName,
                            dataType = td.dataType ?? "Bool",
                            address = td.address,
                            connection = td.connection,
                            acquisitionCycle = td.acquisitionCycle
                        }));
                        return ImportHmiTagsAbsolute(absolutePayload, distinctConnections.FirstOrDefault(), null);
                    }

                    // ── 预验证 1: 只有外部符号变量才要求 HMI 连接；内部变量可独立创建 ──
                    var requiresConnection = tagDefs.Any(td => !string.IsNullOrWhiteSpace(td.plcTag));
                    var connectionName = "";
                    // ★崩溃修复★ 已知连接名集合：Connections 集合 + 现有变量表导出 XML 提取。
                    // 集成连接（TIA 自动生成的 HMI_连接_x）不出现在 hmi.Connections 集合，
                    // 但会出现在已有变量的 LinkList/Connection 中；两者合并后用于存在性校验。
                    var knownConnectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (requiresConnection)
                    {
                        var conns = hmi.Connections;
                        var firstConn = conns.FirstOrDefault();
                        if (firstConn != null) connectionName = firstConn.Name;
                        try
                        {
                            foreach (var c in conns)
                            {
                                if (!string.IsNullOrEmpty(c.Name)) knownConnectionNames.Add(c.Name);
                            }
                        }
                        catch { }

                        try
                        {
                            var tempExport = Path.Combine(Path.GetTempPath(), $"tia_tagtable_{Guid.NewGuid():N}.xml");
                            try
                            {
                                hmi.TagFolder.DefaultTagTable.Export(new FileInfo(tempExport), ExportOptions.WithDefaults);
                                var tdoc = XDocument.Load(tempExport);
                                var tns = tdoc.Root?.Name.Namespace ?? XNamespace.None;
                                foreach (var tagElem in tdoc.Descendants(tns + "Hmi.Tag.Tag"))
                                {
                                    var connName = tagElem.Element(tns + "LinkList")?.Element(tns + "Connection")?.Value?.Trim();
                                    if (!string.IsNullOrEmpty(connName))
                                    {
                                        knownConnectionNames.Add(connName!);
                                        if (string.IsNullOrEmpty(connectionName)) connectionName = connName!;
                                    }
                                }
                            }
                            finally { TryDelete3(tempExport); }
                        }
                        catch { }

                        if (string.IsNullOrEmpty(connectionName))
                        {
                            var explicitConnections = tagDefs
                                .Where(td => !string.IsNullOrWhiteSpace(td.plcTag))
                                .Select(td => td.connection)
                                .Where(c => !string.IsNullOrWhiteSpace(c))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (explicitConnections.Count == 1) connectionName = explicitConnections[0]!;
                        }

                        var missingConnectionTags = tagDefs
                            .Where(td => !string.IsNullOrWhiteSpace(td.plcTag)
                                         && string.IsNullOrWhiteSpace(td.connection)
                                         && string.IsNullOrWhiteSpace(connectionName))
                            .Select(td => td.tagName ?? "<unnamed>")
                            .ToList();
                        if (missingConnectionTags.Count > 0)
                            return Err("以下外部 HMI 变量没有可用连接: " + string.Join(", ", missingConnectionTags) +
                                       "。请为每个变量显式指定 connection，或先用带真实 PLC 伙伴关系的连接 XML 模板创建连接。");

                        // ── 预验证 1.5: 校验请求的连接确实存在（杜绝悬空 Connection 引用导入导致博途崩溃） ──
                        // ★注意★ 已知连接集合为空时（新项目无种子变量；集成连接 HMI_连接_x 不出现在
                        // hmi.Connections 集合）无法确认连接存在性，此时放行——合法性交由 TIA 验证，
                        // 由对话框自动应答 + 事务保护兜底防崩溃。集合非空时严格拦截不存在的连接名。
                        var requestedConnections = tagDefs
                            .Where(td => !string.IsNullOrWhiteSpace(td.plcTag))
                            .Select(td => string.IsNullOrWhiteSpace(td.connection) ? connectionName : td.connection!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var missingConnections = knownConnectionNames.Count == 0
                            ? new List<string>()
                            : requestedConnections.Where(c => !knownConnectionNames.Contains(c)).ToList();
                        if (missingConnections.Count > 0)
                            return Err("以下 HMI 连接不存在，未执行变量导入: " + string.Join(", ", missingConnections)
                                       + "。现有连接: " + (knownConnectionNames.Count == 0 ? "无" : string.Join(", ", knownConnectionNames))
                                       + "。请先创建连接，或使用真实存在的连接名。");
                    }

                    // ── 预验证 2: 检查 PLC 变量是否存在 ──
                    // 获取 PLC 中所有变量名（从 PLC 变量表读取）
                    var plcTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var plc = GetPlcSoftware();
                        if (plc != null)
                        {
                            foreach (var tt in plc.TagTableGroup.TagTables)
                            {
                                foreach (var tag in tt.Tags)
                                {
                                    if (!string.IsNullOrEmpty(tag.Name))
                                        plcTagNames.Add(tag.Name);
                                }
                            }
                        }
                    }
                    catch { /* 读取 PLC 变量失败不阻塞，但会跳过验证 */ }

                    // 检查每个 ControllerTag 是否存在（只有显式指定了 plcTag 才检查，
                    // 未指定 plcTag 的 Tag 视为内部变量，不绑定 PLC 变量）
                    // ★修复★ DB 成员符号（含 "." 或引号，如 "DB_Status"."压力kPa"）不在 PLC 变量表中，
                    // 无法用变量表名单校验；统一跳过，交由 TIA 导入时解析（导入失败有事务保护兜底）。
                    var missingTags = new List<string>();
                    foreach (var td in tagDefs)
                    {
                        var plcTag = td.plcTag ?? "";
                        if (string.IsNullOrEmpty(plcTag) || plcTagNames.Count == 0) continue;
                        if (plcTag.IndexOf('.') >= 0 || plcTag.IndexOf('"') >= 0) continue;
                        if (!plcTagNames.Contains(plcTag)) missingTags.Add(plcTag);
                    }
                    if (missingTags.Count > 0)
                        return Err($"以下 PLC 变量不存在，无法绑定: {string.Join(", ", missingTags)}。" +
                                   $"\n现有 PLC 变量: {string.Join(", ", plcTagNames.Take(30))}" +
                                   (plcTagNames.Count > 30 ? "..." : "") +
                                   $"\n请先在 PLC 变量表中创建这些变量，或修改 plcTag 参数。");

                    // ── 预验证 2.5: DB 成员符号存在性校验 ──
                    // ★崩溃修复★ 指向不存在 DB 块/成员的 plcTag 会导致 Import 失败并使博途进程退出，
                    // 因此必须在导入前拦截。读取块名集合（必做）与成员映射（尽力而为）。
                    var dbBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        var plc = GetPlcSoftware();
                        if (plc != null)
                        {
                            foreach (var blk in plc.BlockGroup.Blocks)
                            {
                                if (string.Equals(GetBlockTypeName(blk), "DB", StringComparison.OrdinalIgnoreCase))
                                    dbBlockNames.Add(blk.Name);
                            }
                        }
                    }
                    catch { }
                    Dictionary<string, HashSet<string>>? dbMembersMap = null;
                    try { dbMembersMap = GetDbMembersMap(); } catch { }
                    foreach (var td in tagDefs)
                    {
                        var plcTag = td.plcTag ?? "";
                        var dotIndex = plcTag.IndexOf('.');
                        if (dotIndex <= 0) continue;
                        var blockRaw = plcTag.Substring(0, dotIndex).Trim().Trim('"').Trim();
                        var memberRaw = plcTag.Substring(dotIndex + 1).Trim().Trim('"').Trim();
                        if (dbBlockNames.Count > 0 && !dbBlockNames.Contains(blockRaw))
                            return Err($"PLC DB 块不存在，无法绑定: {blockRaw}。" +
                                       $"\n现有 DB 块: {string.Join(", ", dbBlockNames.OrderBy(x => x).Take(30))}" +
                                       $"\n请修改 plcTag 为真实存在的 DB 块名。");
                        if (dbMembersMap != null && dbMembersMap.Count > 0 && dbMembersMap.ContainsKey(blockRaw)
                            && dbMembersMap[blockRaw].Count > 0
                            && !dbMembersMap[blockRaw].Contains(memberRaw))
                            return Err($"DB 块 {blockRaw} 中不存在成员: {memberRaw}。" +
                                       $"\n现有成员: {string.Join(", ", dbMembersMap[blockRaw].OrderBy(x => x).Take(30))}" +
                                       $"\n请修改 plcTag 为真实存在的 DB 成员。");
                    }

                    // ── 预验证 3: 检查 HMI 变量是否已存在（避免重复导入） ──
                    var existingHmiTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var t in GetAllTags(hmi.TagFolder))
                        {
                            var np = t.GetType().GetProperty("Name");
                            var n = np?.GetValue(t)?.ToString();
                            if (!string.IsNullOrEmpty(n)) existingHmiTags.Add(n);
                        }
                    }
                    catch { /* 忽略 */ }

                    var newTagDefs = tagDefs.Where(td =>
                        !existingHmiTags.Contains(td.tagName ?? "")).ToList();
                    if (newTagDefs.Count == 0)
                        return Ok($"所有 {tagDefs.Count} 个 HMI 变量已存在，无需导入。");
                    if (newTagDefs.Count < tagDefs.Count)
                    {
                        var skipped = tagDefs.Where(td => existingHmiTags.Contains(td.tagName ?? ""))
                            .Select(td => td.tagName).ToList();
                        // 只导入新变量
                    }

                    // ── 生成 XML 并导入 ──
                    // 使用与项目匹配的 Engineering 版本号（从现有画面导出 XML 提取），避免版本不匹配导致 Import 失败
                    var engVer = GetEngineeringVersion(hmi);

                    // 重要：ImportOptions.Override 会覆盖整个变量表，导致现有 Tag 丢失。
                    // 因此必须先导出现有变量表，合并新 Tag 后再导入。
                    // ★崩溃修复★ tagTableBackupXml 保留原变量表备份：Import 失败时恢复，避免半状态残留。
                    string? tagTableBackupXml = null;
                    string mergedXml;
                    var tempExportMerge = Path.Combine(Path.GetTempPath(), $"tia_tagtable_export_{Guid.NewGuid():N}.xml");
                    try
                    {
                        hmi.TagFolder.DefaultTagTable.Export(new FileInfo(tempExportMerge), ExportOptions.WithDefaults);
                        tagTableBackupXml = File.ReadAllText(tempExportMerge);
                        TryDelete3(tempExportMerge);
                        // 解析现有 XML，提取现有 Tag 数量和最大 ID
                        var existingDoc = System.Xml.Linq.XDocument.Parse(tagTableBackupXml);
                        var ens = existingDoc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                        var existingTags = existingDoc.Descendants(ens + "Hmi.Tag.Tag").ToList();
                        // 找出所有元素的最大数字 ID（包括 Tag 和子对象 MultilingualText 等），新 Tag 从此基础上继续递增
                        int maxId = 0;
                        foreach (var elem in existingDoc.Descendants())
                        {
                            var idAttr = elem.Attribute("ID")?.Value;
                            if (int.TryParse(idAttr, System.Globalization.NumberStyles.HexNumber, null, out int idVal))
                                if (idVal > maxId) maxId = idVal;
                        }
                        // 找到 ObjectList 节点（TagTable 下的）。
                        // ★空表修复★ 空变量表导出的 XML 可能既无 <Hmi.Tag.TagTable> 也无 <ObjectList>：
                        // 找不到 TagTable 时重建最小 TagTable 结构，找不到 ObjectList 时在 TagTable 下新建，
                        // 避免"无法从现有变量表 XML 中找到 ObjectList 节点"失败。
                        System.Xml.Linq.XElement tagTable;
                        var rootIsTagTable = existingDoc.Root?.Name == ens + "Hmi.Tag.TagTable";
                        var foundTagTable = existingDoc.Descendants(ens + "Hmi.Tag.TagTable").FirstOrDefault();
                        if (rootIsTagTable) tagTable = existingDoc.Root!;
                        else if (foundTagTable != null) tagTable = foundTagTable;
                        else
                        {
                            tagTable = new System.Xml.Linq.XElement(ens + "Hmi.Tag.TagTable", new System.Xml.Linq.XAttribute("ID", "0"));
                            tagTable.Add(new System.Xml.Linq.XElement(ens + "AttributeList",
                                new System.Xml.Linq.XElement(ens + "Name", "默认变量表")));
                            if (existingDoc.Root != null) existingDoc.Root.Add(tagTable);
                        }
                        var objectList = tagTable.Element(ens + "ObjectList");
                        if (objectList == null)
                        {
                            objectList = new System.Xml.Linq.XElement(ens + "ObjectList");
                            tagTable.Add(objectList);
                        }

                        // ★V17 修复★ 优先复用现有变量表导出 XML 中的 Culture（如 de-DE/en-US），
                        // 提取不到时回退 zh-CN，避免硬编码 Culture 与项目语言设置不符。
                        var cultureName = existingDoc.Descendants(ens + "Culture")
                            .Select(e => e.Value)
                            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "zh-CN";

                        // 生成新 Tag 的 XML 片段并插入到 ObjectList
                        int idCounter = maxId + 1;
                        var fragment = GenerateHmiTagXmlFragment(newTagDefs, connectionName, idCounter, ens, cultureName);
                        // fragment 是 <Root><Hmi.Tag.Tag/>...</Root>，需要取出 Root 下的子元素
                        var fragmentRoot = System.Xml.Linq.XElement.Parse(fragment);
                        foreach (var child in fragmentRoot.Elements())
                            objectList.Add(child);

                        // ★修复★ 写文件前显式声明 utf-8：XDocument.ToString() 可能输出 utf-16 声明，
                        // 与下方 UTF-8+BOM 写入不一致会导致导入器解析失败。
                        existingDoc.Declaration = new System.Xml.Linq.XDeclaration("1.0", "utf-8", null);
                        // 保存合并后的 XML
                        mergedXml = existingDoc.ToString();
                    }
                    catch (Exception ex)
                    {
                        TryDelete3(tempExportMerge);
                        return Err($"合并现有变量表失败: {ex.Message}");
                    }

                    var tempImport = Path.Combine(Path.GetTempPath(), $"tia_tagtable_import_{Guid.NewGuid():N}.xml");
                    try
                    {
                        File.WriteAllText(tempImport, mergedXml, new UTF8Encoding(true));
                        var imported = hmi.TagFolder.TagTables.Import(new FileInfo(tempImport), ImportOptions.Override);
                        var first = imported.FirstOrDefault();
                        var namesAfterImport = new HashSet<string>(
                            GetAllTags(hmi.TagFolder).Select(t =>
                            {
                                try { return t.Name; } catch { return ""; }
                            }).Where(n => !string.IsNullOrWhiteSpace(n)),
                            StringComparer.OrdinalIgnoreCase);
                        var missingAfterImport = newTagDefs
                            .Select(td => td.tagName ?? "")
                            .Where(n => !namesAfterImport.Contains(n))
                            .ToList();
                        if (missingAfterImport.Count > 0)
                            return Err("HMI 变量导入调用已返回，但以下变量未在项目中找到: " + string.Join(", ", missingAfterImport));

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = first != null
                                ? $"已批量导入 {newTagDefs.Count} 个变量到变量表: {first.Name}"
                                : $"已批量导入 {newTagDefs.Count} 个变量",
                            connection = string.IsNullOrWhiteSpace(connectionName) ? null : connectionName,
                            externalTagCount = newTagDefs.Count(td => !string.IsNullOrWhiteSpace(td.plcTag)),
                            internalTagCount = newTagDefs.Count(td => string.IsNullOrWhiteSpace(td.plcTag)),
                            verified = true
                        });
                    }
                    catch (Exception importEx)
                    {
                        // ★崩溃修复★ 导入失败时用备份恢复原变量表，避免半状态残留引发后续崩溃
                        TryRestoreTagTableBackup(hmi, tagTableBackupXml);
                        return Err($"HMI 变量导入失败，已尝试恢复原变量表: {importEx.Message}");
                    }
                    finally { TryDelete3(tempImport); }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 导入失败时的变量表恢复：用备份 XML 重新导入原变量表。
        /// 导入失败后项目可能残留半状态，后续访问易触发 disposed 崩溃；恢复可回到导入前状态。
        /// </summary>
        private void TryRestoreTagTableBackup(Siemens.Engineering.Hmi.HmiTarget hmi, string? backupXml)
        {
            if (hmi == null || string.IsNullOrWhiteSpace(backupXml)) return;
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"tia_tagrestore_{Guid.NewGuid():N}.xml");
                try
                {
                    File.WriteAllText(tempFile, backupXml, new UTF8Encoding(true));
                    hmi.TagFolder.TagTables.Import(new FileInfo(tempFile), ImportOptions.Override);
                }
                finally { TryDelete3(tempFile); }
            }
            catch (Exception restoreEx)
            {
                Console.Error.WriteLine("[tia-mcp] HMI 变量表恢复失败: " + restoreEx.Message);
            }
        }

        /// <summary>
        /// 读取所有全局 DB 块的成员名映射（块名 → 成员名集合），用于 HMI 变量绑定前置校验。
        /// Optimized DB 的 Interface 反射通常不可用（无接口），反射失败时回退 Export XML 解析。
        /// 读取失败时该块成员集合为空，调用方据此跳过成员级校验（保留块级校验）。
        /// </summary>
        private Dictionary<string, HashSet<string>> GetDbMembersMap()
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var plc = GetPlcSoftware();
                if (plc == null) return map;
                foreach (var blk in plc.BlockGroup.Blocks)
                {
                    if (!string.Equals(GetBlockTypeName(blk), "DB", StringComparison.OrdinalIgnoreCase)) continue;
                    var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // 方式1: 反射 Interface.Sections.Members（标准 DB 可用）
                    try
                    {
                        var secObj = blk.GetType().GetProperty("Interface")?.GetValue(blk);
                        var sections = secObj as IEnumerable;
                        if (sections != null)
                        {
                            foreach (var sec in sections)
                            {
                                var mems = sec?.GetType().GetProperty("Members")?.GetValue(sec) as IEnumerable;
                                if (mems == null) continue;
                                foreach (var m in mems)
                                {
                                    var n = GetProperty(m, "Name") as string;
                                    if (!string.IsNullOrEmpty(n)) members.Add(n!);
                                }
                            }
                        }
                    }
                    catch { }

                    // 方式2: Export XML 解析（Optimized DB 反射不可用时回退）
                    if (members.Count == 0)
                    {
                        try
                        {
                            var tempPath = Path.Combine(Path.GetTempPath(), $"TiaMcp_DbMem_{Guid.NewGuid():N}.xml");
                            try
                            {
                                blk.Export(new FileInfo(tempPath), ExportOptions.WithDefaults);
                                var xml = File.ReadAllText(tempPath, Encoding.UTF8);
                                var memberMatches = System.Text.RegularExpressions.Regex.Matches(
                                    xml, @"<Member\s+Name=""([^""]+)""\s+Datatype=""[^""]+""");
                                foreach (System.Text.RegularExpressions.Match match in memberMatches)
                                {
                                    if (!string.IsNullOrEmpty(match.Groups[1].Value))
                                        members.Add(match.Groups[1].Value);
                                }
                            }
                            finally { try { File.Delete(tempPath); } catch { } }
                        }
                        catch { }
                    }

                    map[blk.Name] = members;
                }
            }
            catch { }
            return map;
        }

        /// <summary>
        /// 生成新 Tag 的 XML 片段（多个 Hmi.Tag.Tag 元素包在一个根元素中，便于 XElement.Parse）。
        /// startIdCounter 是新 Tag 起始的 ID（十六进制），每个 Tag 消耗 10 个 ID（1个Tag + 9个MultilingualText子对象）。
        /// ns 是 XML namespace（通常为 None）。
        /// </summary>
        private string GenerateHmiTagXmlFragment(List<HmiTagDef> tagDefs, string connectionName, int startIdCounter, System.Xml.Linq.XNamespace ns, string culture = "zh-CN")
        {
            var sb = new StringBuilder();
            sb.AppendLine("<Root>");
            int idCounter = startIdCounter;
            foreach (var td in tagDefs)
            {
                var tagName = td.tagName ?? $"Tag_{idCounter}";
                var dataType = td.dataType ?? "Bool";
                // 只有显式指定了 plcTag 才绑定 PLC 变量，未指定视为内部变量
                var plcTag = td.plcTag ?? "";
                var tagConn = !string.IsNullOrEmpty(td.connection) ? td.connection : connectionName;
                // ★V17 修复★ Length 必须按数据类型填写（Bool/Byte=1, Word/Int=2, DWord/DInt/Real=4）。
                // V17 导入器不会自动推导，Length=0 会报
                // "Cannot set the length ... The length of the data type is 2, the HMI tag has the value 0"。
                var length = GetHmiAbsoluteTagLength(dataType).ToString();
                var acqCycle = td.acquisitionCycle ?? "1 s";
                var idHex = idCounter.ToString("X");
                idCounter++;

                int c1 = idCounter++, c2 = idCounter++, c3 = idCounter++;
                int d1 = idCounter++, d2 = idCounter++, d3 = idCounter++;
                int e1 = idCounter++, e2 = idCounter++, e3 = idCounter++;

                var plcTagNameForXml = NormalizePlcTagSymbol(plcTag);
                // Real 类型已在前置校验拒绝（XML 导入对 Real 的任何 Coding 值都失败），此处固定 Binary。
                var codingXml = "          <Coding>Binary</Coding>" + Environment.NewLine;

                sb.AppendLine($@"      <Hmi.Tag.Tag ID=""{idHex}"" CompositionName=""Tags"">
        <AttributeList>
          <AcquisitionTriggerMode>Visible</AcquisitionTriggerMode>
          <AddressAccessMode>Symbolic</AddressAccessMode>
{codingXml}          <ConfirmationType>None</ConfirmationType>
          <GmpRelevant>false</GmpRelevant>
          <JobNumber>0</JobNumber>
          <Length>{length}</Length>
          <LinearScaling>false</LinearScaling>
          <LogicalAddress />
          <MandatoryCommenting>false</MandatoryCommenting>
          <Name>{System.Security.SecurityElement.Escape(tagName)}</Name>
          <Persistency>false</Persistency>
          <QualityCode>false</QualityCode>
          <ScalingHmiHigh>100</ScalingHmiHigh>
          <ScalingHmiLow>0</ScalingHmiLow>
          <ScalingPlcHigh>10</ScalingPlcHigh>
          <ScalingPlcLow>0</ScalingPlcLow>
          <StartValue />
          <SubstituteValue />
          <SubstituteValueUsage>None</SubstituteValueUsage>
          <Synchronization>false</Synchronization>
          <UpdateMode>ProjectWide</UpdateMode>
          <UseMultiplexing>false</UseMultiplexing>
        </AttributeList>
        <LinkList>
          <AcquisitionCycle TargetID=""@OpenLink"">
            <Name>{acqCycle}</Name>
          </AcquisitionCycle>");
                if (!string.IsNullOrEmpty(plcTag))
                {
                    if (!string.IsNullOrEmpty(tagConn))
                    {
                        sb.AppendLine($@"          <Connection TargetID=""@OpenLink"">
            <Name>{System.Security.SecurityElement.Escape(tagConn)}</Name>
          </Connection>");
                    }
                    sb.AppendLine($@"          <ControllerTag TargetID=""@OpenLink"">
            <Name>{System.Security.SecurityElement.Escape(plcTagNameForXml)}</Name>
          </ControllerTag>");
                }
                sb.AppendLine($@"          <DataType TargetID=""@OpenLink"">
            <Name>{dataType}</Name>
          </DataType>
          <HmiDataType TargetID=""@OpenLink"">
            <Name>{dataType}</Name>
          </HmiDataType>
        </LinkList>
        <ObjectList>
          <MultilingualText ID=""{c1:X}"" CompositionName=""Comment"">
            <ObjectList>
              <MultilingualTextItem ID=""{c2:X}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>{culture}</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{d1:X}"" CompositionName=""DisplayName"">
            <ObjectList>
              <MultilingualTextItem ID=""{d2:X}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>{culture}</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{e1:X}"" CompositionName=""TagValue"">
            <ObjectList>
              <MultilingualTextItem ID=""{e2:X}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>{culture}</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </Hmi.Tag.Tag>");
            }
            sb.AppendLine("</Root>");
            return sb.ToString();
        }

        /// <summary>
        /// 生成 HMI 变量表 XML，格式与博途导出完全一致。
        /// engVersion 根据项目实际使用的 TIA Portal 版本动态传入（从现有画面导出 XML 提取）。
        /// </summary>
        private string GenerateHmiTagTableXml(List<HmiTagDef> tagDefs, string connectionName, string engVersion = "", string culture = "zh-CN")
        {
            if (string.IsNullOrWhiteSpace(engVersion)) engVersion = EnvironmentDiscoveryService.CurrentEngineeringVersion();
            var sb = new StringBuilder();
            sb.AppendLine($@"<?xml version=""1.0"" encoding=""utf-8""?>
<Document>
  <Engineering version=""{engVersion}"" />
  <DocumentInfo>
    <Created>2026-06-22T08:00:00.0000000Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
    <InstalledProducts>
      <Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></Product>
      <OptionPackage><DisplayName>TIA Portal Openness</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></OptionPackage>
      <Product><DisplayName>STEP 7 Professional</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></Product>
      <Product><DisplayName>{WinccProductName(engVersion)}</DisplayName><DisplayVersion>{engVersion}</DisplayVersion></Product>
    </InstalledProducts>
  </DocumentInfo>
  <Hmi.Tag.TagTable ID=""0"">
    <AttributeList>
      <Name>默认变量表</Name>
    </AttributeList>
    <ObjectList>");

            int idCounter = 1;
            foreach (var td in tagDefs)
            {
                var tagName = td.tagName ?? $"Tag_{idCounter}";
                var dataType = td.dataType ?? "Bool";
                // 只有显式指定了 plcTag 才绑定 PLC 变量，未指定视为内部变量
                var plcTag = td.plcTag ?? "";
                // 每个 Tag 可以指定自己的 connection，覆盖全局 connectionName
                var tagConn = !string.IsNullOrEmpty(td.connection) ? td.connection : connectionName;
                // ★V17 修复★ Length 必须按数据类型填写，V17 导入器不会自动推导（同上）。
                var length = GetHmiAbsoluteTagLength(dataType).ToString();
                var acqCycle = td.acquisitionCycle ?? "1 s";
                var idHex = idCounter.ToString("X");
                idCounter++;

                // 为每个变量的 MultilingualText 子对象分配 ID
                int c1 = idCounter++, c2 = idCounter++, c3 = idCounter++;
                int d1 = idCounter++, d2 = idCounter++, d3 = idCounter++;
                int e1 = idCounter++, e2 = idCounter++, e3 = idCounter++;

                // ControllerTag 的 Name 格式：含特殊字符（如 / ( ) 空格）的变量名需要双引号包裹
                // 例如 "M_手/自动切换" 需要写成 "\"M_手/自动切换\""
                // 普通变量名如 M_给料皮带正转手动 不需要双引号
                // ★崩溃修复★ DB 成员符号统一规范化为 "块名"."成员名"（如 "DB_Status"."压力kPa"），
                // 无引号点分隔格式（DB_Status.压力kPa）导入会被博途拒绝并使进程退出。
                var plcTagNameForXml = NormalizePlcTagSymbol(plcTag);
                // Real 类型已在前置校验拒绝（XML 导入对 Real 的任何 Coding 值都失败），此处固定 Binary。
                var codingXml = "          <Coding>Binary</Coding>" + Environment.NewLine;

                sb.AppendLine($@"      <Hmi.Tag.Tag ID=""{idHex}"" CompositionName=""Tags"">
        <AttributeList>
          <AcquisitionTriggerMode>Visible</AcquisitionTriggerMode>
          <AddressAccessMode>Symbolic</AddressAccessMode>
{codingXml}          <ConfirmationType>None</ConfirmationType>
          <GmpRelevant>false</GmpRelevant>
          <JobNumber>0</JobNumber>
          <Length>{length}</Length>
          <LinearScaling>false</LinearScaling>
          <LogicalAddress />
          <MandatoryCommenting>false</MandatoryCommenting>
          <Name>{System.Security.SecurityElement.Escape(tagName)}</Name>
          <Persistency>false</Persistency>
          <QualityCode>false</QualityCode>
          <ScalingHmiHigh>100</ScalingHmiHigh>
          <ScalingHmiLow>0</ScalingHmiLow>
          <ScalingPlcHigh>10</ScalingPlcHigh>
          <ScalingPlcLow>0</ScalingPlcLow>
          <StartValue />
          <SubstituteValue />
          <SubstituteValueUsage>None</SubstituteValueUsage>
          <Synchronization>false</Synchronization>
          <UpdateMode>ProjectWide</UpdateMode>
          <UseMultiplexing>false</UseMultiplexing>
        </AttributeList>
        <LinkList>
          <AcquisitionCycle TargetID=""@OpenLink"">
            <Name>{acqCycle}</Name>
          </AcquisitionCycle>");
                // 只有有 plcTag 的变量才输出 Connection 和 ControllerTag（内部变量不需要）
                if (!string.IsNullOrEmpty(plcTag))
                {
                    if (!string.IsNullOrEmpty(tagConn))
                    {
                        sb.AppendLine($@"          <Connection TargetID=""@OpenLink"">
            <Name>{System.Security.SecurityElement.Escape(tagConn)}</Name>
          </Connection>");
                    }
                    sb.AppendLine($@"          <ControllerTag TargetID=""@OpenLink"">
            <Name>{System.Security.SecurityElement.Escape(plcTagNameForXml)}</Name>
          </ControllerTag>");
                }
                sb.AppendLine($@"          <DataType TargetID=""@OpenLink"">
            <Name>{dataType}</Name>
          </DataType>
          <HmiDataType TargetID=""@OpenLink"">
            <Name>{dataType}</Name>
          </HmiDataType>
        </LinkList>
        <ObjectList>
          <MultilingualText ID=""{c1:X}"" CompositionName=""Comment"">
            <ObjectList>
              <MultilingualTextItem ID=""{c2:X}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>{culture}</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{d1:X}"" CompositionName=""DisplayName"">
            <ObjectList>
              <MultilingualTextItem ID=""{d2:X}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>{culture}</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID=""{e1:X}"" CompositionName=""TagValue"">
            <ObjectList>
              <MultilingualTextItem ID=""{e2:X}"" CompositionName=""Items"">
                <AttributeList>
                  <Culture>{culture}</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </Hmi.Tag.Tag>");
            }

            sb.AppendLine(@"    </ObjectList>
  </Hmi.Tag.TagTable>
</Document>");
            return sb.ToString();
        }

        private class HmiTagDef
        {
            public string? tagName { get; set; }
            public string? connection { get; set; }
            public string? address { get; set; }
            public string? dataType { get; set; }
            public string? plcTag { get; set; }
            // 采集周期（如 "1 s"、"100 ms"），未指定时默认 "1 s"
            public string? acquisitionCycle { get; set; }
        }

        // ────────────────────────────────────────────────────────────
        // 诊断
        // ────────────────────────────────────────────────────────────

        public string DiagnoseClassicHmi()
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var result = new Dictionary<string, object?>();
                    var hmi = GetClassicHmi();
                    result["found"] = hmi != null;
                    if (hmi == null)
                        return JsonConvert.SerializeObject(new { success = true, result });

                    result["name"] = hmi.Name;
                    result["type"] = hmi.GetType().FullName;
                    try { result["screenCount"] = GetAllScreens(hmi.ScreenFolder).Count(); } catch { }
                    try { result["tagCount"] = GetAllTags(hmi.TagFolder).Count(); } catch { }
                    return JsonConvert.SerializeObject(new { success = true, result }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        private static void EnsureDir3(string outputPath)
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static void TryDelete3(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { }
        }

        /// <summary>
        /// 判断 HMI 变量名是否需要用双引号包裹。
        /// 含特殊字符（如 / ( ) 空格 等）的变量名在 ControllerTag 的 Name 中需要双引号包裹，
        /// 普通变量名（字母数字下划线）不需要。
        /// </summary>
        private static bool NeedsQuoting(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
            {
                // 允许字母、数字、下划线
                if (char.IsLetterOrDigit(c) || c == '_') continue;
                // 其余字符（如 / ( ) 空格 等）都需要加引号
                return true;
            }
            return false;
        }

        /// <summary>
        /// ★崩溃修复★ 规范化 ControllerTag 符号为 TIA 可解析的格式。
        /// TIA 对 DB 成员符号要求 "块名"."成员名"（分段引号，如 "DB_Status"."压力kPa"）；
        /// 无引号点分隔格式（DB_Status.压力kPa）或整体单引号格式（"DB_Status.压力kPa"）
        /// 会导致 Import 失败并使博途进程退出。
        /// - 已带引号的完整格式原样保留
        /// - 点分隔符号规范化为 "块名"."成员名"
        /// - 变量表符号沿用 NeedsQuoting 规则
        /// </summary>
        private static string NormalizePlcTagSymbol(string plcTag)
        {
            var name = (plcTag ?? "").Trim();
            if (name.Length == 0) return name;
            if (name.StartsWith("\"") && name.EndsWith("\""))
                return name;
            var dotIndex = name.IndexOf('.');
            if (dotIndex > 0)
            {
                var block = name.Substring(0, dotIndex).Trim().Trim('"').Trim();
                var member = name.Substring(dotIndex + 1).Trim().Trim('"').Trim();
                if (block.Length > 0 && member.Length > 0)
                {
                    return "\"" + block + "\".\"" + member + "\"";
                }
            }
            return NeedsQuoting(name) ? "\"" + name + "\"" : name;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // HMI 完善与优化（5 个新方法）
        // 通用属性读写、强类型画面项创建、运行时设置、日志变量绑定
        // ═════════════════════════════════════════════════════════════════════════════

        // ── 辅助：查找 Unified HmiSoftware（按设备名或取第一个）──

        /// <summary>
        /// 遍历所有设备查找 Unified HMI 的 HmiSoftware。
        /// 按设备名匹配（避免 COM RCW 引用比对不可靠），名称为空时返回第一个找到的 Unified HMI。
        /// </summary>
        private object? FindUnifiedHmiSoftware(string? hmiDeviceName = null)
        {
            RequireProject();
            foreach (var device in GetAllDevices())
            {
                if (!string.IsNullOrEmpty(hmiDeviceName) &&
                    !device.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        if (swContainer?.Software != null && IsUnifiedHmiSoftware(swContainer.Software)) return swContainer.Software;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>判断软件对象是否为 Unified HMI（Siemens.Engineering.HmiUnified 命名空间）。</summary>
        private static bool IsUnifiedHmiSoftware(object? software)
            => software != null &&
               (software.GetType().FullName?.StartsWith("Siemens.Engineering.HmiUnified", StringComparison.Ordinal) == true
                || software.GetType().Name == "HmiSoftware");

        /// <summary>反射读取对象属性（Unified HMI 兼容层）。</summary>
        private static object? GetHmiProperty(object? target, string property)
        {
            try
            {
                return target?.GetType().GetProperty(property)?.GetValue(target, null);
            }
            catch { return null; }
        }

        /// <summary>反射调用对象方法（Unified HMI 兼容层）。</summary>
        private static object? TryInvoke(object? target, string method, params object[] args)
        {
            try
            {
                if (target == null) return null;
                var m = target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
                    ?? target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance, null,
                        args.Select(a => a.GetType()).ToArray(), null);
                return m?.Invoke(target, args);
            }
            catch { return null; }
        }

        // ── 辅助：在 Unified HMI 中递归查找 HmiTag ──

        /// <summary>
        /// 在 Unified HMI 的 TagTables 中查找指定名称的 HmiTag。
        /// tagTableName 为空时遍历所有变量表。
        /// </summary>
        private object? FindUnifiedHmiTag(object? hmi, string tagName, string? tagTableName = null)
        {
            var tables = GetHmiProperty(hmi, "TagTables") as System.Collections.IEnumerable;
            if (tables == null) return null;
            if (!string.IsNullOrEmpty(tagTableName))
            {
                var table = TryInvoke(tables, "Find", new object[] { tagTableName! });
                if (table == null) return null;
                return TryInvoke(GetHmiProperty(table, "Tags"), "Find", new object[] { tagName });
            }
            foreach (var table in tables)
            {
                try
                {
                    var tag = TryInvoke(GetHmiProperty(table, "Tags"), "Find", new object[] { tagName });
                    if (tag != null) return tag;
                }
                catch { }
            }
            return null;
        }

        // ── 辅助：将字符串值转换为 SetAttribute 适用的类型 ──

        /// <summary>
        /// 尝试把字符串转换为 int/bool/double，否则原样返回 string。
        /// 用于 SetAttribute 的值类型推断。
        /// </summary>
        private static object ConvertAttributeValue(string value)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (int.TryParse(value, out var iv)) return iv;
            if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dv)) return dv;
            return value;
        }

        // ── 辅助：枚举 IEngineeringObject 的 GetAttributeInfos ──

        /// <summary>
        /// 反射枚举对象的 GetAttributeInfos，返回 [{ name, accessMode, supportedTypes }] 列表。
        /// </summary>
        private static List<object> GetEngineeringAttributeInfos(object obj)
        {
            var result = new List<object>();
            try
            {
                var infos = obj.GetType().GetMethod("GetAttributeInfos")?.Invoke(obj, null);
                if (infos is not IEnumerable enumInfos) return result;
                foreach (var info in enumInfos)
                {
                    try
                    {
                        var it = info.GetType();
                        var name = it.GetProperty("Name")?.GetValue(info)?.ToString() ?? "";
                        var accessMode = it.GetProperty("AccessMode")?.GetValue(info)?.ToString() ?? "";
                        var supportedTypes = "";
                        try
                        {
                            var stVal = it.GetProperty("SupportedTypes")?.GetValue(info);
                            if (stVal is IEnumerable stEnum)
                            {
                                var names = new List<string>();
                                foreach (var t in stEnum) { try { names.Add(t?.ToString() ?? "?"); } catch { } }
                                supportedTypes = string.Join(", ", names);
                            }
                        }
                        catch { }
                        result.Add(new { name, accessMode, supportedTypes });
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // ────────────────────────────────────────────────────────────
        // 1. SetHmiTagProperty — 通用 HMI 变量属性设置
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 通用 HMI 变量属性设置（支持 Unified 和传统 HMI）。
        /// 通过 SetAttribute 强类型 API 写入，属性不存在时返回可用属性列表。
        /// 常用属性：Address/Comment/DataType/AccessMode/AcquisitionCycle/AcquisitionMode/
        /// Connection/InitialValue/LinearScaling 等。
        /// </summary>
        public string SetHmiTagProperty(string hmiTagName, string propertyName, string value,
            string? tagTableName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(hmiTagName))
                        return Err("参数 hmiTagName 不能为空");
                    if (string.IsNullOrWhiteSpace(propertyName))
                        return Err("参数 propertyName 不能为空");

                    // 先尝试 Unified HMI
                    var unifiedHmi = FindUnifiedHmiSoftware();
                    if (unifiedHmi != null)
                    {
                        var tag = FindUnifiedHmiTag(unifiedHmi, hmiTagName, tagTableName);
                        if (tag != null)
                        {
                            return SetTagAttributeViaApi(tag, hmiTagName, propertyName, value, "Unified");
                        }
                    }

                    // 回退到传统 HMI
                    var classicHmi = GetClassicHmi();
                    if (classicHmi != null)
                    {
                        var tag = FindTagRecursive(classicHmi.TagFolder, hmiTagName);
                        if (tag != null)
                        {
                            return SetTagAttributeViaApi(tag, hmiTagName, propertyName, value, "Classic");
                        }
                    }

                    return Err($"未找到 HMI 变量: {hmiTagName}" +
                        (string.IsNullOrEmpty(tagTableName) ? "" : $"（变量表: {tagTableName}）"));
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 通过 SetAttribute 设置 Tag 属性的内部实现，返回标准 JSON。
        /// 失败时附带 GetAttributeInfos 列出的可用属性名。
        /// </summary>
        private string SetTagAttributeViaApi(object tag, string tagName, string propertyName, string value, string hmiKind)
        {
            // 读取旧值（忽略异常）
            string? oldValue = null;
            try
            {
                var getM = tag.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                if (getM != null)
                {
                    var oldVal = getM.Invoke(tag, new object[] { propertyName });
                    oldValue = oldVal?.ToString();
                }
            }
            catch { }

            try
            {
                var converted = ConvertAttributeValue(value);
                var setM = tag.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (setM == null) return Err($"Tag 对象无 SetAttribute 方法（{hmiKind} HMI）");
                setM.Invoke(tag, new object[] { propertyName, converted });

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    tagName,
                    propertyName,
                    oldValue = oldValue ?? "(null)",
                    newValue = value,
                    hmiKind,
                    message = $"{tagName}.{propertyName} = {value}"
                }, Formatting.Indented);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException?.Message ?? tie.Message;
                var avail = GetEngineeringAttributeInfos(tag);
                return JsonConvert.SerializeObject(new
                {
                    success = false,
                    error = $"设置属性 '{propertyName}' 失败: {inner}",
                    tagName,
                    propertyName,
                    attemptedValue = value,
                    hmiKind,
                    availableAttributes = avail
                }, Formatting.Indented);
            }
        }

        // ────────────────────────────────────────────────────────────
        // 2. GetHmiTagProperties — 读取 HMI 变量所有属性
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 读取 HMI 变量的所有属性（通过 GetAttributeInfos + GetAttribute）。
        /// 返回 [{ name, value, type, canRead, canWrite }] 数组。
        /// </summary>
        public string GetHmiTagProperties(string hmiTagName, string? tagTableName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(hmiTagName))
                        return Err("参数 hmiTagName 不能为空");

                    object? tag = null;
                    string hmiKind = "";

                    // 先尝试 Unified HMI
                    var unifiedHmi = FindUnifiedHmiSoftware();
                    if (unifiedHmi != null)
                    {
                        var uTag = FindUnifiedHmiTag(unifiedHmi, hmiTagName, tagTableName);
                        if (uTag != null) { tag = uTag; hmiKind = "Unified"; }
                    }

                    // 回退到传统 HMI
                    if (tag == null)
                    {
                        var classicHmi = GetClassicHmi();
                        if (classicHmi != null)
                        {
                            var cTag = FindTagRecursive(classicHmi.TagFolder, hmiTagName);
                            if (cTag != null) { tag = cTag; hmiKind = "Classic"; }
                        }
                    }

                    if (tag == null)
                        return Err($"未找到 HMI 变量: {hmiTagName}" +
                            (string.IsNullOrEmpty(tagTableName) ? "" : $"（变量表: {tagTableName}）"));

                    // 枚举 GetAttributeInfos
                    var attrInfos = GetEngineeringAttributeInfos(tag);

                    // 对每个属性用 GetAttribute 读取值
                    var props = new List<object>();
                    var getM = tag.GetType().GetMethod("GetAttribute", new[] { typeof(string) });

                    foreach (var info in attrInfos)
                    {
                        try
                        {
                            var infoType = info.GetType();
                            // 匿名类型属性为小写 name/accessMode/supportedTypes
                            var aName = infoType.GetProperty("name")?.GetValue(info)?.ToString() ?? "";
                            if (string.IsNullOrEmpty(aName)) continue;

                            string? val = null;
                            if (getM != null)
                            {
                                try { val = getM.Invoke(tag, new object[] { aName })?.ToString(); }
                                catch { val = "(读取失败)"; }
                            }

                            var accessMode = infoType.GetProperty("accessMode")?.GetValue(info)?.ToString() ?? "";
                            var supportedTypes = infoType.GetProperty("supportedTypes")?.GetValue(info)?.ToString() ?? "";

                            props.Add(new
                            {
                                name = aName,
                                value = val ?? "(null)",
                                accessMode,
                                supportedTypes,
                                canRead = accessMode.IndexOf("Read", StringComparison.OrdinalIgnoreCase) >= 0,
                                canWrite = accessMode.IndexOf("Write", StringComparison.OrdinalIgnoreCase) >= 0
                            });
                        }
                        catch { }
                    }

                    // 传统 HMI Tag 的 GetAttributeInfos 通常只返回 Name，
                    // 地址/数据类型/采集周期等关键属性需通过 GetAttribute(属性名) 显式读取。
                    // 这里补充显式读取已知 HMI Tag 属性，确保返回完整信息。
                    var knownTagAttrs = new[]
                    {
                        "Name", "Address", "DataType", "DataTypeName",
                        "AcquisitionCycle", "AcquisitionMode", "Cycle", "Mode",
                        "Comment", "Connection", "ConnectionName", "PlcTag", "PlcTagName",
                        "Length", "Unit", "LimitValue", "SubstituteValue", "ControlValue"
                    };
                    var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in props)
                    {
                        try
                        {
                            var n = p.GetType().GetProperty("name")?.GetValue(p)?.ToString();
                            if (n != null) existingNames.Add(n);
                        }
                        catch { }
                    }
                    foreach (var attrName in knownTagAttrs)
                    {
                        if (existingNames.Contains(attrName)) continue;
                        if (getM == null) break;
                        try
                        {
                            object? attrVal = null;
                            try { attrVal = getM.Invoke(tag, new object[] { attrName }); }
                            catch { continue; }  // 该属性不存在或不可读，跳过
                            if (attrVal == null) continue;

                            // Comment 等属性返回 MultilingualText 对象，需特殊处理取文本
                            string valStr;
                            if (attrVal is IEnumerable mlEnum && !(attrVal is string))
                            {
                                // 尝试取 MultilingualText 的文本（取第一个非空 item 的 Text）
                                valStr = "";
                                foreach (var mlItem in mlEnum)
                                {
                                    try
                                    {
                                        var t = mlItem.GetType().GetProperty("Text")?.GetValue(mlItem)?.ToString();
                                        if (!string.IsNullOrEmpty(t)) { valStr = t; break; }
                                    }
                                    catch { }
                                }
                                if (string.IsNullOrEmpty(valStr)) continue;  // 空集合不算属性
                            }
                            else
                            {
                                valStr = attrVal.ToString() ?? "";
                            }

                            props.Add(new
                            {
                                name = attrName,
                                value = valStr,
                                accessMode = "Read",
                                supportedTypes = attrVal.GetType().Name,
                                canRead = true,
                                canWrite = false
                            });
                        }
                        catch { }
                    }

                    // 如果仍然返回不足（传统 HMI Tag 只有 Name），
                    // 补充直接反射枚举 Tag 对象的所有公共属性
                    if (props.Count <= 1 && hmiKind == "Classic")
                    {
                        var tagType = tag.GetType();
                        foreach (var prop in tagType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            // 跳过已存在的属性
                            bool alreadyExists = false;
                            foreach (var p in props)
                            {
                                try
                                {
                                    var n = p.GetType().GetProperty("name")?.GetValue(p)?.ToString();
                                    if (n == prop.Name) { alreadyExists = true; break; }
                                }
                                catch { }
                            }
                            if (alreadyExists) continue;

                            try
                            {
                                var val = prop.GetValue(tag);
                                props.Add(new
                                {
                                    name = prop.Name,
                                    value = val?.ToString() ?? "(null)",
                                    accessMode = prop.CanWrite ? "ReadWrite" : "Read",
                                    supportedTypes = prop.PropertyType.Name,
                                    canRead = prop.CanRead,
                                    canWrite = prop.CanWrite
                                });
                            }
                            catch { }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        tagName = hmiTagName,
                        hmiKind,
                        tagType = tag.GetType().FullName,
                        count = props.Count,
                        properties = props
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 3. CreateUnifiedHmiScreenItemTyped — 强类型画面项创建
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Unified HMI 画面项类型名到类型名的映射（覆盖 Controls/Widgets/Shapes/Screens 命名空间）。
        /// 实际 Type 在运行时从已加载程序集解析（V17 无 Unified 程序集时返回空）。
        /// </summary>
        private static readonly Dictionary<string, string> UnifiedScreenItemTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            // Widgets
            ["Button"]            = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiButton",
            ["IOField"]           = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiIOField",
            ["Text"]              = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiText",
            ["Bar"]               = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiBar",
            ["Slider"]            = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiSlider",
            ["TextBox"]           = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiTextBox",
            ["ToggleSwitch"]      = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiToggleSwitch",
            ["Clock"]             = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiClock",
            ["Gauge"]             = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiGauge",
            ["ListBox"]           = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiListBox",
            ["CheckBoxGroup"]     = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiCheckBoxGroup",
            ["RadioButtonGroup"]  = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiRadioButtonGroup",
            ["SymbolicIOField"]   = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiSymbolicIOField",
            ["TouchArea"]         = "Siemens.Engineering.HmiUnified.UI.Widgets.HmiTouchArea",
            // Shapes
            ["Rectangle"]         = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiRectangle",
            ["Circle"]            = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiCircle",
            ["Line"]              = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiLine",
            ["Point"]             = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiPoint",
            ["Polygon"]           = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiPolygon",
            ["Polyline"]          = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiPolyline",
            ["Ellipse"]           = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiEllipse",
            ["GraphicView"]       = "Siemens.Engineering.HmiUnified.UI.Shapes.HmiGraphicView",
            // Controls
            ["AlarmControl"]             = "Siemens.Engineering.HmiUnified.UI.Controls.HmiAlarmControl",
            ["TrendControl"]             = "Siemens.Engineering.HmiUnified.UI.Controls.HmiTrendControl",
            ["MediaControl"]             = "Siemens.Engineering.HmiUnified.UI.Controls.HmiMediaControl",
            ["ProcessControl"]           = "Siemens.Engineering.HmiUnified.UI.Controls.HmiProcessControl",
            ["SystemDiagnosisControl"]   = "Siemens.Engineering.HmiUnified.UI.Controls.HmiSystemDiagnosisControl",
            ["WebControl"]               = "Siemens.Engineering.HmiUnified.UI.Controls.HmiWebControl",
            // Screens
            ["ScreenWindow"]             = "Siemens.Engineering.HmiUnified.UI.Screens.HmiScreenWindow",
            // Faceplate
            ["FaceplateContainer"]       = "Siemens.Engineering.HmiUnified.UI.Controls.HmiFaceplateContainer",
        };

        /// <summary>从已加载程序集按完整类型名解析 Type（Unified 类型，V17 无则返回 null）。</summary>
        private static Type? ResolveUnifiedType(string typeFullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(typeFullName, false);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// 用 Unified HMI 的强类型 Create&lt;T&gt; 方法创建画面项（替代字符串名反射）。
        /// 仅支持 Unified HMI；非 Unified HMI 返回错误。
        /// 创建后设置 Left/Top/Width/Height（可选）。
        /// </summary>
        public string CreateUnifiedHmiScreenItemTyped(string screenName, string itemTypeName,
            string itemName, int? left = null, int? top = null, int? width = null, int? height = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(screenName))
                        return Err("参数 screenName 不能为空");
                    if (string.IsNullOrWhiteSpace(itemTypeName))
                        return Err("参数 itemTypeName 不能为空");
                    if (string.IsNullOrWhiteSpace(itemName))
                        return Err("参数 itemName 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware();
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI");

                    // 查找画面
                    object? screen = null;
                    try
                    {
                        var screens = GetHmiProperty(unifiedHmi, "Screens");
                        screen = TryInvoke(screens, "Find", new object[] { screenName });
                    }
                    catch { }
                    if (screen == null)
                        return Err($"未找到 Unified HMI 画面: {screenName}");

                    // 类型映射
                    if (!UnifiedScreenItemTypes.TryGetValue(itemTypeName, out var itemTypeNameFull))
                    {
                        var supported = string.Join(", ", UnifiedScreenItemTypes.Keys.OrderBy(k => k));
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"不支持的画面项类型: {itemTypeName}",
                            supportedTypes = supported
                        }, Formatting.Indented);
                    }
                    var itemType = ResolveUnifiedType(itemTypeNameFull);
                    if (itemType == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"当前 TIA 版本未安装 WinCC Unified（类型 {itemTypeNameFull} 不存在）",
                            supportedTypes = UnifiedScreenItemTypes.Keys.OrderBy(k => k).ToList()
                        }, Formatting.Indented);
                    }

                    // 获取 ScreenItems composition（HmiScreenItemBaseComposition）
                    var screenItems = GetHmiProperty(screen, "ScreenItems");
                    if (screenItems == null)
                        return Err("画面 ScreenItems 集合为空");

                    // 反射调用泛型 Create<T>(string name)
                    object? createdItem = null;
                    Type? actualType = null;
                    try
                    {
                        var createMethod = screenItems.GetType().GetMethods()
                            .Where(m => m.Name == "Create" && m.IsGenericMethod
                                && m.GetParameters().Length == 1
                                && m.GetParameters()[0].ParameterType == typeof(string))
                            .FirstOrDefault();
                        if (createMethod == null)
                            throw new Exception("未找到 Create<T>(string) 泛型方法");

                        var generic = createMethod.MakeGenericMethod(itemType);
                        createdItem = generic.Invoke(screenItems, new object[] { itemName });
                        actualType = createdItem?.GetType();
                    }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        return Err($"泛型 Create<{itemType.Name}>(\"{itemName}\") 调用失败: {inner}");
                    }
                    if (createdItem == null)
                        return Err($"创建画面项失败: Create<{itemTypeName}> 返回 null");

                    // 设置坐标和尺寸（通过 SetAttribute）
                    var propResults = new List<object>();
                    if (left.HasValue)   TrySetScreenItemAttr(createdItem, "Left",   left.Value,   propResults);
                    if (top.HasValue)    TrySetScreenItemAttr(createdItem, "Top",    top.Value,    propResults);
                    if (width.HasValue)  TrySetScreenItemAttr(createdItem, "Width",  width.Value,  propResults);
                    if (height.HasValue) TrySetScreenItemAttr(createdItem, "Height", height.Value, propResults);

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建画面项 {itemName}（类型: {itemTypeName}）",
                        screenName,
                        itemName,
                        itemTypeName,
                        actualType = actualType?.FullName ?? "",
                        position = new
                        {
                            left = left?.ToString() ?? "(默认)",
                            top = top?.ToString() ?? "(默认)",
                            width = width?.ToString() ?? "(默认)",
                            height = height?.ToString() ?? "(默认)"
                        },
                        properties = propResults
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 用 SetAttribute 设置画面项属性，捕获异常避免单属性失败中断。
        /// </summary>
        private void TrySetScreenItemAttr(object item, string attrName, object value, List<object> results)
        {
            try
            {
                var setM = item.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                if (setM != null)
                {
                    setM.Invoke(item, new object[] { attrName, value });
                    results.Add(new { attribute = attrName, value = value.ToString(), success = true });
                }
                else
                {
                    // 回退：反射属性
                    var prop = item.GetType().GetProperty(attrName);
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(item, Convert.ChangeType(value, prop.PropertyType));
                        results.Add(new { attribute = attrName, value = value.ToString(), success = true });
                    }
                    else
                    {
                        results.Add(new { attribute = attrName, value = value.ToString(), success = false, error = "属性不存在或不可写" });
                    }
                }
            }
            catch (Exception ex)
            {
                var err = ex is TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? tie.Message)
                    : ex.Message;
                results.Add(new { attribute = attrName, value = value.ToString(), success = false, error = err });
            }
        }

        // ────────────────────────────────────────────────────────────
        // 4. SetUnifiedHmiRuntimeSetting — 通用 Unified 运行时设置
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 通用 Unified HMI 运行时设置（通过 RuntimeSettings.SetAttribute）。
        /// 支持的属性：StartScreen/ScreenResolution/LanguageAndFonts/MaxLoginRuntimeSettings/
        /// OpcUaServerRuntimeSettings/ProcessDiagnosticsRuntimeSettings/RuntimeResourceSettings/
        /// HmiReportingSettings/GMPEnabled/AutoLogOffURL/BitSelection 等。
        /// </summary>
        public string SetUnifiedHmiRuntimeSetting(string propertyName, string value, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(propertyName))
                        return Err("参数 propertyName 不能为空");
                    if (value == null)
                        return Err("参数 value 不能为 null");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"：{hmiDeviceName}") +
                            "，此工具仅支持 WinCC Unified HMI");

                    // 获取 RuntimeSettings
                    var runtimeSettings = GetHmiProperty(unifiedHmi, "RuntimeSettings");
                    if (runtimeSettings == null)
                        return Err("Unified HMI RuntimeSettings 为空");

                    // 读取旧值
                    string? oldValue = null;
                    try
                    {
                        var getM = runtimeSettings.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                        if (getM != null)
                        {
                            var oldVal = getM.Invoke(runtimeSettings, new object[] { propertyName });
                            oldValue = oldVal?.ToString();
                        }
                    }
                    catch { }

                    // 设置新值
                    try
                    {
                        var converted = ConvertAttributeValue(value);
                        var setM = runtimeSettings.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                        if (setM == null) return Err("RuntimeSettings 对象无 SetAttribute 方法");
                        setM.Invoke(runtimeSettings, new object[] { propertyName, converted });

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            propertyName,
                            oldValue = oldValue ?? "(null)",
                            newValue = value,
                            hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                            message = $"RuntimeSettings.{propertyName} = {value}"
                        }, Formatting.Indented);
                    }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        var avail = GetEngineeringAttributeInfos(runtimeSettings);
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"设置运行时属性 '{propertyName}' 失败: {inner}",
                            propertyName,
                            attemptedValue = value,
                            availableAttributes = avail
                        }, Formatting.Indented);
                    }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // 5. CreateHmiLoggingTag — 为 Unified HMI 变量创建日志记录绑定
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 为 Unified HMI 变量创建日志记录绑定（LoggingTag）。
        /// 通过 HmiTag.LoggingTags.Create(loggingTagName) 创建。
        /// 仅支持 Unified HMI。
        /// </summary>
        public string CreateHmiLoggingTag(string hmiTagName, string loggingTagName, string? tagTableName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(hmiTagName))
                        return Err("参数 hmiTagName 不能为空");
                    if (string.IsNullOrWhiteSpace(loggingTagName))
                        return Err("参数 loggingTagName 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware();
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI");

                    var tag = FindUnifiedHmiTag(unifiedHmi, hmiTagName, tagTableName);
                    if (tag == null)
                        return Err($"未找到 Unified HMI 变量: {hmiTagName}" +
                            (string.IsNullOrEmpty(tagTableName) ? "" : $"（变量表: {tagTableName}）"));

                    // 获取 LoggingTags 集合（HmiLoggingTagComposition）
                    var loggingTags = GetHmiProperty(tag, "LoggingTags");
                    if (loggingTags == null)
                        return Err($"HMI 变量 {hmiTagName} 的 LoggingTags 集合为空");

                    // 同名已存在则提示
                    try
                    {
                        var existing = TryInvoke(loggingTags, "Find", new object[] { loggingTagName });
                        if (existing != null)
                            return Ok($"日志记录绑定已存在: {hmiTagName} → {loggingTagName}");
                    }
                    catch { }

                    // 创建日志绑定
                    object? createdLogTag;
                    try
                    {
                        createdLogTag = TryInvoke(loggingTags, "Create", new object[] { loggingTagName });
                    }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        return Err($"LoggingTags.Create(\"{loggingTagName}\") 失败: {inner}");
                    }
                    if (createdLogTag == null)
                        return Err($"LoggingTags.Create(\"{loggingTagName}\") 返回 null");

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已为变量 {hmiTagName} 创建日志记录绑定: {loggingTagName}",
                        hmiTagName,
                        loggingTagName = GetHmiProperty(createdLogTag, "Name"),
                        tagTableName = tagTableName ?? "(自动查找)"
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ────────────────────────────────────────────────────────────
        // HMI 全部 8 项功能（任务 5.5）
        // ────────────────────────────────────────────────────────────

        // ── 1. ExportUnifiedHmiTags — Unified HMI 变量批量导出 ──

        /// <summary>
        /// Unified HMI 变量批量导出。
        /// tagTableName 非空时只导出指定变量表；为空时遍历所有 HmiTagTable 逐个导出。
        /// API：HmiTagComposition.Export(DirectoryInfo)。
        /// </summary>
        public string ExportUnifiedHmiTags(string? tagTableName, string directoryPath,
            string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(directoryPath))
                        return Err("参数 directoryPath 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    DirectoryInfo dir;
                    try { dir = Directory.CreateDirectory(directoryPath); }
                    catch (Exception ex) { return Err($"创建目录失败: {directoryPath} — {ex.Message}"); }

                    var exported = new List<object>();
                    var tagTables = GetHmiProperty(unifiedHmi, "TagTables") as System.Collections.IEnumerable;
                    if (tagTables == null) return Err("unifiedHmi.TagTables 返回 null（集合不可用）");

                    if (!string.IsNullOrWhiteSpace(tagTableName))
                    {
                        var table = TryInvoke(tagTables, "Find", new object[] { tagTableName! });
                        if (table == null)
                            return Err($"未找到变量表: {tagTableName}");
                        try
                        {
                            var tags = GetHmiProperty(table, "Tags");
                            TryInvoke(tags, "Export", dir);
                            exported.Add(new { name = GetHmiProperty(table, "Name"), count = CountCompositionSafe(tags) });
                        }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException?.Message ?? tie.Message;
                            return Err($"导出变量表 {tagTableName} 失败: {inner}");
                        }
                    }
                    else
                    {
                        foreach (var table in tagTables)
                        {
                            try
                            {
                                var tags = GetHmiProperty(table, "Tags");
                                TryInvoke(tags, "Export", dir);
                                exported.Add(new { name = GetHmiProperty(table, "Name"), count = CountCompositionSafe(tags) });
                            }
                            catch (Exception ex)
                            {
                                exported.Add(new { name = GetHmiProperty(table, "Name") ?? "(未知)", error = ex.Message });
                            }
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出 {exported.Count} 个变量表到 {dir.FullName}",
                        exportedTables = exported,
                        directoryPath = dir.FullName
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 2. ImportUnifiedHmiTags — Unified HMI 变量批量导入 ──

        /// <summary>
        /// Unified HMI 变量批量导入。
        /// 遍历 TagTables，对每个变量表的 Tags 集合调用 Import(DirectoryInfo)。
        /// API：HmiTagComposition.Import(DirectoryInfo) / Import(DirectoryInfo, string)。
        /// 注意：若存在多个变量表，会将目录内变量导入到每个变量表（按任务定义行为）。
        /// </summary>
        public string ImportUnifiedHmiTags(string directoryPath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(directoryPath))
                        return Err("参数 directoryPath 不能为空");

                    var dir = new DirectoryInfo(directoryPath);
                    if (!dir.Exists)
                        return Err($"目录不存在: {directoryPath}");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    int totalImported = 0;
                    var perTable = new List<object>();
                    var tagTables = GetHmiProperty(unifiedHmi, "TagTables") as System.Collections.IEnumerable;
                    if (tagTables == null) return Err("unifiedHmi.TagTables 返回 null（集合不可用）");
                    foreach (var table in tagTables)
                    {
                        try
                        {
                            var tags = GetHmiProperty(table, "Tags");
                            var imported = TryInvoke(tags, "Import", dir);
                            int n = 0;
                            if (imported is object obj && obj is IEnumerable e)
                                foreach (var _ in e) n++;
                            totalImported += n;
                            perTable.Add(new { name = GetHmiProperty(table, "Name"), imported = n });
                        }
                        catch (Exception ex)
                        {
                            perTable.Add(new { name = GetHmiProperty(table, "Name") ?? "(未知)", error = ex.Message });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已从 {dir.FullName} 导入变量，共 {totalImported} 个",
                        importedCount = totalImported,
                        perTable,
                        directoryPath = dir.FullName
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 3. ExportUnifiedHmiScripts — Unified HMI 脚本模块导出 ──

        /// <summary>
        /// Unified HMI 脚本模块导出。
        /// 获取 hmiSoftware.Scripts（HmiScriptModuleComposition），调用 Export(DirectoryInfo)。
        /// API：HmiScriptModuleComposition.Export(DirectoryInfo)。
        /// </summary>
        public string ExportUnifiedHmiScripts(string directoryPath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(directoryPath))
                        return Err("参数 directoryPath 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    DirectoryInfo dir;
                    try { dir = Directory.CreateDirectory(directoryPath); }
                    catch (Exception ex) { return Err($"创建目录失败: {directoryPath} — {ex.Message}"); }

                    var scripts = GetHmiProperty(unifiedHmi, "Scripts");
                    if (scripts == null) return Err("unifiedHmi.Scripts 返回 null（集合不可用）");
                    int count = CountCompositionSafe(scripts);
                    try { TryInvoke(scripts, "Export", dir); }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        return Err($"Scripts.Export 失败: {inner}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已导出 {count} 个脚本模块到 {dir.FullName}",
                        scriptCount = count,
                        directoryPath = dir.FullName
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 4. ImportUnifiedHmiScripts — Unified HMI 脚本模块导入 ──

        /// <summary>
        /// Unified HMI 脚本模块导入。
        /// 获取 hmiSoftware.Scripts，调用 Import(DirectoryInfo)。
        /// API：HmiScriptModuleComposition.Import(DirectoryInfo) / Import(DirectoryInfo, string)。
        /// </summary>
        public string ImportUnifiedHmiScripts(string directoryPath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(directoryPath))
                        return Err("参数 directoryPath 不能为空");

                    var dir = new DirectoryInfo(directoryPath);
                    if (!dir.Exists)
                        return Err($"目录不存在: {directoryPath}");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    var scripts = GetHmiProperty(unifiedHmi, "Scripts");
                    if (scripts == null) return Err("unifiedHmi.Scripts 返回 null（集合不可用）");
                    int n = 0;
                    try
                    {
                        var imported = TryInvoke(scripts, "Import", dir);
                        if (imported is object obj && obj is IEnumerable e)
                            foreach (var _ in e) n++;
                    }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        return Err($"Scripts.Import 失败: {inner}");
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已从 {dir.FullName} 导入 {n} 个脚本模块",
                        importedCount = n,
                        directoryPath = dir.FullName
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 5. CreateUnifiedHmiAlarmLog — Unified HMI 创建报警日志 ──

        /// <summary>
        /// Unified HMI 创建报警日志。
        /// 获取 hmiSoftware.AlarmLogs（HmiAlarmLogComposition），调用 Create(name)。
        /// API：HmiAlarmLogComposition.Create(string) 返回 HmiAlarmLog。
        /// 改进点（任务 2）：
        ///   - 增加对 AlarmLogs 集合为 null 的诊断
        ///   - 同名检查时记录异常详情而非静默吞掉
        ///   - Create 失败时输出可用 Create 方法签名列表
        ///   - 成功时返回更多诊断信息（设备名、集合类型、已有日志数等）
        /// </summary>
        public string CreateUnifiedHmiAlarmLog(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return Err("参数 name 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    // 获取 AlarmLogs 集合（HmiAlarmLogComposition）
                    object? alarmLogs;
                    try { alarmLogs = GetHmiProperty(unifiedHmi, "AlarmLogs"); }
                    catch (Exception ex)
                    {
                        return Err($"访问 HmiSoftware.AlarmLogs 失败: {ex.Message}" +
                            $"（HmiSoftware 类型: {unifiedHmi.GetType().FullName}）");
                    }
                    if (alarmLogs == null)
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "unifiedHmi.AlarmLogs 返回 null（集合不可用）",
                            hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                            hmiSoftwareType = unifiedHmi.GetType().FullName,
                            note = "可能该项目未启用 WinCC Unified 报警功能，请在博途 GUI 检查 HMI 设置"
                        }, Formatting.Indented);
                    }

                    // 同名检查 — 改进：记录异常详情而非静默吞掉
                    string? findError = null;
                    try
                    {
                        var existing = TryInvoke(alarmLogs, "Find", new object[] { name });
                        if (existing != null)
                        {
                            return JsonConvert.SerializeObject(new
                            {
                                success = true,
                                message = $"报警日志已存在: {name}",
                                alarmLogName = GetHmiProperty(existing, "Name"),
                                alreadyExisted = true,
                                hmiDeviceName = GetHmiProperty(unifiedHmi, "Name")
                            }, Formatting.Indented);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Find 异常不中断创建流程，仅记录
                        findError = ex.Message;
                    }

                    // 统计已有日志数（用于诊断）
                    int existingCount = CountCompositionSafe(alarmLogs);

                    // 调用 Create(name)
                    object? created;
                    try { created = TryInvoke(alarmLogs, "Create", new object[] { name }); }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        // 列出可用 Create 方法签名，便于调试
                        var createMethods = alarmLogs.GetType().GetMethods()
                            .Where(m => m.Name == "Create")
                            .Select(m => $"Create({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
                            .ToList();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"AlarmLogs.Create(\"{name}\") 失败: {inner}",
                            innerExceptionType = tie.InnerException?.GetType().Name ?? "(unknown)",
                            hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                            collectionType = alarmLogs.GetType().FullName,
                            existingCount,
                            findError = findError ?? "(no error)",
                            availableCreateMethods = createMethods,
                            stackTrace = tie.InnerException?.StackTrace?.Split('\n').Take(5).ToArray()
                        }, Formatting.Indented);
                    }
                    catch (Exception ex)
                    {
                        // 非 TargetInvocationException 的其他异常
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"AlarmLogs.Create(\"{name}\") 抛出异常: {ex.Message}",
                            exceptionType = ex.GetType().Name,
                            hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                            existingCount,
                            findError = findError ?? "(no error)"
                        }, Formatting.Indented);
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建报警日志: {GetHmiProperty(created, "Name")}",
                        alarmLogName = GetHmiProperty(created, "Name"),
                        alreadyExisted = false,
                        hmiDeviceName = GetHmiProperty(unifiedHmi, "Name"),
                        collectionType = alarmLogs.GetType().Name,
                        existingCountBeforeCreate = existingCount,
                        findError = findError ?? "(no error)"
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 6. CreateUnifiedHmiAuditTrail — Unified HMI 创建审计跟踪 ──

        /// <summary>
        /// Unified HMI 创建审计跟踪。
        /// 获取 hmiSoftware.AuditTrails（HmiAuditTrailComposition），尝试反射调用 Create(name)。
        /// 注意：V19 API 文档中 HmiAuditTrailComposition 仅含 Contains/IndexOf/GetEnumerator，
        /// 未公开 Create 方法，故用反射兜底；失败时列出可用 Create/Add 方法。
        /// </summary>
        public string CreateUnifiedHmiAuditTrail(string name, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return Err("参数 name 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    var auditTrails = GetHmiProperty(unifiedHmi, "AuditTrails");
                    if (auditTrails == null)
                        return Err("unifiedHmi.AuditTrails 返回 null（集合不可用）");

                    // 反射尝试 Create(string) —— V19 文档未列出此方法，反射兜底
                    object? created = null;
                    var createM = auditTrails.GetType().GetMethod("Create", new[] { typeof(string) });
                    if (createM != null)
                    {
                        try { created = createM.Invoke(auditTrails, new object[] { name }); }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException?.Message ?? tie.Message;
                            return Err($"AuditTrails.Create(\"{name}\") 失败: {inner}");
                        }
                    }

                    if (created == null)
                    {
                        var methods = auditTrails.GetType().GetMethods()
                            .Where(m => (m.Name == "Create" || m.Name == "Add")
                                && m.GetParameters().Length <= 2)
                            .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                            .ToList();
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = "AuditTrails 集合无 Create(string) 方法，Openness API 可能不支持程序化创建审计跟踪",
                            availableCreateMethods = methods,
                            note = "V19 API 文档中 HmiAuditTrailComposition 仅含 Contains/IndexOf/GetEnumerator，疑似只读集合，请在博途 GUI 手动创建"
                        }, Formatting.Indented);
                    }

                    string createdName = name;
                    try { createdName = created.GetType().GetProperty("Name")?.GetValue(created)?.ToString() ?? name; }
                    catch { }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已创建审计跟踪: {createdName}",
                        auditTrailName = createdName
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 7. CreateUnifiedHmiEventHandler — Unified HMI 画面项事件处理器 ──

        /// <summary>
        /// Unified HMI 为画面项创建事件处理器。
        /// 事件类型是控件专属枚举（如 HmiButtonEventType.Click），通过反射解析并调用
        /// EventHandlers.Create(枚举值)。事件类型不识别时返回该控件支持的事件类型列表。
        /// 可选 scriptName：按名称查找脚本模块并设置事件处理器的 Script 属性（反射）。
        /// </summary>
        public string CreateUnifiedHmiEventHandler(string screenName, string itemName, string eventType,
            string? scriptName = null, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(screenName)) return Err("参数 screenName 不能为空");
                    if (string.IsNullOrWhiteSpace(itemName)) return Err("参数 itemName 不能为空");
                    if (string.IsNullOrWhiteSpace(eventType)) return Err("参数 eventType 不能为空");

                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi == null)
                        return Err("未找到 Unified HMI 设备，此工具仅支持 WinCC Unified HMI" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    // 找画面
                    object? screen = null;
                    try
                    {
                        var screens = GetHmiProperty(unifiedHmi, "Screens");
                        screen = TryInvoke(screens, "Find", new object[] { screenName });
                    }
                    catch { }
                    if (screen == null) return Err($"未找到 Unified HMI 画面: {screenName}");

                    // 找画面项（按 itemName）
                    object? screenItem = null;
                    try
                    {
                        var screenItems = GetHmiProperty(screen, "ScreenItems") as System.Collections.IEnumerable;
                        if (screenItems != null)
                        {
                            foreach (var item in screenItems)
                            {
                                try
                                {
                                    var n = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                                    if (n != null && n.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                                    { screenItem = item; break; }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    if (screenItem == null) return Err($"未找到画面项: {itemName}（画面: {screenName}）");

                    // 反射获取画面项的 EventHandlers 属性
                    var itemRuntimeType = screenItem.GetType();
                    var eventHandlersProp = itemRuntimeType.GetProperty("EventHandlers");
                    if (eventHandlersProp == null)
                        return Err($"画面项类型 {itemRuntimeType.Name} 无 EventHandlers 属性");

                    var eventHandlers = eventHandlersProp.GetValue(screenItem);
                    if (eventHandlers == null)
                        return Err($"画面项 {itemName} 的 EventHandlers 集合为空");

                    // 找 Create(单参) 方法，参数类型即事件枚举
                    var createMethods = eventHandlers.GetType().GetMethods()
                        .Where(m => m.Name == "Create" && m.GetParameters().Length == 1)
                        .ToList();
                    if (createMethods.Count == 0)
                        return Err($"EventHandlers 集合无 Create(单参) 方法（控件类型: {itemRuntimeType.Name}）");

                    var eventTypeEnumType = createMethods[0].GetParameters()[0].ParameterType;

                    // 解析事件类型字符串（支持 "Click" 或 "HmiButtonEventType.Click"）
                    string enumShortName = eventType.Contains('.')
                        ? eventType.Substring(eventType.LastIndexOf('.') + 1)
                        : eventType;

                    Array? enumValues = null;
                    try { enumValues = Enum.GetValues(eventTypeEnumType); }
                    catch { }

                    object? enumValue = null;
                    if (enumValues != null)
                    {
                        foreach (var v in enumValues)
                        {
                            if (v.ToString().Equals(enumShortName, StringComparison.OrdinalIgnoreCase)
                                || v.ToString().Equals(eventType, StringComparison.OrdinalIgnoreCase))
                            { enumValue = v; break; }
                        }
                    }

                    if (enumValue == null)
                    {
                        var supported = new List<string>();
                        if (enumValues != null)
                            foreach (var v in enumValues) supported.Add(v.ToString());
                        return JsonConvert.SerializeObject(new
                        {
                            success = false,
                            error = $"事件类型 '{eventType}' 不被控件 {itemRuntimeType.Name} 支持",
                            screenName, itemName,
                            itemRuntimeType = itemRuntimeType.Name,
                            eventTypeEnumType = eventTypeEnumType.FullName,
                            supportedEventTypes = supported,
                            apiExplored = new { itemRuntimeType = itemRuntimeType.Name, eventTypeEnumType = eventTypeEnumType.FullName }
                        }, Formatting.Indented);
                    }

                    // 反射调用 Create(枚举值)
                    object? createdHandler = null;
                    try
                    {
                        var createM = eventHandlers.GetType().GetMethod("Create", new[] { eventTypeEnumType });
                        if (createM == null)
                            return Err($"EventHandlers.Create({eventTypeEnumType.Name}) 方法未找到");
                        createdHandler = createM.Invoke(eventHandlers, new object[] { enumValue });
                    }
                    catch (TargetInvocationException tie)
                    {
                        var inner = tie.InnerException?.Message ?? tie.Message;
                        return Err($"EventHandlers.Create({enumValue}) 调用失败: {inner}");
                    }

                    if (createdHandler == null)
                        return Err($"EventHandlers.Create({enumValue}) 返回 null");

                    // 设置 Script 属性（若提供 scriptName）
                    string? scriptSetResult = null;
                    if (!string.IsNullOrWhiteSpace(scriptName))
                    {
                        try
                        {
                            var scriptProp = createdHandler.GetType().GetProperty("Script");
                            if (scriptProp != null && scriptProp.CanWrite)
                            {
                                // 按名称查找脚本模块对象
                                object? scriptObj = null;
                                try
                                {
                                    var scripts = GetHmiProperty(unifiedHmi, "Scripts") as System.Collections.IEnumerable;
                                    if (scripts != null)
                                    {
                                        foreach (var s in scripts)
                                        {
                                            try
                                            {
                                                var sn = s.GetType().GetProperty("Name")?.GetValue(s)?.ToString();
                                                if (sn != null && sn.Equals(scriptName, StringComparison.OrdinalIgnoreCase))
                                                { scriptObj = s; break; }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }

                                if (scriptObj != null)
                                {
                                    scriptProp.SetValue(createdHandler, scriptObj);
                                    scriptSetResult = $"已关联脚本: {scriptName}";
                                }
                                else
                                {
                                    scriptSetResult = $"未找到脚本: {scriptName}（事件处理器已创建但未关联脚本）";
                                }
                            }
                            else
                            {
                                scriptSetResult = "事件处理器无 Script 可写属性";
                            }
                        }
                        catch (Exception ex2) { scriptSetResult = $"关联脚本失败: {ex2.Message}"; }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"已为画面项 {itemName} 创建事件处理器（事件: {enumValue}）",
                        screenName,
                        itemName,
                        eventType = enumValue.ToString(),
                        scriptName = scriptName ?? "(无)",
                        scriptSetResult = scriptSetResult ?? "(未指定脚本)",
                        handlerRuntimeType = createdHandler.GetType().Name,
                        apiExplored = new
                        {
                            itemRuntimeType = itemRuntimeType.Name,
                            eventTypeEnumType = eventTypeEnumType.FullName
                        }
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ── 8. CreateClassicHmiScreenFromTemplate — 传统 HMI 模板画面文件夹 ──

        /// <summary>
        /// 传统 HMI 从模板/弹出/滑入文件夹创建画面结构。
        /// 按 templateType 选择 ScreenPopupFolder/ScreenSlideinFolder/ScreenTemplateFolder/ScreenFolder，
        /// 若 folderName 非空则创建子文件夹。
        /// 实际画面创建需 MasterCopy 或 XML 文件（配合 import_hmi_screen），本方法完成文件夹结构准备。
        /// </summary>
        public string CreateClassicHmiScreenFromTemplate(string screenName, string templateType,
            string? folderName = null, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(screenName)) return Err("参数 screenName 不能为空");
                    if (string.IsNullOrWhiteSpace(templateType)) return Err("参数 templateType 不能为空");

                    // 找传统 HMI Target（按名称或取第一个）
                    HmiTarget? hmiTarget = null;
                    if (!string.IsNullOrEmpty(hmiDeviceName))
                    {
                        foreach (var device in GetAllDevices())
                        {
                            if (!device.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
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
                    }
                    else
                    {
                        hmiTarget = GetClassicHmi();
                    }

                    if (hmiTarget == null)
                        return Err("未找到传统 HMI 设备，此工具仅支持经典 HMI（WinCC Advanced/Comfort）" +
                            (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    // 按 templateType 选择文件夹
                    var tNorm = templateType.Trim();
                    object screenFolder;
                    string folderKind;
                    if (tNorm.Equals("Popup", StringComparison.OrdinalIgnoreCase))
                    { screenFolder = hmiTarget.ScreenPopupFolder; folderKind = "Popup"; }
                    else if (tNorm.Equals("Slidein", StringComparison.OrdinalIgnoreCase))
                    { screenFolder = hmiTarget.ScreenSlideinFolder; folderKind = "Slidein"; }
                    else if (tNorm.Equals("Template", StringComparison.OrdinalIgnoreCase))
                    { screenFolder = hmiTarget.ScreenTemplateFolder; folderKind = "Template"; }
                    else if (tNorm.Equals("Screen", StringComparison.OrdinalIgnoreCase))
                    { screenFolder = hmiTarget.ScreenFolder; folderKind = "Screen"; }
                    else
                        return Err($"不支持的 templateType: {templateType}（支持: Popup/Slidein/Template/Screen）");

                    string note = $"已定位 {folderKind} 画面系统文件夹";

                    // 若 folderName 非空，反射创建子文件夹
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        try
                        {
                            var foldersProp = screenFolder.GetType().GetProperty("Folders");
                            if (foldersProp == null)
                                return Err($"{folderKind} 文件夹无 Folders 属性");
                            var folders = foldersProp.GetValue(screenFolder);
                            var createM = folders?.GetType().GetMethod("Create", new[] { typeof(string) });
                            if (createM == null)
                                return Err($"Folders 集合无 Create(string) 方法");
                            createM.Invoke(folders, new object[] { folderName });
                            note = $"已在 {folderKind} 文件夹下创建子文件夹: {folderName}";
                        }
                        catch (TargetInvocationException tie)
                        {
                            var inner = tie.InnerException?.Message ?? tie.Message;
                            return Err($"创建子文件夹 {folderName} 失败: {inner}");
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = note + "（实际画面创建需用 import_hmi_screen 导入 XML，或从主控对象复制）",
                        screenName,
                        templateType = folderKind,
                        folderName = folderName ?? "(未创建子文件夹)",
                        hmiDeviceName = hmiTarget.Name,
                        note = "传统 HMI 画面创建需 MasterCopy 或 XML 文件；本方法完成文件夹结构准备，" +
                               "请配合 export_hmi_screen/import_hmi_screen 或主控对象库使用"
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // HMI 无人干预全自动生成（编排层）
        // CreateHmiFromSpec / ExportHmiWorkspace / ImportHmiWorkspace
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 从 JSON 规格自动创建完整 HMI 设备（画面+变量+报警+连接）。
        /// 内部按顺序调用：创建连接 → 创建变量 → 批量绑定 PLC → 创建画面 → 创建画面项 → 绑定标签 → 创建报警 → 设置启动画面。
        /// 某一步失败时记录错误但继续执行后续步骤，最终返回汇总结果。
        /// </summary>
        /// <param name="specJson">
        /// JSON 规格，包含 hmiDeviceName/hmiType/connection/screens/tags/alarms/startScreen。
        /// 示例：
        /// {
        ///   "hmiDeviceName": "HMI_1",
        ///   "hmiType": "Unified",
        ///   "connection": { "plcName": "PLC_1", "connectionName": "HMI_Connection_1" },
        ///   "screens": [
        ///     {
        ///       "name": "MainScreen",
        ///       "items": [
        ///         { "type": "Button", "name": "btnStart", "left": 50, "top": 50, "width": 100, "height": 40, "tag": "StartButton" },
        ///         { "type": "IOField", "name": "iofTemp", "left": 200, "top": 50, "width": 80, "height": 30, "tag": "Temperature" }
        ///       ]
        ///     }
        ///   ],
        ///   "tags": [
        ///     { "name": "StartButton", "dataType": "Bool", "connection": "HMI_Connection_1", "address": "M0.0" },
        ///     { "name": "Temperature", "dataType": "Real", "connection": "HMI_Connection_1", "address": "MD10" }
        ///   ],
        ///   "alarms": [
        ///     { "name": "HighTemp", "type": "Analog", "triggerTag": "Temperature", "limit": 100.0 }
        ///   ],
        ///   "startScreen": "MainScreen"
        /// }
        /// </param>
        /// <returns>JSON 结果，包含每个步骤的成功/失败状态</returns>
        public string CreateHmiFromSpec(string specJson)
        {
            lock (_lock)
            {
                var steps = new List<object>();
                var hasError = false;
                try
                {
                    RequireProject();

                    var spec = JsonConvert.DeserializeObject<Dictionary<string, object?>>(specJson);
                    if (spec == null)
                        return Err("无法解析 specJson");

                    var hmiType = (spec.TryGetValue("hmiType", out var ht) ? ht?.ToString() : null) ?? "Classic";
                    var isUnified = hmiType.Equals("Unified", StringComparison.OrdinalIgnoreCase);

                    // ── 步骤 1: 创建连接 ──
                    string? connectionName = null;
                    if (spec.TryGetValue("connection", out var connObj) && connObj is JObject conn)
                    {
                        connectionName = conn.Value<string>("connectionName") ?? "HMI_Connection_1";
                        var plcName = conn.Value<string>("plcName") ?? "PLC_1";
                        try
                        {
                            string connResult;
                            if (isUnified)
                            {
                                connResult = CreateUnifiedHmiConnection(
                                    spec.TryGetValue("hmiDeviceName", out var hdn) ? hdn?.ToString() ?? "HMI_1" : "HMI_1",
                                    connectionName, plcName);
                            }
                            else
                            {
                                connResult = CreateHmiConnection(connectionName, plcName: plcName);
                            }
                            var connParsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(connResult);
                            steps.Add(new { step = "create_connection", success = connParsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                connectionName, details = connResult });
                            if (connParsed?.TryGetValue("success", out var cs) != true || cs is not bool csb || !csb)
                                hasError = true;
                        }
                        catch (Exception ex)
                        {
                            steps.Add(new { step = "create_connection", success = false, connectionName, error = ex.Message });
                            hasError = true;
                        }
                    }

                    // ── 步骤 2: 创建变量 ──
                    int tagsCreated = 0, tagsFailed = 0;
                    if (spec.TryGetValue("tags", out var tagsObj) && tagsObj is JArray tags)
                    {
                        foreach (var tag in tags)
                        {
                            var tagName = tag.Value<string>("name") ?? "";
                            if (string.IsNullOrEmpty(tagName)) continue;
                            var dataType = tag.Value<string>("dataType") ?? "Bool";
                            var address = tag.Value<string>("address");
                            var connName = tag.Value<string>("connection") ?? connectionName;
                            try
                            {
                                string tagResult;
                                if (isUnified)
                                {
                                    // Unified HMI: 直接使用现有 CreateHmiTag 逻辑
                                    tagResult = CreateHmiTag(tagName, connection: connName, address: address);
                                }
                                else
                                {
                                    tagResult = CreateHmiTag(tagName, connection: connName, address: address);
                                }

                                var tagParsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(tagResult);
                                if (tagParsed?.TryGetValue("success", out var ts) == true && ts is bool tsb && tsb)
                                {
                                    tagsCreated++;
                                    // 绑定 PLC 地址
                                    if (!string.IsNullOrEmpty(address))
                                    {
                                        try
                                        {
                                            BindHmiTagToPlc(tagName, address, connName);
                                        }
                                        catch { /* 绑定失败不阻塞 */ }
                                    }
                                }
                                else
                                {
                                    tagsFailed++;
                                    hasError = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                tagsFailed++;
                                hasError = true;
                                steps.Add(new { step = "create_tag", success = false, tagName, error = ex.Message });
                            }
                        }
                    }
                    steps.Add(new { step = "create_tags", success = tagsFailed == 0,
                        created = tagsCreated, failed = tagsFailed });

                    // ── 步骤 3: 创建画面及画面项 ──
                    int screensCreated = 0, screensFailed = 0;
                    int itemsCreated = 0, itemsFailed = 0;
                    if (spec.TryGetValue("screens", out var screensObj) && screensObj is JArray screens)
                    {
                        foreach (var screen in screens)
                        {
                            var screenName = screen.Value<string>("name") ?? "";
                            if (string.IsNullOrEmpty(screenName)) continue;

                            // 创建画面
                            try
                            {
                                string screenResult;
                                if (isUnified)
                                {
                                    // Unified HMI: 通过 HmiSoftware.Screens.Create(name) 创建
                                    screenResult = CreateUnifiedHmiScreenViaApi(screenName);
                                }
                                else
                                {
                                    screenResult = CreateHmiScreen(screenName);
                                }
                                var screenParsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(screenResult);
                                if (screenParsed?.TryGetValue("success", out var ss) == true && ss is bool ssb && ssb)
                                {
                                    screensCreated++;
                                }
                                else
                                {
                                    screensFailed++;
                                    hasError = true;
                                    steps.Add(new { step = "create_screen", success = false, screenName, details = screenResult });
                                    continue; // 画面创建失败，跳过画面项
                                }
                            }
                            catch (Exception ex)
                            {
                                screensFailed++;
                                hasError = true;
                                steps.Add(new { step = "create_screen", success = false, screenName, error = ex.Message });
                                continue;
                            }

                            // 创建画面项
                            if (screen.Value<JArray>("items") is JArray items)
                            {
                                foreach (var item in items)
                                {
                                    var itemType = item.Value<string>("type") ?? "Button";
                                    var itemName = item.Value<string>("name") ?? "";
                                    var itemTag = item.Value<string>("tag");
                                    if (string.IsNullOrEmpty(itemName)) continue;

                                    try
                                    {
                                        string itemResult;
                                        if (isUnified)
                                        {
                                            itemResult = CreateUnifiedHmiScreenItemTyped(
                                                screenName, itemType, itemName,
                                                item.Value<int?>("left"),
                                                item.Value<int?>("top"),
                                                item.Value<int?>("width"),
                                                item.Value<int?>("height"));
                                        }
                                        else
                                        {
                                            itemResult = CreateHmiScreenItem(
                                                screenName, itemType, itemName,
                                                itemTag,
                                                item.Value<int?>("left"),
                                                item.Value<int?>("top"),
                                                item.Value<int?>("width"),
                                                item.Value<int?>("height"));
                                        }

                                        var itemParsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(itemResult);
                                        if (itemParsed?.TryGetValue("success", out var ns) == true && ns is bool nsb && nsb)
                                        {
                                            itemsCreated++;
                                            // 绑定标签
                                            if (!string.IsNullOrEmpty(itemTag))
                                            {
                                                try
                                                {
                                                    BindHmiScreenItemTag(screenName, itemName, itemTag);
                                                }
                                                catch { /* 绑定失败不阻塞 */ }
                                            }
                                        }
                                        else
                                        {
                                            itemsFailed++;
                                            hasError = true;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        itemsFailed++;
                                        hasError = true;
                                        steps.Add(new { step = "create_screen_item", success = false,
                                            screenName, itemName, error = ex.Message });
                                    }
                                }
                            }
                        }
                    }
                    steps.Add(new { step = "create_screens", success = screensFailed == 0,
                        created = screensCreated, failed = screensFailed });
                    steps.Add(new { step = "create_screen_items", success = itemsFailed == 0,
                        created = itemsCreated, failed = itemsFailed });

                    // ── 步骤 4: 创建报警 ──
                    int alarmsCreated = 0, alarmsFailed = 0;
                    if (spec.TryGetValue("alarms", out var alarmsObj) && alarmsObj is JArray alarms)
                    {
                        foreach (var alarm in alarms)
                        {
                            var alarmName = alarm.Value<string>("name") ?? "";
                            var alarmType = alarm.Value<string>("type") ?? "Discrete";
                            if (string.IsNullOrEmpty(alarmName)) continue;

                            try
                            {
                                string alarmResult;
                                if (alarmType.Equals("Analog", StringComparison.OrdinalIgnoreCase))
                                    alarmResult = CreateHmiAnalogAlarm(alarmName);
                                else
                                    alarmResult = CreateHmiDiscreteAlarm(alarmName);

                                var alarmParsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(alarmResult);
                                if (alarmParsed?.TryGetValue("success", out var asb) == true && asb is bool asbb && asbb)
                                    alarmsCreated++;
                                else
                                { alarmsFailed++; hasError = true; }
                            }
                            catch (Exception ex)
                            {
                                alarmsFailed++;
                                hasError = true;
                                steps.Add(new { step = "create_alarm", success = false, alarmName, error = ex.Message });
                            }
                        }
                    }
                    steps.Add(new { step = "create_alarms", success = alarmsFailed == 0,
                        created = alarmsCreated, failed = alarmsFailed });

                    // ── 步骤 5: 设置启动画面 ──
                    if (spec.TryGetValue("startScreen", out var startScreen) && !string.IsNullOrEmpty(startScreen?.ToString()))
                    {
                        try
                        {
                            var startResult = SetHmiStartScreen(startScreen.ToString()!);
                            var startParsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(startResult);
                            steps.Add(new { step = "set_start_screen", success = startParsed?.TryGetValue("success", out var sss) == true && sss is bool sssb && sssb,
                                startScreen = startScreen.ToString(), details = startResult });
                            if (startParsed?.TryGetValue("success", out var sss2) != true || sss2 is not bool sss3 || !sss3)
                                hasError = true;
                        }
                        catch (Exception ex)
                        {
                            steps.Add(new { step = "set_start_screen", success = false,
                                startScreen = startScreen.ToString(), error = ex.Message });
                            hasError = true;
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = !hasError,
                        message = hasError ? "HMI 创建完成，但部分步骤失败，请查看 steps 了解详情" : "HMI 创建全部完成",
                        hmiType = isUnified ? "Unified" : "Classic",
                        steps
                    }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        steps
                    }, Formatting.Indented);
                }
            }
        }

        /// <summary>
        /// 导出完整 HMI 工作区到指定目录。
        /// 导出画面(export_hmi_screen)、变量表(export_hmi_tag_table)、连接(export_hmi_connection)，
        /// 并生成 workspace.json 索引文件。
        /// </summary>
        /// <param name="directoryPath">导出目标目录绝对路径</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选，为空时使用第一个找到的 HMI）</param>
        /// <returns>JSON 结果，包含导出汇总和 workspace.json 路径</returns>
        public string ExportHmiWorkspace(string directoryPath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                var exports = new List<object>();
                var errors = new List<string>();
                try
                {
                    RequireProject();

                    if (!Directory.Exists(directoryPath))
                        Directory.CreateDirectory(directoryPath);

                    // 检测 HMI 类型
                    var isUnified = false;
                    HmiTarget? classicHmi = null;
                    object? unifiedHmi = null;

                    try
                    {
                        classicHmi = GetClassicHmi();
                        // 如果 hmiDeviceName 指定了，尝试匹配
                        if (classicHmi != null && !string.IsNullOrEmpty(hmiDeviceName))
                        {
                            var info = GetHmiDeviceInfo();
                            if (info != null && !info.Target.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase))
                                classicHmi = null;
                        }
                    }
                    catch { }

                    if (classicHmi == null)
                    {
                        unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                        if (unifiedHmi != null) isUnified = true;
                    }

                    if (classicHmi == null && unifiedHmi == null)
                        return Err("未找到 HMI 设备" + (string.IsNullOrEmpty(hmiDeviceName) ? "" : $"（设备名: {hmiDeviceName}）"));

                    var hmiName = classicHmi?.Name ?? hmiDeviceName ?? "HMI";

                    // ── 导出画面 ──
                    var screensDir = Path.Combine(directoryPath, "screens");
                    Directory.CreateDirectory(screensDir);
                    var screenNames = new List<string>();
                    try
                    {
                        var screenList = classicHmi != null
                            ? GetAllScreens(classicHmi.ScreenFolder).ToList()
                            : new List<Screen>();
                        // Unified HMI 画面导出
                        if (unifiedHmi != null)
                        {
                            var screens = GetHmiProperty(unifiedHmi, "Screens") as System.Collections.IEnumerable;
                            if (screens == null) errors.Add("导出画面失败: Unified HMI 无 Screens 集合");
                            else
                            {
                                foreach (var screen in screens)
                                {
                                    try
                                    {
                                        var screenName = GetHmiProperty(screen, "Name")?.ToString() ?? "";
                                        var outputPath = Path.Combine(screensDir, $"{screenName}.xml");
                                        // 尝试通过反射调用 Export（HmiScreen 可能直接支持 Export）
                                        var exportMethod = screen.GetType().GetMethod("Export",
                                            new[] { typeof(FileInfo), typeof(ExportOptions) });
                                        if (exportMethod != null)
                                        {
                                            exportMethod.Invoke(screen, new object[] { new FileInfo(outputPath), ExportOptions.WithDefaults });
                                        }
                                        else
                                        {
                                            errors.Add($"导出画面 {screenName} 失败: 该类型的 HmiScreen 无 Export 方法");
                                            continue;
                                        }
                                        screenNames.Add(screenName);
                                        exports.Add(new { type = "screen", name = screenName, file = outputPath });
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add($"导出画面 {GetHmiProperty(screen, "Name")} 失败: {ex.Message}");
                                    }
                                }
                            }
                        }
                        else
                        {
                            foreach (var screen in screenList)
                            {
                                try
                                {
                                    var outputPath = Path.Combine(screensDir, $"{screen.Name}.xml");
                                    screen.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                                    screenNames.Add(screen.Name);
                                    exports.Add(new { type = "screen", name = screen.Name, file = outputPath });
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"导出画面 {screen.Name} 失败: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"导出画面列表失败: {ex.Message}");
                    }

                    // ── 导出变量表 ──
                    var tagsDir = Path.Combine(directoryPath, "tags");
                    Directory.CreateDirectory(tagsDir);
                    var tagTables = new List<string>();
                    try
                    {
                        if (classicHmi != null)
                        {
                            var allTables = GetAllTagTables(classicHmi.TagFolder).ToList();
                            foreach (var table in allTables)
                            {
                                try
                                {
                                    var outputPath = Path.Combine(tagsDir, $"{table.Name}.xml");
                                    table.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                                    tagTables.Add(table.Name);
                                    exports.Add(new { type = "tag_table", name = table.Name, file = outputPath });
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"导出变量表 {table.Name} 失败: {ex.Message}");
                                }
                            }
                        }
                        else if (unifiedHmi != null)
                        {
                            // Unified HMI 变量导出
                            var tagTablesColl = GetHmiProperty(unifiedHmi, "TagTables") as System.Collections.IEnumerable;
                            if (tagTablesColl == null) errors.Add("导出变量表失败: Unified HMI 无 TagTables 集合");
                            else
                            {
                                foreach (var table in tagTablesColl)
                                {
                                    try
                                    {
                                        var tableName = GetHmiProperty(table, "Name")?.ToString() ?? "";
                                        var outputPath = Path.Combine(tagsDir, tableName);
                                        var tags = GetHmiProperty(table, "Tags");
                                        TryInvoke(tags, "Export", new DirectoryInfo(outputPath));
                                        tagTables.Add(tableName);
                                        exports.Add(new { type = "tag_table", name = tableName, directory = outputPath });
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add($"导出变量表 {GetHmiProperty(table, "Name")} 失败: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"导出变量表列表失败: {ex.Message}");
                    }

                    // ── 导出连接 ──
                    var connsDir = Path.Combine(directoryPath, "connections");
                    Directory.CreateDirectory(connsDir);
                    var connectionNames = new List<string>();
                    try
                    {
                        if (classicHmi != null)
                        {
                            foreach (var conn in classicHmi.Connections)
                            {
                                try
                                {
                                    var outputPath = Path.Combine(connsDir, $"{conn.Name}.xml");
                                    conn.Export(new FileInfo(outputPath), ExportOptions.WithDefaults);
                                    connectionNames.Add(conn.Name);
                                    exports.Add(new { type = "connection", name = conn.Name, file = outputPath });
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"导出连接 {conn.Name} 失败: {ex.Message}");
                                }
                            }
                        }
                        else if (unifiedHmi != null)
                        {
                            var conns = GetHmiProperty(unifiedHmi, "Connections") as System.Collections.IEnumerable;
                            if (conns == null) errors.Add("导出连接失败: Unified HMI 无 Connections 集合");
                            else
                            {
                                foreach (var conn in conns)
                                {
                                    try
                                    {
                                        var connName = GetHmiProperty(conn, "Name")?.ToString() ?? "";
                                        var outputPath = Path.Combine(connsDir, $"{connName}.xml");
                                        // 尝试通过反射调用 Export
                                        var exportMethod = conn.GetType().GetMethod("Export",
                                            new[] { typeof(FileInfo), typeof(ExportOptions) });
                                        if (exportMethod != null)
                                        {
                                            exportMethod.Invoke(conn, new object[] { new FileInfo(outputPath), ExportOptions.WithDefaults });
                                        }
                                        else
                                        {
                                            errors.Add($"导出连接 {connName} 失败: 该类型的 HmiConnection 无 Export 方法");
                                            continue;
                                        }
                                        connectionNames.Add(connName);
                                        exports.Add(new { type = "connection", name = connName, file = outputPath });
                                    }
                                    catch (Exception ex)
                                    {
                                        errors.Add($"导出连接 {GetHmiProperty(conn, "Name")} 失败: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"导出连接列表失败: {ex.Message}");
                    }

                    // ── 生成 workspace.json ──
                    var workspaceJson = Path.Combine(directoryPath, "workspace.json");
                    var workspace = new
                    {
                        hmiName,
                        hmiType = isUnified ? "Unified" : "Classic",
                        exportedAt = DateTime.UtcNow.ToString("o"),
                        screens = screenNames,
                        tagTables,
                        connections = connectionNames,
                        exports,
                        errors
                    };
                    File.WriteAllText(workspaceJson, JsonConvert.SerializeObject(workspace, Formatting.Indented), Encoding.UTF8);

                    return JsonConvert.SerializeObject(new
                    {
                        success = errors.Count == 0,
                        message = errors.Count == 0
                            ? $"HMI 工作区已导出到: {directoryPath}"
                            : $"HMI 工作区导出完成，但有 {errors.Count} 个错误",
                        workspaceFile = workspaceJson,
                        hmiName,
                        hmiType = isUnified ? "Unified" : "Classic",
                        screenCount = screenNames.Count,
                        tagTableCount = tagTables.Count,
                        connectionCount = connectionNames.Count,
                        exportCount = exports.Count,
                        errors
                    }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        exports,
                        errors
                    }, Formatting.Indented);
                }
            }
        }

        /// <summary>
        /// 从 workspace.json 索引文件导入完整 HMI 工作区。
        /// 按依赖顺序导入：连接 → 变量 → 画面 → 报警 → 设置启动画面。
        /// </summary>
        /// <param name="directoryPath">包含 workspace.json 的目录绝对路径</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选）</param>
        /// <returns>JSON 结果，包含导入汇总</returns>
        public string ImportHmiWorkspace(string directoryPath, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                var imports = new List<object>();
                var errors = new List<string>();
                try
                {
                    RequireProject();

                    var workspaceFile = Path.Combine(directoryPath, "workspace.json");
                    if (!File.Exists(workspaceFile))
                        return Err($"未找到 workspace.json: {workspaceFile}");

                    var workspaceJson = File.ReadAllText(workspaceFile, Encoding.UTF8);
                    var workspace = JsonConvert.DeserializeObject<Dictionary<string, object?>>(workspaceJson);
                    if (workspace == null)
                        return Err("无法解析 workspace.json");

                    var isUnified = workspace.TryGetValue("hmiType", out var ht) &&
                        ht?.ToString()?.Equals("Unified", StringComparison.OrdinalIgnoreCase) == true;

                    // ── 步骤 1: 导入连接 ──
                    var connsDir = Path.Combine(directoryPath, "connections");
                    if (Directory.Exists(connsDir))
                    {
                        var connFiles = Directory.GetFiles(connsDir, "*.xml");
                        foreach (var connFile in connFiles)
                        {
                            try
                            {
                                // 连接导入通过 XML Import 方式（复用现有 ImportHmiScreen 的 XML 导入模式）
                                string result = ImportHmiConnectionViaXml(connFile, isUnified);
                                var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(result);
                                imports.Add(new { type = "connection", file = connFile,
                                    success = parsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                    details = result });
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"导入连接 {Path.GetFileName(connFile)} 失败: {ex.Message}");
                                imports.Add(new { type = "connection", file = connFile, success = false, error = ex.Message });
                            }
                        }
                    }

                    // ── 步骤 2: 导入变量表 ──
                    var tagsDir = Path.Combine(directoryPath, "tags");
                    if (Directory.Exists(tagsDir))
                    {
                        if (isUnified)
                        {
                            // Unified HMI: 变量按子目录导出，每个子目录是一个变量表
                            foreach (var subDir in Directory.GetDirectories(tagsDir))
                            {
                                try
                                {
                                    var result = ImportUnifiedHmiTags(subDir, null);
                                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(result);
                                    imports.Add(new { type = "tag_table", directory = subDir,
                                        success = parsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                        details = result });
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"导入 Unified HMI 变量表 {Path.GetFileName(subDir)} 失败: {ex.Message}");
                                    imports.Add(new { type = "tag_table", directory = subDir, success = false, error = ex.Message });
                                }
                            }
                            // 如果 tagsDir 直接包含 XML 文件（而非子目录），用 ImportUnifiedHmiTags 导入整个目录
                            var xmlFiles = Directory.GetFiles(tagsDir, "*.xml");
                            if (xmlFiles.Length > 0)
                            {
                                try
                                {
                                    var result = ImportUnifiedHmiTags(tagsDir, null);
                                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(result);
                                    imports.Add(new { type = "tag_table", directory = tagsDir,
                                        success = parsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                        details = result });
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"导入 Unified HMI 变量失败: {ex.Message}");
                                }
                            }
                        }
                        else
                        {
                            // Classic HMI: 变量按 XML 文件导出
                            var tagFiles = Directory.GetFiles(tagsDir, "*.xml");
                            foreach (var tagFile in tagFiles)
                            {
                                try
                                {
                                    var result = ImportHmiTagTable(tagFile);
                                    var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(result);
                                    imports.Add(new { type = "tag_table", file = tagFile,
                                        success = parsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                        details = result });
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"导入变量表 {Path.GetFileName(tagFile)} 失败: {ex.Message}");
                                    imports.Add(new { type = "tag_table", file = tagFile, success = false, error = ex.Message });
                                }
                            }
                        }
                    }

                    // ── 步骤 3: 导入画面 ──
                    var screensDir = Path.Combine(directoryPath, "screens");
                    if (Directory.Exists(screensDir))
                    {
                        var screenFiles = Directory.GetFiles(screensDir, "*.xml");
                        foreach (var screenFile in screenFiles)
                        {
                            try
                            {
                                var result = ImportHmiScreen(screenFile);
                                var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(result);
                                imports.Add(new { type = "screen", file = screenFile,
                                    success = parsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                    details = result });
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"导入画面 {Path.GetFileName(screenFile)} 失败: {ex.Message}");
                                imports.Add(new { type = "screen", file = screenFile, success = false, error = ex.Message });
                            }
                        }
                    }

                    // ── 步骤 4: 设置启动画面（如果 workspace 中有记录） ──
                    if (workspace.TryGetValue("startScreen", out var startScreen) && !string.IsNullOrEmpty(startScreen?.ToString()))
                    {
                        try
                        {
                            var result = SetHmiStartScreen(startScreen.ToString()!);
                            var parsed = JsonConvert.DeserializeObject<Dictionary<string, object?>>(result);
                            imports.Add(new { type = "start_screen", screen = startScreen.ToString(),
                                success = parsed?.TryGetValue("success", out var s) == true && s is bool sb && sb,
                                details = result });
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"设置启动画面失败: {ex.Message}");
                            imports.Add(new { type = "start_screen", success = false, error = ex.Message });
                        }
                    }

                    return JsonConvert.SerializeObject(new
                    {
                        success = errors.Count == 0,
                        message = errors.Count == 0
                            ? $"HMI 工作区导入完成"
                            : $"HMI 工作区导入完成，但有 {errors.Count} 个错误",
                        workspaceFile,
                        hmiType = isUnified ? "Unified" : "Classic",
                        importCount = imports.Count,
                        imports,
                        errors
                    }, Formatting.Indented);
                }
                catch (Exception ex)
                {
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = ex.Message,
                        imports,
                        errors
                    }, Formatting.Indented);
                }
            }
        }

        // ── 辅助：Unified HMI 画面创建（通过 HmiSoftware.Screens API） ──

        /// <summary>
        /// 通过 HmiSoftware.Screens 的 Create 方法创建 Unified HMI 画面。
        /// 尝试 Screens.Create(string name) 或回退到 TryCreateViaReflection。
        /// </summary>
        private string CreateUnifiedHmiScreenViaApi(string screenName)
        {
            RequireProject();
            var unifiedHmi = FindUnifiedHmiSoftware();
            if (unifiedHmi == null)
                return Err("未找到 Unified HMI 设备，无法创建画面");

            try
            {
                // 尝试强类型 Screens.Create(name)
                var screens = GetHmiProperty(unifiedHmi, "Screens");
                var created = TryCreateViaReflection(screens, screenName, "HmiSoftware.Screens");
                var np = created?.GetType().GetProperty("Name")?.GetValue(created)?.ToString();
                return Ok($"已创建 Unified HMI 画面: {np ?? screenName}");
            }
            catch (Exception ex)
            {
                return Err($"创建 Unified HMI 画面失败: {ex.Message}");
            }
        }

        // ── 辅助：HMI 连接导入（通过 XML Import） ──

        /// <summary>
        /// 通过 XML Import 方式导入 HMI 连接。
        /// 对于 Classic HMI：使用 hmi.Connections.Import(fileInfo, ImportOptions)
        /// 对于 Unified HMI：使用 unifiedHmi.Connections.Import(fileInfo, ImportOptions)
        /// </summary>
        private string ImportHmiConnectionViaXml(string filePath, bool isUnified)
        {
            // ★防护★ 用户可控连接 XML 先做良构校验，坏 XML 曾直接终止 TIA 进程
            var vErr = ValidateImportXmlFile(filePath, "导入 HMI 连接");
            if (vErr != null) return Err(vErr);

            if (isUnified)
            {
                var unifiedHmi = FindUnifiedHmiSoftware();
                if (unifiedHmi == null)
                    return Err("未找到 Unified HMI 设备");
                try
                {
                    // 尝试通过反射调用 Import（HmiConnectionComposition 可能支持 Import）
                    var conns = GetHmiProperty(unifiedHmi, "Connections");
                    var importMethod = conns.GetType().GetMethod("Import",
                        new[] { typeof(FileInfo), typeof(ImportOptions) });
                    if (importMethod != null)
                    {
                        var imported = importMethod.Invoke(conns, new object[] { new FileInfo(filePath), ImportOptions.Override });
                        var count = (imported as IEnumerable)?.Cast<object>().Count() ?? 0;
                        return Ok($"已导入 Unified HMI 连接 ({count} 个)");
                    }
                    return Err("Unified HMI 连接集合不支持 Import 方法，请使用 create_unified_hmi_connection 创建连接");
                }
                catch (Exception ex)
                {
                    return Err($"导入 Unified HMI 连接失败: {ex.Message}");
                }
            }
            else
            {
                var hmi = RequireClassicHmi();
                try
                {
                    var imported = hmi.Connections.Import(new FileInfo(filePath), ImportOptions.Override);
                    var count = (imported as IEnumerable)?.Cast<object>().Count() ?? 0;
                    return Ok($"已导入经典 HMI 连接 ({count} 个)");
                }
                catch (Exception ex)
                {
                    return Err($"导入经典 HMI 连接失败: {ex.Message}");
                }
            }
        }

        // ── 辅助：安全计数 Composition（优先 Count 属性，回退枚举计数） ──

        /// <summary>
        /// 通过 Count 属性或枚举计数获取集合元素数，全部异常吞掉返回 0。
        /// 用于 Tag/Script 等 Composition 的数量统计。
        /// </summary>
        private static int CountCompositionSafe(object? collection)
        {
            if (collection == null) return 0;
            try
            {
                var countProp = collection.GetType().GetProperty("Count");
                if (countProp != null && countProp.GetValue(collection) is int c) return c;
            }
            catch { }
            try
            {
                if (collection is IEnumerable e)
                {
                    int n = 0;
                    foreach (var _ in e) n++;
                    return n;
                }
            }
            catch { }
            return 0;
        }

        // ────────────────────────────────────────────────────────────
        // 任务 1: CreateHmiTrendView — 经典 HMI 趋势图画面项
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 在经典 HMI 画面上创建趋势图（Trend View）画面项。
        /// 实现思路：
        ///   1. 找到指定画面（screenName）
        ///   2. 通过 XML 往返方式注入 Hmi.Screen.TrendView 节点
        ///   3. 反射方式作为备用方案（ScreenItems.Create）
        /// 经典 HMI 趋势图的 XML 类型名为 "Hmi.Screen.TrendView"。
        /// </summary>
        /// <param name="screenName">画面名称（必填）</param>
        /// <param name="trendName">趋势图画面项名称（必填）</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选，多 HMI 时指定）</param>
        /// <returns>JSON 结果字符串</returns>
        public string CreateHmiTrendView(string screenName, string trendName, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(screenName))
                        return Err("参数 screenName 不能为空");
                    if (string.IsNullOrWhiteSpace(trendName))
                        return Err("参数 trendName 不能为空");

                    var hmi = RequireClassicHmi(hmiDeviceName);
                    var screen = FindScreenRecursive(hmi.ScreenFolder, screenName);
                    if (screen == null) return Err($"未找到画面: {screenName}");

                    // 路径 1: 反射尝试 ScreenItems.Create("TrendView") / Create("HmiScreenTrendView") 等
                    var reflectionAttempts = new List<object>();
                    object? createdViaReflection = null;
                    try
                    {
                        var screenItemsColl = GetScreenItemsCollection(screen);
                        if (screenItemsColl != null)
                        {
                            // 尝试常见的趋势图类型标识符
                            var typeIds = new[] { "TrendView", "HmiScreenTrendView", "Hmi.Screen.TrendView" };
                            foreach (var tid in typeIds)
                            {
                                try
                                {
                                    var createM = screenItemsColl.GetType().GetMethod(
                                        "Create", new[] { typeof(string), typeof(string) });
                                    if (createM != null)
                                    {
                                        var obj = createM.Invoke(screenItemsColl, new object[] { trendName, tid });
                                        if (obj != null)
                                        {
                                            createdViaReflection = obj;
                                            reflectionAttempts.Add(new { typeId = tid, success = true });
                                            break;
                                        }
                                    }
                                }
                                catch (TargetInvocationException tie)
                                {
                                    reflectionAttempts.Add(new
                                    {
                                        typeId = tid,
                                        success = false,
                                        error = tie.InnerException?.Message ?? tie.Message
                                    });
                                }
                                catch (Exception ex)
                                {
                                    reflectionAttempts.Add(new { typeId = tid, success = false, error = ex.Message });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        reflectionAttempts.Add(new { typeId = "(reflection-setup)", success = false, error = ex.Message });
                    }

                    // 反射创建成功则直接返回
                    if (createdViaReflection != null)
                    {
                        var createdName = createdViaReflection.GetType().GetProperty("Name")?.GetValue(createdViaReflection)?.ToString() ?? trendName;
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"已创建趋势图画面项: {createdName}（反射方式）",
                            screenName,
                            trendName = createdName,
                            method = "Reflection",
                            reflectionAttempts
                        }, Formatting.Indented);
                    }

                    // 路径 2: XML 往返方式（与 CreateHmiScreenItem 一致）
                    var tempFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_trend_{Guid.NewGuid():N}.xml");
                    var exportFile = Path.Combine(Path.GetTempPath(), $"tia_hmi_trend_exp_{Guid.NewGuid():N}.xml");
                    try
                    {
                        // 1. 导出当前画面 XML
                        screen.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                        var xml = File.ReadAllText(exportFile, Encoding.UTF8);

                        // 2. 构建 TrendView XML 片段并注入
                        var trendXml = BuildTrendViewXml(trendName);
                        InjectScreenItemXmlToFile(xml, trendXml, tempFile);

                        // 3. 重新导入
                        hmi.ScreenFolder.Screens.Import(new FileInfo(tempFile), ImportOptions.Override);

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"已创建趋势图画面项: {trendName}（XML 往返方式）",
                            screenName,
                            trendName,
                            method = "XmlRoundTrip",
                            reflectionAttempts
                        }, Formatting.Indented);
                    }
                    finally
                    {
                        TryDelete3(tempFile);
                        TryDelete3(exportFile);
                    }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// 构建经典 HMI 趋势图（Hmi.Screen.TrendView）的最小化 XML 片段。
        /// 含基础属性（坐标/尺寸/ObjectName）和一个默认 TrendCurve 子对象。
        /// 对照经典 HMI 导出 XML 的 Hmi.Screen.* 风格手工构造。
        /// </summary>
        private static string BuildTrendViewXml(string trendName)
        {
            var escName = System.Security.SecurityElement.Escape(trendName);
            var id = GenerateHexId();
            // 趋势图默认尺寸：宽 400，高 300，位置 50,50
            int L = 50, T = 50, W = 400, H = 300;

            var sb = new StringBuilder();
            sb.AppendLine($"          <Hmi.Screen.TrendView ID=\"{id}\" CompositionName=\"ScreenItems\">");
            sb.AppendLine("            <AttributeList>");
            sb.AppendLine("              <BackColor>255, 255, 255</BackColor>");
            sb.AppendLine("              <BackFillStyle>Solid</BackFillStyle>");
            sb.AppendLine("              <BorderColor>0, 0, 0</BorderColor>");
            sb.AppendLine("              <BorderWidth>1</BorderWidth>");
            sb.AppendLine("              <EdgeStyle>Solid</EdgeStyle>");
            sb.AppendLine("              <Flashing>None</Flashing>");
            sb.AppendLine($"              <Height>{H}</Height>");
            sb.AppendLine($"              <Left>{L}</Left>");
            sb.AppendLine($"              <ObjectName>{escName}</ObjectName>");
            sb.AppendLine("              <TabIndex>-1</TabIndex>");
            sb.AppendLine($"              <Top>{T}</Top>");
            sb.AppendLine("              <UseDesignColorSchema>false</UseDesignColorSchema>");
            sb.AppendLine($"              <Width>{W}</Width>");
            sb.AppendLine("            </AttributeList>");
            sb.AppendLine("            <ObjectList>");
            // 默认曲线占位（Hmi.Screen.TrendCurve）
            var curveId = GenerateHexId();
            sb.AppendLine($"              <Hmi.Screen.TrendCurve ID=\"{curveId}\" CompositionName=\"TrendCurves\">");
            sb.AppendLine("                <AttributeList>");
            sb.AppendLine("                  <Color>255, 0, 0</Color>");
            sb.AppendLine("                  <CurveType>Line</CurveType>");
            sb.AppendLine("                  <LineWidth>1</LineWidth>");
            sb.AppendLine("                  <Name>曲线_1</Name>");
            sb.AppendLine("                  <Visible>true</Visible>");
            sb.AppendLine("                </AttributeList>");
            sb.AppendLine("              </Hmi.Screen.TrendCurve>");
            sb.AppendLine("            </ObjectList>");
            sb.AppendLine("          </Hmi.Screen.TrendView>");
            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────
        // 任务 4: BindTrendViewTag — HMI 趋势曲线变量绑定
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 为 HMI 趋势图绑定变量到指定曲线。
        /// 支持两种 HMI 类型：
        ///   - Unified HMI：通过 HmiTrendControl.TrendAreas[0].Trends[curveIndex].DataSourceY.Source = tagName
        ///   - Classic HMI：通过 XML 往返方式修改 TrendCurve 的 Tag 绑定属性
        /// </summary>
        /// <param name="screenName">画面名称（必填）</param>
        /// <param name="trendItemName">趋势图画面项名称（必填）</param>
        /// <param name="tagName">要绑定的 HMI 变量名（必填）</param>
        /// <param name="curveIndex">曲线索引（从 0 开始，必填）</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选）</param>
        /// <returns>JSON 结果字符串</returns>
        public string BindTrendViewTag(string screenName, string trendItemName, string tagName,
            int curveIndex, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(screenName))
                        return Err("参数 screenName 不能为空");
                    if (string.IsNullOrWhiteSpace(trendItemName))
                        return Err("参数 trendItemName 不能为空");
                    if (string.IsNullOrWhiteSpace(tagName))
                        return Err("参数 tagName 不能为空");
                    if (curveIndex < 0)
                        return Err("参数 curveIndex 不能为负数");

                    // 优先尝试 Unified HMI（强类型 API）
                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi != null)
                    {
                        return BindTrendViewTagUnified(unifiedHmi, screenName, trendItemName, tagName, curveIndex);
                    }

                    // 回退到经典 HMI（XML 往返方式）
                    try
                    {
                        var hmi = RequireClassicHmi(hmiDeviceName);
                        return BindTrendViewTagClassic(hmi, screenName, trendItemName, tagName, curveIndex);
                    }
                    catch (Exception ex)
                    {
                        return Err($"未找到 Unified HMI 也未找到经典 HMI: {ex.Message}");
                    }
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// Unified HMI 趋势曲线变量绑定（强类型 API）。
        /// 路径：HmiScreen → ScreenItems（找 HmiTrendControl）→ TrendAreas[0] → Trends[curveIndex] → DataSourceY.Source = tagName
        /// 若 curveIndex 超出已有 Trends 数量，会自动 Create 新的 HmiTrendPart。
        /// </summary>
        private string BindTrendViewTagUnified(object? unifiedHmi, string screenName,
            string trendItemName, string tagName, int curveIndex)
        {
            object? screen = null;
            try
            {
                var screens = GetHmiProperty(unifiedHmi, "Screens");
                screen = TryInvoke(screens, "Find", new object[] { screenName });
            }
            catch { }
            if (screen == null) return Err($"未找到 Unified HMI 画面: {screenName}");

            // 在画面项中查找 HmiTrendControl
            object? trendControl = null;
            var screenItems = GetHmiProperty(screen, "ScreenItems") as System.Collections.IEnumerable;
            if (screenItems != null)
            {
                foreach (var item in screenItems)
                {
                    try
                    {
                        var n = item.GetType().GetProperty("Name")?.GetValue(item)?.ToString();
                        if (n != null && n.Equals(trendItemName, StringComparison.OrdinalIgnoreCase))
                        {
                            trendControl = item;
                            break;
                        }
                    }
                    catch { }
                }
            }
            if (trendControl == null)
                return Err($"未找到趋势图画面项: {trendItemName}（画面: {screenName}）");

            // 获取 TrendAreas 集合
            var trendAreasProp = trendControl.GetType().GetProperty("TrendAreas");
            if (trendAreasProp == null)
                return Err($"画面项 {trendItemName} 无 TrendAreas 属性（类型: {trendControl.GetType().Name}）");

            var trendAreas = trendAreasProp.GetValue(trendControl) as System.Collections.IEnumerable;
            if (trendAreas == null)
                return Err($"画面项 {trendItemName} 的 TrendAreas 集合为空");

            // 获取第一个 TrendArea（若没有则创建）
            object? trendArea = null;
            int areaIdx = 0;
            foreach (var area in trendAreas)
            {
                if (areaIdx == 0) { trendArea = area; break; }
                areaIdx++;
            }
            if (trendArea == null)
            {
                // 创建默认 TrendArea
                var createArea = trendAreas.GetType().GetMethod("Create", Type.EmptyTypes);
                if (createArea == null)
                    return Err("TrendAreas 集合无 Create() 方法，无法创建默认趋势区域");
                try { trendArea = createArea.Invoke(trendAreas, null); }
                catch (TargetInvocationException tie)
                {
                    return Err($"TrendAreas.Create() 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
            }
            if (trendArea == null) return Err("无法获取或创建 TrendArea");

            // 获取 Trends 集合
            var trendsProp = trendArea.GetType().GetProperty("Trends");
            if (trendsProp == null)
                return Err("TrendArea 无 Trends 属性");

            var trends = trendsProp.GetValue(trendArea) as System.Collections.IEnumerable;
            if (trends == null)
                return Err("TrendArea.Trends 集合为空");

            // 找到或创建指定 curveIndex 的 HmiTrendPart
            var trendsList = new List<object>();
            foreach (var t in trends) trendsList.Add(t);

            object? trendPart = null;
            if (curveIndex < trendsList.Count)
            {
                trendPart = trendsList[curveIndex];
            }
            else
            {
                // 需要创建到 curveIndex 位置（依次创建直至达到索引）
                var createMethod = trends.GetType().GetMethod("Create", Type.EmptyTypes);
                if (createMethod == null)
                    return Err($"Trends 集合无 Create() 方法，无法创建曲线索引 {curveIndex}");

                while (trendsList.Count <= curveIndex)
                {
                    object? newPart;
                    try { newPart = createMethod.Invoke(trends, null); }
                    catch (TargetInvocationException tie)
                    {
                        return Err($"Trends.Create() 失败（索引 {trendsList.Count}）: {tie.InnerException?.Message ?? tie.Message}");
                    }
                    if (newPart == null) return Err($"Trends.Create() 返回 null（索引 {trendsList.Count}）");
                    trendsList.Add(newPart);
                }
                trendPart = trendsList[curveIndex];
            }
            if (trendPart == null) return Err($"无法获取曲线索引 {curveIndex}");

            // 设置 DataSourceY.Source = tagName
            var dataSourceYProp = trendPart.GetType().GetProperty("DataSourceY");
            if (dataSourceYProp == null)
                return Err("HmiTrendPart 无 DataSourceY 属性");

            var dataSourceY = dataSourceYProp.GetValue(trendPart);
            if (dataSourceY == null)
            {
                // DataSourceY 可能为只读引用，若为 null 则无法继续
                return Err("HmiTrendPart.DataSourceY 为 null 且不可实例化");
            }

            var sourceProp = dataSourceY.GetType().GetProperty("Source");
            if (sourceProp == null || !sourceProp.CanWrite)
                return Err("DataSourceY.Source 属性不存在或不可写");

            string? oldValue = null;
            try { oldValue = sourceProp.GetValue(dataSourceY)?.ToString(); } catch { }

            try
            {
                sourceProp.SetValue(dataSourceY, tagName);
            }
            catch (Exception ex)
            {
                return Err($"设置 DataSourceY.Source 失败: {ex.Message}");
            }

            // 可选：设置 DisplayName 为 tagName（便于在图例中显示）
            try
            {
                var displayNameProp = trendPart.GetType().GetProperty("DisplayName");
                if (displayNameProp != null && displayNameProp.CanWrite)
                {
                    var displayName = displayNameProp.GetValue(trendPart);
                    // DisplayName 是 MultilingualText，设置其默认文本
                    var setTextMethod = displayName?.GetType().GetMethod("SetText",
                        new[] { typeof(string), typeof(string) });
                    setTextMethod?.Invoke(displayName, new object[] { "zh-CN", tagName });
                }
            }
            catch { /* 非关键，忽略 */ }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                message = $"已绑定趋势曲线: {screenName}.{trendItemName}[{curveIndex}] → {tagName}",
                screenName,
                trendItemName,
                tagName,
                curveIndex,
                hmiKind = "Unified",
                oldValue = oldValue ?? "(空)"
            }, Formatting.Indented);
        }

        /// <summary>
        /// 经典 HMI 趋势曲线变量绑定（XML 往返方式）。
        /// 流程：导出画面 XML → 找到指定 TrendView → 找到/创建指定 curveIndex 的 TrendCurve →
        /// 设置其 Tag Link → 重新导入。
        /// </summary>
        private string BindTrendViewTagClassic(HmiTarget hmi, string screenName,
            string trendItemName, string tagName, int curveIndex)
        {
            var screen = FindScreenRecursive(hmi.ScreenFolder, screenName);
            if (screen == null) return Err($"未找到画面: {screenName}");

            var tempFile = Path.Combine(Path.GetTempPath(), $"tia_trend_bind_{Guid.NewGuid():N}.xml");
            var exportFile = Path.Combine(Path.GetTempPath(), $"tia_trend_bind_exp_{Guid.NewGuid():N}.xml");
            try
            {
                screen.Export(new FileInfo(exportFile), ExportOptions.WithDefaults);
                var xml = File.ReadAllText(exportFile, Encoding.UTF8);

                // 用 XmlDocument 修改：定位 Hmi.Screen.TrendView 节点（按 ObjectName）
                var doc = new System.Xml.XmlDocument();
                doc.PreserveWhitespace = true;
                doc.LoadXml(xml);

                var trendViewNodes = doc.SelectNodes("//Hmi.Screen.TrendView");
                if (trendViewNodes == null || trendViewNodes.Count == 0)
                    return Err($"画面 {screenName} 中未找到任何 TrendView 画面项");

                System.Xml.XmlNode? targetTrendView = null;
                foreach (System.Xml.XmlNode tvNode in trendViewNodes)
                {
                    var nameNode = tvNode.SelectSingleNode("AttributeList/ObjectName");
                    if (nameNode != null && nameNode.InnerText == trendItemName)
                    {
                        targetTrendView = tvNode;
                        break;
                    }
                }
                if (targetTrendView == null)
                    return Err($"画面 {screenName} 中未找到名为 {trendItemName} 的 TrendView 画面项");

                // 找到 TrendCurves 子集合（CompositionName="TrendCurves"）
                var trendCurvesContainer = targetTrendView.SelectSingleNode("ObjectList/Hmi.Screen.TrendCurve/..")
                    ?? targetTrendView.SelectSingleNode("ObjectList");
                if (trendCurvesContainer == null)
                    return Err($"TrendView {trendItemName} 内无 ObjectList 节点");

                var curveNodes = targetTrendView.SelectNodes("ObjectList/Hmi.Screen.TrendCurve");
                var curveList = new List<System.Xml.XmlNode>();
                if (curveNodes != null)
                    foreach (System.Xml.XmlNode cn in curveNodes) curveList.Add(cn);

                // 若 curveIndex 超出现有曲线数，则创建占位曲线直至达到索引
                var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Xml.XmlNode idNode in doc.SelectNodes("//*[@ID]"))
                {
                    var idVal = idNode.Attributes?["ID"]?.Value;
                    if (!string.IsNullOrEmpty(idVal)) existingIds.Add(idVal);
                }

                while (curveList.Count <= curveIndex)
                {
                    var newCurve = doc.CreateElement("Hmi.Screen.TrendCurve");
                    var idAttr = doc.CreateAttribute("ID");
                    idAttr.Value = GetUniqueId(existingIds);
                    newCurve.Attributes.Append(idAttr);
                    var compAttr = doc.CreateAttribute("CompositionName");
                    compAttr.Value = "TrendCurves";
                    newCurve.Attributes.Append(compAttr);

                    var attrList = doc.CreateElement("AttributeList");
                    var colorElem = doc.CreateElement("Color"); colorElem.InnerText = "0, 0, 255";
                    var curveTypeElem = doc.CreateElement("CurveType"); curveTypeElem.InnerText = "Line";
                    var lineWidthElem = doc.CreateElement("LineWidth"); lineWidthElem.InnerText = "1";
                    var nameElem = doc.CreateElement("Name"); nameElem.InnerText = $"曲线_{curveList.Count + 1}";
                    var visibleElem = doc.CreateElement("Visible"); visibleElem.InnerText = "true";
                    attrList.AppendChild(colorElem);
                    attrList.AppendChild(curveTypeElem);
                    attrList.AppendChild(lineWidthElem);
                    attrList.AppendChild(nameElem);
                    attrList.AppendChild(visibleElem);
                    newCurve.AppendChild(attrList);

                    targetTrendView.SelectSingleNode("ObjectList")?.AppendChild(newCurve);
                    existingIds.Add(idAttr.Value);
                    curveList.Add(newCurve);
                }

                // 在目标曲线上设置 Tag 链接（LinkList/Value）
                var targetCurve = curveList[curveIndex];
                // 确保 AttributeList 存在
                var curveAttrList = targetCurve.SelectSingleNode("AttributeList");
                if (curveAttrList == null)
                {
                    curveAttrList = doc.CreateElement("AttributeList");
                    targetCurve.PrependChild(curveAttrList);
                }

                // 设置/更新 TagName 属性（经典 HMI 用 TagName 字符串引用变量）
                var tagNameNode = curveAttrList.SelectSingleNode("TagName") as System.Xml.XmlElement;
                if (tagNameNode == null)
                {
                    tagNameNode = doc.CreateElement("TagName");
                    curveAttrList.AppendChild(tagNameNode);
                }
                tagNameNode.InnerText = tagName;

                // 同时设置 LinkList/Value 引用（与导出 XML 结构一致）
                var existingLinkList = targetCurve.SelectSingleNode("LinkList");
                if (existingLinkList != null) targetCurve.RemoveChild(existingLinkList);

                var linkList = doc.CreateElement("LinkList");
                var valueTemplate = doc.CreateElement("Value");
                var targetAttr = doc.CreateAttribute("TargetID");
                targetAttr.Value = "@OpenLink";
                valueTemplate.Attributes.Append(targetAttr);
                var valueName = doc.CreateElement("Name");
                valueName.InnerText = tagName;
                valueTemplate.AppendChild(valueName);
                linkList.AppendChild(valueTemplate);
                targetCurve.AppendChild(linkList);

                // 写入临时文件并重新导入
                var bom = new UTF8Encoding(true);
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                using (var xw = System.Xml.XmlWriter.Create(fs, new System.Xml.XmlWriterSettings
                {
                    Encoding = bom,
                    Indent = true,
                    OmitXmlDeclaration = false
                }))
                {
                    doc.Save(xw);
                }

                hmi.ScreenFolder.Screens.Import(new FileInfo(tempFile), ImportOptions.Override);

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = $"已绑定趋势曲线: {screenName}.{trendItemName}[{curveIndex}] → {tagName}（XML 往返）",
                    screenName,
                    trendItemName,
                    tagName,
                    curveIndex,
                    hmiKind = "Classic"
                }, Formatting.Indented);
            }
            finally
            {
                TryDelete3(tempFile);
                TryDelete3(exportFile);
            }
        }

        // ────────────────────────────────────────────────────────────
        // 任务 3 扩展: AddDataLogVariable — 为数据日志添加变量绑定
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 为 HMI 数据日志添加变量绑定。
        /// Unified HMI：通过 HmiTag.LoggingTags.Create(loggingTagName) 创建日志绑定，
        ///   并将 LoggingTag.DataLog 设置为指定数据日志名（关联到具体日志）。
        /// Classic HMI：尝试反射在 DataLog 对象上找 LogTags/Tags 集合，无则提示需 GUI 配置。
        /// </summary>
        /// <param name="dataLogName">数据日志名称（必填）</param>
        /// <param name="tagName">要绑定的 HMI 变量名（必填）</param>
        /// <param name="hmiDeviceName">HMI 设备名（可选）</param>
        /// <returns>JSON 结果字符串</returns>
        public string AddDataLogVariable(string dataLogName, string tagName, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dataLogName))
                        return Err("参数 dataLogName 不能为空");
                    if (string.IsNullOrWhiteSpace(tagName))
                        return Err("参数 tagName 不能为空");

                    // 优先 Unified HMI（DataLogs 在 HmiSoftware 上强类型公开）
                    var unifiedHmi = FindUnifiedHmiSoftware(hmiDeviceName);
                    if (unifiedHmi != null)
                    {
                        return AddDataLogVariableUnified(unifiedHmi, dataLogName, tagName);
                    }

                    // 经典 HMI 回退：DataLogs 不在 HmiTarget 公开 API 上，提示用户
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error = "经典 HMI 的 DataLogs 集合未在 Openness API 公开，无法程序化添加变量",
                        dataLogName,
                        tagName,
                        note = "请在博途 GUI 中打开数据日志并手动添加变量；或使用 Unified HMI 设备"
                    }, Formatting.Indented);
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        /// <summary>
        /// Unified HMI 数据日志变量绑定。
        /// 1. 在 HmiSoftware.DataLogs 中 Find(dataLogName) 验证数据日志存在
        /// 2. 在所有 TagTables 中 Find(tagName) 找到目标 HmiTag
        /// 3. 调用 tag.LoggingTags.Create(loggingTagName) 创建日志绑定
        /// 4. 设置 LoggingTag.DataLog = dataLogName（关联到具体数据日志）
        /// </summary>
        private string AddDataLogVariableUnified(object? unifiedHmi, string dataLogName, string tagName)
        {
            // 验证数据日志存在
            object? dataLog = null;
            try
            {
                var dataLogs = GetHmiProperty(unifiedHmi, "DataLogs");
                dataLog = TryInvoke(dataLogs, "Find", new object[] { dataLogName });
            }
            catch { }
            if (dataLog == null)
                return Err($"未找到数据日志: {dataLogName}");

            // 查找 HMI 变量
            var tag = FindUnifiedHmiTag(unifiedHmi, tagName);
            if (tag == null)
                return Err($"未找到 Unified HMI 变量: {tagName}");

            // 获取 LoggingTags 集合
            var loggingTags = GetHmiProperty(tag, "LoggingTags");
            if (loggingTags == null)
                return Err($"HMI 变量 {tagName} 的 LoggingTags 集合为空");

            // 日志绑定名：默认用 "变量名_数据日志名" 形式，便于追溯
            var loggingTagName = $"{tagName}_{dataLogName}";

            // 同名已存在则更新其 DataLog 关联
            object? existingLogTag = null;
            try { existingLogTag = TryInvoke(loggingTags, "Find", new object[] { loggingTagName }); }
            catch { }

            object? logTag;
            bool created = false;
            if (existingLogTag != null)
            {
                logTag = existingLogTag;
            }
            else
            {
                try { logTag = TryInvoke(loggingTags, "Create", new object[] { loggingTagName }); created = true; }
                catch (TargetInvocationException tie)
                {
                    return Err($"LoggingTags.Create(\"{loggingTagName}\") 失败: {tie.InnerException?.Message ?? tie.Message}");
                }
            }
            if (logTag == null)
                return Err($"LoggingTags.Create(\"{loggingTagName}\") 返回 null");

            // 设置 DataLog 关联属性（字符串型，指向数据日志名）
            string? oldDataLog = null;
            try { oldDataLog = GetHmiProperty(logTag, "DataLog")?.ToString(); } catch { }
            try
            {
                var dataLogProp = logTag.GetType().GetProperty("DataLog");
                dataLogProp?.SetValue(logTag, dataLogName);
            }
            catch (Exception ex)
            {
                return Err($"设置 LoggingTag.DataLog 失败: {ex.Message}" +
                    $"（已创建日志绑定 {GetHmiProperty(logTag, "Name")}，但未关联到数据日志 {dataLogName}）");
            }

            // 设置默认 LoggingMode（如有需要）
            try
            {
                // 若 LoggingMode 可写且当前为默认值，则设置为 OnChange（变化时记录）
                var modeProp = logTag.GetType().GetProperty("LoggingMode");
                if (modeProp != null && modeProp.CanWrite)
                {
                    // HmiLoggingMode 枚举：OnChange = 0（默认）, OnCycle = 1 等
                    // 不强制设置，保留默认即可
                }
            }
            catch { /* 非关键 */ }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                message = created
                    ? $"已为变量 {tagName} 创建日志绑定 {loggingTagName} 并关联到数据日志 {dataLogName}"
                    : $"日志绑定 {loggingTagName} 已存在，已更新关联到数据日志 {dataLogName}",
                dataLogName,
                tagName,
                loggingTagName = GetHmiProperty(logTag, "Name"),
                hmiKind = "Unified",
                oldDataLog = oldDataLog ?? "(空)",
                newDataLog = dataLogName
            }, Formatting.Indented);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
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
using Siemens.Engineering.SW;
using TiaMcpServer.Xml;

namespace TiaMcpServer
{
    /// <summary>
    /// HMI 快速配置工具集（任务 5.5 新增）：
    /// 1. SetupNetworkAndHmiConnection — 一键配置子网、IP、HMI↔PLC连接
    /// 2. ImportHmiTagsAbsolute — 批量导入 Absolute 地址模式的 HMI 标签（直接绑 PLC 绝对地址）
    /// 3. CreateHmiScreenFromSpec — 按 JSON 规格参数化生成 HMI 画面
    /// </summary>
    public partial class PortalService
    {
        // ────────────────────────────────────────────────────────────
        // 工具 1：配置网络和 HMI 连接
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 一键配置 PROFINET 子网、设备 IP、HMI↔PLC 连接。
        /// 所有参数都有合理默认值，可直接调用。
        /// </summary>
        public string SetupNetworkAndHmiConnection(
            string? subnetName = null,
            string? plcDeviceName = null,
            string? hmiDeviceName = null,
            string? plcIp = null,
            string? hmiIp = null,
            string? subnetMask = null,
            string? connectionName = null,
            string? connectionDriver = null,
            string? connectionTemplateFilePath = null,
            string? connectionMode = null)
        {
            lock (_lock)
            {
                try
                {
                    RequireProject();
                    var steps = new List<object>();
                    var failedSteps = new List<string>();

                    var hmi = RequireClassicHmi(hmiDeviceName);
                    var hmiDev = FindDeviceForHmiTarget(hmi)
                                 ?? (!string.IsNullOrWhiteSpace(hmiDeviceName) ? FindDeviceByName(hmiDeviceName!) : null);
                    if (hmiDev == null) return Err("未能定位 Classic HMI 对应的 Device，无法配置网络接口");

                    PlcSoftware? plcSw = null;
                    Device? plcDev = null;
                    if (!string.IsNullOrWhiteSpace(plcDeviceName))
                    {
                        plcSw = FindPlcByName(plcDeviceName!);
                        if (plcSw != null) plcDev = FindDeviceForPlcObject(plcSw);
                        plcDev ??= FindDeviceByName(plcDeviceName!);
                    }
                    if (plcDev == null)
                    {
                        plcSw = GetPlcSoftwareList().FirstOrDefault();
                        if (plcSw != null) plcDev = FindDeviceForPlcObject(plcSw);
                    }
                    if (plcDev == null) return Err("未找到 PLC 设备，无法创建 PLC-HMI 通信链路");

                    var sName = subnetName ?? "PN/IE_1";
                    var sMask = subnetMask ?? "255.255.255.0";
                    var pIp = plcIp ?? "192.168.0.1";
                    var hIp = hmiIp ?? "192.168.0.2";
                    var cName = connectionName ?? "HMI_Connection";
                    var driver = connectionDriver ?? "SIMATIC S7 1200";

                    var subnetExists = _project!.Subnets.Any(s =>
                        string.Equals(s.Name, sName, StringComparison.OrdinalIgnoreCase));
                    if (!subnetExists)
                    {
                        Exception? lastError = null;
                        // ★V17校准★ PROFINET 优先：Ethernet 子网可能无法承载 PN 设备接入（ConnectToSubnet 失败）
                        foreach (var typeId in new[] { "System:Subnet.PROFINET", "System:Subnet.Ethernet", "System:Subnet.IE" })
                        {
                            try
                            {
                                _project.Subnets.Create(typeId, sName);
                                lastError = null;
                                steps.Add(new { step = "create_subnet", success = true, subnet = sName, typeId });
                                break;
                            }
                            catch (Exception ex) { lastError = ex; }
                        }
                        if (lastError != null) return Err("创建子网失败: " + lastError.Message);
                    }
                    else steps.Add(new { step = "create_subnet", success = true, subnet = sName, skipped = true });

                    void RecordJsonStep(string stepName, string raw)
                    {
                        if (TryReadSuccess(raw, out var ok, out var payload))
                        {
                            steps.Add(new { step = stepName, success = ok, result = payload });
                            if (!ok) failedSteps.Add(stepName);
                        }
                        else
                        {
                            steps.Add(new { step = stepName, success = false, rawResult = raw, error = "子工具返回值不是标准 JSON success 结果" });
                            failedSteps.Add(stepName);
                        }
                    }

                    // ★修复★ 网络配置（子网连接/设置 IP）属在线操作，必须进入 SafeOnlineExecutor 授权范围，
                    // 否则 SetDeviceIp 的内部安全链会拒绝（B 版原缺陷：setup 工具被自家安全链拦截）。
                    using (SafeOnlineExecutor.EnterAuthorizedScope(new ToolMetadata { TouchesOnlineDevice = true }))
                    {
                        RecordJsonStep("connect_plc_to_subnet", ConnectToSubnet(plcDev.Name, sName));
                        RecordJsonStep("connect_hmi_to_subnet", ConnectToSubnet(hmiDev.Name, sName));
                        RecordJsonStep("set_plc_ip", SetDeviceIp(plcDev.Name, plcDev.Name, pIp, sMask));
                        RecordJsonStep("set_hmi_ip", SetDeviceIp("", hmiDev.Name, hIp, sMask));
                    }

                    var mode = string.IsNullOrWhiteSpace(connectionMode) ? "integrated" : connectionMode!.Trim();
                    if (mode.Equals("nonIntegrated", StringComparison.OrdinalIgnoreCase))
                    {
                        var connResult = CreateHmiConnection(
                            cName, null, hIp, pIp, sMask, sName, driver,
                            connectionTemplateFilePath, hmiDev.Name, "nonIntegrated");
                        RecordJsonStep("create_non_integrated_hmi_connection", connResult);
                    }
                    else
                    {
                        // 同一 TIA 项目内的 Classic HMI 集成连接由网络组态产生：
                        // PLC PN 接口与 HMI Ethernet 接口接入同一 PN/IE 子网并配置同网段 IP 后，
                        // TIA 生成默认连接名。这里不得额外导入/创建第二条连接 XML。
                        var networkReady = failedSteps.Count == 0;
                        // ★V17校准★ 尝试枚举 TIA 已生成的集成连接名，便于后续 import_hmi_tags_absolute 使用
                        var discoveredConnectionName = "";
                        if (networkReady)
                        {
                            try { discoveredConnectionName = hmi.Connections.FirstOrDefault()?.Name ?? ""; }
                            catch { }
                        }
                        steps.Add(new
                        {
                            step = "integrated_connection_by_shared_subnet",
                            success = networkReady,
                            subnet = sName,
                            plcDevice = plcDev.Name,
                            hmiDevice = hmiDev.Name,
                            connectionName = !string.IsNullOrEmpty(discoveredConnectionName) ? discoveredConnectionName : "generated_by_tia",
                            note = "请在后续 compile_all_hardware / HMI 编译结果中验证连接；若后续标签导入提示找不到连接，请在 TIA 画面编辑器连接列表确认连接名后显式传入 connectionName"
                        });
                        if (!networkReady) failedSteps.Add("integrated_connection_by_shared_subnet");
                    }

                    var success = failedSteps.Count == 0;
                    return JsonConvert.SerializeObject(new
                    {
                        success,
                        message = success
                            ? (mode.Equals("nonIntegrated", StringComparison.OrdinalIgnoreCase)
                                ? "PLC、HMI 网络及非集成连接配置已完成；仍需执行全量编译验证"
                                : "PLC 与 HMI 已接入同一 PN/IE 子网并配置 IP；TIA 将生成默认集成连接名，仍需执行全量编译验证")
                            : "网络或 IP 配置存在失败步骤，未达到集成连接生成条件",
                        subnetName = sName,
                        plcDevice = plcDev.Name,
                        hmiDevice = hmiDev.Name,
                        plcIp = pIp,
                        hmiIp = hIp,
                        connectionName = mode.Equals("nonIntegrated", StringComparison.OrdinalIgnoreCase) ? cName : "generated_by_tia",
                        connectionMode = mode,
                        connectionTemplateFilePath = mode.Equals("nonIntegrated", StringComparison.OrdinalIgnoreCase) ? connectionTemplateFilePath : null,
                        requiresCompileVerification = true,
                        failedSteps,
                        steps
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex) { return Err("配置网络和HMI连接失败: " + ex.Message); }
            }
        }

        /// <summary>
        /// 生成最小化的 HMI 连接 XML（Name+Driver+InterfaceType=Ethernet+Online）
        /// ★修复★ V19 的 Hmi.Communication.Connection 必须带 InterfaceType：
        ///   缺属性 → "The attribute 'InterfaceType' is missing"；
        ///   值 PN/IE → "The value 'PN/IE' of attribute 'InterfaceType' is incompatible with other attributes"（实测，且曾致进程退出）。
        /// 实测验证的正确组合是 InterfaceType=Ethernet + Online=true（参考 HMI-SOURCE 版注释记录）。
        /// </summary>
        private string GenerateHmiConnectionXmlMinimal(string connectionName, string engVersion, string driver)
        {
            var escapedName = System.Security.SecurityElement.Escape(connectionName);
            var sb = new StringBuilder();
            sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
            sb.AppendLine("<Document>");
            sb.AppendLine($"  <Engineering version=\"{engVersion}\" />");
            sb.AppendLine("  <DocumentInfo>");
            sb.AppendLine("    <Created>2026-06-30T00:00:00.0000000Z</Created>");
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
            sb.AppendLine($"      <Name>{escapedName}</Name>");
            sb.AppendLine($"      <Driver>{driver}</Driver>");
            sb.AppendLine("      <InterfaceType>Ethernet</InterfaceType>");
            sb.AppendLine("      <Online>true</Online>");
            sb.AppendLine("    </AttributeList>");
            sb.AppendLine("    <ObjectList>");
            // AreaPointers（连接创建必需的结构）
            string[] areaPtrTypes = { "Coordination", "DateTime", "DateTimereturn", "EventId",
                "FieldbusReadmailbox", "FieldbusWritemailbox", "HmiIdentification", "Jobmailbox",
                "ProjectId", "ScreenNumber", "TagManagement" };
            int nextId = 1;
            string Id() => (nextId++).ToString("X");
            for (int i = 0; i < areaPtrTypes.Length; i++)
            {
                var c1 = Id(); var c2 = Id(); var c3 = Id();
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
            return sb.ToString();
        }

        // ────────────────────────────────────────────────────────────
        // 工具 2：导入 HMI 标签（Absolute 地址模式）
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 批量导入 HMI 标签（Absolute 地址模式，直接绑定 PLC 绝对地址，无需 PLC 符号变量）
        /// </summary>
        /// <param name="tagsJson">JSON 数组，每项包含 {name, dataType, address, [length], [connection], [acquisitionCycle]}</param>
        /// <param name="connectionName">连接名（默认自动检测）</param>
        /// <param name="targetTableName">目标变量表名（默认使用默认变量表）</param>
        public string ImportHmiTagsAbsolute(string tagsJson, string? connectionName = null, string? targetTableName = null)
        {
            lock (_lock)
            {
                // ★崩溃修复★ 提升到方法级作用域，供失败 catch 中的变量表恢复使用
                string existingXml = "";
                Siemens.Engineering.Hmi.HmiTarget? hmiRef = null;
                try
                {
                    RequireProject();
                    var hmi = RequireClassicHmi();
                    hmiRef = hmi;

                    var tagsArr = JArray.Parse(tagsJson);
                    if (tagsArr.Count == 0)
                        return Err("tagsJson 为空数组");

                    // 解析标签定义
                    var tagDefs = new List<HmiAbsoluteTagDef>();
                    foreach (var t in tagsArr)
                    {
                        var name = t["name"]?.Value<string>();
                        var dataType = t["dataType"]?.Value<string>() ?? "Bool";
                        var address = t["address"]?.Value<string>();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(address))
                            return Err($"标签定义缺少 name 或 address: {t}");
                        // ★V17校准★ Real 此前被拒绝是因为 Coding 固定输出 Binary（Real 必须用 IEEE754）。
                        // 现按类型输出正确 Coding；若目标 TIA 仍拒绝 Real 导入，请改用 Int+kPa 镜像方案（FC/FB 做换算）。
                        var supportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "Bool", "Byte", "Word", "Int", "UInt", "DWord", "DInt", "UDInt", "Real" };
                        if (!supportedTypes.Contains(dataType))
                            return Err($"标签 '{name}' 的 dataType '{dataType}' 当前未验证。Absolute 模式支持: {string.Join(", ", supportedTypes)}");
                        if (!System.Text.RegularExpressions.Regex.IsMatch(address!,
                                @"^%(I|Q|M)(X|B|W|D)?\d+(\.\d+)?$|^%?DB\d+\.DB(X|B|W|D)\d+(\.\d+)?$",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            return Err($"标签 '{name}' 的绝对地址格式无效: {address}。示例: %M0.0、%MW10、DB1.DBW0");
                        if (!IsAbsoluteAddressCompatible(dataType, address!))
                            return Err($"标签 '{name}' 的 dataType '{dataType}' 与地址 '{address}' 宽度不匹配。Bool 必须使用位地址；Byte 使用 B；Word/Int/UInt 使用 W；DWord/DInt/UDInt/Real 使用 D");
                        tagDefs.Add(new HmiAbsoluteTagDef
                        {
                            name = name!,
                            dataType = dataType,
                            address = address!,
                            length = t["length"]?.Value<int?>(),
                            connection = t["connection"]?.Value<string>(),
                            acquisitionCycle = t["acquisitionCycle"]?.Value<string>() ?? "1 s",
                        });
                    }

                    // 确定目标变量表
                    TagTable? targetTable = null;
                    if (!string.IsNullOrEmpty(targetTableName))
                    {
                        targetTable = FindTagTableRecursive(hmi.TagFolder, targetTableName!);
                        if (targetTable == null)
                            return Err($"未找到目标变量表: {targetTableName}");
                    }
                    var exportTable = targetTable ?? hmi.TagFolder.DefaultTagTable;

                    // 解析连接名
                    var connName = connectionName ?? "";
                    if (string.IsNullOrEmpty(connName))
                    {
                        try
                        {
                            var firstConn = hmi.Connections.FirstOrDefault();
                            if (firstConn != null) connName = firstConn.Name;
                        }
                        catch { }
                    }
                    if (string.IsNullOrEmpty(connName))
                    {
                        // 从现有变量表导出提取
                        try
                        {
                            var tmpExp = Path.Combine(Path.GetTempPath(), $"tia_tbl_{Guid.NewGuid():N}.xml");
                            exportTable.Export(new FileInfo(tmpExp), ExportOptions.WithDefaults);
                            var tdoc = XDocument.Load(tmpExp);
                            TryDelete3(tmpExp);
                            var tns = tdoc.Root?.Name.Namespace ?? XNamespace.None;
                            foreach (var tagElem in tdoc.Descendants(tns + "Hmi.Tag.Tag"))
                            {
                                var ce = tagElem.Element(tns + "LinkList")?.Element(tns + "Connection");
                                var cn = ce?.Value?.Trim();
                                if (!string.IsNullOrEmpty(cn)) { connName = cn!; break; }
                            }
                        }
                        catch { }
                    }
                    // 从标签定义中取连接名
                    if (string.IsNullOrEmpty(connName))
                    {
                        var ec = tagDefs.FirstOrDefault(td => !string.IsNullOrEmpty(td.connection))?.connection;
                        if (!string.IsNullOrEmpty(ec)) connName = ec!;
                    }
                    if (string.IsNullOrEmpty(connName))
                        return Err("未找到HMI连接，请先调用 setup_network_and_hmi_connection 创建连接，或指定 connectionName 参数。");

                    // 连接存在性校验（★V17校准★）：
                    // 集成连接（integrated 模式，由 TIA 网络组态生成）不出现在 hmi.Connections 集合中，
                    // 因此：① API 能枚举到连接时，以枚举结果 + 导出表提取到的连接名做严格校验；
                    // ② 枚举为空（新项目/纯集成连接）时跳过严格校验、交由 TIA 导入验证
                    //    （与 BatchCreateHmiTags 的放行行为一致），避免误报"连接不存在"。
                    var enumeratedConnectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        foreach (var c in hmi.Connections)
                        {
                            var n = c.Name;
                            if (!string.IsNullOrEmpty(n)) enumeratedConnectionNames.Add(n);
                        }
                    }
                    catch { }
                    var knownConnectionNames = new HashSet<string>(enumeratedConnectionNames, StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(connName)) knownConnectionNames.Add(connName);
                    foreach (var td in tagDefs)
                        if (!string.IsNullOrWhiteSpace(td.connection)) knownConnectionNames.Add(td.connection!);
                    var requestedConnectionNames = tagDefs
                        .Select(td => string.IsNullOrWhiteSpace(td.connection) ? connName : td.connection!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var missingConnections = requestedConnectionNames
                        .Where(c => enumeratedConnectionNames.Count > 0 && !knownConnectionNames.Contains(c)).ToList();
                    if (missingConnections.Count > 0)
                        return Err("以下 HMI 连接不存在，未执行变量导入: " + string.Join(", ", missingConnections)
                                   + "。现有连接: " + string.Join(", ", enumeratedConnectionNames));

                    // 检查重复标签名
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
                    catch { }

                    var newTagDefs = tagDefs.Where(td => !existingHmiTags.Contains(td.name)).ToList();
                    if (newTagDefs.Count == 0)
                        return Ok($"所有 {tagDefs.Count} 个标签已存在，无需导入。");

                    var skipped = tagDefs.Count - newTagDefs.Count;

                    // 导出现有变量表（同时作为事务保护备份）
                    var tempExport = Path.Combine(Path.GetTempPath(), $"tia_tagexp_{Guid.NewGuid():N}.xml");
                    try
                    {
                        exportTable.Export(new FileInfo(tempExport), ExportOptions.WithDefaults);
                        existingXml = File.ReadAllText(tempExport, Encoding.UTF8);
                    }
                    finally { TryDelete3(tempExport); }

                    var existingDoc = XDocument.Parse(existingXml);
                    var ens = existingDoc.Root?.Name.Namespace ?? XNamespace.None;

                    // ★V17校准★ 语言从现有变量表导出中提取（项目参考语言可能不是 zh-CN），无则回退 zh-CN
                    var culture = "zh-CN";
                    try
                    {
                        var cultureElem = existingDoc.Descendants(ens + "Culture").FirstOrDefault();
                        if (cultureElem != null && !string.IsNullOrWhiteSpace(cultureElem.Value))
                            culture = cultureElem.Value.Trim();
                    }
                    catch { }

                    // 找最大ID
                    int maxId = 0;
                    foreach (var elem in existingDoc.Descendants())
                    {
                        var idAttr = elem.Attribute("ID");
                        if (idAttr != null && int.TryParse(idAttr.Value, System.Globalization.NumberStyles.HexNumber, null, out var idVal))
                            if (idVal > maxId) maxId = idVal;
                    }

                    // 找到 ObjectList。
                    // ★空表崩溃修复★ 空变量表导出的 XML 可能既无 <Hmi.Tag.TagTable> 也无 <ObjectList>：
                    // 旧逻辑把 <ObjectList> 挂到 <Document> 根节点产生非法 XML，导入时博途崩溃。
                    // 现在找不到 TagTable 时重建最小 TagTable 结构，找不到 ObjectList 时在 TagTable 下新建。
                    XElement rootTagTable;
                    var rootIsTagTable = existingDoc.Root?.Name == ens + "Hmi.Tag.TagTable";
                    var foundTagTable = existingDoc.Descendants(ens + "Hmi.Tag.TagTable").FirstOrDefault();
                    if (rootIsTagTable) rootTagTable = existingDoc.Root!;
                    else if (foundTagTable != null) rootTagTable = foundTagTable;
                    else
                    {
                        rootTagTable = new XElement(ens + "Hmi.Tag.TagTable", new XAttribute("ID", "0"));
                        rootTagTable.Add(new XElement(ens + "AttributeList",
                            new XElement(ens + "Name", exportTable.Name ?? "默认变量表")));
                        if (existingDoc.Root != null) existingDoc.Root.Add(rootTagTable);
                    }
                    var objectList = rootTagTable.Element(ens + "ObjectList");
                    if (objectList == null)
                    {
                        objectList = new XElement(ens + "ObjectList");
                        rootTagTable.Add(objectList);
                    }

                    // 为每个新标签生成 XML 片段并追加
                    int idCounter = maxId + 1;
                    var addedCount = 0;
                    foreach (var td in newTagDefs)
                    {
                        var length = td.length ?? GetHmiAbsoluteTagLength(td.dataType);
                        var tagConn = !string.IsNullOrEmpty(td.connection) ? td.connection : connName;
                        var tagXml = GenerateAbsoluteTagXml(td, tagConn!, idCounter, ens, culture);
                        idCounter += 6; // 每个标签占用6个ID（1个Tag + 2个MultilingualText × 2子ID）
                        objectList.Add(tagXml);
                        addedCount++;
                    }

                    // 保存合并后的 XML
                    var tempImport = Path.Combine(Path.GetTempPath(), $"tia_tagimp_{Guid.NewGuid():N}.xml");
                    try
                    {
                        var bom = new UTF8Encoding(true);
                        using (var sw = new StreamWriter(tempImport, false, bom))
                        using (var xw = System.Xml.XmlWriter.Create(sw, new System.Xml.XmlWriterSettings
                        {
                            Indent = true,
                            IndentChars = "  ",
                            Encoding = bom,
                            OmitXmlDeclaration = false
                        }))
                        {
                            existingDoc.Save(xw);
                        }

                        hmi.TagFolder.TagTables.Import(new FileInfo(tempImport), ImportOptions.Override);
                    }
                    finally { TryDelete3(tempImport); }

                    var namesAfterImport = new HashSet<string>(
                        GetAllTags(hmi.TagFolder).Select(t =>
                        {
                            try { return t.Name; } catch { return ""; }
                        }).Where(n => !string.IsNullOrWhiteSpace(n)),
                        StringComparer.OrdinalIgnoreCase);
                    var missingAfterImport = newTagDefs.Select(td => td.name)
                        .Where(n => !namesAfterImport.Contains(n)).ToList();
                    if (missingAfterImport.Count > 0)
                        return Err("HMI 标签导入调用已返回，但以下标签未在项目中找到: " + string.Join(", ", missingAfterImport));

                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = $"成功导入 {addedCount} 个HMI标签（Absolute地址模式）" + (skipped > 0 ? $"，跳过 {skipped} 个已存在标签" : ""),
                        connection = connName,
                        targetTable = targetTable?.Name ?? "默认变量表",
                        tags = newTagDefs.Select(td => new { td.name, td.dataType, td.address }),
                        verified = true
                    }, Newtonsoft.Json.Formatting.Indented);
                }
                catch (Exception ex)
                {
                    // ★崩溃修复★ 导入失败时用备份恢复原变量表，避免半状态残留引发后续崩溃
                    bool restoreFailed = false;
                    if (!string.IsNullOrEmpty(existingXml) && hmiRef != null)
                    {
                        try
                        {
                            var restoreFile = Path.Combine(Path.GetTempPath(), $"tia_tagrestore_{Guid.NewGuid():N}.xml");
                            try
                            {
                                File.WriteAllText(restoreFile, existingXml, new UTF8Encoding(true));
                                hmiRef.TagFolder.TagTables.Import(new FileInfo(restoreFile), ImportOptions.Override);
                            }
                            finally { TryDelete3(restoreFile); }
                        }
                        catch (Exception restoreEx)
                        {
                            restoreFailed = true;
                            Console.Error.WriteLine("[tia-mcp] HMI 变量表恢复失败: " + restoreEx.Message);
                        }
                    }
                    return Err("导入HMI标签失败: " + ex.Message
                               + (restoreFailed ? "；变量表恢复亦失败，项目可能处于半状态，请人工检查" : ""));
                }
            }
        }

        private class HmiAbsoluteTagDef
        {
            public string name = "";
            public string dataType = "Bool";
            public string address = "";
            public int? length;
            public string? connection;
            public string acquisitionCycle = "1 s";
        }

        private static bool IsAbsoluteAddressCompatible(string dataType, string address)
        {
            var type = (dataType ?? "").Trim().ToUpperInvariant();
            var addr = (address ?? "").Trim().ToUpperInvariant();
            var isBit = System.Text.RegularExpressions.Regex.IsMatch(addr,
                @"^%(I|Q|M)(X)?\d+\.\d+$|^%?DB\d+\.DBX\d+\.\d+$");
            var isByte = System.Text.RegularExpressions.Regex.IsMatch(addr,
                @"^%(I|Q|M)B\d+$|^%?DB\d+\.DBB\d+$");
            var isWord = System.Text.RegularExpressions.Regex.IsMatch(addr,
                @"^%(I|Q|M)W\d+$|^%?DB\d+\.DBW\d+$");
            var isDWord = System.Text.RegularExpressions.Regex.IsMatch(addr,
                @"^%(I|Q|M)D\d+$|^%?DB\d+\.DBD\d+$");
            return type switch
            {
                "BOOL" => isBit,
                "BYTE" => isByte,
                "WORD" or "INT" or "UINT" => isWord,
                "DWORD" or "DINT" or "UDINT" or "REAL" => isDWord,
                _ => false
            };
        }

        private int GetHmiAbsoluteTagLength(string dataType)
        {
            return dataType.ToLowerInvariant() switch
            {
                "bool" => 1,
                "byte" => 1,
                "word" => 2,
                "int" => 2,
                "uint" => 2,
                "dword" => 4,
                "dint" => 4,
                "udint" => 4,
                "real" => 4,
                _ => 1
            };
        }

        /// <summary>
        /// 生成单个 Absolute 模式标签 XML（使用XElement）
        /// </summary>
        private XElement GenerateAbsoluteTagXml(HmiAbsoluteTagDef td, string connName, int startId, XNamespace ns, string culture = "zh-CN")
        {
            int c1 = startId, c2 = startId + 1, c3 = startId + 2;
            int d1 = startId + 3, d2 = startId + 4, d3 = startId + 5;

            var tag = new XElement(ns + "Hmi.Tag.Tag",
                new XAttribute("ID", c1.ToString("X")),
                new XAttribute("CompositionName", "Tags"),
                new XElement(ns + "AttributeList",
                    new XElement(ns + "AcquisitionTriggerMode", "Visible"),
                    new XElement(ns + "AddressAccessMode", "Absolute"),
                    // ★V17校准★ Real 必须用 IEEE754 编码（Binary 会被博途拒绝），其余类型用 Binary
                    new XElement(ns + "Coding", td.dataType.Equals("Real", StringComparison.OrdinalIgnoreCase) ? "IEEE754" : "Binary"),
                    new XElement(ns + "ConfirmationType", "None"),
                    new XElement(ns + "GmpRelevant", "false"),
                    new XElement(ns + "JobNumber", "0"),
                    new XElement(ns + "Length", td.length ?? GetHmiAbsoluteTagLength(td.dataType)),
                    new XElement(ns + "LinearScaling", "false"),
                    new XElement(ns + "LogicalAddress", td.address),
                    new XElement(ns + "MandatoryCommenting", "false"),
                    new XElement(ns + "Name", td.name),
                    new XElement(ns + "Persistency", "false"),
                    new XElement(ns + "QualityCode", "false"),
                    new XElement(ns + "ScalingHmiHigh", "100"),
                    new XElement(ns + "ScalingHmiLow", "0"),
                    new XElement(ns + "ScalingPlcHigh", "10"),
                    new XElement(ns + "ScalingPlcLow", "0"),
                    new XElement(ns + "StartValue"),
                    new XElement(ns + "SubstituteValue"),
                    new XElement(ns + "SubstituteValueUsage", "None"),
                    new XElement(ns + "Synchronization", "false"),
                    new XElement(ns + "UpdateMode", "ProjectWide"),
                    new XElement(ns + "UseMultiplexing", "false")
                ),
                new XElement(ns + "LinkList",
                    new XElement(ns + "AcquisitionCycle",
                        new XAttribute("TargetID", "@OpenLink"),
                        new XElement(ns + "Name", td.acquisitionCycle)
                    ),
                    new XElement(ns + "Connection",
                        new XAttribute("TargetID", "@OpenLink"),
                        new XElement(ns + "Name", connName)
                    ),
                    new XElement(ns + "DataType",
                        new XAttribute("TargetID", "@OpenLink"),
                        new XElement(ns + "Name", td.dataType)
                    ),
                    new XElement(ns + "HmiDataType",
                        new XAttribute("TargetID", "@OpenLink"),
                        new XElement(ns + "Name", td.dataType)
                    )
                ),
                new XElement(ns + "ObjectList",
                    // Comment
                    new XElement(ns + "MultilingualText",
                        new XAttribute("ID", (c2).ToString("X")),
                        new XAttribute("CompositionName", "Comment"),
                        new XElement(ns + "ObjectList",
                            new XElement(ns + "MultilingualTextItem",
                                new XAttribute("ID", (c3).ToString("X")),
                                new XAttribute("CompositionName", "Items"),
                                new XElement(ns + "AttributeList",
                                    new XElement(ns + "Culture", culture),
                                    new XElement(ns + "Text")
                                )
                            )
                        )
                    ),
                    // DisplayName
                    new XElement(ns + "MultilingualText",
                        new XAttribute("ID", (d1).ToString("X")),
                        new XAttribute("CompositionName", "DisplayName"),
                        new XElement(ns + "ObjectList",
                            new XElement(ns + "MultilingualTextItem",
                                new XAttribute("ID", (d2).ToString("X")),
                                new XAttribute("CompositionName", "Items"),
                                new XElement(ns + "AttributeList",
                                    new XElement(ns + "Culture", culture),
                                    new XElement(ns + "Text")
                                )
                            )
                        )
                    )
                    // TagValue 在 Absolute 模式下可以省略（TIA会自动补）
                )
            );
            return tag;
        }

        // ────────────────────────────────────────────────────────────
        // 工具 3：按规格创建 HMI 画面
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// 按 JSON 规格参数化创建 HMI 画面
        /// specJson 格式示例：
        /// {
        ///   "screenName": "主监控画面",
        ///   "width": 800, "height": 480,
        ///   "backColor": "240,240,240",
        ///   "items": [
        ///     { "type": "rectangle", "name": "Panel1", "left": 20, "top": 20, "width": 760, "height": 200,
        ///       "fillColor": "220,220,220", "borderColor": "100,100,100", "cornerRadius": 8 },
        ///     { "type": "text", "name": "Title", "left": 300, "top": 30, "width": 200, "height": 40,
        ///       "text": "电梯监控系统", "fontSize": "22", "foreColor": "49,52,74" },
        ///     { "type": "indicator", "name": "Lamp1", "left": 100, "top": 100, "radius": 25,
        ///       "tag": "上行指示灯", "offColor": "255,0,0", "onColor": "0,255,0" },
        ///     { "type": "button", "name": "Btn1", "left": 500, "top": 100, "width": 120, "height": 60,
        ///       "text": "上行", "tag": "上行按钮", "backColor": "70,130,220", "fontSize": "16" },
        ///     { "type": "iofield", "name": "FloorDisp", "left": 200, "top": 260, "width": 200, "height": 100,
        ///       "tag": "当前楼层", "mode": "Output", "formatPattern": "999",
        ///       "backColor": "0,0,0", "foreColor": "0,255,0", "fontSize": "48" }
        ///   ]
        /// }
        /// </summary>
        public string CreateHmiScreenFromSpec(string specJson, string? hmiDeviceName = null)
        {
            lock (_lock)
            {
                // 调试保留用（catch 中引用）
                string dbgScreenName = "";
                string dbgScreenXml = "";
                string? dbgTemplateXml = null;
                try
                {
                    RequireProject();
                    var hmi = RequireClassicHmi(hmiDeviceName);

                    var spec = JObject.Parse(specJson);
                    var screenName = spec["screenName"]?.Value<string>() ?? "MainScreen";
                    dbgScreenName = screenName;

                    // 画面尺寸必须以目标 HMI 的真实画布为准。AI 传入的 width/height 仅作为后备值，
                    // 防止把 1280x800 的布局导入 800x480 设备后出现整体越界。
                    var layoutWarnings = new List<string>();
                    int screenW, screenH;
                    if (TryGetExistingHmiCanvasSize(hmi, out var actualW, out var actualH))
                    {
                        screenW = actualW;
                        screenH = actualH;
                        var requestedW = spec["width"]?.Value<int?>();
                        var requestedH = spec["height"]?.Value<int?>();
                        if ((requestedW.HasValue && requestedW.Value != screenW)
                            || (requestedH.HasValue && requestedH.Value != screenH))
                        {
                            layoutWarnings.Add($"忽略规格中的画布 {requestedW ?? 0}x{requestedH ?? 0}，使用目标 HMI 实际画布 {screenW}x{screenH}");
                        }
                    }
                    else
                    {
                        // ★修复★ 型号目录优先于 AI 请求值（与本方法"画布尺寸必须以目标 HMI
                        // 的真实画布为准"的约束一致）：探测失败时若能从设备型号确定分辨率，
                        // 直接采用并告警忽略请求值；此前请求值排在型号目录之前，会在
                        // TP700(800x480) 这类设备上按 IR 的 1280x800 生成画面，导入被
                        // TIA 以 "The screen size does not match the device" 拒绝。
                        var info = GetHmiDeviceInfo();
                        // 注意：不能用 ReferenceEquals 比较 HmiTarget——COM 每次访问返回
                        // 新的 RCW 包装实例，同一底层对象的两次获取引用必然不同。
                        if (info != null && string.Equals(info.Target.Name, hmi.Name, StringComparison.OrdinalIgnoreCase)
                            && HmiScreenSizes.TryGetValue(info.DeviceType, out var catalogSize))
                        {
                            screenW = catalogSize.W;
                            screenH = catalogSize.H;
                            var requestedW = spec["width"]?.Value<int?>();
                            var requestedH = spec["height"]?.Value<int?>();
                            if ((requestedW.HasValue && requestedW.Value != screenW)
                                || (requestedH.HasValue && requestedH.Value != screenH))
                            {
                                layoutWarnings.Add($"忽略规格中的画布 {requestedW ?? 0}x{requestedH ?? 0}，按设备型号 {info.DeviceType} 使用 {screenW}x{screenH}");
                            }
                        }
                        else if (spec["width"] != null && spec["height"] != null)
                        {
                            screenW = spec["width"]!.Value<int>();
                            screenH = spec["height"]!.Value<int>();
                        }
                        else
                        {
                            var devItem = FindHmiDeviceItem(hmi, hmiDeviceName);
                            var tid = devItem?.TypeIdentifier ?? "";
                            var deviceType = ExtractHmiDeviceType(tid);
                            if (HmiScreenSizes.TryGetValue(deviceType, out var size))
                                (screenW, screenH) = (size.W, size.H);
                            else
                            {
                                (screenW, screenH) = (800, 480);
                                layoutWarnings.Add($"无法识别 HMI 型号 '{tid}'，暂按 800x480 布局；建议先保留一个空白参考画面用于自动探测真实尺寸");
                            }
                        }
                    }
                    if (screenW <= 0 || screenH <= 0)
                        return Err($"画面尺寸无效: {screenW}x{screenH}");

                    var backColor = spec["backColor"]?.Value<string>() ?? "240,240,240";
                    JArray items;

                    // 检查画面是否已存在
                    var existingScreen = FindScreenRecursive(hmi.ScreenFolder, screenName);
                    if (existingScreen != null)
                        return Err($"画面 '{screenName}' 已存在，请先删除或使用其他名称。");

                    // 画面号：V17 中 Screen.Number 无法经 API 可靠读取（GetAttribute 亦不可靠），
                    // 使用随机高位号（10000-29999，合法范围 1-32767）避开现有画面号冲突；
                    // 若 GetAttribute 能读到已用号，则在已用号集合内继续递增。
                    // 使用随机高位号（10000-29999，合法范围 1-32767）避开现有画面号冲突；
                    // ★修复★ Environment.TickCount 在系统连续运行约 24.9 天后为负数，
                    // 取模结果可能为负导致画面号非法，先取绝对值。
                    int nextNumber = 10000 + (Math.Abs(Environment.TickCount) % 20000);
                    try
                    {
                        var usedNumbers = new HashSet<int>();
                        foreach (var s in GetAllScreens(hmi.ScreenFolder))
                        {
                            try
                            {
                                var v = s.GetAttribute("Number");
                                if (v != null) usedNumbers.Add(Convert.ToInt32(v));
                            }
                            catch { }
                        }
                        while (usedNumbers.Contains(nextNumber)) nextNumber++;
                    }
                    catch { }

                    // 从现有画面导出获取 engineering version
                    string engVer = EnvironmentDiscoveryService.CurrentEngineeringVersion();
                    try
                    {
                        var screens = GetAllScreens(hmi.ScreenFolder).ToList();
                        if (screens.Count > 0)
                        {
                            var tmpExp = Path.Combine(Path.GetTempPath(), $"tia_scrver_{Guid.NewGuid():N}.xml");
                            screens[0].Export(new FileInfo(tmpExp), ExportOptions.WithDefaults);
                            var xdoc = XDocument.Load(tmpExp);
                            TryDelete3(tmpExp);
                            var engElem = xdoc.Descendants("Engineering").FirstOrDefault();
                            var ver = engElem?.Attribute("version")?.Value;
                            if (!string.IsNullOrEmpty(ver)) engVer = ver!;
                        }
                    }
                    catch { }

                    // compile/validate/apply/create 共用正式 HMI 规格校验。
                    var validationText = ValidateHmiScreenSpecJson(spec.ToString(Newtonsoft.Json.Formatting.None), screenW, screenH);
                    var validation = JObject.Parse(validationText);
                    if (validation["success"]?.Value<bool>() != true)
                        return validationText;
                    items = validation["normalizedItems"] as JArray ?? new JArray();
                    foreach (var warning in validation["warnings"] as JArray ?? new JArray())
                    {
                        var text = warning?.ToString();
                        if (!string.IsNullOrWhiteSpace(text)) layoutWarnings.Add(text!);
                    }

                    // ★V17校准★ 先导出模板并扫描模板内最大对象 ID：
                    // 新控件 ID 必须从模板最大 ID 之后起算，否则与模板残留 ID 重复，
                    // V17 导入器对文档内重复 ID 直接拒绝（且 Import 失败会终止 TIA 进程）。
                    var templateXml = ExportFirstScreenAsTemplate(hmi);
                    int templateMaxId = 0;
                    if (templateXml != null)
                    {
                        try
                        {
                            var probeDoc = new XmlDocument();
                            probeDoc.LoadXml(templateXml);
                            templateMaxId = ScanMaxObjectId(probeDoc);
                        }
                        catch { }
                    }

                    // 使用 XML 编排器构建画面。普通控件先落层，Group 第二阶段按成员名组装。
                    var builder = new HmiScreenXmlBuilder(startId: Math.Max(0x100, templateMaxId + 1));
                    var layerItems = new SortedDictionary<int, List<XmlElement>>();
                    var layerNames = ReadHmiLayerNames(spec);
                    foreach (var declaredLayer in layerNames.Keys) layerItems[declaredLayer] = new List<XmlElement>();
                    var builtItems = new Dictionary<string, (XmlElement Element, int Layer)>(StringComparer.OrdinalIgnoreCase);
                    var groupSpecs = new List<JObject>();
                    var createdItems = new List<object>();

                    foreach (var token in items.OfType<JObject>())
                    {
                        var item = (JObject)token.DeepClone();
                        var name = item["name"]?.Value<string>() ?? "";
                        var canonicalType = HmiControlCatalog.Canonicalize(item["type"]?.Value<string>());
                        if (HmiControlCatalog.IsGroup(canonicalType))
                        {
                            groupSpecs.Add(item);
                            continue;
                        }

                        var layer = item["layer"]?.Value<int?>() ?? 0;
                        var element = BuildHmiScreenItemFromSpec(builder, item, canonicalType);
                        builder.ApplyAnimations(element, ParseHmiAnimations(item));
                        if (!layerItems.TryGetValue(layer, out var targetLayer))
                        {
                            targetLayer = new List<XmlElement>();
                            layerItems[layer] = targetLayer;
                        }
                        if (canonicalType == "indicator")
                        {
                            var tag = item["tag"]?.ToString() ?? "";
                            var lamp = builder.CreateV17StatusLamp(
                                name,
                                item["left"]?.Value<int>() ?? 4,
                                item["top"]?.Value<int>() ?? 4,
                                item["radius"]?.Value<int?>() ?? Math.Max(4, Math.Min(item["width"]?.Value<int>() ?? 30, item["height"]?.Value<int>() ?? 30) / 2),
                                tag,
                                item["offColor"]?.ToString() ?? "173, 174, 181",
                                item["onColor"]?.ToString() ?? "0, 200, 0");
                            targetLayer.Add(lamp.Base);
                            targetLayer.Add(lamp.Active);
                        }
                        else
                        {
                            targetLayer.Add(element);
                        }
                        builtItems[name] = (element, layer);
                        if (!string.IsNullOrWhiteSpace(item["layerName"]?.ToString()))
                            layerNames[layer] = item["layerName"]!.ToString();
                        createdItems.Add(BuildHmiCreatedItemSummary(item, canonicalType, layer));
                    }

                    foreach (var groupSpec in groupSpecs)
                    {
                        var groupName = groupSpec["name"]?.ToString() ?? "";
                        var memberNames = (groupSpec["members"] as JArray ?? new JArray())
                            .Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                        if (memberNames.Count == 0)
                            return Err($"Group '{groupName}' 缺少 members[]。");
                        var members = new List<XmlElement>();
                        int? groupLayer = groupSpec["layer"]?.Value<int?>();
                        foreach (var memberName in memberNames)
                        {
                            if (!builtItems.TryGetValue(memberName, out var member))
                                return Err($"Group '{groupName}' 引用了不存在或已被其他 Group 消费的成员 '{memberName}'。");
                            if (groupLayer.HasValue && groupLayer.Value != member.Layer)
                                return Err($"Group '{groupName}' 与成员 '{memberName}' 不在同一画面层。");
                            groupLayer ??= member.Layer;
                            members.Add(member.Element);
                        }
                        var layer = groupLayer ?? 0;
                        foreach (var memberName in memberNames)
                        {
                            var member = builtItems[memberName];
                            layerItems[member.Layer].Remove(member.Element);
                            builtItems.Remove(memberName);
                        }
                        var groupElement = builder.CreateGroup(groupName, members);
                        builder.ApplyAnimations(groupElement, ParseHmiAnimations(groupSpec));
                        if (!layerItems.TryGetValue(layer, out var targetLayer))
                        {
                            targetLayer = new List<XmlElement>();
                            layerItems[layer] = targetLayer;
                        }
                        targetLayer.Add(groupElement);
                        builtItems[groupName] = (groupElement, layer);
                        createdItems.Add(BuildHmiCreatedItemSummary(groupSpec, "group", layer));
                    }

                    if (layerItems.Count == 0) layerItems[0] = new List<XmlElement>();

                    // 构建完整画面 XML
                    string screenXml = "";
                    string? templateXmlDebug = null;
                    if (templateXml != null)
                    {
                        templateXmlDebug = templateXml;
                        // ★ V17 兼容主路径：基于真实导出模板改造（保留 LinkList/HelpText 骨架）
                        screenXml = BuildScreenXmlFromTemplateWithItems(templateXml, screenName, nextNumber, layerItems, layerNames);
                    }
                    else
                    {
                        // 无现有画面可用作模板时回退到硬编码骨架
                        screenXml = builder.BuildScreenDocument(
                            screenName, screenW, screenH, new List<XmlElement>(), backColor, engVer, nextNumber,
                            layerItems, layerNames);
                    }
                    dbgScreenXml = screenXml;
                    dbgTemplateXml = templateXmlDebug;

                    // 写入临时文件并导入
                    var tempScreen = Path.Combine(Path.GetTempPath(), $"tia_screen_{Guid.NewGuid():N}.xml");
                    try
                    {
                        // ★防护★ 导入前保存项目：Openness 中 ScreenComposition.Import 失败会直接终止 TIA 进程，
                        // 先保存可避免未保存更改随崩溃丢失。
                        try { _project?.Save(); } catch { }
                        File.WriteAllText(tempScreen, screenXml, new UTF8Encoding(true));
                        var imported = hmi.ScreenFolder.Screens.Import(new FileInfo(tempScreen), ImportOptions.Override);
                        var importedScreen = imported.FirstOrDefault();
                        var verified = importedScreen != null || FindScreenRecursive(hmi.ScreenFolder, screenName) != null;
                        if (!verified)
                        {
                            TryDelete3(tempScreen);
                            return Err($"画面 '{screenName}' 导入后未能在目标 HMI 中找到，已拒绝返回成功");
                        }
                        TryDelete3(tempScreen);

                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            message = $"画面 '{screenName}' 创建成功",
                            screenName,
                            screenWidth = screenW,
                            screenHeight = screenH,
                            itemCount = createdItems.Count,
                            items = createdItems,
                            layoutWarnings,
                            note = "所有控件已在导入前执行边界裁剪、文本容器扩展和字号适配；仍应在目标设备分辨率下做一次人工预览"
                        }, Newtonsoft.Json.Formatting.Indented);
                    }
                    finally { TryDelete3(tempScreen); }
                }
                catch (Exception ex)
                {
                    // ★调试保留：失败时把生成 XML 与模板保留到临时目录供离线对比
                    try
                    {
                        var dbgDir = Path.Combine(Path.GetTempPath(), "tia_mcp_debug");
                        Directory.CreateDirectory(dbgDir);
                        File.WriteAllText(Path.Combine(dbgDir, $"failed_screen_{dbgScreenName}.xml"), dbgScreenXml, new UTF8Encoding(true));
                        if (dbgTemplateXml != null)
                            File.WriteAllText(Path.Combine(dbgDir, $"failed_template_{dbgScreenName}.xml"), dbgTemplateXml, new UTF8Encoding(true));
                    }
                    catch { }
                    return Err("创建HMI画面失败: " + ex.Message);
                }
            }
        }

        /// <summary>导出第一个现有画面作为模板 XML；无画面时返回 null。</summary>
        private string? ExportFirstScreenAsTemplate(HmiTarget hmi)
        {
            try
            {
                var screens = GetAllScreens(hmi.ScreenFolder).ToList();
                if (screens.Count == 0) return null;
                var tmp = Path.Combine(Path.GetTempPath(), $"tia_tmpl_{Guid.NewGuid():N}.xml");
                try
                {
                    screens[0].Export(new FileInfo(tmp), ExportOptions.WithDefaults);
                    return File.ReadAllText(tmp, Encoding.UTF8);
                }
                finally { TryDelete3(tmp); }
            }
            catch { return null; }
        }

        /// <summary>
        /// 基于真实导出的画面 XML 模板创建新画面（V17 兼容）：
        /// 替换 Name/Number、保留模板骨架（LinkList/HelpText 等）、按层清空并插入新控件。
        /// 模板骨架来自博途真实导出，规避硬编码骨架缺 LinkList 导致 V17 导入失败的问题。
        /// ★V17校准★ 克隆补位层必须重新分配唯一 ID 并清空子控件，且模板全部层统一清空、
        /// 超出规格最大层的模板层直接移除——否则重复 ID / 残留控件会被 V17 导入器拒绝。
        /// </summary>
        private string BuildScreenXmlFromTemplateWithItems(
            string templateXml, string screenName, int screenNumber,
            IReadOnlyDictionary<int, List<XmlElement>> layerItems,
            IReadOnlyDictionary<int, string>? layerNames)
        {
            var doc = new XmlDocument();
            doc.LoadXml(templateXml);

            var ns = new XmlNamespaceManager(doc.NameTable);
            var nameNode = doc.SelectSingleNode("//Hmi.Screen.Screen/AttributeList/Name", ns);
            if (nameNode != null) nameNode.InnerText = screenName;
            var numberNode = doc.SelectSingleNode("//Hmi.Screen.Screen/AttributeList/Number", ns);
            if (numberNode != null) numberNode.InnerText = screenNumber.ToString();

            // 移除 Screen 直接子 ObjectList 中除 HelpText/ScreenLayer 外的节点
            var screenObjList = doc.SelectSingleNode("//Hmi.Screen.Screen/ObjectList", ns);
            if (screenObjList != null)
            {
                var toRemove = new List<XmlNode>();
                foreach (XmlNode child in screenObjList.ChildNodes)
                {
                    if (child.Name == "MultilingualText" || child.Name == "Hmi.Screen.ScreenLayer")
                        continue;
                    toRemove.Add(child);
                }
                foreach (var node in toRemove) screenObjList.RemoveChild(node);
            }

            // 模板内最大对象 ID（十六进制），克隆层/新控件都要从它之后起算
            int maxId = ScanMaxObjectId(doc);

            // 收集模板中已有 ScreenLayer（按 Index）
            var templateLayers = new Dictionary<int, XmlElement>();
            foreach (XmlElement layer in doc.SelectNodes("//Hmi.Screen.ScreenLayer", ns))
            {
                var indexNode = layer.SelectSingleNode("AttributeList/Index", ns);
                if (indexNode != null && int.TryParse(indexNode.InnerText, out var idx))
                    templateLayers[idx] = layer;
            }

            int maxLayer = layerItems.Count > 0 ? layerItems.Keys.Max(k => k) : 0;
            // 确保 Index 0..maxLayer 的层都存在（不足则克隆第一层或新建空层）
            for (int i = 0; i <= maxLayer; i++)
            {
                if (templateLayers.TryGetValue(i, out var layer)) continue;
                XmlElement? source = templateLayers.Count > 0 ? templateLayers.Values.First() : null;
                if (source != null)
                {
                    var clone = (XmlElement)source.CloneNode(true);
                    // ★V17校准★ 克隆体必须换新 ID（源层 ID 如 "4" 会被原样复制导致文档内重复 ID）
                    clone.SetAttribute("ID", (++maxId).ToString("X"));
                    var idxNode = clone.SelectSingleNode("AttributeList/Index", ns);
                    if (idxNode != null) idxNode.InnerText = i.ToString();
                    var nameNodeL = clone.SelectSingleNode("AttributeList/Name", ns);
                    if (nameNodeL != null && layerNames != null && layerNames.TryGetValue(i, out var ln))
                        nameNodeL.InnerText = ln;
                    // 清空克隆层的 ObjectList，避免携带源层控件及子 ID
                    var cloneObjList = clone.SelectSingleNode("ObjectList", ns);
                    if (cloneObjList != null) cloneObjList.RemoveAll();
                    source.ParentNode?.AppendChild(clone);
                    templateLayers[i] = clone;
                }
                else
                {
                    // 模板连层都没有时新建空层（挂到 Screen 的 ObjectList 下）
                    var newLayer = doc.CreateElement("Hmi.Screen.ScreenLayer");
                    newLayer.SetAttribute("ID", (++maxId).ToString("X"));
                    newLayer.SetAttribute("CompositionName", "Layers");
                    var newAttr = doc.CreateElement("AttributeList");
                    newAttr.AppendChild(CreateXmlElement(doc, "Index", i.ToString()));
                    newAttr.AppendChild(CreateXmlElement(doc, "Name",
                        layerNames != null && layerNames.TryGetValue(i, out var ln2) ? ln2 : ""));
                    newAttr.AppendChild(CreateXmlElement(doc, "VisibleES", "true"));
                    newLayer.AppendChild(newAttr);
                    newLayer.AppendChild(doc.CreateElement("ObjectList"));
                    screenObjList?.AppendChild(newLayer);
                    templateLayers[i] = newLayer;
                }
            }

            // ★V17校准★ 统一清空模板全部层（含未在规格中出现的残留层）：
            // ① 层名按规格更新；② 超出 maxLayer 的层整体移除（其控件不得带入新画面）；
            // ③ 规格命中的层清空后插入新控件。
            var extraLayers = new List<XmlElement>();
            foreach (var kvp in templateLayers)
            {
                var layer = kvp.Value;
                if (kvp.Key > maxLayer)
                {
                    extraLayers.Add(layer);
                    continue;
                }
                var layerObj = layer.SelectSingleNode("ObjectList", ns);
                if (layerObj == null)
                {
                    layerObj = doc.CreateElement("ObjectList");
                    layer.AppendChild(layerObj);
                }
                layerObj.RemoveAll();
                if (layerNames != null && layerNames.TryGetValue(kvp.Key, out var ln))
                {
                    var lnNode = layer.SelectSingleNode("AttributeList/Name", ns);
                    if (lnNode != null) lnNode.InnerText = ln;
                }
                if (layerItems.TryGetValue(kvp.Key, out var items))
                {
                    foreach (var item in items)
                    {
                        var frag = doc.CreateDocumentFragment();
                        frag.InnerXml = item.OuterXml;
                        layerObj.AppendChild(frag);
                    }
                }
            }
            foreach (var extra in extraLayers) extra.ParentNode?.RemoveChild(extra);

            return doc.OuterXml;
        }

        /// <summary>扫描文档中所有 ID 属性（十六进制优先，兼容十进制）并返回最大值。</summary>
        private static int ScanMaxObjectId(XmlDocument doc)
        {
            int maxId = 0;
            foreach (XmlElement elem in doc.GetElementsByTagName("*"))
            {
                var idAttr = elem.GetAttribute("ID");
                if (string.IsNullOrEmpty(idAttr)) continue;
                if (int.TryParse(idAttr, System.Globalization.NumberStyles.HexNumber, null, out var hexVal))
                {
                    if (hexVal > maxId) maxId = hexVal;
                }
                else if (int.TryParse(idAttr, out var decVal) && decVal > maxId)
                    maxId = decVal;
            }
            return maxId;
        }

        private static XmlElement CreateXmlElement(XmlDocument doc, string name, string text)
        {
            var elem = doc.CreateElement(name);
            elem.InnerText = text;
            return elem;
        }

        private static XmlElement BuildHmiScreenItemFromSpec(HmiScreenXmlBuilder builder, JObject item, string canonicalType)        {
            var name = item["name"]?.ToString() ?? "";
            var left = item["left"]?.Value<int>() ?? 4;
            var top = item["top"]?.Value<int>() ?? 4;
            var width = item["width"]?.Value<int>() ?? 100;
            var height = item["height"]?.Value<int>() ?? 30;

            switch (canonicalType)
            {
                case "rectangle":
                    return builder.CreateRectangle(name, left, top, width, height,
                        item["fillColor"]?.ToString() ?? "220,220,220",
                        item["borderColor"]?.ToString() ?? "100,100,100",
                        item["borderWidth"]?.Value<int?>() ?? 2,
                        item["cornerRadius"]?.Value<int?>() ?? 8);
                case "text":
                    return builder.CreateTextLabel(name, left, top, width, height,
                        item["text"]?.ToString() ?? "",
                        item["fontSize"]?.ToString() ?? "14",
                        item["transparent"]?.Value<bool?>() ?? true,
                        item["foreColor"]?.ToString() ?? "49,52,74");
                case "indicator":
                    return builder.CreateCircleIndicator(name, left, top,
                        item["radius"]?.Value<int?>() ?? Math.Max(4, Math.Min(width, height) / 2),
                        item["tag"]?.ToString() ?? "",
                        item["offColor"]?.ToString() ?? "255,0,0",
                        item["onColor"]?.ToString() ?? "0,255,0");
                case "button":
                case "navigation":
                {
                    var behavior = HmiActionCatalog.CanonicalBehavior(canonicalType == "navigation"
                        ? "activateScreen"
                        : item["eventType"]?.ToString() ?? item["behavior"]?.ToString() ?? "invertBit");
                    if (behavior.Equals("multiAction", StringComparison.OrdinalIgnoreCase))
                    {
                        var actions = (item["actions"] as JArray ?? new JArray()).OfType<JObject>()
                            .Select(HmiActionCatalog.FromJson).ToList();
                        return builder.CreateButtonAdvanced(name, left, top, width, height,
                            item["text"]?.ToString() ?? "Button", actions,
                            item["backColor"]?.ToString() ?? "70,130,220",
                            item["foreColor"]?.ToString() ?? "255,255,255",
                            item["fontSize"]?.ToString() ?? "14",
                            item["fontStyle"]?.ToString() ?? "Bold");
                    }
                    // ★修复★ targetScreen 与 eventTarget 都是合法输入（QualityGuard 两者都接受），
                    // 构建时统一回退解析，避免校验通过但构建报"参数不完整"
                    var target = item["eventTarget"]?.ToString() ?? item["targetScreen"]?.ToString();
                    return builder.CreateButton(name, left, top, width, height,
                        item["text"]?.ToString() ?? "Button",
                        item["tag"]?.ToString() ?? "",
                        item["backColor"]?.ToString() ?? "70,130,220",
                        item["foreColor"]?.ToString() ?? "255,255,255",
                        item["fontSize"]?.ToString() ?? "14",
                        item["fontStyle"]?.ToString() ?? "Bold",
                        behavior, target);
                }
                case "switch":
                    return builder.CreateSwitch(name, left, top, width, height,
                        item["tag"]?.ToString() ?? "",
                        item["textOff"]?.ToString() ?? "OFF",
                        item["textOn"]?.ToString() ?? "ON",
                        item["offColor"]?.ToString() ?? "255,100,100",
                        item["onColor"]?.ToString() ?? "100,180,100",
                        item["caption"]?.ToString() ?? "");
                case "iofield":
                    return builder.CreateIoField(name, left, top, width, height,
                        item["tag"]?.ToString() ?? "",
                        item["mode"]?.ToString() ?? "Output",
                        item["formatPattern"]?.ToString() ?? "999",
                        item["backColor"]?.ToString() ?? "0,0,0",
                        item["foreColor"]?.ToString() ?? "0,255,0",
                        item["fontSize"]?.ToString() ?? "36",
                        item["fontStyle"]?.ToString() ?? "Bold",
                        item["fieldLength"]?.Value<int?>() ?? 3);
                case "line":
                    return builder.CreateLine(name,
                        item["startLeft"]?.Value<int?>() ?? left,
                        item["startTop"]?.Value<int?>() ?? top,
                        item["endLeft"]?.Value<int?>() ?? left + width,
                        item["endTop"]?.Value<int?>() ?? top + height,
                        item["color"]?.ToString() ?? "0,0,0",
                        item["lineWidth"]?.Value<int?>() ?? 1,
                        item["style"]?.ToString() ?? "Solid",
                        item["startStyle"]?.ToString() ?? "NoEnd",
                        item["endStyle"]?.ToString() ?? "NoEnd");
                case "symboliciofield":
                    return builder.CreateSymbolicIoField(name, left, top, width, height,
                        item["tag"]?.ToString() ?? "",
                        item["textList"]?.ToString() ?? "",
                        item["mode"]?.ToString() ?? "Output",
                        item["backColor"]?.ToString() ?? "255,255,255",
                        item["foreColor"]?.ToString() ?? "49,52,74",
                        item["fontSize"]?.ToString() ?? "16",
                        item["fontStyle"]?.ToString() ?? "Bold",
                        item["showDropDown"]?.Value<bool?>() ?? false,
                        item["textOff"]?.ToString() ?? "0",
                        item["textOn"]?.ToString() ?? "1");
                case "graphicview":
                    return builder.CreateGraphicView(name, left, top, width, height,
                        item["picture"]?.ToString() ?? "",
                        item["autoSizing"]?.ToString() ?? "StretchPicture",
                        item["useTransparentColor"]?.Value<bool?>() ?? false,
                        item["transparentColor"]?.ToString() ?? "255,0,255");
                default:
                    throw new InvalidOperationException($"不支持的控件类型: {item["type"]}。支持: {string.Join(", ", HmiControlCatalog.CanonicalTypes)}");
            }
        }

        private static List<HmiAnimationSpec> ParseHmiAnimations(JObject item)
        {
            var result = new List<HmiAnimationSpec>();
            foreach (var animation in (item["animations"] as JArray ?? new JArray()).OfType<JObject>())
            {
                result.Add(new HmiAnimationSpec
                {
                    Type = animation["type"]?.ToString() ?? "visibility",
                    Tag = animation["tag"]?.ToString() ?? "",
                    RangeStart = animation["rangeStart"]?.Value<int?>() ?? 1,
                    RangeEnd = animation["rangeEnd"]?.Value<int?>() ?? 1,
                    BitPosition = animation["bitPosition"]?.Value<int?>() ?? 0,
                    Visible = animation["visible"]?.Value<bool?>() ?? true,
                    ObjectEnabled = animation["objectEnabled"]?.Value<bool?>() ?? true
                });
            }
            if (item["visibleWhen"] is JObject visible)
            {
                result.Add(new HmiAnimationSpec
                {
                    Type = visible["bitPosition"] != null ? "singleBitVisibility" : "visibility",
                    Tag = visible["tag"]?.ToString() ?? "",
                    RangeStart = visible["rangeStart"]?.Value<int?>() ?? 1,
                    RangeEnd = visible["rangeEnd"]?.Value<int?>() ?? 1,
                    BitPosition = visible["bitPosition"]?.Value<int?>() ?? 0,
                    Visible = visible["visible"]?.Value<bool?>() ?? true
                });
            }
            if (item["enabledWhen"] is JObject enabled)
            {
                result.Add(new HmiAnimationSpec
                {
                    Type = "enabling",
                    Tag = enabled["tag"]?.ToString() ?? "",
                    RangeStart = enabled["rangeStart"]?.Value<int?>() ?? 1,
                    RangeEnd = enabled["rangeEnd"]?.Value<int?>() ?? 1,
                    ObjectEnabled = enabled["objectEnabled"]?.Value<bool?>() ?? true
                });
            }
            return result;
        }

        private static Dictionary<int, string> ReadHmiLayerNames(JObject spec)
        {
            var result = new Dictionary<int, string>();
            foreach (var layer in (spec["layers"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var index = layer["index"]?.Value<int?>() ?? 0;
                if (index >= 0 && index <= 31) result[index] = layer["name"]?.ToString() ?? "";
            }
            return result;
        }

        private static object BuildHmiCreatedItemSummary(JObject item, string canonicalType, int layer)
            => new
            {
                type = canonicalType,
                name = item["name"]?.ToString() ?? "",
                layer,
                left = item["left"]?.Value<int?>(),
                top = item["top"]?.Value<int?>(),
                width = item["width"]?.Value<int?>(),
                height = item["height"]?.Value<int?>(),
                tag = item["tag"]?.ToString(),
                picture = item["picture"]?.ToString(),
                textList = item["textList"]?.ToString()
            };

        /// <summary>
        /// 查找 HmiTarget 所属的 Device 对象
        /// </summary>
        private Device? FindDeviceForHmiTarget(HmiTarget hmi)
        {
            // ★V17 修复★ COM 互操作对象每次 GetService 可能返回不同包装实例，
            // 引用相等比较（==）在 V17 上会失败。改为"引用相等或名称相等"，
            // 并增加直接查询 HmiTarget 服务的兜底路径。
            var hmiName = "";
            try { hmiName = hmi.Name ?? ""; } catch { }
            foreach (var device in GetAllDevices())
            {
                foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        var sw = swContainer?.Software;
                        if (sw != null && (ReferenceEquals(sw, hmi) || (sw is HmiTarget t && string.Equals(t.Name, hmiName, StringComparison.OrdinalIgnoreCase))))
                            return device;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// 查找HMI对应的DeviceItem（获取TypeIdentifier等信息）
        /// </summary>
        private DeviceItem? FindHmiDeviceItem(HmiTarget hmi, string? hmiDeviceName)
        {
            foreach (var device in GetAllDevices())
            {
                if (!string.IsNullOrEmpty(hmiDeviceName) && !device.Name.Equals(hmiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        if (swContainer?.Software == hmi) return di;
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcpServer
{
    internal static class BlockXmlBuilder
    {
        private const string IfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
        private const string StNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";

        public static string GenerateBlockXml(string blockType, string name, int blockNumber,
            ProgrammingLanguage lang, List<string>? flgNetList = null,
            string? interfaceSectionsXml = null, string? sclCode = null, List<string>? titles = null,
            string? secondaryType = null)
        {
            var langStr = lang.ToString();
            var xmlBlockType = blockType == "DB" ? "GlobalDB" : blockType;

            // Interface Sections
            string sectionsXml;
            if (!string.IsNullOrEmpty(interfaceSectionsXml))
            {
                sectionsXml = FilterInterfaceSections(interfaceSectionsXml, blockType);
            }
            else
            {
                sectionsXml = blockType switch
                {
                    "DB" => @"<Section Name=""Static"" />",
                    "FB" => @"<Section Name=""Input"" /><Section Name=""Output"" /><Section Name=""InOut"" /><Section Name=""Static"" /><Section Name=""Temp"" /><Section Name=""Constant"" />",
                    "OB" => @"<Section Name=""Temp"" /><Section Name=""Constant"" />",
                    _ => @"<Section Name=""Input"" /><Section Name=""Output"" /><Section Name=""InOut"" /><Section Name=""Temp"" /><Section Name=""Constant"" />"
                };
            }

            // ObjectList
            string objectListXml;
            if (blockType == "DB")
            {
                objectListXml = DbObjectList();
            }
            else if (lang == ProgrammingLanguage.SCL)
            {
                objectListXml = SclObjectList(sclCode);
            }
            else if (flgNetList != null && flgNetList.Count > 0)
            {
                objectListXml = CompileUnitsObjectList(flgNetList, langStr, titles);
            }
            else
            {
                objectListXml = EmptyBlockObjectList();
            }

            var autoNumberAttr = blockNumber > 0 ? "false" : "true";
            var isIECAttr = blockType != "DB" ? "<IsIECCheckEnabled>false</IsIECCheckEnabled>" : "";
            var memLayoutAttr = blockType != "DB" ? "<MemoryLayout>Optimized</MemoryLayout>" : "";

            string numberAttr = "";
            if (blockType == "OB" && blockNumber == 0) numberAttr = "<Number>1</Number>";
            else if (blockNumber > 0) numberAttr = $"<Number>{blockNumber}</Number>";
            // DB 的 blockNumber <= 0 表示自动编号。此时保持 AutoNumber=true 且省略 Number，
            // 不能把 0（或任意固定兜底号）写入 XML，否则会生成非法 DB0 或制造编号冲突。

            var langAttr = $"<ProgrammingLanguage>{langStr}</ProgrammingLanguage>";

            // OB 子类型：默认 ProgramCycle (OB1)，可传 CyclicInterrupt (OB30)/Startup (OB100)/HardwareInterrupt (OB200) 等
            // 参考 OB 类型对照表，CyclicInterrupt 等 PID 必需的循环中断 OB 通过 secondaryType 参数指定
            var obSpecific = blockType == "OB"
                ? $"<SecondaryType>{(string.IsNullOrEmpty(secondaryType) ? "ProgramCycle" : secondaryType)}</SecondaryType><SetENOAutomatically>false</SetENOAutomatically>"
                : "";

            var escapedName = SecurityElement.Escape(name);
            var createdTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            // ★ 使用 StringBuilder 拼接，避免 string.Format 花括号问题
            var sb = new StringBuilder();
            sb.Append(@"<?xml version=""1.0"" encoding=""UTF-8""?>");
            sb.Append("<Document>");
            sb.Append("<Engineering version=\"" + EnvironmentDiscoveryService.CurrentEngineeringVersion() + "\"/>");
            sb.Append("<DocumentInfo>");
            sb.Append($"<Created>{createdTime}</Created>");
            sb.Append("<ExportSetting>WithDefaults</ExportSetting>");
            sb.Append("<InstalledProducts><Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>" + EnvironmentDiscoveryService.CurrentEngineeringVersion() + "</DisplayVersion></Product></InstalledProducts>");
            sb.Append("</DocumentInfo>");
            sb.Append($"<SW.Blocks.{xmlBlockType} ID=\"0\">");
            sb.Append("<AttributeList>");
            sb.Append($"<AutoNumber>{autoNumberAttr}</AutoNumber>");
            sb.Append("<HeaderAuthor/><HeaderFamily/><HeaderName/><HeaderVersion>0.1</HeaderVersion>");
            sb.Append($"<Interface><Sections xmlns=\"{IfaceNs}\">{sectionsXml}</Sections></Interface>");
            sb.Append(isIECAttr);
            sb.Append(memLayoutAttr);
            sb.Append($"<Name>{escapedName}</Name>");
            // ★V17 校准：块 Namespace 属性为 V19 新增，V17 导入器拒绝该属性，按版本条件输出
            if (!EnvironmentDiscoveryService.CurrentEngineeringVersion().StartsWith("V17", StringComparison.OrdinalIgnoreCase))
                sb.Append("<Namespace/>");
            sb.Append(numberAttr);
            sb.Append(langAttr);
            sb.Append(obSpecific);
            sb.Append("</AttributeList>");
            sb.Append(objectListXml);
            sb.Append($"</SW.Blocks.{xmlBlockType}>");
            sb.Append("</Document>");
            return sb.ToString();
        }

        public static string BuildInterfaceSections(LadNetworkDef def)
        {
            var allNets = def.networks != null && def.networks.Count > 0 ? def.networks : new List<LadNetworkDef> { def };
            var seen = new HashSet<string>(); var vars = new List<LadVariableDef>();
            foreach (var net in allNets) { if (net.variables != null) foreach (var v in net.variables) if (seen.Add(v.name)) vars.Add(v); }
            return BuildSectionsHelp(vars);
        }

        public static string BuildInterfaceSections(List<LadVariableDef> variables)
        { return BuildSectionsHelp(variables ?? new List<LadVariableDef>()); }

        private static readonly string[] ValidSections = { "Input", "Output", "InOut", "Static", "Temp", "Constant", "Return" };

        private static string BuildSectionsHelp(List<LadVariableDef> vars)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = new XElement("Root");
            foreach (var group in vars
                         .Where(v => v != null && !string.IsNullOrWhiteSpace(v.name) && seen.Add(v.name))
                         .GroupBy(v => NormalizeSectionName(v.section)))
            {
                var section = new XElement("Section", new XAttribute("Name", group.Key));
                foreach (var variable in group)
                {
                    var datatype = NormalizeInstanceDatatype(variable.datatype);
                    var member = new XElement("Member",
                        new XAttribute("Name", variable.name.Trim()),
                        new XAttribute("Datatype", datatype));

                    var originalType = (variable.datatype ?? "Bool").Trim().ToUpperInvariant();
                    if (originalType is "TON_TIME" or "TOF_TIME" or "TP_TIME")
                    {
                        member.SetAttributeValue("Version", "1.0");
                        member.Add(new XElement("Sections",
                            new XElement("Section", new XAttribute("Name", "None"),
                                new XElement("Member", new XAttribute("Name", "PT"), new XAttribute("Datatype", "Time")),
                                new XElement("Member", new XAttribute("Name", "ET"), new XAttribute("Datatype", "Time")),
                                new XElement("Member", new XAttribute("Name", "IN"), new XAttribute("Datatype", "Bool")),
                                new XElement("Member", new XAttribute("Name", "Q"), new XAttribute("Datatype", "Bool")))));
                    }
                    else if (originalType == "IEC_COUNTER"
                             || originalType.StartsWith("CTU_", StringComparison.Ordinal)
                             || originalType.StartsWith("CTD_", StringComparison.Ordinal)
                             || originalType.StartsWith("CTUD_", StringComparison.Ordinal))
                    {
                        member.SetAttributeValue("Version", "1.0");
                        member.Add(new XElement("Sections", new XElement("Section", new XAttribute("Name", "None"))));
                    }
                    else if (originalType is "SR_BOOL" or "RS_BOOL")
                    {
                        var nested = new XElement("Section", new XAttribute("Name", "None"));
                        if (originalType == "SR_BOOL")
                        {
                            nested.Add(new XElement("Member", new XAttribute("Name", "S1"), new XAttribute("Datatype", "Bool")));
                            nested.Add(new XElement("Member", new XAttribute("Name", "R"), new XAttribute("Datatype", "Bool")));
                        }
                        else
                        {
                            nested.Add(new XElement("Member", new XAttribute("Name", "S"), new XAttribute("Datatype", "Bool")));
                            nested.Add(new XElement("Member", new XAttribute("Name", "R1"), new XAttribute("Datatype", "Bool")));
                        }
                        nested.Add(new XElement("Member", new XAttribute("Name", "Q1"), new XAttribute("Datatype", "Bool")));
                        member.Add(new XElement("Sections", nested));
                    }
                    else if (originalType is "R_TRIG" or "F_TRIG")
                    {
                        member.SetAttributeValue("Version", "1.0");
                        member.Add(new XElement("Sections",
                            new XElement("Section", new XAttribute("Name", "None"),
                                new XElement("Member", new XAttribute("Name", "CLK"), new XAttribute("Datatype", "Bool")),
                                new XElement("Member", new XAttribute("Name", "Q"), new XAttribute("Datatype", "Bool")))));
                    }
                    else if (originalType.StartsWith("FB_", StringComparison.OrdinalIgnoreCase))
                    {
                        member.SetAttributeValue("Version", "1.0");
                        member.Add(new XElement("Sections", new XElement("Section", new XAttribute("Name", "None"))));
                    }
                    section.Add(member);
                }
                root.Add(section);
            }
            return string.Concat(root.Nodes().Select(x => x.ToString(SaveOptions.DisableFormatting)));
        }

        private static string NormalizeSectionName(string? section)
        {
            var value = string.IsNullOrWhiteSpace(section) ? "Temp" : section.Trim();
            var canonical = ValidSections.FirstOrDefault(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
                throw new InvalidOperationException($"不支持的接口区域: {value}");
            return canonical;
        }

        private static string NormalizeInstanceDatatype(string? datatype)
        {
            // 保留指令专用实例类型。CTU_INT、CTD_INT、CTUD_INT 不是可互换的
            // IEC_COUNTER 别名；把它们折叠会使接口声明与调用指令不一致。
            return string.IsNullOrWhiteSpace(datatype) ? "Bool" : datatype.Trim();
        }

        private static string DbObjectList()
        {
            return "<ObjectList><MultilingualText ID=\"1\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"2\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>";
        }

        // ★ 空块 ObjectList：ID 布局与 TIA Portal 导出 XML 对齐
        // 导出空块: Comment(ID=1,2), Title(ID=3,4)
        private static string EmptyBlockObjectList()
        {
            return "<ObjectList><MultilingualText ID=\"1\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"2\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><MultilingualText ID=\"3\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"4\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>";
        }

        // SCL 块的 ObjectList —— 使用 <Text UId="21"> 格式
        // 参考项目 (D:\mcp服务器) 验证此格式在 TIA V19 中可行
        // 空块: <Text UId="21"> </Text> (含一个空格)
        // 有代码: <Text UId="21">{SecurityElement.Escape(sclCode)}</Text>
        private static string SclObjectList(string? sclCode)
        {
            var textContent = string.IsNullOrEmpty(sclCode) ? " " : SecurityElement.Escape(sclCode);
            var stXml = $"<StructuredText xmlns=\"{StNs}\"><Text UId=\"21\">{textContent}</Text></StructuredText>";
            return $"<ObjectList><MultilingualText ID=\"1\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"2\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><SW.Blocks.CompileUnit ID=\"10\" CompositionName=\"CompileUnits\"><AttributeList><NetworkSource>{stXml}</NetworkSource><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList><MultilingualText ID=\"11\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"12\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><MultilingualText ID=\"13\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"14\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit><MultilingualText ID=\"3\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"4\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>";
        }

        // ★ CompileUnit ID 从 10 开始，避免与块级 MultilingualText ID 冲突
        // 空块导出: Comment(ID=1,2), Title(ID=3,4)
        // CompileUnit: ID=10, internal Comment(ID=11,12), Title(ID=13,14)
        private static string CompileUnitsObjectList(List<string> flgNetList, string langStr, List<string>? titles = null)
        {
            var sb = new StringBuilder();
            // CompileUnit 与其 Comment/Title 文本各占一个独立的五节点 ID 段。
            // 显式计算所有 ID，避免跨网络复用或自增表达式求值顺序造成 Simatic ML ID 重复。
            var nextCompileUnitId = 10;
            for (int i = 0; i < flgNetList.Count; i++)
            {
                var compileUnitId = nextCompileUnitId;
                var commentId = compileUnitId + 1;
                var commentItemId = compileUnitId + 2;
                var titleId = compileUnitId + 3;
                var titleItemId = compileUnitId + 4;
                var net = flgNetList[i];
                // ★ 网络标题（支持中文），按索引从 titles 列表取，无则空
                string titleText = (titles != null && i < titles.Count && !string.IsNullOrEmpty(titles[i]))
                    ? SecurityElement.Escape(titles[i]) : "";
                sb.Append($"<SW.Blocks.CompileUnit ID=\"{compileUnitId}\" CompositionName=\"CompileUnits\"><AttributeList><NetworkSource>{net}</NetworkSource><ProgrammingLanguage>{langStr}</ProgrammingLanguage></AttributeList><ObjectList><MultilingualText ID=\"{commentId}\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"{commentItemId}\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><MultilingualText ID=\"{titleId}\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"{titleItemId}\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text>{titleText}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit>");
                nextCompileUnitId += 10;
            }
            return $"<ObjectList><MultilingualText ID=\"1\" CompositionName=\"Comment\"><ObjectList><MultilingualTextItem ID=\"2\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText><MultilingualText ID=\"3\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"4\" CompositionName=\"Items\"><AttributeList><Culture>zh-CN</Culture><Text/></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>{sb}</ObjectList>";
        }

        public static ProgrammingLanguage ParseProgrammingLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang)) return ProgrammingLanguage.LAD;
            if (lang.Equals("LAD", StringComparison.OrdinalIgnoreCase)) return ProgrammingLanguage.LAD;
            if (lang.Equals("FBD", StringComparison.OrdinalIgnoreCase)) return ProgrammingLanguage.FBD;
            if (lang.Equals("SCL", StringComparison.OrdinalIgnoreCase)) return ProgrammingLanguage.SCL;
            if (lang.Equals("STL", StringComparison.OrdinalIgnoreCase)) return ProgrammingLanguage.STL;
            if (lang.Equals("DB", StringComparison.OrdinalIgnoreCase)) return ProgrammingLanguage.DB;
            return ProgrammingLanguage.LAD;
        }

        public static string FilterInterfaceSections(string sectionsXml, string blockType)
        {
            if (string.IsNullOrWhiteSpace(sectionsXml)) return sectionsXml;
            var unsupported = new HashSet<string>(blockType switch
            {
                "OB" => new[] { "Input", "Output", "InOut", "Static" },
                "FC" => new[] { "Static" },
                "DB" => new[] { "Input", "Output", "InOut", "Temp", "Constant" },
                _ => Array.Empty<string>()
            }, StringComparer.OrdinalIgnoreCase);

            var wrapper = XDocument.Parse($"<Sections xmlns=\"{IfaceNs}\">{sectionsXml}</Sections>", LoadOptions.PreserveWhitespace);
            foreach (var section in wrapper.Root!.Elements().Where(e => e.Name.LocalName == "Section").ToList())
            {
                var name = section.Attribute("Name")?.Value ?? "";
                if (unsupported.Contains(name)) section.Remove();
            }
            return string.Concat(wrapper.Root.Nodes().Select(x => x.ToString(SaveOptions.DisableFormatting)));
        }

        /// <summary>
        /// 在原始块 XML 上做最小 DOM 修改：仅补充缺失接口成员并追加 CompileUnit。
        /// 原块 AttributeList、注释、访问属性、保留区和其他未知节点全部原样保留。
        /// </summary>
        public static string PatchExistingLadBlockXml(string originalBlockXml, string blockType,
            IEnumerable<string> newFlgNets, IEnumerable<string>? newTitles, ProgrammingLanguage language,
            IEnumerable<LadVariableDef>? variables)
        {
            if (string.IsNullOrWhiteSpace(originalBlockXml))
                throw new ArgumentException("原块 XML 为空", nameof(originalBlockXml));

            var doc = XDocument.Parse(originalBlockXml, LoadOptions.PreserveWhitespace);
            var block = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            if (block == null) throw new InvalidOperationException("原块 XML 中未找到 SW.Blocks.* 节点");

            MergeInterfaceMembers(block, blockType, variables ?? Enumerable.Empty<LadVariableDef>());

            var objectList = block.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
            if (objectList == null)
            {
                objectList = new XElement("ObjectList");
                block.Add(objectList);
            }

            var titles = (newTitles ?? Enumerable.Empty<string>()).ToList();
            var networks = (newFlgNets ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var nextId = NextAvailableObjectId(doc);
            // ★V17修复★ 新 CompileUnit 必须紧跟最后一个已有 CompileUnit 之后：
            // TIA 导出顺序为 Comment(MLT) → CompileUnit×N → Title(MLT)，
            // 若追加到 ObjectList 末尾会被块级 MultilingualText 分隔，导致
            // V17 导入报 "Objects modeled as IOrdered must be empty before importing"。
            XElement? lastCu = objectList.Elements()
                .LastOrDefault(e => e.Name.LocalName.EndsWith("CompileUnit", StringComparison.Ordinal));
            for (var i = 0; i < networks.Count; i++)
            {
                var flgNet = XElement.Parse(networks[i], LoadOptions.PreserveWhitespace);
                var title = i < titles.Count ? titles[i] ?? "" : "";
                var compileUnit = new XElement("SW.Blocks.CompileUnit",
                    new XAttribute("ID", nextId++),
                    new XAttribute("CompositionName", "CompileUnits"),
                    new XElement("AttributeList",
                        new XElement("NetworkSource", flgNet),
                        new XElement("ProgrammingLanguage", language.ToString())),
                    new XElement("ObjectList",
                        CreateMultilingualText(nextId++, nextId++, "Comment", ""),
                        CreateMultilingualText(nextId++, nextId++, "Title", title)));
                if (lastCu != null)
                    lastCu.AddAfterSelf(compileUnit);
                else
                    objectList.Add(compileUnit);
                lastCu = compileUnit;
            }

            doc.Declaration ??= new XDeclaration("1.0", "UTF-8", null);
            // ★V17修复★ XDocument.ToString() 不输出 XML 声明；TIA 导入器对缺 <?xml?> 声明
            // 的块 XML 报 "Objects modeled as IOrdered must be empty before importing"。
            // 手动拼接 UTF-8 声明（文件由 ImportXmlTemp 以 UTF-8+BOM 写入，声明必须为 utf-8）。
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + doc.ToString(SaveOptions.DisableFormatting);
        }

        private static void MergeInterfaceMembers(XElement block, string blockType, IEnumerable<LadVariableDef> variables)
        {
            var sections = block.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections"
                && e.Parent?.Name.LocalName == "Interface");
            if (sections == null) throw new InvalidOperationException("原块 XML 中未找到 Interface/Sections");

            var fragment = BuildInterfaceSections(variables.ToList());
            fragment = FilterInterfaceSections(fragment, blockType);
            var generated = XDocument.Parse($"<Sections xmlns=\"{IfaceNs}\">{fragment}</Sections>").Root!;
            var targetNs = sections.Name.Namespace;

            var existingMembers = sections.Elements().Where(e => e.Name.LocalName == "Section")
                .SelectMany(section => section.Elements().Where(e => e.Name.LocalName == "Member")
                    .Select(member => new
                    {
                        Name = member.Attribute("Name")?.Value ?? "",
                        Datatype = member.Attribute("Datatype")?.Value ?? "",
                        Section = section.Attribute("Name")?.Value ?? "",
                        Member = member
                    }))
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase);

            foreach (var generatedSection in generated.Elements().Where(e => e.Name.LocalName == "Section"))
            {
                var sectionName = generatedSection.Attribute("Name")?.Value ?? "";
                var targetSection = sections.Elements().FirstOrDefault(e => e.Name.LocalName == "Section"
                    && string.Equals(e.Attribute("Name")?.Value, sectionName, StringComparison.OrdinalIgnoreCase));
                if (targetSection == null)
                {
                    targetSection = new XElement(targetNs + "Section", new XAttribute("Name", sectionName));
                    sections.Add(targetSection);
                }

                foreach (var member in generatedSection.Elements().Where(e => e.Name.LocalName == "Member"))
                {
                    var name = member.Attribute("Name")?.Value ?? "";
                    var datatype = member.Attribute("Datatype")?.Value ?? "";
                    if (existingMembers.TryGetValue(name, out var existing))
                    {
                        if (!string.Equals(existing.Section, sectionName, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(existing.Datatype, datatype, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"接口变量 '{name}' 与原块冲突：原声明 {existing.Section}/{existing.Datatype}，请求 {sectionName}/{datatype}");
                        continue;
                    }
                    var cloned = CloneWithNamespace(member, targetNs);
                    targetSection.Add(cloned);
                    existingMembers[name] = new { Name = name, Datatype = datatype, Section = sectionName, Member = cloned };
                }
            }
        }

        private static XElement CloneWithNamespace(XElement source, XNamespace ns)
            => new XElement(ns + source.Name.LocalName,
                source.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name.LocalName, a.Value)),
                source.Nodes().Select(n => n is XElement e ? CloneWithNamespace(e, ns) : n));

        private static XElement CreateMultilingualText(int textId, int itemId, string composition, string value)
            => new XElement("MultilingualText",
                new XAttribute("ID", textId), new XAttribute("CompositionName", composition),
                new XElement("ObjectList",
                    new XElement("MultilingualTextItem",
                        new XAttribute("ID", itemId), new XAttribute("CompositionName", "Items"),
                        new XElement("AttributeList",
                            new XElement("Culture", "zh-CN"), new XElement("Text", value ?? "")))));

        private static int NextAvailableObjectId(XDocument doc)
        {
            var max = 0;
            foreach (var attribute in doc.Descendants().Attributes("ID"))
            {
                if (int.TryParse(attribute.Value, out var value)) max = Math.Max(max, value);
                else if (int.TryParse(attribute.Value, System.Globalization.NumberStyles.HexNumber, null, out value)) max = Math.Max(max, value);
            }
            return Math.Max(10, max + 1);
        }

        public static string MergeBlockXmlSections(string newBlockXml, string existingSectionsXml)
        {
            if (string.IsNullOrWhiteSpace(existingSectionsXml)) return newBlockXml;
            var doc = XDocument.Parse(newBlockXml, LoadOptions.PreserveWhitespace);
            var sections = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections");
            if (sections == null) return newBlockXml;
            var existing = XDocument.Parse(existingSectionsXml, LoadOptions.PreserveWhitespace);
            var oldSections = existing.Descendants().FirstOrDefault(e => e.Name.LocalName == "Sections") ?? existing.Root;
            if (oldSections == null) return newBlockXml;
            sections.ReplaceNodes(oldSections.Nodes().Select(n => n is XElement e ? new XElement(e) : n));
            return doc.ToString(SaveOptions.DisableFormatting);
        }
    }
}

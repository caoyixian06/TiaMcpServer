using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// V4.4 XML orchestration facade.
    /// AI submits stable engineering IR; this service converts it to the legacy LAD/HMI models.
    /// It deliberately hides Siemens XML UIds, FlgNet wiring and HMI pixel details from clients.
    /// </summary>
    public sealed class XmlOrchestrationService
    {
        private readonly Lazy<PortalService> _tia;

        private sealed class InstructionSpec
        {
            public string Name = "";
            public string Category = "";
            public string[] RequiredPins = Array.Empty<string>();
            public bool RequiresInstance;
            public string DefaultDatatype = "Bool";
        }

        private static readonly Dictionary<string, InstructionSpec> Instructions =
            new Dictionary<string, InstructionSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["TON"] = Spec("TON", "timer", new[] { "IN", "PT" }, true, "Time"),
                ["TOF"] = Spec("TOF", "timer", new[] { "IN", "PT" }, true, "Time"),
                ["TP"] = Spec("TP", "timer", new[] { "IN", "PT" }, true, "Time"),
                ["TONR"] = Spec("TONR", "timer", new[] { "IN", "PT" }, true, "Time"),
                ["CTU"] = Spec("CTU", "counter", new[] { "CU", "PV" }, true, "Int"),
                ["CTD"] = Spec("CTD", "counter", new[] { "CD", "PV" }, true, "Int"),
                ["CTUD"] = Spec("CTUD", "counter", new[] { "CU", "CD", "PV" }, true, "Int"),
                ["R_TRIG"] = Spec("R_TRIG", "edge", new[] { "CLK" }, true, "Bool"),
                ["F_TRIG"] = Spec("F_TRIG", "edge", new[] { "CLK" }, true, "Bool"),
                ["MOVE"] = Spec("MOVE", "data", new[] { "IN", "OUT" }, false, "Int"),
                ["ADD"] = Spec("ADD", "math", new[] { "IN1", "IN2", "OUT" }, false, "Int"),
                ["SUB"] = Spec("SUB", "math", new[] { "IN1", "IN2", "OUT" }, false, "Int"),
                ["MUL"] = Spec("MUL", "math", new[] { "IN1", "IN2", "OUT" }, false, "Int"),
                ["DIV"] = Spec("DIV", "math", new[] { "IN1", "IN2", "OUT" }, false, "Real"),
                ["EQ"] = Spec("EQ", "compare", new[] { "IN1", "IN2" }, false, "Int"),
                ["NE"] = Spec("NE", "compare", new[] { "IN1", "IN2" }, false, "Int"),
                ["GT"] = Spec("GT", "compare", new[] { "IN1", "IN2" }, false, "Int"),
                ["GE"] = Spec("GE", "compare", new[] { "IN1", "IN2" }, false, "Int"),
                ["LT"] = Spec("LT", "compare", new[] { "IN1", "IN2" }, false, "Int"),
                ["LE"] = Spec("LE", "compare", new[] { "IN1", "IN2" }, false, "Int"),
                ["NORM_X"] = Spec("NORM_X", "convert", new[] { "MIN", "VALUE", "MAX", "OUT" }, false, "Real"),
                ["SCALE_X"] = Spec("SCALE_X", "convert", new[] { "MIN", "VALUE", "MAX", "OUT" }, false, "Real")
            };

        public XmlOrchestrationService(Lazy<PortalService> tia) { _tia = tia; }

        private static InstructionSpec Spec(string name, string category, string[] pins, bool instance, string datatype)
            => new InstructionSpec { Name = name, Category = category, RequiredPins = pins, RequiresInstance = instance, DefaultDatatype = datatype };

        public string GetCapabilities()
        {
            return JsonConvert.SerializeObject(new
            {
                success = true,
                version = "4.4.4-hmi-controls",
                purpose = "AI submits compact IR; server owns XML structure, UIds, wiring and normalization.",
                ladIr = new
                {
                    document = new { networks = "array" },
                    network = new { title = "string", variables = "array", steps = "array" },
                    stepTypes = new[] { "contact", "edgeContact", "coil", "setCoil", "resetCoil", "box", "call", "parallel" },
                    contactEdges = new[] { "positive", "negative" },
                    coilModes = new[] { "normal", "set", "reset" },
                    productionProfile = "Calibrated from 223 TIA Portal V19 PLC XML exports: Contact, NContact, PContact, Coil, SCoil, RCoil, Eq/Ne/Gt/Ge/Lt/Le, Move, Add/Sub/Mul and branch O parts.",
                    note = "Clients no longer need to inspect FlgNetBuilder or hand-write Siemens XML."
                },
                hmiIr = new
                {
                    schemaVersion = "hmi-ir/1.2",
                    layout = "grid or pixel",
                    components = HmiControlCatalog.CanonicalTypes,
                    aliases = HmiControlCatalog.AliasMap,
                    compositeComponents = new[] { "pumpFaceplate" },
                    animations = new[] { "visibility", "singleBitVisibility", "enabling" },
                    buttonBehaviors = HmiActionCatalog.Behaviors,
                    eventTriggers = HmiActionCatalog.Triggers,
                    systemFunctions = HmiActionCatalog.Functions,
                    layers = "items may specify layer=0..31; screen.layers may name layers",
                    groups = "group.members references existing non-group controls on the same layer",
                    note = "Based on 41 user HMI exports: Line, Group, SymbolicIOField, GraphicView and verified animation structures were added. SymbolLibrary remains template-only because OcxState is opaque."
                },
                instructionCount = Instructions.Count,
                instructions = Instructions.Values.OrderBy(x => x.Category).ThenBy(x => x.Name).Select(x => new
                {
                    name = x.Name,
                    category = x.Category,
                    requiredPins = x.RequiredPins,
                    requiresInstance = x.RequiresInstance,
                    defaultDatatype = x.DefaultDatatype
                })
            }, Formatting.Indented);
        }

        public string GetHmiCapabilities()
        {
            return JsonConvert.SerializeObject(new
            {
                success = true,
                version = "4.4.4-hmi-controls",
                schemaVersion = "hmi-ir/1.2",
                controls = HmiControlCatalog.CanonicalTypes,
                aliases = HmiControlCatalog.AliasMap,
                requiredBindings = new
                {
                    indicator = new[] { "tag" },
                    @switch = new[] { "tag" },
                    iofield = new[] { "tag" },
                    symboliciofield = new[] { "tag", "textList" },
                    graphicview = new[] { "picture" },
                    group = new[] { "members[]" }
                },
                animations = new
                {
                    visibility = new[] { "tag", "rangeStart", "rangeEnd", "visible" },
                    singleBitVisibility = new[] { "tag", "bitPosition", "visible" },
                    enabling = new[] { "tag", "rangeStart", "rangeEnd", "objectEnabled" }
                },
                events = new { behaviors = HmiActionCatalog.Behaviors, triggers = HmiActionCatalog.Triggers, functions = HmiActionCatalog.Functions },
                unsupported = new[]
                {
                    new { control = "SymbolLibrary", reason = "requires opaque OcxState; arbitrary generation is deliberately disabled" },
                    new { control = "UserView", reason = "only one sample and no verified data-column binding contract" }
                }
            }, Formatting.Indented);
        }

        public string CompileLadIr(string irJson)
        {
            try
            {
                var root = JObject.Parse(irJson ?? "{}");
                var networks = root["networks"] as JArray;
                if (networks == null && root["steps"] is JArray)
                    networks = new JArray(root);
                if (networks == null || networks.Count == 0)
                    return Error("IR_NO_NETWORKS", "LAD IR must contain at least one network.");

                var errors = new JArray();
                var warnings = new JArray();
                var legacy = new JArray();
                var index = 0;
                foreach (var token in networks)
                {
                    index++;
                    if (!(token is JObject network))
                    {
                        AddIssue(errors, "NETWORK_INVALID", $"networks[{index - 1}] must be an object.", $"networks[{index - 1}]");
                        continue;
                    }
                    var converted = ConvertNetwork(network, index, errors, warnings);
                    if (converted != null) legacy.Add(converted);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = errors.Count == 0,
                    schemaVersion = "lad-ir/1.1",
                    networkCount = legacy.Count,
                    errors,
                    warnings,
                    legacyNetworks = legacy,
                    legacyNetworksJson = legacy.ToString(Formatting.None),
                    nextAction = errors.Count == 0
                        ? "Call apply_lad_ir to append these networks; do not hand-write XML."
                        : "Fix the reported IR fields and compile again."
                }, Formatting.Indented);
            }
            catch (JsonException ex) { return Error("IR_JSON_INVALID", ex.Message); }
            catch (Exception ex) { return Error("IR_COMPILE_FAILED", ex.Message); }
        }

        public string ValidateLadIr(string irJson)
        {
            var compiledText = CompileLadIr(irJson);
            var compiled = JObject.Parse(compiledText);
            var errors = compiled["errors"] as JArray ?? new JArray();
            var warnings = compiled["warnings"] as JArray ?? new JArray();
            if (compiled["success"]?.Value<bool>() == true)
            {
                var networkDefs = (compiled["legacyNetworks"] as JArray ?? new JArray())
                    .ToObject<List<LadNetworkDef>>() ?? new List<LadNetworkDef>();
                PortalService.PrepareLadProgram(networkDefs, null, null, out var sharedErrors, out var sharedWarnings);
                foreach (var error in sharedErrors)
                    AddIssue(errors, "LAD_RULE_VIOLATION", error, "networks");
                foreach (var warning in sharedWarnings)
                    AddIssue(warnings, "LAD_RULE_WARNING", warning, "networks");
                var normalized = JArray.FromObject(networkDefs);
                compiled["legacyNetworks"] = normalized;
                compiled["legacyNetworksJson"] = normalized.ToString(Formatting.None);
            }
            compiled["success"] = errors.Count == 0;
            compiled["validated"] = true;
            compiled["productionProfile"] = "TIA Portal V19 formal-project calibration";
            compiled["nextAction"] = errors.Count == 0 ? "Call apply_lad_ir." : "Fix the reported LAD IR fields.";
            return compiled.ToString(Formatting.Indented);
        }

        public string GetLadReferenceProfile()
        {
            return JsonConvert.SerializeObject(new
            {
                success = true,
                source = "Formal TIA Portal V19 project XML supplied by the user",
                xmlFiles = 223,
                observedParts = new Dictionary<string, int>
                {
                    ["Contact"] = 1780, ["Eq"] = 518, ["Move"] = 516, ["Coil"] = 360,
                    ["RCoil"] = 320, ["Ge"] = 205, ["Le"] = 195, ["SCoil"] = 193,
                    ["O"] = 95, ["Sub"] = 67, ["SdCoil"] = 44, ["Add"] = 15,
                    ["PContact"] = 10, ["Ne"] = 9, ["Gt"] = 8, ["NContact"] = 8,
                    ["Lt"] = 7, ["CuCoil"] = 6, ["Mul"] = 2, ["Sd"] = 10
                },
                supportedByIr = new[] { "Contact", "NContact", "PContact", "Coil", "SCoil", "RCoil", "Eq", "Ne", "Gt", "Ge", "Lt", "Le", "Move", "Add", "Sub", "Mul", "O/parallel" },
                deferred = new[] { "SdCoil", "CuCoil", "vendor-specific or uncommon coil semantics" },
                note = "Deferred instructions are intentionally not generated until their semantics and import behavior are verified."
            }, Formatting.Indented);
        }

        public string ApplyLadIr(string blockName, string irJson, string? plcName, bool compileAfter)
        {
            if (!compileAfter)
                return Error("LAD_COMPILE_REQUIRED", "apply_lad_ir 必须启用 compileAfter=true，以便用 TIA 编译结果作为事务提交条件。仅预览请使用 compile_lad_ir。");
            var compiledText = ValidateLadIr(irJson);
            var compiled = JObject.Parse(compiledText);
            if (compiled["success"]?.Value<bool>() != true) return compiledText;
            var networksJson = compiled["legacyNetworksJson"]?.ToString() ?? "[]";
            return _tia.Value.AddLadNetworksBatch(blockName, networksJson, plcName, compileAfter);
        }

        private static JObject? ConvertNetwork(JObject input, int networkIndex, JArray errors, JArray warnings)
        {
            var output = new JObject
            {
                ["title"] = input["title"]?.ToString() ?? $"Network {networkIndex}"
            };
            if (input["variables"] is JArray vars) output["variables"] = vars.DeepClone();

            var steps = input["steps"] as JArray;
            if (steps == null || steps.Count == 0)
            {
                AddIssue(errors, "NETWORK_EMPTY", "Network must contain steps.", $"networks[{networkIndex - 1}].steps");
                return null;
            }

            var rung = new JArray();
            for (var i = 0; i < steps.Count; i++)
            {
                if (!(steps[i] is JObject step))
                {
                    AddIssue(errors, "STEP_INVALID", "Step must be an object.", $"networks[{networkIndex - 1}].steps[{i}]");
                    continue;
                }
                var item = ConvertStep(step, $"networks[{networkIndex - 1}].steps[{i}]", errors, warnings);
                if (item != null) rung.Add(item);
            }

            if (rung.Count > 0 && ((JObject)rung[rung.Count - 1]!)["contact"] != null)
                AddIssue(warnings, "OPEN_RUNG", "The network ends with a contact and has no explicit output.", $"networks[{networkIndex - 1}]");

            output["rung"] = rung;
            return output;
        }

        private static JObject? ConvertStep(JObject step, string path, JArray errors, JArray warnings)
        {
            var type = (step["type"]?.ToString() ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "contact":
                case "edgecontact":
                {
                    var tag = First(step, "tag", "variable", "name");
                    if (string.IsNullOrWhiteSpace(tag)) { AddIssue(errors, "CONTACT_TAG_REQUIRED", "Contact requires tag.", path); return null; }
                    var edge = (step["edge"]?.ToString() ?? "").Trim().ToLowerInvariant();
                    string? edgeType = null;
                    if (edge == "positive" || edge == "rising" || edge == "p" || edge == "pcontact") edgeType = "pcontact";
                    else if (edge == "negative" || edge == "falling" || edge == "n" || edge == "ncontact") edgeType = "ncontact";
                    else if (!string.IsNullOrWhiteSpace(edge)) AddIssue(errors, "CONTACT_EDGE_INVALID", "edge must be positive/rising or negative/falling.", path + ".edge");
                    var contact = new JObject { ["contact"] = tag, ["negated"] = step["negated"]?.Value<bool>() ?? false, ["type"] = edgeType };
                    var bit = First(step, "bit", "edgeMemory", "memory");
                    if (edgeType != null)
                    {
                        if (string.IsNullOrWhiteSpace(bit)) AddIssue(errors, "EDGE_MEMORY_REQUIRED", "Edge contact requires bit/edgeMemory storage tag.", path);
                        else contact["bit"] = bit;
                    }
                    return new JObject { ["contact"] = contact };
                }
                case "coil":
                case "setcoil":
                case "resetcoil":
                {
                    var tag = First(step, "tag", "variable", "name");
                    if (string.IsNullOrWhiteSpace(tag)) { AddIssue(errors, "COIL_TAG_REQUIRED", "Coil requires tag.", path); return null; }
                    var mode = (step["mode"]?.ToString() ?? "").Trim().ToLowerInvariant();
                    if (type == "setcoil") mode = "set";
                    if (type == "resetcoil") mode = "reset";
                    if (mode == "normal") mode = "";
                    if (mode != "" && mode != "set" && mode != "reset")
                        AddIssue(errors, "COIL_MODE_INVALID", "Coil mode must be normal, set or reset.", path + ".mode");
                    return new JObject { ["coil"] = new JObject { ["coil"] = tag, ["type"] = mode } };
                }
                case "box":
                {
                    var name = First(step, "instruction", "box", "name").ToUpperInvariant();
                    if (!Instructions.TryGetValue(name, out var spec))
                    {
                        AddIssue(errors, "INSTRUCTION_UNKNOWN", $"Unknown instruction '{name}'.", path);
                        return null;
                    }
                    var pins = step["pins"] as JObject ?? new JObject();
                    // ★修复★ 计数器（CTU/CTD/CTUD）的触发引脚（CU/CD）不能接字面量 TRUE：
                    // 生成的"电源轨直连 CU"会被 TIA 编译拒绝（A preceding Boolean logic
                    // operation is missing）。真实工程中计数器触发脚前必须有触点，校验期
                    // 提前报错，避免写入后编译失败再回滚。
                    var counterTrigger = LadInstructionCatalog.GetTriggerPin(name);
                    if (string.Equals(name, "CTU", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "CTD", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "CTUD", StringComparison.OrdinalIgnoreCase))
                    {
                        var triggerProp = pins.Properties().FirstOrDefault(p =>
                            p.Name.Equals(counterTrigger, StringComparison.OrdinalIgnoreCase));
                        var triggerValue = triggerProp?.Value?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(triggerValue)
                            && triggerValue.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
                            AddIssue(errors, "COUNTER_TRIGGER_LITERAL",
                                $"{name} 的 {counterTrigger} 引脚不能接字面量 TRUE；TIA 要求计数触发前置布尔逻辑（请先串联一个触点 step）。", path + ".pins");
                    }
                    foreach (var required in LadInstructionCatalog.GetRequiredPins(name))
                        if (!pins.Properties().Any(p => p.Name.Equals(required, StringComparison.OrdinalIgnoreCase)
                                                       && !string.IsNullOrWhiteSpace(p.Value?.ToString())))
                            AddIssue(errors, "PIN_REQUIRED", $"{name} requires pin {required}.", path + ".pins");
                    var instance = step["instance"]?.ToString();
                    var instanceScope = (step["instanceScope"]?.ToString() ?? "").Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(instanceScope) && instanceScope != "local" && instanceScope != "global")
                        AddIssue(errors, "INSTANCE_SCOPE_INVALID", "instanceScope must be local or global.", path + ".instanceScope");
                    if (spec.RequiresInstance && string.IsNullOrWhiteSpace(instance))
                        AddIssue(warnings, "INSTANCE_ALLOCATED_AT_APPLY", $"{name} 未指定实例；validate/apply 将使用同一规则分配批次内唯一名称。OB/FC 必须显式使用 instanceScope=global 和已有实例 DB。", path);
                    string normalizedDatatype;
                    try { normalizedDatatype = LadInstructionCatalog.NormalizeValueDatatype(name, step["datatype"]?.ToString() ?? spec.DefaultDatatype); }
                    catch (Exception ex) { AddIssue(errors, "DATATYPE_INVALID", ex.Message, path + ".datatype"); normalizedDatatype = spec.DefaultDatatype; }
                    return new JObject { ["box"] = new JObject
                    {
                        ["box"] = name,
                        ["pins"] = pins.DeepClone(),
                        ["datatype"] = normalizedDatatype,
                        ["destType"] = step["destType"]?.ToString(),
                        ["instance"] = instance,
                        ["instanceScope"] = instanceScope,
                        ["negateOutput"] = step["negateOutput"]?.Value<bool>() ?? false,
                        ["equation"] = step["equation"]?.ToString()
                    } };
                }
                case "call":
                {
                    var block = First(step, "block", "blockName", "name");
                    if (string.IsNullOrWhiteSpace(block)) { AddIssue(errors, "CALL_BLOCK_REQUIRED", "Call requires block name.", path); return null; }
                    var blockType = (step["blockType"]?.ToString() ?? "FB").Trim().ToUpperInvariant();
                    if (blockType != "FB" && blockType != "FC")
                        AddIssue(errors, "CALL_BLOCK_TYPE_INVALID", "blockType must be FB or FC.", path + ".blockType");
                    var instance = step["instance"]?.ToString();
                    var instanceScope = step["instanceScope"]?.ToString();
                    if (blockType == "FB")
                    {
                        if (string.IsNullOrWhiteSpace(instance))
                            AddIssue(errors, "CALL_INSTANCE_REQUIRED", "FB call requires an explicit instance name.", path + ".instance");
                        if (!string.Equals(instanceScope, "local", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(instanceScope, "global", StringComparison.OrdinalIgnoreCase))
                            AddIssue(errors, "CALL_INSTANCE_SCOPE_REQUIRED", "FB call requires instanceScope=local or global; the server will not guess instance semantics.", path + ".instanceScope");
                    }
                    else if (!string.IsNullOrWhiteSpace(instance))
                    {
                        AddIssue(errors, "FC_INSTANCE_FORBIDDEN", "FC call must not define an instance.", path + ".instance");
                    }
                    var pinArray = new JArray();
                    if (step["pins"] is JArray pinsArray)
                    {
                        foreach (var pinToken in pinsArray.OfType<JObject>())
                        {
                            var pinName = First(pinToken, "name", "pin");
                            var variable = First(pinToken, "variable", "tag", "value");
                            var direction = (pinToken["direction"]?.ToString() ?? "in").ToLowerInvariant();
                            if (string.IsNullOrWhiteSpace(pinName) || string.IsNullOrWhiteSpace(variable))
                                AddIssue(errors, "CALL_PIN_INVALID", "Call pin requires name and variable/value.", path + ".pins");
                            if (direction != "in" && direction != "out" && direction != "inout")
                                AddIssue(errors, "CALL_PIN_DIRECTION_INVALID", "Pin direction must be in, out or inout.", path + ".pins");
                            if (direction == "inout")
                                AddIssue(errors, "CALL_INOUT_UNSUPPORTED", "InOut call pins are disabled until their bidirectional TIA XML wiring has passed regression tests.", path + ".pins");
                            var pinDatatype = pinToken["datatype"]?.ToString();
                            if (string.IsNullOrWhiteSpace(pinDatatype))
                                AddIssue(errors, "CALL_PIN_DATATYPE_REQUIRED", "Each call pin requires explicit datatype; guessing from the variable name is unsafe.", path + ".pins");
                            if (LooksLikeLadConstant(variable))
                                AddIssue(errors, "CALL_LITERAL_UNSUPPORTED", "Call pins currently require a symbolic variable binding; use an explicit intermediate variable for literals.", path + ".pins");
                            pinArray.Add(new JObject { ["name"] = pinName, ["variable"] = variable, ["direction"] = direction, ["scope"] = pinToken["scope"]?.ToString(), ["datatype"] = pinDatatype });
                        }
                    }
                    else if (step["pins"] is JObject)
                    {
                        AddIssue(errors, "CALL_PIN_ARRAY_REQUIRED", "Call pins must use array form with explicit name, variable, direction and datatype.", path + ".pins");
                    }
                    return new JObject { ["call"] = new JObject
                    {
                        ["call"] = block,
                        ["instance"] = instance,
                        ["instanceScope"] = step["instanceScope"]?.ToString(),
                        ["blockType"] = blockType,
                        ["pins"] = pinArray
                    } };
                }
                case "parallel":
                case "branch":
                {
                    var branches = step["branches"] as JArray;
                    if (branches == null || branches.Count < 2)
                    {
                        AddIssue(errors, "PARALLEL_BRANCHES_REQUIRED", "Parallel requires at least two branches.", path);
                        return null;
                    }
                    var branchOut = new JArray();
                    for (var b = 0; b < branches.Count; b++)
                    {
                        var list = branches[b] as JArray;
                        if (list == null) { AddIssue(errors, "BRANCH_INVALID", "Branch must be an array.", path + $".branches[{b}]"); continue; }
                        var items = new JArray();
                        for (var i = 0; i < list.Count; i++)
                        {
                            var obj = list[i] as JObject;
                            if (obj == null) continue;
                            var converted = ConvertStep(obj, path + $".branches[{b}][{i}]", errors, warnings);
                            if (converted != null) items.Add(converted);
                        }
                        branchOut.Add(items);
                    }
                    return new JObject { ["branch"] = new JObject { ["branch"] = branchOut } };
                }
                default:
                    AddIssue(errors, "STEP_TYPE_UNKNOWN", $"Unknown step type '{type}'.", path);
                    return null;
            }
        }

        public string CompileHmiIr(string irJson)
        {
            try
            {
                var root = JObject.Parse(irJson ?? "{}");
                var screen = root["screen"] as JObject ?? root;
                var width = screen["width"]?.Value<int>() ?? 1280;
                var height = screen["height"]?.Value<int>() ?? 800;
                var columns = screen["columns"]?.Value<int>() ?? 12;
                var rows = screen["rows"]?.Value<int>() ?? 8;
                if (width <= 0 || height <= 0 || columns <= 0 || rows <= 0)
                    return Error("HMI_LAYOUT_INVALID", "Screen width, height, columns and rows must be positive.");

                var source = screen["components"] as JArray ?? new JArray();
                var expanded = new JArray();
                var errors = new JArray();
                var warnings = new JArray();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var token in source)
                {
                    if (!(token is JObject c)) continue;
                    ExpandHmiComponent(c, expanded, warnings);
                }

                foreach (var token in expanded.OfType<JObject>())
                {
                    var name = token["name"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) AddIssue(errors, "HMI_NAME_REQUIRED", "Component name is required.", "components");
                    else if (!names.Add(name)) AddIssue(errors, "HMI_NAME_DUPLICATE", $"Duplicate component name '{name}'.", "components");
                    var canonical = HmiControlCatalog.Canonicalize(token["type"]?.ToString());
                    if (string.IsNullOrEmpty(canonical))
                    {
                        AddIssue(errors, "HMI_COMPONENT_TYPE_UNKNOWN", $"Unsupported HMI component type '{token["type"]}'.", "components");
                        continue;
                    }
                    token["type"] = canonical;
                    if (HmiControlCatalog.IsGroup(canonical)) continue;

                    // 已给出像素坐标时保留；否则按网格归一化。
                    if (token["left"] != null && token["top"] != null && token["width"] != null && token["height"] != null)
                        continue;
                    var col = token["column"]?.Value<int>() ?? 0;
                    var row = token["row"]?.Value<int>() ?? 0;
                    var colSpan = Math.Max(1, token["columnSpan"]?.Value<int>() ?? 1);
                    var rowSpan = Math.Max(1, token["rowSpan"]?.Value<int>() ?? 1);
                    if (col < 0 || row < 0 || col + colSpan > columns || row + rowSpan > rows)
                        AddIssue(errors, "HMI_OUT_OF_BOUNDS", $"Component '{name}' is outside the grid.", "components");
                    token["left"] = (int)Math.Round((double)width * col / columns);
                    token["top"] = (int)Math.Round((double)height * row / rows);
                    token["width"] = (int)Math.Round((double)width * colSpan / columns);
                    token["height"] = (int)Math.Round((double)height * rowSpan / rows);
                }

                return JsonConvert.SerializeObject(new
                {
                    success = errors.Count == 0,
                    schemaVersion = "hmi-ir/1.2",
                    screen = new
                    {
                        width, height, columns, rows,
                        backColor = screen["backColor"]?.ToString() ?? "240,240,240",
                        layers = screen["layers"]?.DeepClone() ?? new JArray()
                    },
                    componentCount = expanded.Count,
                    errors,
                    warnings,
                    normalizedComponents = expanded,
                    nextAction = "Use the normalized plan with the existing HMI builder/importer. AI should not calculate pixel coordinates or hand-write button event XML."
                }, Formatting.Indented);
            }
            catch (JsonException ex) { return Error("HMI_IR_JSON_INVALID", ex.Message); }
            catch (Exception ex) { return Error("HMI_IR_COMPILE_FAILED", ex.Message); }
        }


        public string ValidateHmiIr(string irJson)
        {
            var compiledText = CompileHmiIr(irJson);
            var compiled = JObject.Parse(compiledText);
            if (compiled["success"]?.Value<bool>() != true) return compiledText;

            var screenInfo = compiled["screen"] as JObject ?? new JObject();
            var spec = new JObject
            {
                ["width"] = screenInfo["width"]?.DeepClone() ?? new JValue(1280),
                ["height"] = screenInfo["height"]?.DeepClone() ?? new JValue(800),
                ["backColor"] = screenInfo["backColor"]?.DeepClone() ?? new JValue("240,240,240"),
                ["layers"] = screenInfo["layers"]?.DeepClone() ?? new JArray(),
                ["items"] = new JArray()
            };
            var items = (JArray)spec["items"]!;
            foreach (var component in (compiled["normalizedComponents"] as JArray ?? new JArray()).OfType<JObject>())
                items.Add(ToLegacyHmiItem(component));

            var validatedText = _tia.Value.ValidateHmiScreenSpecJson(
                spec.ToString(Formatting.None),
                screenInfo["width"]?.Value<int?>(),
                screenInfo["height"]?.Value<int?>());
            var validated = JObject.Parse(validatedText);
            validated["schemaVersion"] = "hmi-ir/1.2";
            validated["compiled"] = true;
            validated["validated"] = validated["success"]?.Value<bool>() == true;
            validated["screen"] = screenInfo.DeepClone();
            validated["normalizedComponents"] = validated["normalizedItems"]?.DeepClone() ?? new JArray();
            validated.Remove("normalizedItems");
            var warnings = validated["warnings"] as JArray ?? new JArray();
            foreach (var warning in compiled["warnings"] as JArray ?? new JArray()) warnings.Add(warning.DeepClone());
            validated["warnings"] = warnings;
            validated["nextAction"] = validated["success"]?.Value<bool>() == true ? "Call apply_hmi_ir." : "Fix the reported HMI IR fields.";
            return validated.ToString(Formatting.Indented);
        }

        private static void ValidateHmiAction(JObject action, string path, JArray errors)
        {
            try { HmiActionCatalog.Validate(HmiActionCatalog.FromJson(action)); }
            catch (Exception ex) { AddIssue(errors, "HMI_ACTION_INVALID", ex.Message, path); }
        }

        public string ApplyHmiIr(string screenName, string irJson, string? hmiDeviceName)
        {
            if (string.IsNullOrWhiteSpace(screenName)) return Error("HMI_SCREEN_NAME_REQUIRED", "screenName is required.");
            var validatedText = ValidateHmiIr(irJson);
            var validated = JObject.Parse(validatedText);
            if (validated["success"]?.Value<bool>() != true) return validatedText;
            var screenInfo = validated["screen"] as JObject ?? new JObject();
            var spec = new JObject
            {
                ["screenName"] = screenName,
                ["width"] = screenInfo["width"]?.DeepClone() ?? new JValue(1280),
                ["height"] = screenInfo["height"]?.DeepClone() ?? new JValue(800),
                ["backColor"] = screenInfo["backColor"]?.DeepClone() ?? new JValue("240,240,240"),
                ["layers"] = screenInfo["layers"]?.DeepClone() ?? new JArray(),
                ["items"] = new JArray()
            };
            var items = (JArray)spec["items"]!;
            foreach (var c in (validated["normalizedComponents"] as JArray ?? new JArray()).OfType<JObject>())
                items.Add(ToLegacyHmiItem(c));
            var resultText = _tia.Value.CreateHmiScreenFromSpec(spec.ToString(Formatting.None), hmiDeviceName);
            try
            {
                var result = JObject.Parse(resultText);
                result["orchestratorVersion"] = "4.4.4-hmi-controls";
                result["schemaVersion"] = "hmi-ir/1.2";
                result["normalizedComponentCount"] = items.Count;
                result["note"] = "Generated through HMI IR. UID, event XML and tag links were owned by the server.";
                return result.ToString(Formatting.Indented);
            }
            catch { return resultText; }
        }

        private static JObject ToLegacyHmiItem(JObject source)
        {
            var item = (JObject)source.DeepClone();
            var type = HmiControlCatalog.Canonicalize(item["type"]?.ToString());
            if (string.IsNullOrEmpty(type)) return item;
            item["type"] = type;
            if (type == "navigation")
            {
                item["type"] = "button"; item["eventType"] = "activateScreen";
                item["eventTarget"] = item["targetScreen"] ?? item["eventTarget"] ?? "";
            }
            else if (type == "button")
            {
                item["type"] = "button";
                var behavior = HmiActionCatalog.CanonicalBehavior(item["behavior"]?.ToString() ?? item["eventType"]?.ToString() ?? "momentary");
                item["eventType"] = behavior;
                if (behavior == "activateScreen") item["eventTarget"] = item["targetScreen"] ?? item["eventTarget"] ?? "";
            }
            return item;
        }

        private static void ExpandHmiComponent(JObject source, JArray target, JArray warnings)
        {
            var type = (source["type"]?.ToString() ?? "").Trim();
            if (!type.Equals("pumpFaceplate", StringComparison.OrdinalIgnoreCase))
            {
                target.Add(source.DeepClone());
                return;
            }

            var name = source["name"]?.ToString() ?? "Pump";
            var tagPrefix = source["tagPrefix"]?.ToString() ?? name;
            var col = source["column"]?.Value<int>() ?? 0;
            var row = source["row"]?.Value<int>() ?? 0;
            var span = Math.Max(3, source["columnSpan"]?.Value<int>() ?? 3);
            target.Add(Component("status", name + "_Running", col, row, 1, 1, tagPrefix + ".Running"));
            target.Add(Component("commandButton", name + "_Start", col + 1, row, 1, 1, tagPrefix + ".StartCmd", "启动", "momentary"));
            target.Add(Component("commandButton", name + "_Stop", col + 2, row, 1, 1, tagPrefix + ".StopCmd", "停止", "momentary"));
            target.Add(Component("modeSelector", name + "_Mode", col, row + 1, Math.Min(span, 2), 1, tagPrefix + ".AutoMode"));
            target.Add(Component("commandButton", name + "_Reset", col + 2, row + 1, 1, 1, tagPrefix + ".ResetCmd", "复位", "momentary"));
            AddIssue(warnings, "HMI_COMPONENT_EXPANDED", $"pumpFaceplate '{name}' expanded into reusable primitive components.", "components");
        }

        private static JObject Component(string type, string name, int col, int row, int colSpan, int rowSpan, string tag, string? text = null, string? behavior = null)
        {
            var o = new JObject
            {
                ["type"] = type, ["name"] = name, ["column"] = col, ["row"] = row,
                ["columnSpan"] = colSpan, ["rowSpan"] = rowSpan, ["tag"] = tag
            };
            if (text != null) o["text"] = text;
            if (behavior != null) o["behavior"] = behavior;
            return o;
        }

        private static bool LooksLikeLadConstant(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("T#", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("TIME#", StringComparison.OrdinalIgnoreCase)) return true;
            return decimal.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        private static string First(JObject obj, params string[] names)
        {
            foreach (var name in names)
            {
                var value = obj[name]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return "";
        }

        private static void AddIssue(JArray array, string code, string message, string path)
            => array.Add(new JObject { ["code"] = code, ["message"] = message, ["path"] = path });

        private static string Error(string code, string message)
            => JsonConvert.SerializeObject(new { success = false, errors = new[] { new { code, message } } }, Formatting.Indented);
    }
}

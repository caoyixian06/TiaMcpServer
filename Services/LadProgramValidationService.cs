using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer
{
    public partial class PortalService
    {
        /// <summary>
        /// LAD validate/apply 的唯一共享入口：先规范化并分配 IEC 实例，再执行网络级与程序级校验。
        /// reservedVariables / reservedInstanceNames 用于 apply 时防止与原块接口和既有实例冲突。
        /// </summary>
        internal static bool PrepareLadProgram(
            IList<LadNetworkDef> networks,
            IEnumerable<LadVariableDef>? reservedVariables,
            IEnumerable<string>? reservedInstanceNames,
            out List<string> errors,
            out List<string> warnings)
        {
            errors = new List<string>();
            warnings = new List<string>();
            if (networks == null || networks.Count == 0)
            {
                errors.Add("LAD 程序至少需要一个网络");
                return false;
            }

            var reservedVars = (reservedVariables ?? Enumerable.Empty<LadVariableDef>()).ToList();
            ValidateVariableDeclarations(networks, reservedVars, errors);
            var usedVariableNames = new HashSet<string>(
                reservedVars.Where(v => !string.IsNullOrWhiteSpace(v.name)).Select(v => v.name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var variable in EnumerateVariables(networks))
                if (!string.IsNullOrWhiteSpace(variable.name)) usedVariableNames.Add(variable.name);

            var usedInstances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in reservedInstanceNames ?? Enumerable.Empty<string>())
                if (!string.IsNullOrWhiteSpace(name)) usedInstances[name] = "原块既有实例";

            for (var i = 0; i < networks.Count; i++)
                NormalizeNetworkInstances(networks[i], $"network[{i}]", usedVariableNames, usedInstances, errors);

            // IEC 本地实例会在规范化阶段追加为 Static 变量；再次执行声明冲突检查，
            // 防止显式实例名与其他网络/原块变量同名但类型或 Section 不一致。
            ValidateVariableDeclarations(networks, reservedVars, errors);

            for (var i = 0; i < networks.Count; i++)
            {
                if (!ValidateLadNetworkDefinition(networks[i], out var childErrors, out var childWarnings))
                    errors.AddRange(childErrors.Select(x => $"network[{i}]: {x}"));
                warnings.AddRange(childWarnings.Select(x => $"network[{i}]: {x}"));
            }

            ValidateProgramConflicts(networks, errors);
            return errors.Count == 0;
        }

        internal static IEnumerable<string> ExtractLadInstanceNames(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return Enumerable.Empty<string>();
            try
            {
                var document = System.Xml.Linq.XDocument.Parse(xml);
                return document.Descendants()
                    .Where(e => e.Name.LocalName == "Instance")
                    .SelectMany(e => e.Descendants().Where(x => x.Name.LocalName == "Component"))
                    .Select(e => e.Attribute("Name")?.Value ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }


        private static void ValidateVariableDeclarations(
            IEnumerable<LadNetworkDef> networks,
            IEnumerable<LadVariableDef> reservedVariables,
            ICollection<string> errors)
        {
            var declarations = new Dictionary<string, LadVariableDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in reservedVariables.Concat(EnumerateVariables(networks)))
            {
                if (string.IsNullOrWhiteSpace(variable.name)) continue;
                if (!declarations.TryGetValue(variable.name, out var prior))
                {
                    declarations[variable.name] = variable;
                    continue;
                }
                if (!string.Equals(prior.datatype, variable.datatype, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(prior.section, variable.section, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"变量 '{variable.name}' 存在冲突声明：{prior.section}/{prior.datatype} 与 {variable.section}/{variable.datatype}");
            }
        }

        private static IEnumerable<LadVariableDef> EnumerateVariables(IEnumerable<LadNetworkDef> networks)
        {
            foreach (var network in networks)
            {
                foreach (var variable in network.variables ?? Enumerable.Empty<LadVariableDef>()) yield return variable;
                if (network.networks != null)
                    foreach (var child in EnumerateVariables(network.networks)) yield return child;
                if (network.branches != null)
                    foreach (var child in EnumerateVariables(network.branches)) yield return child;
            }
        }

        private static void NormalizeNetworkInstances(
            LadNetworkDef network,
            string path,
            ISet<string> usedVariableNames,
            IDictionary<string, string> usedInstances,
            ICollection<string> errors)
        {
            network.variables ??= new List<LadVariableDef>();
            if (network.sysCall != null)
                NormalizeBackgroundCall(network.sysCall.inst, network.sysCall.instance, network.sysCall.instanceScope,
                    network.sysCall.datatype, path + ".sysCall", network.variables, usedVariableNames, usedInstances, errors,
                    (instance, scope, valueType) =>
                    {
                        network.sysCall.instance = instance;
                        network.sysCall.instanceScope = scope;
                        network.sysCall.datatype = valueType;
                    });

            NormalizeRungInstances(network.rung, path + ".rung", network.variables, usedVariableNames, usedInstances, errors);

            if (network.networks != null)
                for (var i = 0; i < network.networks.Count; i++)
                    NormalizeNetworkInstances(network.networks[i], path + $".networks[{i}]", usedVariableNames, usedInstances, errors);
            if (network.branches != null)
                for (var i = 0; i < network.branches.Count; i++)
                    NormalizeNetworkInstances(network.branches[i], path + $".branches[{i}]", usedVariableNames, usedInstances, errors);
        }

        private static void NormalizeRungInstances(
            IEnumerable<RungElement>? elements,
            string path,
            IList<LadVariableDef> variables,
            ISet<string> usedVariableNames,
            IDictionary<string, string> usedInstances,
            ICollection<string> errors)
        {
            if (elements == null) return;
            var index = 0;
            foreach (var element in elements)
            {
                if (element.box != null)
                {
                    var box = element.box;
                    NormalizeBackgroundCall(box.box, box.instance, box.instanceScope, box.datatype,
                        path + $"[{index}].box", variables, usedVariableNames, usedInstances, errors,
                        (instance, scope, valueType) =>
                        {
                            box.instance = instance;
                            box.instanceScope = scope;
                            box.datatype = valueType;
                        });
                }
                if (element.branch?.branch != null)
                    for (var branch = 0; branch < element.branch.branch.Count; branch++)
                        NormalizeRungInstances(element.branch.branch[branch], path + $"[{index}].branch[{branch}]",
                            variables, usedVariableNames, usedInstances, errors);
                index++;
            }
        }

        private static void NormalizeBackgroundCall(
            string instruction,
            string? suppliedInstance,
            string? suppliedScope,
            string? suppliedDatatype,
            string path,
            IList<LadVariableDef> variables,
            ISet<string> usedVariableNames,
            IDictionary<string, string> usedInstances,
            ICollection<string> errors,
            Action<string?, string?, string> assign)
        {
            if (!LadInstructionCatalog.IsBackground(instruction)) return;

            var scope = string.IsNullOrWhiteSpace(suppliedScope) ? "local" : suppliedScope!.Trim().ToLowerInvariant();
            if (scope != "local" && scope != "global")
            {
                errors.Add($"{path}: instanceScope 只能是 local 或 global");
                return;
            }

            string valueType;
            string localType;
            try
            {
                valueType = LadInstructionCatalog.NormalizeValueDatatype(instruction, suppliedDatatype);
                localType = LadInstructionCatalog.GetLocalInstanceDatatype(instruction, valueType);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
                return;
            }

            var instance = suppliedInstance?.Trim();
            if (scope == "global" && string.IsNullOrWhiteSpace(instance))
            {
                errors.Add($"{path}: instanceScope=global 时必须显式提供 instance");
                return;
            }
            if (string.IsNullOrWhiteSpace(instance))
            {
                var prefix = LadInstructionCatalog.GetInstancePrefix(instruction);
                for (var suffix = 1; suffix < 100000; suffix++)
                {
                    var candidate = prefix + "_" + suffix;
                    if (!usedVariableNames.Contains(candidate) && !usedInstances.ContainsKey(candidate))
                    {
                        instance = candidate;
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(instance))
                {
                    errors.Add($"{path}: 无法分配唯一 IEC 实例名");
                    return;
                }
            }

            if (usedInstances.TryGetValue(instance!, out var previous))
            {
                errors.Add($"{path}: IEC 实例 '{instance}' 已被 {previous} 使用；每个定时器、计数器或触发器调用必须使用独立实例");
                return;
            }
            usedInstances[instance!] = path;
            usedVariableNames.Add(instance!);

            if (scope == "local")
            {
                var existing = variables.FirstOrDefault(v => v.name.Equals(instance, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    variables.Add(new LadVariableDef { name = instance!, datatype = localType, section = "Static" });
                }
                else if (!existing.section.Equals("Static", StringComparison.OrdinalIgnoreCase)
                         || !existing.datatype.Equals(localType, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{path}: 本地实例 '{instance}' 必须声明为 Static/{localType}，当前为 {existing.section}/{existing.datatype}");
                }
            }

            assign(instance, scope, valueType);
        }

        private static void ValidateProgramConflicts(IList<LadNetworkDef> networks, ICollection<string> errors)
        {
            var writers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var setResetWriters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < networks.Count; i++)
                CollectNetworkWriters(networks[i], $"network[{i}]", writers, setResetWriters, errors);
        }

        private static void CollectNetworkWriters(
            LadNetworkDef network,
            string path,
            IDictionary<string, string> writers,
            IDictionary<string, string> setResetWriters,
            ICollection<string> errors)
        {
            foreach (var coil in network.coils ?? Enumerable.Empty<LadCoilDef>())
                RegisterCoilWriter(coil.variable, coil.name, path + ".coils", writers, setResetWriters, errors);
            if (network.sysCall != null)
                RegisterPinWriters(network.sysCall.inst, network.sysCall.pins, path + ".sysCall", writers, errors);
            CollectRungWritersShared(network.rung, path + ".rung", writers, setResetWriters, errors);
            if (network.networks != null)
                for (var i = 0; i < network.networks.Count; i++)
                    CollectNetworkWriters(network.networks[i], path + $".networks[{i}]", writers, setResetWriters, errors);
            if (network.branches != null)
                for (var i = 0; i < network.branches.Count; i++)
                    CollectNetworkWriters(network.branches[i], path + $".branches[{i}]", writers, setResetWriters, errors);
        }

        private static void CollectRungWritersShared(
            IEnumerable<RungElement>? elements,
            string path,
            IDictionary<string, string> writers,
            IDictionary<string, string> setResetWriters,
            ICollection<string> errors)
        {
            if (elements == null) return;
            var index = 0;
            foreach (var element in elements)
            {
                if (element.coil != null)
                    RegisterCoilWriter(element.coil.coil, element.coil.type, path + $"[{index}].coil", writers, setResetWriters, errors);
                if (element.box != null) RegisterPinWriters(element.box.box, element.box.pins, path + $"[{index}].box", writers, errors);
                if (element.call?.pins != null)
                    foreach (var pin in element.call.pins.Where(p => string.Equals(p.direction, "out", StringComparison.OrdinalIgnoreCase)))
                        RegisterWriter(pin.variable, path + $"[{index}].call.{pin.name}", writers, errors);
                if (element.branch?.branch != null)
                    for (var branch = 0; branch < element.branch.branch.Count; branch++)
                        CollectRungWritersShared(element.branch.branch[branch], path + $"[{index}].branch[{branch}]", writers, setResetWriters, errors);
                index++;
            }
        }

        /// <summary>线圈写入登记。置位/复位线圈（SCoil/RCoil）允许跨网络配对使用——
        /// 一个网络置位、另一网络复位是 LAD 的标准锁存模式，不再判为多写入冲突；
        /// 普通线圈与任何其他写入（含 S/R）混用仍然报错。</summary>
        private static void RegisterCoilWriter(
            string? variable, string? coilKind, string path,
            IDictionary<string, string> writers,
            IDictionary<string, string> setResetWriters,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(variable) || IsLadConstant(variable)) return;
            bool isSetReset = string.Equals(coilKind, "set", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(coilKind, "reset", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(coilKind, "SCoil", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(coilKind, "RCoil", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(coilKind, "SetCoil", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(coilKind, "ResetCoil", StringComparison.OrdinalIgnoreCase);
            if (isSetReset)
            {
                if (!setResetWriters.ContainsKey(variable)) setResetWriters[variable] = path;
                if (writers.TryGetValue(variable, out var normalWriter))
                    errors.Add($"变量 '{variable}' 同时被普通线圈写入（{normalWriter}）与置位/复位线圈写入（{path}）");
                return;
            }
            RegisterWriter(variable, path, writers, errors);
            if (setResetWriters.ContainsKey(variable))
                errors.Add($"变量 '{variable}' 同时被普通线圈写入（{path}）与置位/复位线圈写入（{setResetWriters[variable]}）");
        }

        private static void RegisterPinWriters(
            string instruction,
            IDictionary<string, string>? pins,
            string path,
            IDictionary<string, string> writers,
            ICollection<string> errors)
        {
            if (pins == null) return;
            var outputPins = new HashSet<string>(LadInstructionCatalog.GetOutputPins(instruction), StringComparer.OrdinalIgnoreCase);
            foreach (var pin in pins)
                if (outputPins.Contains(pin.Key)) RegisterBoxWriter(pin.Value, path + ".pins." + pin.Key, writers);
        }

        /// <summary>指令盒（MOVE/算术/比较等）输出引脚写入登记。★放宽★ 盒子的数据传送写入
        /// 允许多个网络写同一变量（按扫描顺序后写优先）——这是"条件 MOVE 刷新显示量 /
        /// 优先级报警代码"类程序的标准惯用法，TIA 完全允许；与普通线圈的无条件覆盖
        /// （继电器竞争语义，RegisterCoilWriter 仍严格拦截）有本质区别。仅登记不报错。</summary>
        private static void RegisterBoxWriter(
            string? variable,
            string path,
            IDictionary<string, string> writers)
        {
            if (string.IsNullOrWhiteSpace(variable) || IsLadConstant(variable)) return;
            if (!writers.ContainsKey(variable)) writers[variable] = path;
        }

        private static void RegisterWriter(
            string? variable,
            string path,
            IDictionary<string, string> writers,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(variable) || variable == "_" || IsLadConstant(variable)) return;
            if (writers.TryGetValue(variable, out var previous) && previous != path)
                errors.Add($"变量 '{variable}' 被多个位置写入（{previous} 与 {path}）");
            else writers[variable] = path;
        }
    }
}

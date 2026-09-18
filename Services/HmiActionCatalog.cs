using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer
{
    /// <summary>
    /// 经用户正式 HMI XML 样本验证的事件触发器、系统函数及参数签名目录。
    /// Builder 与所有校验入口共用，避免 validate/apply 规则漂移。
    /// </summary>
    internal static class HmiActionCatalog
    {
        internal static string CanonicalTrigger(string? triggerName)
        {
            var trigger = (triggerName ?? "").Trim();
            if (trigger.Equals("Click", StringComparison.OrdinalIgnoreCase)) return "Click";
            if (trigger.Equals("Press", StringComparison.OrdinalIgnoreCase)) return "Press";
            if (trigger.Equals("Release", StringComparison.OrdinalIgnoreCase)) return "Release";
            if (trigger.Equals("SwitchOn", StringComparison.OrdinalIgnoreCase)) return "SwitchOn";
            if (trigger.Equals("SwitchOff", StringComparison.OrdinalIgnoreCase)) return "SwitchOff";
            throw new InvalidOperationException($"不支持的 HMI 事件触发器 '{triggerName}'。");
        }

        internal static string CanonicalBehavior(string? behaviorName)
        {
            var behavior = (behaviorName ?? "").Trim();
            if (behavior.Equals("momentary", StringComparison.OrdinalIgnoreCase) || behavior.Equals("impulse", StringComparison.OrdinalIgnoreCase)) return "impulse";
            if (behavior.Equals("set", StringComparison.OrdinalIgnoreCase) || behavior.Equals("setbit", StringComparison.OrdinalIgnoreCase)) return "setBit";
            if (behavior.Equals("reset", StringComparison.OrdinalIgnoreCase) || behavior.Equals("resetbit", StringComparison.OrdinalIgnoreCase)) return "resetBit";
            if (behavior.Equals("toggle", StringComparison.OrdinalIgnoreCase) || behavior.Equals("invertbit", StringComparison.OrdinalIgnoreCase)) return "invertBit";
            if (behavior.Equals("navigate", StringComparison.OrdinalIgnoreCase) || behavior.Equals("activatescreen", StringComparison.OrdinalIgnoreCase)) return "activateScreen";
            if (behavior.Equals("multiaction", StringComparison.OrdinalIgnoreCase)) return "multiAction";
            if (behavior.Equals("none", StringComparison.OrdinalIgnoreCase)) return "none";
            throw new InvalidOperationException($"不支持的 HMI 按钮行为 '{behaviorName}'。");
        }

        internal static string CanonicalFunction(string? functionName)
        {
            var function = (functionName ?? "").Trim();
            if (function.Equals("SetBit", StringComparison.OrdinalIgnoreCase)) return "SetBit";
            if (function.Equals("ResetBit", StringComparison.OrdinalIgnoreCase)) return "ResetBit";
            if (function.Equals("InvertBit", StringComparison.OrdinalIgnoreCase)) return "InvertBit";
            if (function.Equals("ActivateScreen", StringComparison.OrdinalIgnoreCase)) return "ActivateScreen";
            if (function.Equals("SetTag", StringComparison.OrdinalIgnoreCase)) return "SetTag";
            throw new InvalidOperationException($"HMI 系统函数 '{functionName}' 尚未建立经样本验证的签名，当前版本拒绝生成。");
        }

        internal static IReadOnlyList<HmiFunctionParameterSpec> ResolveParameters(HmiButtonActionSpec action)
        {
            if (action.Parameters != null && action.Parameters.Count > 0)
                return action.Parameters;

            var function = CanonicalFunction(action.Function);
            if (function == "ActivateScreen")
            {
                return new[]
                {
                    new HmiFunctionParameterSpec { Name = "Screen name", Value = action.Target, IsLink = true },
                    new HmiFunctionParameterSpec { Name = "Object number", Value = "0", IsLink = false, Type = "System.Int32" }
                };
            }

            return new[]
            {
                new HmiFunctionParameterSpec
                {
                    Name = string.IsNullOrWhiteSpace(action.ParameterName) ? "Tag" : action.ParameterName,
                    Value = action.Target,
                    IsLink = true
                }
            };
        }

        internal static void Validate(HmiButtonActionSpec action)
        {
            if (action == null) throw new InvalidOperationException("HMI action 不能为空。");
            CanonicalTrigger(action.Trigger);
            var function = CanonicalFunction(action.Function);
            var parameters = ResolveParameters(action);
            if (parameters.Count == 0 || parameters.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Value)))
                throw new InvalidOperationException($"HMI 系统函数 {function} 的参数不完整。");

            bool Has(string name, bool? mustBeLink = null) => parameters.Any(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                && (!mustBeLink.HasValue || p.IsLink == mustBeLink.Value));

            if (function is "SetBit" or "ResetBit" or "InvertBit")
            {
                if (parameters.Count != 1 || !Has("Tag", true))
                    throw new InvalidOperationException($"{function} 必须且只能包含链接参数 Tag。");
                return;
            }
            if (function == "ActivateScreen")
            {
                if (parameters.Count != 2 || !Has("Screen name", true) || !Has("Object number", false))
                    throw new InvalidOperationException("ActivateScreen 必须包含链接参数 'Screen name' 和字面量参数 'Object number'。");
                var objectNumber = parameters.First(p => string.Equals(p.Name, "Object number", StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(objectNumber.Type, "System.Int32", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(objectNumber.Value, out _))
                    throw new InvalidOperationException("ActivateScreen 的 Object number 必须是 System.Int32 整数。");
                return;
            }
            if (function == "SetTag")
            {
                if (parameters.Count != 2 || !Has("Tag", true) || !Has("Value"))
                    throw new InvalidOperationException("SetTag 必须包含链接参数 Tag 和参数 Value。");
                var value = parameters.First(p => string.Equals(p.Name, "Value", StringComparison.OrdinalIgnoreCase));
                if (value.IsLink)
                    throw new InvalidOperationException("当前经样本验证的 SetTag.Value 必须是字面量，不能是链接参数。");
            }
        }

        internal static HmiButtonActionSpec FromJson(JObject action)
        {
            var function = action["function"]?.ToString() ?? "";
            var result = new HmiButtonActionSpec
            {
                Trigger = action["trigger"]?.ToString() ?? "Click",
                Function = function,
                Target = action["target"]?.ToString() ?? "",
                ParameterName = action["parameterName"]?.ToString() ?? "Tag"
            };

            if (action["parameters"] is JObject parameterObject)
            {
                foreach (var property in parameterObject.Properties())
                {
                    if (property.Value is JObject spec)
                    {
                        result.Parameters.Add(new HmiFunctionParameterSpec
                        {
                            Name = property.Name,
                            Value = spec["value"]?.ToString() ?? "",
                            IsLink = spec["link"]?.Value<bool?>() ?? true,
                            Type = spec["type"]?.ToString() ?? "System.String"
                        });
                    }
                    else
                    {
                        var isLiteral = property.Name.Equals("Value", StringComparison.OrdinalIgnoreCase)
                                        || property.Name.Equals("Object number", StringComparison.OrdinalIgnoreCase);
                        result.Parameters.Add(new HmiFunctionParameterSpec
                        {
                            Name = property.Name,
                            Value = property.Value.ToString(),
                            IsLink = !isLiteral,
                            Type = property.Name.Equals("Object number", StringComparison.OrdinalIgnoreCase)
                                ? "System.Int32" : "System.Double"
                        });
                    }
                }
            }
            else if (function.Equals("ActivateScreen", StringComparison.OrdinalIgnoreCase))
            {
                result.Parameters.Add(new HmiFunctionParameterSpec { Name = "Screen name", Value = result.Target, IsLink = true });
                result.Parameters.Add(new HmiFunctionParameterSpec { Name = "Object number", Value = "0", IsLink = false, Type = "System.Int32" });
            }
            else if (function.Equals("SetTag", StringComparison.OrdinalIgnoreCase))
            {
                result.Parameters.Add(new HmiFunctionParameterSpec { Name = "Tag", Value = result.Target, IsLink = true });
                result.Parameters.Add(new HmiFunctionParameterSpec
                {
                    Name = "Value",
                    Value = action["value"]?.ToString() ?? "",
                    IsLink = false,
                    Type = action["valueType"]?.ToString() ?? "System.Double"
                });
            }
            else
            {
                result.Parameters.Add(new HmiFunctionParameterSpec
                {
                    Name = result.ParameterName,
                    Value = result.Target,
                    IsLink = true
                });
            }

            return result;
        }

        internal static IReadOnlyList<string> Behaviors { get; } = new[] { "impulse", "setBit", "resetBit", "invertBit", "activateScreen", "multiAction", "none" };
        internal static IReadOnlyList<string> Triggers { get; } = new[] { "Click", "Press", "Release", "SwitchOn", "SwitchOff" };
        internal static IReadOnlyList<string> Functions { get; } = new[] { "SetBit", "ResetBit", "InvertBit", "ActivateScreen", "SetTag" };
    }
}

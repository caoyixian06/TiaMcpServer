using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

/// <summary>在 Tool Handler 运行前执行基础 JSON Schema 校验，提前拒绝非法参数。</summary>
public static class ToolSchemaValidator
{
    public static bool TryValidate(object schemaObject, JObject arguments, out string error)
    {
        error = "";
        try
        {
            var schema = schemaObject as JObject ?? JObject.FromObject(schemaObject ?? new { });
            return ValidateObject(schema, arguments ?? new JObject(), "$", out error);
        }
        catch (Exception ex)
        {
            error = "参数 Schema 校验失败：" + ex.Message;
            return false;
        }
    }

    private static bool ValidateObject(JObject schema, JObject value, string path, out string error)
    {
        error = "";
        var properties = schema["properties"] as JObject ?? new JObject();
        var required = new HashSet<string>((schema["required"] as JArray)?.Values<string>().Where(x => x != null).Select(x => x!) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        foreach (var name in required)
        {
            if (value[name] == null || value[name]!.Type == JTokenType.Null)
            {
                error = $"缺少必填参数：{name}";
                return false;
            }
        }

        var additionalAllowed = schema["additionalProperties"]?.Value<bool?>() ?? true;
        if (!additionalAllowed)
        {
            // 调度层控制参数由 McpServer 统一消费（wait=同步等待长任务、dryRun/confirm/confirmation
            // =破坏性操作二次确认），不进入各工具 Handler 的 schema，此处放行，否则文档
            // 宣传的 wait=true 会被 -32602 拒绝而永远无法使用。
            var reserved = new[] { "wait", "dryRun", "confirm", "confirmation" };
            var unknown = value.Properties().FirstOrDefault(p => properties[p.Name] == null && !reserved.Contains(p.Name, StringComparer.Ordinal));
            if (unknown != null)
            {
                error = $"不支持的参数：{unknown.Name}";
                return false;
            }
        }

        var maxProperties = schema["maxProperties"]?.Value<int?>();
        if (maxProperties.HasValue && value.Properties().Count() > maxProperties.Value)
        {
            error = $"参数数量超过限制：最多 {maxProperties.Value} 项。";
            return false;
        }

        foreach (var property in value.Properties())
        {
            if (!(properties[property.Name] is JObject propertySchema)) continue;
            if (!ValidateValue(propertySchema, property.Value, path + "." + property.Name, out error)) return false;
        }
        return true;
    }

    private static bool ValidateValue(JObject schema, JToken value, string path, out string error)
    {
        error = "";
        if (value.Type == JTokenType.Null) return true;
        var expected = schema["type"]?.ToString() ?? "";
        var typeOk = expected switch
        {
            "string" => value.Type == JTokenType.String,
            "integer" => value.Type == JTokenType.Integer,
            "number" => value.Type == JTokenType.Integer || value.Type == JTokenType.Float,
            "boolean" => value.Type == JTokenType.Boolean,
            "array" => value.Type == JTokenType.Array,
            "object" => value.Type == JTokenType.Object,
            _ => true
        };
        if (!typeOk)
        {
            error = $"参数 {path} 类型错误：应为 {expected}。";
            return false;
        }

        if (value.Type == JTokenType.String)
        {
            var text = value.Value<string>() ?? "";
            var maxLength = schema["maxLength"]?.Value<int?>();
            var minLength = schema["minLength"]?.Value<int?>();
            if (maxLength.HasValue && text.Length > maxLength.Value)
            {
                error = $"参数 {path} 长度超过限制：最多 {maxLength.Value} 个字符。";
                return false;
            }
            if (minLength.HasValue && text.Length < minLength.Value)
            {
                error = $"参数 {path} 长度不足：至少 {minLength.Value} 个字符。";
                return false;
            }
            if (schema["enum"] is JArray enumValues && !enumValues.Values<string>().Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                error = $"参数 {path} 不在允许枚举中：{string.Join(", ", enumValues.Values<string>())}";
                return false;
            }
        }

        if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
        {
            var number = value.Value<double>();
            var minimum = schema["minimum"]?.Value<double?>();
            var maximum = schema["maximum"]?.Value<double?>();
            if (minimum.HasValue && number < minimum.Value)
            {
                error = $"参数 {path} 小于最小值 {minimum.Value}。";
                return false;
            }
            if (maximum.HasValue && number > maximum.Value)
            {
                error = $"参数 {path} 大于最大值 {maximum.Value}。";
                return false;
            }
        }

        if (value is JArray array)
        {
            var minItems = schema["minItems"]?.Value<int?>();
            var maxItems = schema["maxItems"]?.Value<int?>();
            if (minItems.HasValue && array.Count < minItems.Value)
            {
                error = $"参数 {path} 数组项不足：至少 {minItems.Value} 项。";
                return false;
            }
            if (maxItems.HasValue && array.Count > maxItems.Value)
            {
                error = $"参数 {path} 数组项过多：最多 {maxItems.Value} 项。";
                return false;
            }
            if (schema["items"] is JObject itemSchema)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (!ValidateValue(itemSchema, array[i], path + "[" + i + "]", out error)) return false;
                }
            }
        }

        if (value is JObject obj && !ValidateObject(schema, obj, path, out error)) return false;
        return true;
    }
}

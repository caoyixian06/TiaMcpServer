using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

internal static class RuntimePolicyConfig
{
    public static void Apply(ServerRuntimeState state)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "server_policy.json");
            if (!File.Exists(path))
            {
                // ★阶段2 修复★ 策略文件缺失时自动生成为 Auto。
                // 此前构建清理会删掉 bin 里的策略文件 → 回落 Safe → 全部写入类工具被拦且难以排查。
                try
                {
                    File.WriteAllText(path, "{\"defaultExecutionMode\":\"Auto\",\"notes\":[\"首次运行自动生成；如需安全模式改为 Safe\"]}");
                    state.AddLog("server_policy.json 缺失，已自动生成（Auto）");
                }
                catch (Exception ioEx)
                {
                    state.AddLog("server_policy.json 自动生成失败：" + ioEx.Message);
                }
            }
            if (!File.Exists(path)) return;
            var cfg = JObject.Parse(File.ReadAllText(path));
            var modeText = cfg["defaultExecutionMode"]?.ToString();
            if (TryParseMode(modeText, out var mode))
            {
                state.SetExecutionMode(mode);
                return;
            }

            // 兼容 V4.1.4 及更早配置；只做一次映射，不再保留多状态并行控制。
            var autoApprove = cfg["autoApproveDangerous"]?.Value<bool?>() == true;
            var beginnerMode = cfg["beginnerModeDefault"]?.Value<bool?>() ?? true;
            state.SetExecutionMode(autoApprove ? ExecutionMode.Auto : beginnerMode ? ExecutionMode.Safe : ExecutionMode.Assist);
        }
        catch (Exception ex)
        {
            state.AddLog("安全策略配置加载失败：" + ex.Message);
            state.SetExecutionMode(ExecutionMode.Safe);
        }
    }

    private static bool TryParseMode(string? value, out ExecutionMode mode)
    {
        if (Enum.TryParse(value, true, out mode)) return true;
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "observe":
            case "sandbox":
                mode = ExecutionMode.Safe;
                return true;
            case "engineering":
                mode = ExecutionMode.Assist;
                return true;
            case "full":
                mode = ExecutionMode.Auto;
                return true;
            default:
                mode = ExecutionMode.Safe;
                return false;
        }
    }
}

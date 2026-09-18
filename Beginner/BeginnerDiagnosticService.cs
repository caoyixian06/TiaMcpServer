using System;
using System.Collections.Generic;

namespace TiaMcpServer;

internal sealed class BeginnerDiagnosis
{
    public string Overall { get; set; } = "";
    public List<string> Problems { get; } = new List<string>();
    public List<string> Actions { get; } = new List<string>();
}

internal static class BeginnerDiagnosticService
{
    public static BeginnerDiagnosis Diagnose(ServerRuntimeState state)
    {
        var status = state.Snapshot();
        var env = EnvironmentCheckService.Run();
        var result = new BeginnerDiagnosis();

        if (env.FailedCount > 0)
        {
            result.Problems.Add("运行环境还有必须解决的问题。");
            result.Actions.Add("打开“新手首页”，运行“一键环境检查”，按红色项目逐项处理。");
        }
        else if (env.WarningCount > 0)
        {
            result.Problems.Add("运行环境存在需要关注的提醒项。");
            result.Actions.Add("先查看环境检查中的黄色项目；如果博途连接正常，可以继续使用。 ");
        }

        if (!status.ServerRunning)
        {
            result.Problems.Add("通信服务器没有处于运行状态。");
            result.Actions.Add("关闭程序后重新启动控制中心。 ");
        }

        if (status.AiState == AiClientState.Waiting || status.AiState == AiClientState.Disconnected)
        {
            result.Problems.Add("还没有检测到模型客户端连接。 ");
            result.Actions.Add("使用“模型客户端配置”完成配置，然后完全退出并重新启动模型客户端。 ");
        }

        if (status.TiaRunningProcessCount == 0)
        {
            result.Problems.Add("没有检测到正在运行的博途。 ");
            result.Actions.Add("先启动环境发现中心选中的 TIA Portal，再打开或创建工程。 ");
        }
        else if (!status.TiaConnected)
        {
            result.Problems.Add("检测到博途正在运行，但服务器还没有连接到工程会话。 ");
            result.Actions.Add("在博途中打开工程，再刷新状态；如果仍失败，检查开放接口用户权限。 ");
        }

        if (status.TiaConnected && string.IsNullOrWhiteSpace(status.TiaProjectName))
        {
            result.Problems.Add("服务器已连接博途，但当前没有打开工程。 ");
            result.Actions.Add("使用“创建新工程”或“打开已有工程”。 ");
        }

        if (status.ExecutionMode == ExecutionMode.Safe && !status.SandboxActive && status.TiaConnected && !string.IsNullOrWhiteSpace(status.TiaProjectName))
        {
            result.Problems.Add("当前处于沙盒保护模式，但还没有创建沙盒副本。 ");
            result.Actions.Add("点击“创建沙盒副本”，再让模型修改工程。 ");
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            result.Problems.Add("最近一次操作记录到了错误。 ");
            result.Actions.Add("查看“运行日志”和“变更记录”；如果无法判断原因，先不要切换到完整权限。 ");
        }

        if (result.Problems.Count == 0)
        {
            result.Overall = "没有发现明显问题，可以继续使用。";
            result.Actions.Add("建议保持沙盒保护模式，先在工程副本中完成修改和编译。 ");
        }
        else
        {
            result.Overall = "发现 " + result.Problems.Count + " 个需要处理的问题。";
        }

        return result;
    }
}

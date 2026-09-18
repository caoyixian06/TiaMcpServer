using System;
using System.Collections.Generic;

namespace TiaMcpServer;

public partial class McpServer
{
    private static void RegisterEngineeringWorkflowTools(McpServer server, EngineeringWorkflowService workflow)
    {
        server.RegisterTool("create_requirement_session",
            "把模糊工程要求转为持久化的结构化需求会话，并返回必须向用户补问的 openQuestions。新项目创建必须从这里开始。",
            Props(new Dictionary<string, object>
            {
                ["taskSummary"] = StrProp("用户原始目标或任务摘要"),
                ["workType"] = EnumStrProp("工作类型", "create_new_project", "modify_existing_project"),
                ["projectName"] = StrProp("已知项目名称，可选"),
                ["projectPath"] = StrProp("已知项目路径，可选"),
                ["initialRequirementsJson"] = StrProp("可选 JSON 对象，写入已知结构化需求"),
                ["autonomyMode"] = EnumStrProp("自主程度；standard 为默认，仅阻塞项才提问", "fast", "standard", "strict")
            }, new[] { "taskSummary", "workType" }),
            args => workflow.CreateRequirementSession(
                GetStringArg(args, "taskSummary") ?? "",
                GetStringArg(args, "workType") ?? "create_new_project",
                GetStringArg(args, "projectName"),
                GetStringArg(args, "projectPath"),
                GetStringArg(args, "initialRequirementsJson"),
                GetStringArg(args, "autonomyMode")));

        server.RegisterTool("update_requirement_session",
            "用 JSON 对象补充需求。返回仍未回答的问题；需求冻结后不允许静默修改。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["answersJson"] = StrProp("结构化答案 JSON 对象，例如 {\"project\":{\"tiaVersion\":\"V19\"}}")
            }, new[] { "answersJson" }),
            args => workflow.UpdateRequirementSession(GetStringArg(args, "sessionId") ?? "", GetStringArg(args, "answersJson") ?? "{}"));

        server.RegisterTool("freeze_requirements",
            "在用户确认需求摘要后冻结需求基线。仍有必填问题时会拒绝。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["approvedByUser"] = BoolProp("用户是否明确批准当前需求基线")
            }, new[] { "approvedByUser" }),
            args => workflow.FreezeRequirements(GetStringArg(args, "sessionId") ?? "", GetBoolArg(args, "approvedByUser") == true));

        server.RegisterTool("generate_project_plan",
            "根据已冻结需求生成顺序化工程计划、阶段退出条件和按需规范主题。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话") }, Array.Empty<string>()),
            args => workflow.GenerateProjectPlan(GetStringArg(args, "sessionId") ?? ""));

        server.RegisterTool("approve_project_plan",
            "用户确认计划后批准精确 planId。过期或不匹配的计划不能批准。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["planId"] = StrProp("generate_project_plan 返回的精确计划 ID"),
                ["approvedByUser"] = BoolProp("用户是否明确批准该计划")
            }, new[] { "planId", "approvedByUser" }),
            args => workflow.ApproveProjectPlan(GetStringArg(args, "sessionId") ?? "", GetStringArg(args, "planId") ?? "", GetBoolArg(args, "approvedByUser") == true));

        server.RegisterTool("get_requirement_status",
            "读取结构化需求、未决问题、计划、当前阶段和近期证据。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话") }, Array.Empty<string>()),
            args => workflow.GetRequirementStatus(GetStringArg(args, "sessionId")));

        server.RegisterTool("get_current_workflow_stage",
            "以紧凑形式读取当前工程工作流阶段和门禁状态。",
            Props(new Dictionary<string, object>(), Array.Empty<string>()),
            _ => workflow.GetRuntimeSummary().ToString(Newtonsoft.Json.Formatting.Indented));

        server.RegisterTool("set_active_engineering_session",
            "切换活动工程会话。后续门禁和证据自动关联到该会话。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("工程会话 ID") }, new[] { "sessionId" }),
            args => workflow.SetActiveSession(GetStringArg(args, "sessionId") ?? ""));

        server.RegisterTool("deactivate_engineering_session",
            "停用当前活动工程会话但保留记录。用于完成、暂停或切换到不受该会话门禁影响的普通维护任务。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("可选；仅当它与当前活动会话一致时停用") }, Array.Empty<string>()),
            args => workflow.DeactivateSession(GetStringArg(args, "sessionId")));

        server.RegisterTool("advance_engineering_stage",
            "仅推进到下一合法工程阶段，并持久化完成证据；不能跳过需求、计划或新项目创建。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["targetStage"] = EnumStrProp("目标阶段", "HardwarePlanning", "PlcArchitecture", "PlcImplementation", "HmiPlanning", "HmiImplementation", "Verification", "UserAcceptance", "Deployment", "Completed"),
                ["evidenceSummary"] = StrProp("前一阶段完成内容和验证结果摘要"),
                ["approvedByUser"] = BoolProp("进入部署或完成时必须由用户明确批准")
            }, new[] { "targetStage", "evidenceSummary" }),
            args => workflow.AdvanceStage(
                GetStringArg(args, "sessionId") ?? "",
                GetStringArg(args, "targetStage") ?? "",
                GetStringArg(args, "evidenceSummary") ?? "",
                GetBoolArg(args, "approvedByUser") == true));

        server.RegisterTool("record_engineering_test_evidence",
            "记录一条可审计的行为测试证据。相同 testName 的最新结果用于验收判断；失败后必须修复并重新记录通过结果。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["testName"] = StrProp("稳定测试名称，例如 EmergencyStopWhileRunning"),
                ["initialConditions"] = StrProp("测试初始条件"),
                ["action"] = StrProp("输入动作或操作步骤"),
                ["expectedResult"] = StrProp("预期内部状态和输出"),
                ["observedResult"] = StrProp("实际观察结果"),
                ["passed"] = BoolProp("是否通过")
            }, new[] { "testName", "initialConditions", "action", "expectedResult", "observedResult", "passed" }),
            args => workflow.RecordTestEvidence(
                GetStringArg(args, "sessionId") ?? "",
                GetStringArg(args, "testName") ?? "",
                GetStringArg(args, "initialConditions") ?? "",
                GetStringArg(args, "action") ?? "",
                GetStringArg(args, "expectedResult") ?? "",
                GetStringArg(args, "observedResult") ?? "",
                GetBoolArg(args, "passed") == true));

        server.RegisterTool("approve_engineering_acceptance",
            "验证证据完整后，由用户明确批准工程验收；未验收不得进入在线下载。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["acceptanceSummary"] = StrProp("用户确认的功能、测试结果和已知风险摘要"),
                ["approvedByUser"] = BoolProp("用户是否明确批准验收")
            }, new[] { "acceptanceSummary", "approvedByUser" }),
            args => workflow.ApproveAcceptance(
                GetStringArg(args, "sessionId") ?? "",
                GetStringArg(args, "acceptanceSummary") ?? "",
                GetBoolArg(args, "approvedByUser") == true));

        server.RegisterTool("get_applicable_standards",
            "只加载当前任务所需的小型工程规范包，避免长上下文中一次塞入全部 LAD/HMI/部署规范。",
            Props(new Dictionary<string, object>
            {
                ["topic"] = EnumStrProp("规范主题", "core", "project-creation", "hardware", "plc-architecture", "lad", "hmi-planning", "hmi-implementation", "verification", "deployment"),
                ["maxRules"] = IntProp("最多返回规则数，建议 8-16")
            }, new[] { "topic" }),
            args => workflow.GetApplicableStandards(GetStringArg(args, "topic") ?? "core", GetIntArg(args, "maxRules")));

        server.RegisterTool("validate_workflow_readiness",
            "在真正调用写工具前预检当前工程阶段是否允许该动作。",
            Props(new Dictionary<string, object>
            {
                ["sessionId"] = StrProp("工程会话 ID；为空时使用活动会话"),
                ["action"] = StrProp("准备调用的原始工具名")
            }, new[] { "action" }),
            args => workflow.ValidateReadiness(GetStringArg(args, "sessionId"), GetStringArg(args, "action") ?? ""));


        server.RegisterTool("prepare_autonomous_project",
            "首选入口：用户只需给一次项目要求。自动补全保守默认值、生成工程蓝图和离线计划；仅返回真正阻塞的集中问题。不会自动下载或在线写入。",
            Props(new Dictionary<string, object>
            {
                ["taskSummary"] = StrProp("用户完整或模糊的项目要求"),
                ["workType"] = EnumStrProp("工作类型", "create_new_project", "modify_existing_project"),
                ["projectName"] = StrProp("可选项目名"),
                ["projectPath"] = StrProp("可选项目路径"),
                ["initialRequirementsJson"] = StrProp("可选已知要求 JSON"),
                ["autonomyMode"] = EnumStrProp("fast/standard 自动执行；strict 逐项批准", "fast", "standard", "strict")
            }, new[] { "taskSummary" }),
            args => workflow.PrepareAutonomousProject(GetStringArg(args,"taskSummary") ?? "", GetStringArg(args,"workType") ?? "create_new_project", GetStringArg(args,"projectName"), GetStringArg(args,"projectPath"), GetStringArg(args,"initialRequirementsJson"), GetStringArg(args,"autonomyMode") ?? "standard"));

        server.RegisterTool("generate_engineering_blueprint",
            "根据活动需求生成统一 PLC/HMI/报警/测试工程蓝图，减少 AI 在长上下文中遗忘规范。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("可选工程会话 ID") }, Array.Empty<string>()),
            args => workflow.GenerateEngineeringBlueprint(GetStringArg(args,"sessionId")));

        server.RegisterTool("get_engineering_blueprint",
            "读取持久化工程蓝图和默认假设，不依赖聊天历史。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("可选工程会话 ID") }, Array.Empty<string>()),
            args => workflow.GetEngineeringBlueprint(GetStringArg(args,"sessionId")));

        server.RegisterTool("validate_engineering_blueprint",
            "在创建 PLC/HMI 内容前验证工程蓝图完整性、测试数量和部署待确认项。",
            Props(new Dictionary<string, object> { ["sessionId"] = StrProp("可选工程会话 ID") }, Array.Empty<string>()),
            args => workflow.ValidateEngineeringBlueprint(GetStringArg(args,"sessionId")));
    }
}

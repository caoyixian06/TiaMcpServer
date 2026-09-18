using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

public sealed partial class EngineeringWorkflowService
{
    private static string NormalizeAutonomyMode(string? mode)
    {
        var value = (mode ?? "standard").Trim().ToLowerInvariant();
        return value == "fast" || value == "strict" ? value : "standard";
    }

    private void ApplyAutonomousDefaults(EngineeringWorkflowRecord record)
    {
        if (record.AutonomyMode == "strict") return;
        var task = string.IsNullOrWhiteSpace(record.TaskSummary) ? "TIA automation project" : record.TaskSummary.Trim();
        Default(record, "process.description", task, "工艺描述沿用用户原始要求");
        Default(record, "project.tiaVersion", Environment.GetEnvironmentVariable("TIA_PORTAL_VERSION") ?? EnvironmentDiscoveryService.CurrentEngineeringVersion(), "优先采用服务器当前绑定的 TIA 版本");
        var name = GetPath(record.Requirements, "project.name")?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = BuildSafeProjectName(task);
            Default(record, "project.name", name, "由用户目标生成安全项目名");
        }
        if (record.WorkType == "create_new_project")
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(docs)) docs = Environment.CurrentDirectory;
            Default(record, "project.path", Path.Combine(docs, "TIA-AI-Projects", name!), "采用独立默认工作区，避免覆盖现有项目");
            Default(record, "controller.model", "S7-1200 CPU 1214C DC/DC/DC (simulation baseline)", "未指定硬件时采用教学/仿真基线；部署前必须替换为真实订货号");
        }
        else
        {
            Default(record, "change.scope", "仅修改用户目标直接涉及的对象；其他对象只读", "最小变更原则");
            Default(record, "change.rollbackPolicy", "项目归档 + 变更日志", "写入前建立可回滚基线");
        }
        DefaultArray(record, "operation.modes", new[] { "Manual", "Auto" }, "通用设备控制默认提供手动和自动模式");
        Default(record, "safety.stopPolicy", "停止优先；故障时撤销普通输出；急停由独立安全回路处理", "采用保守停止策略，不替代安全 PLC/硬接线");
        Default(record, "io.assignmentPolicy", "AI 规划仿真地址并标记待电气图确认", "未知真实 IO 时不猜现场地址");
        var hmi = ContainsAny(task, "HMI", "触摸屏", "画面", "WinCC", "监控");
        Default(record, "hmi.required", hmi, hmi ? "用户目标包含 HMI/画面意图" : "未明确要求 HMI，默认不创建以减少无用内容");
        if (GetBool(record.Requirements, "hmi.required") == true)
        {
            Default(record, "hmi.deviceModel", "WinCC Unified PC Runtime (simulation baseline)", "未指定面板时采用设备无关仿真基线");
            DefaultArray(record, "hmi.screenGoals", new[] { "Overview: 总览与关键状态", "Manual: 手动操作与联锁提示", "Alarms: 当前/历史报警", "Parameters: 可授权参数", "Diagnostics: 通信与设备诊断" }, "采用最小完整 HMI 信息架构");
        }
        Default(record, "validation.simulationRequired", true, "离线项目默认必须编译、静态检查和行为测试");
        Default(record, "deployment.allowed", false, "自主模式默认禁止下载和在线写入，等待用户单独授权");
        Default(record, "quality.minimumBehaviorTests", _options.MinimumBehaviorTests, "沿用服务器质量门禁");
    }

    public string PrepareAutonomousProject(string taskSummary, string workType, string? projectName, string? projectPath, string? initialRequirementsJson, string? autonomyMode)
    {
        var created = JObject.Parse(CreateRequirementSession(taskSummary, workType, projectName, projectPath, initialRequirementsJson, autonomyMode));
        var id = created["sessionId"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(id)) return created.ToString(Formatting.Indented);
        lock (_sync)
        {
            var record = RequireRecord(id);
            ApplyAutonomousDefaults(record);
            record.OpenQuestions = BuildQuestions(record).Take(_options.MaxBlockingQuestions).ToList();
            BuildBlueprint(record);
            if (record.OpenQuestions.Count == 0 && record.AutonomyMode != "strict")
            {
                record.RequirementsFrozen = true;
                record.RequirementsFrozenAtUtc = DateTime.UtcNow;
                record.Stage = EngineeringStage.RequirementsFrozen;
                AddEvidence(record, "requirements", "自主模式已使用保守默认值冻结离线需求基线。", "prepare_autonomous_project", true, new JObject { ["deploymentAllowed"] = false });
                Save(record);
            }
            else
            {
                Save(record);
                return SerializeAutonomous(record, "存在无法安全推断的阻塞项。请一次性回答 blockingQuestions；其他字段不会继续追问。");
            }
        }
        GenerateProjectPlan(id);
        lock (_sync)
        {
            var record = RequireRecord(id);
            if (record.Plan != null && _options.AutoApproveLowRiskPlans && record.AutonomyMode != "strict")
            {
                record.Plan.Approved = true;
                record.Plan.ApprovedAtUtc = DateTime.UtcNow;
                record.Stage = EngineeringStage.PlanApproved;
                AddEvidence(record, "plan", "用户选择自主工程模式，低风险离线计划已自动批准；部署仍锁定。", "prepare_autonomous_project", true);
                Save(record);
            }
            return SerializeAutonomous(record, "自主工程准备完成：需求、默认假设、工程蓝图和离线计划均已生成。AI 可按计划继续创建项目；不要再次逐项询问用户。");
        }
    }

    public string GenerateEngineeringBlueprint(string? sessionId)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId ?? "");
            ApplyAutonomousDefaults(record);
            BuildBlueprint(record);
            Save(record);
            return SerializeAutonomous(record, "工程蓝图已生成或刷新。");
        }
    }

    public string GetEngineeringBlueprint(string? sessionId)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId ?? "");
            return new JObject { ["success"] = true, ["sessionId"] = record.SessionId, ["blueprint"] = record.EngineeringBlueprint.DeepClone(), ["assumptions"] = record.Assumptions.DeepClone() }.ToString(Formatting.Indented);
        }
    }

    public string ValidateEngineeringBlueprint(string? sessionId)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId ?? "");
            var errors = new JArray(); var warnings = new JArray();
            if (!record.EngineeringBlueprint.HasValues) errors.Add("工程蓝图为空");
            if (!(record.EngineeringBlueprint["plc"]?["blocks"] is JArray blocks) || blocks.Count == 0) errors.Add("PLC 块架构为空");
            if (!(record.EngineeringBlueprint["tests"] is JArray tests) || tests.Count < _options.MinimumBehaviorTests) errors.Add("行为测试数量不足");
            if (GetBool(record.Requirements, "hmi.required") == true && !(record.EngineeringBlueprint["hmi"]?["screens"] is JArray screens && screens.Count > 0)) errors.Add("要求 HMI 但未规划画面");
            if (record.Assumptions["controller.model"] != null) warnings.Add("控制器型号为默认假设，真实部署前必须确认订货号");
            if (record.Assumptions["io.assignmentPolicy"] != null) warnings.Add("IO 地址为规划值，真实部署前必须对照电气图");
            var ok = errors.Count == 0;
            AddEvidence(record, "validation", ok ? "工程蓝图验证通过。" : "工程蓝图验证失败。", "validate_engineering_blueprint", ok, new JObject { ["errors"] = errors, ["warnings"] = warnings });
            Save(record);
            return new JObject { ["success"] = ok, ["sessionId"] = record.SessionId, ["errors"] = errors, ["warnings"] = warnings, ["readyForOfflineImplementation"] = ok, ["deploymentAllowed"] = false }.ToString(Formatting.Indented);
        }
    }

    private void BuildBlueprint(EngineeringWorkflowRecord record)
    {
        var task = record.TaskSummary;
        var hmi = GetBool(record.Requirements, "hmi.required") == true;
        var deviceType = InferDeviceType(task);
        var blocks = new JArray("OB_Main", "FB_ModeManager", "FB_" + deviceType, "DB_Parameters", "DB_Alarms", "DB_HMI");
        var tests = new JArray("PowerUpSafeState", "ManualStartStop", "AutoSequenceNominal", "StopPriority", "InterlockTrip", "FaultReset", "FeedbackTimeout", "ManualAutoTransition", "CommunicationLoss");
        var screens = hmi ? new JArray("Overview", "ManualControl", "Alarms", "Parameters", "Diagnostics") : new JArray();
        record.EngineeringBlueprint = new JObject
        {
            ["schemaVersion"] = "1.0",
            ["generatedAtUtc"] = DateTime.UtcNow,
            ["goal"] = task,
            ["deviceModel"] = new JObject { ["primaryType"] = deviceType, ["signals"] = DefaultSignals(deviceType), ["safeState"] = "all ordinary commands off" },
            ["plc"] = new JObject { ["architecture"] = "OB -> mode manager -> device FB -> single output owner", ["blocks"] = blocks, ["rules"] = new JArray("stop/fault priority", "single writer per physical output", "feedback timeout", "manual mode keeps safety interlocks", "first scan safe state") },
            ["hmi"] = new JObject { ["required"] = hmi, ["screens"] = screens, ["navigation"] = "Overview is home; every screen has deterministic back navigation", ["colorSemantics"] = new JObject { ["running"] = "green", ["stopped"] = "gray", ["warning"] = "yellow", ["fault"] = "red" } },
            ["alarms"] = new JArray("DeviceFault", "StartFeedbackTimeout", "StopFeedbackTimeout", "InterlockActive", "CommunicationLoss"),
            ["tests"] = tests,
            ["delivery"] = new JObject { ["offlineOnly"] = true, ["compileRequired"] = true, ["staticValidationRequired"] = true, ["behaviorEvidenceRequired"] = true, ["onlineDeploymentRequiresExplicitApproval"] = true }
        };
    }

    private void Default(EngineeringWorkflowRecord r, string path, object value, string reason)
    {
        if (GetPath(r.Requirements, path) != null) return;
        SetPath(r.Requirements, path, JToken.FromObject(value));
        r.Assumptions[path] = new JObject { ["value"] = JToken.FromObject(value), ["reason"] = reason, ["mustConfirmBeforeDeployment"] = path.StartsWith("controller.") || path.StartsWith("io.") || path.StartsWith("safety.") };
    }
    private void DefaultArray(EngineeringWorkflowRecord r, string path, IEnumerable<string> values, string reason) { if (GetPath(r.Requirements, path) == null) Default(r, path, new JArray(values), reason); }
    private static bool ContainsAny(string text, params string[] values) => values.Any(v => text.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0);
    private static string BuildSafeProjectName(string task) { var chars = task.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').Take(18).ToArray(); var n = new string(chars); return string.IsNullOrWhiteSpace(n) ? "TIA_AI_Project" : n; }
    private static string InferDeviceType(string task) { if (ContainsAny(task,"水箱","tank")) return "TankControl"; if (ContainsAny(task,"水泵","pump")) return "Pump"; if (ContainsAny(task,"电机","motor")) return "Motor"; if (ContainsAny(task,"阀","valve")) return "Valve"; if (ContainsAny(task,"输送","conveyor")) return "Conveyor"; return "ProcessControl"; }
    private static JArray DefaultSignals(string type) => type == "TankControl" ? new JArray("LevelPV","LowLevel","HighLevel","PumpCmd","PumpRunning","PumpFault","AutoMode","Reset") : new JArray("StartCmd","StopCmd","RunningFeedback","FaultFeedback","InterlockOk","AutoMode","ResetCmd","OutputCmd");
    private static string SerializeAutonomous(EngineeringWorkflowRecord r, string message) => new JObject { ["success"] = true, ["message"] = message, ["sessionId"] = r.SessionId, ["autonomyMode"] = r.AutonomyMode, ["stage"] = r.Stage.ToString(), ["blockingQuestions"] = JArray.FromObject(r.OpenQuestions), ["assumptions"] = r.Assumptions.DeepClone(), ["blueprint"] = r.EngineeringBlueprint.DeepClone(), ["plan"] = r.Plan == null ? JValue.CreateNull() : JToken.FromObject(r.Plan), ["nextAction"] = r.OpenQuestions.Count > 0 ? "一次性回答 blockingQuestions" : "按 plan 顺序执行离线工程；部署前再请求用户授权" }.ToString(Formatting.Indented);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

public enum EngineeringStage
{
    RequirementsCollection = 0,
    RequirementsFrozen = 1,
    PlanGenerated = 2,
    PlanApproved = 3,
    HardwarePlanning = 4,
    PlcArchitecture = 5,
    PlcImplementation = 6,
    HmiPlanning = 7,
    HmiImplementation = 8,
    Verification = 9,
    UserAcceptance = 10,
    Deployment = 11,
    Completed = 12
}

public sealed class EngineeringWorkflowOptions
{
    public bool Enabled { get; set; } = true;
    public string EnforcementMode { get; set; } = "creation_and_active_session";
    public bool RequireApprovedPlanForProjectCreation { get; set; } = true;
    public int MaxContextRules { get; set; } = 12;
    public int MinimumBehaviorTests { get; set; } = 5;
    public string DefaultAutonomyMode { get; set; } = "standard";
    public bool AutoApproveLowRiskPlans { get; set; } = true;
    public int MaxBlockingQuestions { get; set; } = 5;

    public static EngineeringWorkflowOptions Load(string baseDirectory)
    {
        var result = new EngineeringWorkflowOptions();
        try
        {
            var path = Path.Combine(baseDirectory, "server_policy.json");
            if (!File.Exists(path)) return result;
            var root = JObject.Parse(File.ReadAllText(path));
            var node = root["engineeringWorkflow"] as JObject;
            if (node == null) return result;
            result.Enabled = node["enabled"]?.Value<bool?>() ?? result.Enabled;
            result.EnforcementMode = node["enforcementMode"]?.ToString() ?? result.EnforcementMode;
            result.RequireApprovedPlanForProjectCreation = node["requireApprovedPlanForProjectCreation"]?.Value<bool?>() ?? result.RequireApprovedPlanForProjectCreation;
            result.MaxContextRules = Math.Max(4, Math.Min(30, node["maxContextRules"]?.Value<int?>() ?? result.MaxContextRules));
            result.MinimumBehaviorTests = Math.Max(1, Math.Min(20, node["minimumBehaviorTests"]?.Value<int?>() ?? result.MinimumBehaviorTests));
            result.DefaultAutonomyMode = node["defaultAutonomyMode"]?.ToString() ?? result.DefaultAutonomyMode;
            result.AutoApproveLowRiskPlans = node["autoApproveLowRiskPlans"]?.Value<bool?>() ?? result.AutoApproveLowRiskPlans;
            result.MaxBlockingQuestions = Math.Max(1, Math.Min(10, node["maxBlockingQuestions"]?.Value<int?>() ?? result.MaxBlockingQuestions));
        }
        catch
        {
            // Invalid policy must not prevent server startup; secure defaults remain active.
        }
        return result;
    }
}

public sealed class EngineeringRequirementQuestion
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Question { get; set; } = "";
    public string Why { get; set; } = "";
    public string[] Options { get; set; } = Array.Empty<string>();
    public bool Required { get; set; } = true;
}

public sealed class EngineeringPlanStep
{
    public int Order { get; set; }
    public string Stage { get; set; } = "";
    public string Goal { get; set; } = "";
    public string[] Actions { get; set; } = Array.Empty<string>();
    public string[] ExitCriteria { get; set; } = Array.Empty<string>();
    public string[] StandardsTopics { get; set; } = Array.Empty<string>();
}

public sealed class EngineeringProjectPlan
{
    public string PlanId { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; }
    public bool Approved { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public List<EngineeringPlanStep> Steps { get; set; } = new List<EngineeringPlanStep>();
}

public sealed class EngineeringEvidence
{
    public DateTime AtUtc { get; set; }
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Tool { get; set; } = "";
    public bool Success { get; set; }
    public JObject Details { get; set; } = new JObject();
}

public sealed class EngineeringWorkflowRecord
{
    public string SessionId { get; set; } = "";
    public string WorkType { get; set; } = "create_new_project";
    public string TaskSummary { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public EngineeringStage Stage { get; set; } = EngineeringStage.RequirementsCollection;
    public JObject Requirements { get; set; } = new JObject();
    public List<EngineeringRequirementQuestion> OpenQuestions { get; set; } = new List<EngineeringRequirementQuestion>();
    public bool RequirementsFrozen { get; set; }
    public DateTime? RequirementsFrozenAtUtc { get; set; }
    public EngineeringProjectPlan? Plan { get; set; }
    public bool UserAcceptanceApproved { get; set; }
    public DateTime? UserAcceptanceApprovedAtUtc { get; set; }
    public List<EngineeringEvidence> Evidence { get; set; } = new List<EngineeringEvidence>();
    public string AutonomyMode { get; set; } = "standard";
    public JObject Assumptions { get; set; } = new JObject();
    public JObject EngineeringBlueprint { get; set; } = new JObject();
}

public sealed class EngineeringWorkflowGateDecision
{
    public bool Allowed { get; set; }
    public string Error { get; set; } = "";
    public JObject Details { get; set; } = new JObject();

    public string ToJson()
    {
        var result = new JObject
        {
            ["success"] = false,
            ["error"] = Error,
            ["workflowGate"] = Details
        };
        return result.ToString(Formatting.Indented);
    }
}

/// <summary>
/// Persistent engineering workflow and context-pack service.
/// The workflow is stored outside the chat context so long conversations cannot erase requirements,
/// approvals, stage, or evidence. Only the small standard pack relevant to the current task is returned.
/// </summary>
public sealed partial class EngineeringWorkflowService
{
    private static readonly HashSet<string> WorkflowTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create_requirement_session", "update_requirement_session", "freeze_requirements",
        "generate_project_plan", "approve_project_plan", "get_requirement_status",
        "get_current_workflow_stage", "set_active_engineering_session", "deactivate_engineering_session",
        "advance_engineering_stage", "record_engineering_test_evidence", "approve_engineering_acceptance",
        "get_applicable_standards", "validate_workflow_readiness",
        "prepare_autonomous_project", "generate_engineering_blueprint", "get_engineering_blueprint", "validate_engineering_blueprint"
    };

    private static readonly HashSet<string> ProjectCreationTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create_project", "create_project_from_template", "create_industrial_project_skeleton",
        "create_proj_skeleton", "orchestrate_project"
    };

    private static readonly string[] HardwareMutationPrefixes =
    {
        "create_device", "delete_device", "plug_module", "create_subnet", "delete_subnet",
        "connect_to_subnet", "create_io_system", "connect_io_device", "connect_network_ports",
        "set_network_config", "set_device_ip", "auto_configure_network", "set_analog_channel", "configure_hardware"
    };

    private static readonly string[] PlcArchitecturePrefixes =
    {
        "create_block", "delete_block", "create_db", "create_instance_db", "create_tag_table", "delete_tag_table",
        "add_block_interface", "delete_block_interface", "add_db_variable", "add_db_members", "delete_db_variable",
        "add_tag_to_table", "delete_tag", "rename_block", "import_tag_table", "auto_allocate_io_addresses"
    };

    private static readonly string[] PlcImplementationPrefixes =
    {
        "add_lad", "apply_lad_ir", "update_network", "delete_network", "add_scl", "write_block_source",
        "import_block", "generate_block", "set_block_comment", "set_block_header", "block_"
    };

    private static readonly string[] HmiMutationFragments =
    {
        "hmi_screen", "hmi_tag", "hmi_connection", "hmi_start_screen", "screen_item",
        "classic_hmi", "unified_hmi", "plant_object", "plant_view"
    };

    private static readonly string[] OnlineMutationFragments =
    {
        "download_to_device", "write_online", "cpu_start", "cpu_stop", "start_cpu", "stop_cpu",
        "force_", "factory_reset", "reset_to_factory", "upload_station", "auto_compile_and_download",
        "download_with_config"
    };

    private readonly object _sync = new object();
    private readonly string _dataDirectory;
    private readonly string _standardsDirectory;
    private readonly string _activeSessionPath;
    private readonly EngineeringWorkflowOptions _options;

    public EngineeringWorkflowService(string baseDirectory)
    {
        _options = EngineeringWorkflowOptions.Load(baseDirectory);
        _standardsDirectory = Path.Combine(baseDirectory, "standards", "engineering");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = baseDirectory;
        _dataDirectory = Path.Combine(local, "TIA-MCP-Copilot", "engineering-workflow");
        _activeSessionPath = Path.Combine(_dataDirectory, "active-session.txt");
        Directory.CreateDirectory(_dataDirectory);
    }

    public bool Enabled => _options.Enabled;

    public string GetCompactServerInstructions()
    {
        if (!Enabled) return "";
        return "自主工程工作流已启用。用户只给一次项目要求时，优先调用 prepare_autonomous_project：系统自动补全保守默认值、生成工程蓝图并批准低风险离线计划；仅当存在硬件冲突、目标工程歧义或安全边界不明时，才一次性返回 blockingQuestions。不要逐字段追问。真实下载、在线写入和安全动作仍必须由用户明确授权。";
    }

    public string GetToolGuidance(string toolName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(toolName)) return "";
        if (ProjectCreationTools.Contains(toolName))
            return "\n工程门禁：新项目创建前必须存在已冻结需求和用户已批准的项目计划；模糊要求先调用 create_requirement_session。";
        if (StartsWithAny(toolName, PlcImplementationPrefixes))
            return "\n工程提示：实施前读取 topic=lad 或 topic=plc-architecture 的按需规范包，并在后续执行静态验证和行为验收。";
        if (HmiMutationFragments.Any(f => toolName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
            return "\n工程提示：HMI 实施前先完成画面目标、导航、权限、报警和变量映射规划，并读取 topic=hmi-planning 规范包。";
        return "";
    }

    public string CreateRequirementSession(string taskSummary, string workType, string? projectName, string? projectPath, string? initialRequirementsJson, string? autonomyMode = null)
    {
        lock (_sync)
        {
            var normalizedWorkType = string.Equals(workType, "modify_existing_project", StringComparison.OrdinalIgnoreCase)
                ? "modify_existing_project"
                : "create_new_project";
            var record = new EngineeringWorkflowRecord
            {
                SessionId = "eng-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                WorkType = normalizedWorkType,
                AutonomyMode = NormalizeAutonomyMode(autonomyMode ?? _options.DefaultAutonomyMode),
                TaskSummary = (taskSummary ?? "").Trim(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Stage = EngineeringStage.RequirementsCollection,
                Requirements = new JObject
                {
                    ["workType"] = normalizedWorkType,
                    ["taskSummary"] = (taskSummary ?? "").Trim()
                }
            };
            if (!string.IsNullOrWhiteSpace(projectName)) SetPath(record.Requirements, "project.name", projectName!.Trim());
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                SetPath(record.Requirements, "project.path", projectPath!.Trim());
                if (normalizedWorkType == "modify_existing_project") SetPath(record.Requirements, "existingProject.path", projectPath!.Trim());
            }
            if (!string.IsNullOrWhiteSpace(initialRequirementsJson))
            {
                var patch = JObject.Parse(initialRequirementsJson!);
                MergeInto(record.Requirements, patch);
                SyncRecordFields(record);
            }
            ApplyAutonomousDefaults(record);
            record.OpenQuestions = BuildQuestions(record).Take(_options.MaxBlockingQuestions).ToList();
            Save(record);
            SetActiveSessionIdUnsafe(record.SessionId);
            return SerializeRecord(record, includeRequirements: true, includeEvidence: false, message: record.OpenQuestions.Count == 0 ? "工程需求会话已创建，默认值已自动补全；可继续自动冻结、生成蓝图和计划。" : "工程需求会话已创建。仅返回无法安全推断的阻塞问题，请一次性回答 blockingQuestions。");
        }
    }

    public string UpdateRequirementSession(string sessionId, string answersJson)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (record.RequirementsFrozen)
                return Error("需求已冻结。若需求发生变化，请创建新的需求会话，避免静默改变已批准基线。", record);
            JObject answers;
            try { answers = JObject.Parse(answersJson ?? "{}"); }
            catch (Exception ex) { return Error("answersJson 不是有效 JSON 对象：" + ex.Message, record); }
            MergeInto(record.Requirements, answers);
            SyncRecordFields(record);
            record.UpdatedAtUtc = DateTime.UtcNow;
            ApplyAutonomousDefaults(record);
            record.OpenQuestions = BuildQuestions(record).Take(_options.MaxBlockingQuestions).ToList();
            Save(record);
            return SerializeRecord(record, includeRequirements: true, includeEvidence: false,
                message: record.OpenQuestions.Count == 0 ? "必填需求已补齐，可以调用 freeze_requirements。" : "需求已更新，请继续回答 openQuestions。" );
        }
    }

    public string FreezeRequirements(string sessionId, bool approvedByUser)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (record.RequirementsFrozen)
                return SerializeRecord(record, includeRequirements: true, includeEvidence: true, message: "需求已经冻结，未重复修改基线。" );
            record.OpenQuestions = BuildQuestions(record);
            if (!approvedByUser)
                return Error("冻结需求需要用户明确确认。请在用户确认需求摘要后传入 approvedByUser=true。", record);
            if (record.OpenQuestions.Any(q => q.Required))
                return Error("仍有必填需求未补齐，不能冻结。", record);
            record.RequirementsFrozen = true;
            record.RequirementsFrozenAtUtc = DateTime.UtcNow;
            record.Stage = EngineeringStage.RequirementsFrozen;
            AddEvidence(record, "requirements", "用户已批准并冻结结构化需求。", "freeze_requirements", true);
            Save(record);
            return SerializeRecord(record, includeRequirements: true, includeEvidence: true, message: "需求基线已冻结。下一步调用 generate_project_plan。" );
        }
    }

    public string GenerateProjectPlan(string sessionId)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (!record.RequirementsFrozen)
                return Error("需求尚未冻结。先补齐 openQuestions 并调用 freeze_requirements。", record);
            if (record.Plan?.Approved == true)
                return Error("当前计划已经批准。若需求或计划需要变化，请创建新的需求会话，避免覆盖已批准基线。", record);
            var hmiRequired = GetBool(record.Requirements, "hmi.required") == true;
            var steps = new List<EngineeringPlanStep>
            {
                Step(1, "ProjectCreation", "建立可回滚的 TIA 工程基线", new[]{"确认 TIA 版本、名称和路径", "创建或附加目标工程", "保存首次基线"}, new[]{"项目可重新打开", "项目路径与需求一致", "无第二个误启动 TIA 实例"}, new[]{"project-creation","core"}),
                Step(2, "HardwarePlanning", "建立硬件、网络和 IO 规划", new[]{"选择已验证的设备标识", "规划子网/IP/IO 地址", "生成 IO 映射表"}, new[]{"无地址冲突", "硬件编译通过", "设备命名与需求一致"}, new[]{"hardware","core"}),
                Step(3, "PlcArchitecture", "先搭建数据与块架构", new[]{"建立 UDT/DB/FB/FC/OB 调用层次", "定义单一输出所有者", "定义模式、联锁、报警接口"}, new[]{"块接口完整", "OB 调用链可追踪", "输出写入责任唯一"}, new[]{"plc-architecture","core"}),
                Step(4, "PlcImplementation", "实现并逐网络验证 PLC 逻辑", new[]{"按网络单一职责实现", "验证定时器/边沿实例", "检查停止和故障优先级"}, new[]{"LAD 静态检查通过", "块与 PLC 编译通过", "测试向量已生成"}, new[]{"lad","verification"})
            };
            if (hmiRequired)
            {
                steps.Add(Step(5, "HmiPlanning", "完成 HMI 信息架构和画面规格", new[]{"定义用户、导航、权限、报警和趋势", "建立 PLC-HMI 变量映射", "确认画布和触控尺寸"}, new[]{"每个画面有明确目标", "导航无死路", "全部控件有变量或静态用途"}, new[]{"hmi-planning","core"}));
                steps.Add(Step(6, "HmiImplementation", "按规格创建并回读诊断 HMI", new[]{"先建连接与标签", "按布局规格创建画面", "回读并诊断越界、重叠和缺失标签"}, new[]{"HMI 编译通过", "无文本截断", "无缺失变量绑定"}, new[]{"hmi-implementation","verification"}));
            }
            steps.Add(Step(steps.Count + 1, "Verification", "执行静态、编译和行为验收", new[]{"PLC/HMI 全编译", "执行启动、停止、急停、故障和模式切换用例", "记录观察结果"}, new[]{"所有关键测试有结果", "失败项已修复或被用户接受", "回滚点可用"}, new[]{"verification","lad"}));
            steps.Add(Step(steps.Count + 1, "UserAcceptance", "由用户确认功能和风险边界", new[]{"提交需求追踪矩阵", "提交编译与测试报告", "确认未解决风险"}, new[]{"用户明确批准验收"}, new[]{"verification","core"}));
            steps.Add(Step(steps.Count + 1, "Deployment", "受控下载和投运", new[]{"确认目标设备身份", "执行最终备份", "下载、启动并监视"}, new[]{"现场状态符合验收标准", "无未授权在线写入"}, new[]{"deployment","core"}));

            record.Plan = new EngineeringProjectPlan
            {
                PlanId = "plan-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                GeneratedAtUtc = DateTime.UtcNow,
                Approved = false,
                Steps = steps
            };
            record.Stage = EngineeringStage.PlanGenerated;
            record.UpdatedAtUtc = DateTime.UtcNow;
            AddEvidence(record, "plan", "已根据冻结需求生成工程计划。", "generate_project_plan", true);
            Save(record);
            return SerializeRecord(record, includeRequirements: true, includeEvidence: true, message: "项目计划已生成。请向用户展示 plan，并在用户确认后调用 approve_project_plan。" );
        }
    }

    public string ApproveProjectPlan(string sessionId, string planId, bool approvedByUser)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (record.Plan == null) return Error("尚未生成项目计划。", record);
            if (!string.Equals(record.Plan.PlanId, planId, StringComparison.Ordinal)) return Error("planId 与当前计划不一致，禁止批准过期计划。", record);
            if (record.Plan.Approved)
                return SerializeRecord(record, includeRequirements: false, includeEvidence: true, message: "该计划已经批准，未重复改变状态。" );
            if (!approvedByUser) return Error("计划批准需要用户明确确认，必须传入 approvedByUser=true。", record);
            record.Plan.Approved = true;
            record.Plan.ApprovedAtUtc = DateTime.UtcNow;
            record.Stage = EngineeringStage.PlanApproved;
            record.UpdatedAtUtc = DateTime.UtcNow;
            AddEvidence(record, "approval", "用户已批准工程计划。", "approve_project_plan", true);
            Save(record);
            return SerializeRecord(record, includeRequirements: false, includeEvidence: true,
                message: record.WorkType == "create_new_project" ? "计划已批准，现在可以调用 create_project。" : "计划已批准。确认已附加现有工程后，可推进到 HardwarePlanning。" );
        }
    }

    public string GetRequirementStatus(string? sessionId)
    {
        lock (_sync)
        {
            var record = TryLoad(ResolveSessionId(sessionId));
            if (record == null) return JsonConvert.SerializeObject(new { success = false, error = "未找到工程需求会话。", action = "调用 create_requirement_session 开始。" }, Formatting.Indented);
            record.OpenQuestions = BuildQuestions(record);
            return SerializeRecord(record, includeRequirements: true, includeEvidence: true, message: "当前工程工作流状态。" );
        }
    }

    public string SetActiveSession(string sessionId)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            SetActiveSessionIdUnsafe(record.SessionId);
            return JsonConvert.SerializeObject(new { success = true, activeSessionId = record.SessionId, stage = record.Stage.ToString(), taskSummary = record.TaskSummary }, Formatting.Indented);
        }
    }

    public string DeactivateSession(string? sessionId)
    {
        lock (_sync)
        {
            var activeId = ResolveSessionId(null);
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.Equals(activeId, sessionId, StringComparison.Ordinal))
                return JsonConvert.SerializeObject(new { success = false, error = "指定会话不是当前活动会话，未执行停用。", activeSessionId = activeId }, Formatting.Indented);
            try { if (File.Exists(_activeSessionPath)) File.Delete(_activeSessionPath); }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = "停用活动会话失败：" + ex.Message }, Formatting.Indented); }
            return JsonConvert.SerializeObject(new { success = true, message = "工程会话已停用，记录仍保留。后续可用 set_active_engineering_session 恢复。", deactivatedSessionId = activeId }, Formatting.Indented);
        }
    }

    public string AdvanceStage(string sessionId, string targetStage, string evidenceSummary, bool approvedByUser)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (!Enum.TryParse(targetStage, true, out EngineeringStage target))
                return Error("未知阶段。允许值：" + string.Join(", ", Enum.GetNames(typeof(EngineeringStage))), record);
            var expected = NextStage(record);
            if (target != expected)
                return Error($"只能推进到下一合法阶段 {expected}，不能从 {record.Stage} 跳到 {target}。", record);
            if (record.Stage == EngineeringStage.PlanApproved && record.WorkType == "create_new_project")
                return Error("新项目不能跳过创建步骤。请调用 create_project；成功后系统自动进入 HardwarePlanning。", record);
            if (target == EngineeringStage.UserAcceptance && !HasVerificationEvidence(record))
                return Error($"进入用户验收前需要成功的编译、静态验证，以及至少 {_options.MinimumBehaviorTests} 个通过的行为测试，且不能存在未修复失败项。", record);
            if (target == EngineeringStage.Deployment && !record.UserAcceptanceApproved)
                return Error("进入 Deployment 前必须先调用 approve_engineering_acceptance 完成用户验收批准。", record);
            if ((target == EngineeringStage.Deployment || target == EngineeringStage.Completed) && !approvedByUser)
                return Error("进入部署或完成阶段需要用户明确批准 approvedByUser=true。", record);
            if (string.IsNullOrWhiteSpace(evidenceSummary))
                return Error("阶段推进必须提供 evidenceSummary，说明前一阶段完成了什么以及如何验证。", record);

            record.Stage = target;
            if (target == EngineeringStage.UserAcceptance && approvedByUser)
            {
                record.UserAcceptanceApproved = true;
                record.UserAcceptanceApprovedAtUtc = DateTime.UtcNow;
            }
            record.UpdatedAtUtc = DateTime.UtcNow;
            AddEvidence(record, "stage", evidenceSummary.Trim(), "advance_engineering_stage", true);
            Save(record);
            return SerializeRecord(record, includeRequirements: false, includeEvidence: true, message: "阶段已推进。请仅加载当前阶段相关规范包。" );
        }
    }

    public string RecordTestEvidence(string sessionId, string testName, string initialConditions, string action,
        string expectedResult, string observedResult, bool passed)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (record.Stage < EngineeringStage.PlcImplementation || record.Stage > EngineeringStage.UserAcceptance)
                return Error("行为测试证据只能在 PLC 实施完成后、部署前记录。", record);
            if (string.IsNullOrWhiteSpace(testName) || string.IsNullOrWhiteSpace(expectedResult) || string.IsNullOrWhiteSpace(observedResult))
                return Error("testName、expectedResult 和 observedResult 不能为空。", record);
            var details = new JObject
            {
                ["testName"] = testName.Trim(),
                ["initialConditions"] = initialConditions ?? "",
                ["action"] = action ?? "",
                ["expectedResult"] = expectedResult.Trim(),
                ["observedResult"] = observedResult.Trim()
            };
            AddEvidence(record, "behavior_test", $"{testName.Trim()}: {(passed ? "PASS" : "FAIL")}", "record_engineering_test_evidence", passed, details);
            Save(record);
            var latest = LatestBehaviorTests(record);
            return JsonConvert.SerializeObject(new
            {
                success = true,
                sessionId = record.SessionId,
                stage = record.Stage.ToString(),
                recorded = details,
                passed,
                latestBehaviorTestCount = latest.Count,
                latestPassedCount = latest.Count(x => x.Success),
                minimumRequired = _options.MinimumBehaviorTests,
                acceptanceReady = HasVerificationEvidence(record),
                message = passed ? "行为测试证据已记录。" : "失败证据已记录；修复后请用相同 testName 重新测试，最新结果会覆盖验收判断。"
            }, Formatting.Indented);
        }
    }

    public string ApproveAcceptance(string sessionId, string acceptanceSummary, bool approvedByUser)
    {
        lock (_sync)
        {
            var record = RequireRecord(sessionId);
            if (record.UserAcceptanceApproved)
                return SerializeRecord(record, includeRequirements: false, includeEvidence: true, message: "用户验收已经批准，未重复改变状态。" );
            if (record.Stage != EngineeringStage.Verification && record.Stage != EngineeringStage.UserAcceptance)
                return Error("只有完成验证后才能批准验收。", record);
            if (!approvedByUser) return Error("必须由用户明确批准验收。", record);
            if (!HasVerificationEvidence(record)) return Error($"验收证据不足：需要成功编译、静态验证、至少 {_options.MinimumBehaviorTests} 个最新通过的行为测试，且不能有未修复失败项。", record);
            record.UserAcceptanceApproved = true;
            record.UserAcceptanceApprovedAtUtc = DateTime.UtcNow;
            record.Stage = EngineeringStage.UserAcceptance;
            AddEvidence(record, "acceptance", acceptanceSummary ?? "用户已批准验收。", "approve_engineering_acceptance", true);
            Save(record);
            return SerializeRecord(record, includeRequirements: false, includeEvidence: true, message: "用户验收已批准。下一步可推进到 Deployment，但在线操作仍受现有安全审批控制。" );
        }
    }

    public string GetApplicableStandards(string topic, int? maxRules)
    {
        var normalized = NormalizeTopic(topic);
        var limit = Math.Max(4, Math.Min(30, maxRules ?? _options.MaxContextRules));
        var path = Path.Combine(_standardsDirectory, normalized + ".json");
        if (!File.Exists(path))
        {
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = "未找到该规范主题。",
                supportedTopics = SupportedTopics()
            }, Formatting.Indented);
        }
        try
        {
            var module = JObject.Parse(File.ReadAllText(path));
            var rules = (module["rules"] as JArray ?? new JArray()).Take(limit).ToArray();
            var checks = (module["checklist"] as JArray ?? new JArray()).Take(Math.Max(6, limit)).ToArray();
            return new JObject
            {
                ["success"] = true,
                ["topic"] = normalized,
                ["purpose"] = module["purpose"]?.DeepClone() ?? new JValue(""),
                ["rules"] = new JArray(rules),
                ["checklist"] = new JArray(checks),
                ["forbiddenPatterns"] = module["forbiddenPatterns"]?.DeepClone() ?? new JArray(),
                ["source"] = "server-side context pack",
                ["note"] = "仅返回当前主题的小型规范包，避免把全部规范塞入长上下文。"
            }.ToString(Formatting.Indented);
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new { success = false, error = "规范文件读取失败：" + ex.Message }, Formatting.Indented);
        }
    }

    public string ValidateReadiness(string? sessionId, string action)
    {
        lock (_sync)
        {
            var record = TryLoad(ResolveSessionId(sessionId));
            if (record == null)
                return JsonConvert.SerializeObject(new { success = false, ready = false, error = "没有活动工程会话。", action = "create_requirement_session" }, Formatting.Indented);
            var fakeMetadata = new ToolMetadata { OriginalName = action ?? "", ReadOnly = false, RiskLevel = ToolRiskLevel.Medium, Scope = ToolOperationScope.Project, MutatesProject = true };
            var decision = EvaluateBeforeTool(action ?? "", fakeMetadata, new JObject());
            return JsonConvert.SerializeObject(new
            {
                success = true,
                ready = decision.Allowed,
                action,
                sessionId = record.SessionId,
                stage = record.Stage.ToString(),
                reason = decision.Allowed ? "当前阶段允许该操作。" : decision.Error,
                gate = decision.Details
            }, Formatting.Indented);
        }
    }

    public EngineeringWorkflowGateDecision EvaluateBeforeTool(string toolName, ToolMetadata metadata, JObject args)
    {
        if (!Enabled) return Allow();
        if (string.Equals(toolName, "batch_operation", StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync)
            {
                var active = TryLoad(ResolveSessionId(null));
                return Deny("工程工作流启用时禁止 batch_operation，因为其内部反射调用不会逐项经过工作流门禁和证据记录。", active,
                    "改用单个 MCP 工具按计划顺序执行；每一步都经过 WorkflowGate、Schema、风险策略和审计。" );
            }
        }
        if (metadata.ReadOnly || WorkflowTools.Contains(toolName) || metadata.Scope == ToolOperationScope.Session)
            return Allow();

        lock (_sync)
        {
            var record = TryLoad(ResolveSessionId(null));
            var isCreation = ProjectCreationTools.Contains(toolName);
            if (record == null)
            {
                if (isCreation && _options.RequireApprovedPlanForProjectCreation)
                {
                    return Deny("新项目创建被工程门禁阻止：尚未建立并批准需求与项目计划。", null,
                        "调用 create_requirement_session，将用户的模糊要求转为结构化需求和 openQuestions。" );
                }
                return Allow();
            }

            // An active session intentionally enables strict stage checks for relevant mutations.
            if (isCreation)
            {
                if (!string.Equals(toolName, "create_project", StringComparison.OrdinalIgnoreCase))
                    return Deny("当前第一阶段工作流禁止使用跨阶段的一键编排/模板创建工具。", record,
                        "先用 create_project 建立空工程基线，再按 HardwarePlanning、PLC、HMI、Verification 阶段使用颗粒化工具。" );
                if (!record.RequirementsFrozen || record.Plan == null || !record.Plan.Approved || record.Stage != EngineeringStage.PlanApproved)
                    return Deny("项目创建需要已冻结需求、已生成且由用户批准的计划，并处于 PlanApproved 阶段。", record,
                        "依次完成 freeze_requirements、generate_project_plan、approve_project_plan。" );
                return Allow();
            }

            if (OnlineMutationFragments.Any(f => toolName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                if (record.Stage < EngineeringStage.Deployment || !record.UserAcceptanceApproved)
                    return Deny("在线写入/下载只能在用户验收批准后进入 Deployment 阶段执行。", record,
                        "先完成编译、行为验证和 approve_engineering_acceptance。" );
                return Allow();
            }

            var minimum = MinimumStageFor(toolName);
            if (minimum.HasValue && record.Stage < minimum.Value)
            {
                return Deny($"当前工具属于 {minimum.Value} 或后续阶段，当前阶段为 {record.Stage}。", record,
                    $"先完成前置阶段并调用 advance_engineering_stage 推进到 {minimum.Value}。" );
            }
            return Allow();
        }
    }

    public void RecordToolSuccess(string toolName, ToolMetadata metadata, JObject args, string resultText)
    {
        if (!Enabled || WorkflowTools.Contains(toolName)) return;
        var meaningfulRead = toolName.StartsWith("validate_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("diagnose_", StringComparison.OrdinalIgnoreCase)
            || toolName.StartsWith("compare_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "get_project_info", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "get_tia_status", StringComparison.OrdinalIgnoreCase);
        if (metadata.ReadOnly && !meaningfulRead) return;
        lock (_sync)
        {
            var record = TryLoad(ResolveSessionId(null));
            if (record == null) return;
            var summary = SummarizeToolResult(toolName, resultText);
            AddEvidence(record, EvidenceKind(toolName), summary, toolName, true);
            if (ProjectCreationTools.Contains(toolName) && record.Stage == EngineeringStage.PlanApproved)
                record.Stage = EngineeringStage.HardwarePlanning;
            if (toolName.IndexOf("compile", StringComparison.OrdinalIgnoreCase) >= 0 || toolName.IndexOf("validate", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Verification evidence is recorded without forcing a stage jump; iterative compile remains possible.
            }
            record.UpdatedAtUtc = DateTime.UtcNow;
            Save(record);
        }
    }

    public JObject GetRuntimeSummary()
    {
        lock (_sync)
        {
            var record = TryLoad(ResolveSessionId(null));
            if (record == null)
                return new JObject { ["success"] = true, ["enabled"] = Enabled, ["active"] = false, ["enforcementMode"] = _options.EnforcementMode };
            var latestTests = LatestBehaviorTests(record);
            return new JObject
            {
                ["success"] = true,
                ["enabled"] = Enabled,
                ["active"] = true,
                ["sessionId"] = record.SessionId,
                ["workType"] = record.WorkType,
                ["stage"] = record.Stage.ToString(),
                ["requirementsFrozen"] = record.RequirementsFrozen,
                ["planApproved"] = record.Plan?.Approved ?? false,
                ["openQuestionCount"] = record.OpenQuestions.Count,
                ["behaviorTestCount"] = latestTests.Count,
                ["behaviorTestsPassed"] = latestTests.Count(x => x.Success),
                ["minimumBehaviorTests"] = _options.MinimumBehaviorTests,
                ["userAcceptanceApproved"] = record.UserAcceptanceApproved
            };
        }
    }

    private EngineeringStage? MinimumStageFor(string toolName)
    {
        if (string.Equals(toolName, "setup_network_and_hmi_connection", StringComparison.OrdinalIgnoreCase))
            return EngineeringStage.HardwarePlanning;
        if (HardwareMutationPrefixes.Any(p => toolName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
            toolName.IndexOf("compile_hardware", StringComparison.OrdinalIgnoreCase) >= 0)
            return EngineeringStage.HardwarePlanning;
        if (PlcArchitecturePrefixes.Any(p => toolName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return EngineeringStage.PlcArchitecture;
        if (PlcImplementationPrefixes.Any(p => toolName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
            toolName.IndexOf("compile_plc", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("compile_block", StringComparison.OrdinalIgnoreCase) >= 0)
            return EngineeringStage.PlcImplementation;
        if (HmiMutationFragments.Any(f => toolName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) ||
            toolName.IndexOf("compile_hmi", StringComparison.OrdinalIgnoreCase) >= 0)
            return EngineeringStage.HmiImplementation;
        return null;
    }

    private static EngineeringStage NextStage(EngineeringWorkflowRecord record)
    {
        if (record.Stage == EngineeringStage.PlcImplementation && GetBool(record.Requirements, "hmi.required") != true)
            return EngineeringStage.Verification;
        return record.Stage >= EngineeringStage.Completed ? EngineeringStage.Completed : (EngineeringStage)((int)record.Stage + 1);
    }

    private bool HasVerificationEvidence(EngineeringWorkflowRecord record)
    {
        var hasCompile = record.Evidence.Any(e => e.Success && e.Tool.IndexOf("compile", StringComparison.OrdinalIgnoreCase) >= 0);
        var hasValidation = record.Evidence.Any(e => e.Success &&
            (e.Tool.IndexOf("validate", StringComparison.OrdinalIgnoreCase) >= 0 || e.Tool.IndexOf("diagnose", StringComparison.OrdinalIgnoreCase) >= 0));
        var latestTests = LatestBehaviorTests(record);
        var behaviorReady = latestTests.Count >= _options.MinimumBehaviorTests && latestTests.All(e => e.Success);
        return hasCompile && hasValidation && behaviorReady;
    }

    private static List<EngineeringEvidence> LatestBehaviorTests(EngineeringWorkflowRecord record)
    {
        return record.Evidence
            .Where(e => string.Equals(e.Kind, "behavior_test", StringComparison.OrdinalIgnoreCase))
            .Where(e => !string.IsNullOrWhiteSpace(e.Details?["testName"]?.ToString()))
            .GroupBy(e => e.Details["testName"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.AtUtc).First())
            .OrderBy(e => e.Details["testName"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string EvidenceKind(string toolName)
    {
        if (toolName.IndexOf("compile", StringComparison.OrdinalIgnoreCase) >= 0) return "compile";
        if (toolName.IndexOf("validate", StringComparison.OrdinalIgnoreCase) >= 0 || toolName.IndexOf("diagnose", StringComparison.OrdinalIgnoreCase) >= 0) return "validation";
        if (ProjectCreationTools.Contains(toolName)) return "project";
        return "tool";
    }

    private static string SummarizeToolResult(string toolName, string resultText)
    {
        try
        {
            var obj = JObject.Parse(resultText ?? "{}");
            var message = obj["message"]?.ToString() ?? obj["error"]?.ToString() ?? "success=true";
            if (message.Length > 500) message = message.Substring(0, 500);
            return toolName + ": " + message;
        }
        catch { return toolName + " completed successfully."; }
    }

    private static void SyncRecordFields(EngineeringWorkflowRecord record)
    {
        var task = GetPath(record.Requirements, "taskSummary")?.ToString();
        if (!string.IsNullOrWhiteSpace(task)) record.TaskSummary = task.Trim();
    }

    private List<EngineeringRequirementQuestion> BuildQuestions(EngineeringWorkflowRecord record)
    {
        var q = new List<EngineeringRequirementQuestion>();
        if (string.IsNullOrWhiteSpace(record.TaskSummary))
            q.Add(Q("taskSummary", "请用一两句话描述要解决的工艺目标和完成边界。", "没有目标就无法判断逻辑是否真正可用。"));

        if (record.WorkType == "create_new_project")
        {
            Require(q, record.Requirements, "project.name", "项目名称是什么？", "用于创建工程、块命名和交付报告。" );
            Require(q, record.Requirements, "project.path", "项目保存到哪个目录？", "必须避免覆盖现有工程并建立可回滚基线。" );
            Require(q, record.Requirements, "project.tiaVersion", "使用哪个 TIA Portal 版本？", "项目格式、硬件目录和 Openness API 与版本相关。", "V17", "V18", "V19", "V20" );
            Require(q, record.Requirements, "controller.model", "PLC 的准确型号或订单号是什么？", "不同 CPU 的 IO、存储和指令能力不同。" );
        }
        else
        {
            RequireEither(q, record.Requirements, new[]{"existingProject.path","project.path"}, "existingProject.path", "现有项目的完整路径是什么？", "用于确认修改目标，防止改错工程。" );
            Require(q, record.Requirements, "change.scope", "本次允许修改哪些设备、块、画面或网络？", "明确变更边界，避免 AI 扩大修改范围。" );
            Require(q, record.Requirements, "change.rollbackPolicy", "修改失败时采用什么回滚方式？", "现有工程写入前必须有恢复策略。", "安全沙盒", "项目归档", "版本控制" );
        }

        Require(q, record.Requirements, "process.description", "请描述工艺对象、主要动作、启动/停止条件和正常流程。", "编译只能验证语法，工艺描述决定行为是否正确。" );
        RequireArray(q, record.Requirements, "operation.modes", "需要哪些运行模式？例如手动、自动、检修、本地/远程。", "模式必须集中定义，避免多个网络互相争夺输出。" );
        Require(q, record.Requirements, "safety.stopPolicy", "停止、急停、故障出现时输出应如何处理？", "决定停止优先级和安全状态。", "立即安全停止", "受控停止", "由现有安全程序处理" );
        Require(q, record.Requirements, "io.assignmentPolicy", "IO 地址如何确定？", "防止自动分配与电气图或现有地址冲突。", "按电气图", "AI规划后确认", "沿用现有地址" );
        RequireBool(q, record.Requirements, "hmi.required", "是否需要 HMI？", "决定是否进入 HMI 规划与实施阶段。" );
        if (GetBool(record.Requirements, "hmi.required") == true)
        {
            Require(q, record.Requirements, "hmi.deviceModel", "HMI 的准确型号和运行系统是什么？", "画布、控件能力、连接方式和编译目标依赖设备。" );
            RequireArray(q, record.Requirements, "hmi.screenGoals", "需要哪些画面？请按“画面名称 + 使用者 + 目标”列出。", "先做信息架构，避免直接堆控件。" );
        }
        RequireBool(q, record.Requirements, "validation.simulationRequired", "是否要求 PLCSIM/HMI 仿真和行为测试？", "没有行为测试，不能证明编译后的程序可运行。" );
        return q;
    }

    private static EngineeringRequirementQuestion Q(string path, string question, string why, params string[] options) => new EngineeringRequirementQuestion
    {
        Id = "q_" + path.Replace('.', '_'), Path = path, Question = question, Why = why, Options = options ?? Array.Empty<string>(), Required = true
    };

    private static void Require(List<EngineeringRequirementQuestion> q, JObject data, string path, string question, string why, params string[] options)
    {
        if (!HasValue(data, path)) q.Add(Q(path, question, why, options));
    }

    private static void RequireEither(List<EngineeringRequirementQuestion> q, JObject data, string[] paths, string questionPath, string question, string why)
    {
        if (!paths.Any(p => HasValue(data, p))) q.Add(Q(questionPath, question, why));
    }

    private static void RequireArray(List<EngineeringRequirementQuestion> q, JObject data, string path, string question, string why)
    {
        var token = GetPath(data, path);
        if (!(token is JArray array) || array.Count == 0) q.Add(Q(path, question, why));
    }

    private static void RequireBool(List<EngineeringRequirementQuestion> q, JObject data, string path, string question, string why)
    {
        var token = GetPath(data, path);
        if (token == null || token.Type != JTokenType.Boolean) q.Add(Q(path, question, why, "true", "false"));
    }

    private static bool HasValue(JObject data, string path)
    {
        var token = GetPath(data, path);
        if (token == null || token.Type == JTokenType.Null) return false;
        if (token.Type == JTokenType.String) return !string.IsNullOrWhiteSpace(token.ToString());
        if (token is JArray a) return a.Count > 0;
        if (token is JObject o) return o.HasValues;
        return true;
    }

    private static bool? GetBool(JObject data, string path)
    {
        var token = GetPath(data, path);
        return token?.Type == JTokenType.Boolean ? token.Value<bool>() : (bool?)null;
    }

    private static JToken? GetPath(JObject root, string path)
    {
        JToken current = root;
        foreach (var part in path.Split('.'))
        {
            if (!(current is JObject obj)) return null;
            var next = obj[part];
            if (next == null) return null;
            current = next;
        }
        return current;
    }

    private static void SetPath(JObject root, string path, JToken value)
    {
        var parts = path.Split('.');
        JObject current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!(current[parts[i]] is JObject child))
            {
                child = new JObject();
                current[parts[i]] = child;
            }
            current = child;
        }
        current[parts[parts.Length - 1]] = value;
    }

    private static void MergeInto(JObject target, JObject patch)
    {
        foreach (var property in patch.Properties())
        {
            if (property.Value is JObject patchObject && target[property.Name] is JObject targetObject)
                MergeInto(targetObject, patchObject);
            else
                target[property.Name] = property.Value.DeepClone();
        }
    }

    private static EngineeringPlanStep Step(int order, string stage, string goal, string[] actions, string[] exitCriteria, string[] topics) => new EngineeringPlanStep
    {
        Order = order, Stage = stage, Goal = goal, Actions = actions, ExitCriteria = exitCriteria, StandardsTopics = topics
    };

    private static bool StartsWithAny(string value, IEnumerable<string> prefixes) => prefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeTopic(string topic)
    {
        var t = (topic ?? "core").Trim().ToLowerInvariant().Replace('_', '-');
        if (t == "plc" || t == "architecture") return "plc-architecture";
        if (t == "hmi") return "hmi-planning";
        if (t == "test" || t == "testing") return "verification";
        if (!SupportedTopics().Contains(t, StringComparer.OrdinalIgnoreCase)) return t;
        return t;
    }

    private static string[] SupportedTopics() => new[] { "core", "project-creation", "hardware", "plc-architecture", "lad", "hmi-planning", "hmi-implementation", "verification", "deployment" };

    private EngineeringWorkflowRecord RequireRecord(string? sessionId)
    {
        var id = ResolveSessionId(sessionId);
        var record = TryLoad(id);
        if (record == null) throw new InvalidOperationException("未找到工程会话：" + (id ?? "<active>"));
        return record;
    }

    private string? ResolveSessionId(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var supplied = sessionId!.Trim();
            return IsValidSessionId(supplied) ? supplied : null;
        }
        try
        {
            var active = File.Exists(_activeSessionPath) ? File.ReadAllText(_activeSessionPath).Trim() : null;
            return IsValidSessionId(active) ? active : null;
        }
        catch { return null; }
    }

    private static bool IsValidSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId!.Length > 64 || !sessionId.StartsWith("eng-", StringComparison.Ordinal)) return false;
        return sessionId.All(c => char.IsLetterOrDigit(c) || c == '-');
    }

    private void SetActiveSessionIdUnsafe(string sessionId)
    {
        if (!IsValidSessionId(sessionId)) throw new InvalidOperationException("工程会话 ID 非法。");
        Directory.CreateDirectory(_dataDirectory);
        File.WriteAllText(_activeSessionPath, sessionId);
    }

    private string RecordPath(string id)
    {
        if (!IsValidSessionId(id)) throw new InvalidOperationException("工程会话 ID 非法。");
        return Path.Combine(_dataDirectory, id + ".json");
    }

    private EngineeringWorkflowRecord? TryLoad(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            var path = RecordPath(id!);
            if (!File.Exists(path)) return null;
            var record = JsonConvert.DeserializeObject<EngineeringWorkflowRecord>(File.ReadAllText(path));
            if (record != null)
            {
                record.Requirements = record.Requirements ?? new JObject();
                record.OpenQuestions = record.OpenQuestions ?? new List<EngineeringRequirementQuestion>();
                record.Assumptions = record.Assumptions ?? new JObject();
                record.EngineeringBlueprint = record.EngineeringBlueprint ?? new JObject();
                record.AutonomyMode = NormalizeAutonomyMode(record.AutonomyMode);
                record.Evidence = record.Evidence ?? new List<EngineeringEvidence>();
                foreach (var evidence in record.Evidence) evidence.Details = evidence.Details ?? new JObject();
            }
            return record;
        }
        catch { return null; }
    }

    private void Save(EngineeringWorkflowRecord record)
    {
        record.UpdatedAtUtc = DateTime.UtcNow;
        if (record.Evidence.Count > 500) record.Evidence = record.Evidence.Skip(record.Evidence.Count - 500).ToList();
        Directory.CreateDirectory(_dataDirectory);
        var path = RecordPath(record.SessionId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(record, Formatting.Indented));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private static void AddEvidence(EngineeringWorkflowRecord record, string kind, string summary, string tool, bool success, JObject? details = null)
    {
        record.Evidence.Add(new EngineeringEvidence
        {
            AtUtc = DateTime.UtcNow,
            Kind = kind,
            Summary = summary,
            Tool = tool,
            Success = success,
            Details = details ?? new JObject()
        });
    }

    private static EngineeringWorkflowGateDecision Allow() => new EngineeringWorkflowGateDecision { Allowed = true };

    private static EngineeringWorkflowGateDecision Deny(string error, EngineeringWorkflowRecord? record, string nextAction)
    {
        var details = new JObject
        {
            ["blocked"] = true,
            ["nextAction"] = nextAction
        };
        if (record != null)
        {
            details["sessionId"] = record.SessionId;
            details["stage"] = record.Stage.ToString();
            details["requirementsFrozen"] = record.RequirementsFrozen;
            details["planApproved"] = record.Plan?.Approved ?? false;
            details["openQuestions"] = JArray.FromObject(record.OpenQuestions.Take(12));
        }
        return new EngineeringWorkflowGateDecision { Allowed = false, Error = error, Details = details };
    }

    private static string Error(string error, EngineeringWorkflowRecord? record)
    {
        return JsonConvert.SerializeObject(new
        {
            success = false,
            error,
            sessionId = record?.SessionId,
            stage = record?.Stage.ToString(),
            openQuestions = record?.OpenQuestions
        }, Formatting.Indented);
    }

    private static string SerializeRecord(EngineeringWorkflowRecord record, bool includeRequirements, bool includeEvidence, string message)
    {
        var obj = new JObject
        {
            ["success"] = true,
            ["message"] = message,
            ["sessionId"] = record.SessionId,
            ["workType"] = record.WorkType,
            ["taskSummary"] = record.TaskSummary,
            ["stage"] = record.Stage.ToString(),
            ["requirementsFrozen"] = record.RequirementsFrozen,
            ["openQuestionCount"] = record.OpenQuestions.Count,
            ["openQuestions"] = JArray.FromObject(record.OpenQuestions),
            ["plan"] = record.Plan == null ? JValue.CreateNull() : JToken.FromObject(record.Plan),
            ["userAcceptanceApproved"] = record.UserAcceptanceApproved,
            ["updatedAtUtc"] = record.UpdatedAtUtc
        };
        if (includeRequirements) obj["requirements"] = record.Requirements.DeepClone();
        if (includeEvidence) obj["recentEvidence"] = JArray.FromObject(record.Evidence.TakeLastCompat(30));
        return obj.ToString(Formatting.Indented);
    }
}

internal static class EngineeringWorkflowEnumerableExtensions
{
    public static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> source, int count)
    {
        var list = source as IList<T> ?? source.ToList();
        return list.Skip(Math.Max(0, list.Count - Math.Max(0, count)));
    }
}

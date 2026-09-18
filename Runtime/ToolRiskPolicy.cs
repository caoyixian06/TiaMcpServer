using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcpServer;

public enum PolicyDecision
{
    Allow,
    Deny,
    RequireApproval
}

/// <summary>
/// 工具风险分类与执行策略。RiskLevel 默认 Unknown；无法明确分类的新工具禁止执行。
/// </summary>
public sealed class ToolRiskPolicy
{
    private static readonly string[] ReadOnlyPrefixes =
    {
        "get_", "list_", "read_", "find_", "search_", "query_", "validate_", "verify_", "probe_", "dump_",
        "diagnose_", "compare_", "check_", "scan_", "explore_", "is_", "fingerprint_get_",
        "block_get_", "can_", "library_get_", "library_list_", "library_compare_",
        "multiuser_check_", "multiuser_get_", "multiuser_list_", "plcsim_get_", "plcsim_list_",
        "plcsim_advanced_list_", "safety_get_", "safety_list_", "safety_find_", "security_get_",
        "security_list_", "umac_find_", "umac_get_", "umac_list_", "vcs_find_", "vcs_get_", "vcs_list_"
    };

    private static readonly string[] ReadOnlyNames =
    {
        "ping", "get_runtime_status", "download_check", "upload_check", "settings_find",
        "settings_get", "settings_list", "settings_list_folders", "transaction_status",
        "transaction_is_supported", "plcsim_check_available", "get_requirement_status",
        "get_current_workflow_stage", "get_applicable_standards", "validate_workflow_readiness",
        "get_engineering_blueprint", "validate_engineering_blueprint",
        "get_xml_ir_capabilities", "compile_lad_ir", "validate_lad_ir", "get_lad_reference_profile", "compile_hmi_ir", "validate_hmi_ir",
        "get_pending_human_actions"
    };

    private static readonly string[] SessionNames =
    {
        "attach_to_process", "detach", "open_project", "close_project", "go_online", "go_offline",
        "switch_profile", "create_safe_sandbox", "leave_safe_sandbox", "show_block_in_editor",
        "show_device_in_editor", "close_online_session", "create_requirement_session",
        "update_requirement_session", "freeze_requirements", "generate_project_plan",
        "approve_project_plan", "set_active_engineering_session", "deactivate_engineering_session",
        "advance_engineering_stage", "record_engineering_test_evidence", "approve_engineering_acceptance",
        "prepare_autonomous_project", "generate_engineering_blueprint"
    };

    private static readonly string[] CriticalNames =
    {
        "download_to_device", "download_to_device_enhanced", "download_to_device_full",
        "download_with_config", "auto_compile_and_download", "write_online_variable",
        "set_device_ip", "upload_station", "cpu_stop", "cpu_start",
        "reset_plc_master_secret_online", "set_plc_master_secret_online",
        "create_project_from_template", "auto_configure_network", "orchestrate_project",
        "batch_operation", "setup_network_and_hmi_connection"
    };

    private static readonly string[] UnsafeCompositeNames =
    {
        "batch_operation", "orchestrate_project"
    };

    private static readonly string[] ExplicitProjectMutationNames =
    {
        "create_project_from_template", "auto_configure_network", "orchestrate_project",
        "batch_operation", "setup_network_and_hmi_connection"
    };

    private static readonly string[] CriticalFragments =
    {
        "factory_reset", "reset_to_factory", "memory_card", "format_", "start_cpu", "stop_cpu",
        "warm_restart", "cold_restart", "master_secret_online", "download_to_device"
    };

    private static readonly string[] HighPrefixes =
    {
        "delete_", "remove_", "reset_", "settings_set", "set_dialog_suppression", "upload_from_device",
        "umac_delete_", "safety_", "sf_", "security_", "protect_", "unprotect_", "multiuser_delete_",
        "vcs_delete_"
    };

    private static readonly string[] KnownMutationPrefixes =
    {
        "create_", "add_", "apply_", "update_", "set_", "write_", "import_", "generate_", "publish_", "rename_",
        "plug_", "connect_", "disconnect_", "instantiate_", "configure_", "activate_", "deactivate_",
        "assign_", "unassign_", "orchestrate_", "auto_", "batch_", "transaction_", "save_", "export_",
        "compile_", "download_", "upload_", "run_", "setup_", "switch_", "leave_", "cancel_",
        "archive_", "bind_", "block_", "change_", "close_", "copy_", "cpu_", "fingerprint_",
        "library_", "multiuser_", "plcsim_", "protect_", "safety_", "security_", "settings_",
        "umac_", "unprotect_", "vcs_", "modify_",
    };

    private static readonly string[] OnlineFragments =
    {
        "online", "download", "upload_from_device", "upload_station", "cpu_", "set_device_ip",
        "force_", "write_online", "plcsim_"
    };

    private static readonly string[] SecurityFragments =
    {
        "password", "certificate", "umac", "safety", "security", "master_secret", "access_rule", "syslog"
    };

    private static readonly string[] FileSystemPrefixes =
    {
        "archive_", "export_", "import_", "generate_", "create_external_", "download_to_folder", "write_block_source"
    };

    private static readonly string[] LongRunningPrefixes =
    {
        // ★修复★ compile_ 不再判为 LongRunning：编译通常数秒内完成且常需返回详细编译消息；
        // job 化后失败详情被通用错误吞掉（Result 为空），无法定位编译错误。
        // 直接同步执行可拿到完整 CompileResult（含 errors/warnings/messages）。
        "download_", "upload_", "export_", "import_", "generate_", "orchestrate_", "auto_compile_",
        "create_project_from_template", "batch_operation", "run_cross_reference", "safety_print_"
    };

    public ToolMetadata Describe(string originalName)
    {
        var original = originalName ?? "";
        var name = original.ToLowerInvariant();

        // get_set_* / *_get_set_* are conditional setters. They must never inherit the get_ read-only classification.
        var conditionalSetter = name.StartsWith("get_set_", StringComparison.Ordinal)
                                || name.IndexOf("_get_set_", StringComparison.Ordinal) >= 0;
        var readOnly = !conditionalSetter
                       && (ReadOnlyPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || ReadOnlyNames.Contains(name));
        var session = SessionNames.Contains(name);
        // Critical does not imply online. Project orchestrators are critical but still mutate the offline project.
        var touchesOnline = OnlineFragments.Any(f => name.Contains(f)) || name == "go_online" || name == "go_offline";
        var security = SecurityFragments.Any(f => name.Contains(f));
        var filesystem = FileSystemPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
        var destructive = name.StartsWith("delete_", StringComparison.Ordinal) ||
                          name.StartsWith("remove_", StringComparison.Ordinal) ||
                          name.IndexOf("_delete_", StringComparison.Ordinal) >= 0 ||
                          name.StartsWith("reset_", StringComparison.Ordinal) ||
                          name.IndexOf("factory_reset", StringComparison.Ordinal) >= 0;
        var explicitProjectMutation = ExplicitProjectMutationNames.Contains(name);
        var knownMutation = conditionalSetter || explicitProjectMutation
                            || KnownMutationPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || destructive;

        var risk = ToolRiskLevel.Unknown;
        if (readOnly) risk = ToolRiskLevel.Low;
        else if (session) risk = ToolRiskLevel.Medium;
        else if (knownMutation) risk = ToolRiskLevel.Medium;
        if (HighPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || destructive) risk = ToolRiskLevel.High;
        if (CriticalNames.Contains(name) || CriticalFragments.Any(f => name.Contains(f))) risk = ToolRiskLevel.Critical;
        if (security && risk != ToolRiskLevel.Unknown && risk < ToolRiskLevel.High) risk = ToolRiskLevel.High;
        if (filesystem && risk != ToolRiskLevel.Unknown && !readOnly && risk < ToolRiskLevel.High) risk = ToolRiskLevel.High;

        ToolOperationScope scope;
        if (readOnly) scope = ToolOperationScope.ReadOnly;
        else if (security) scope = ToolOperationScope.Security;
        else if (touchesOnline) scope = ToolOperationScope.OnlineDevice;
        else if (session) scope = ToolOperationScope.Session;
        else if (filesystem) scope = ToolOperationScope.FileSystem;
        else scope = ToolOperationScope.Project;

        // Project mutation is independent from the primary scope: a security or composite tool may both
        // change project data and touch another boundary. This flag drives write locks, audit and snapshots.
        var mutatesProject = !readOnly && !session && !touchesOnline
                             && (scope == ToolOperationScope.Project || scope == ToolOperationScope.Security
                                 || explicitProjectMutation || name.StartsWith("import_", StringComparison.Ordinal));

        return new ToolMetadata
        {
            OriginalName = original,
            RiskLevel = risk,
            ReadOnly = readOnly,
            Destructive = destructive,
            Idempotent = readOnly || name.StartsWith("set_", StringComparison.Ordinal),
            OpenWorld = touchesOnline || filesystem,
            MutatesProject = mutatesProject,
            TouchesOnlineDevice = touchesOnline,
            RequiresSnapshot = mutatesProject && name != "create_project" && name != "save_project" && !name.StartsWith("compile_", StringComparison.Ordinal),
            LongRunning = LongRunningPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)),
            Scope = scope
        };
    }

    public PolicyDecision Evaluate(ToolMetadata metadata, RuntimeStatusSnapshot status, out string reason)
    {
        reason = "";
        if (metadata.RiskLevel == ToolRiskLevel.Unknown)
        {
            reason = "该 Tool 未声明明确风险等级（Unknown），已按安全原则禁止执行。";
            return PolicyDecision.Deny;
        }

        if (UnsafeCompositeNames.Any(name => string.Equals(name, metadata.OriginalName, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "该复合工具尚未接入统一子调用安全分发器，已禁用；请调用对应的原子工具。";
            return PolicyDecision.Deny;
        }

        if (metadata.OriginalName == "switch_profile" || metadata.OriginalName == "get_runtime_status" ||
            metadata.OriginalName == "create_safe_sandbox" || metadata.OriginalName == "leave_safe_sandbox" ||
            metadata.OriginalName == "get_job_status" || metadata.OriginalName == "list_jobs" || metadata.OriginalName == "cancel_job" ||
            metadata.OriginalName == "create_requirement_session" || metadata.OriginalName == "update_requirement_session" ||
            metadata.OriginalName == "freeze_requirements" || metadata.OriginalName == "generate_project_plan" ||
            metadata.OriginalName == "approve_project_plan" || metadata.OriginalName == "get_requirement_status" ||
            metadata.OriginalName == "get_current_workflow_stage" || metadata.OriginalName == "set_active_engineering_session" ||
            metadata.OriginalName == "deactivate_engineering_session" || metadata.OriginalName == "advance_engineering_stage" ||
            metadata.OriginalName == "record_engineering_test_evidence" || metadata.OriginalName == "approve_engineering_acceptance" ||
            metadata.OriginalName == "get_applicable_standards" || metadata.OriginalName == "validate_workflow_readiness" ||
            metadata.OriginalName == "prepare_autonomous_project" || metadata.OriginalName == "generate_engineering_blueprint" ||
            metadata.OriginalName == "get_engineering_blueprint" || metadata.OriginalName == "validate_engineering_blueprint" ||
            metadata.OriginalName == "get_xml_ir_capabilities" || metadata.OriginalName == "compile_lad_ir" || metadata.OriginalName == "validate_lad_ir" || metadata.OriginalName == "get_lad_reference_profile" || metadata.OriginalName == "compile_hmi_ir" || metadata.OriginalName == "validate_hmi_ir")
            return PolicyDecision.Allow;

        switch (status.ExecutionMode)
        {
            case ExecutionMode.Safe:
                if (metadata.ReadOnly)
                    return PolicyDecision.Allow;
                if (metadata.TouchesOnlineDevice || metadata.RiskLevel == ToolRiskLevel.Critical)
                {
                    reason = "安全模式禁止在线写入、下载、Force、处理器控制和其他关键操作。";
                    return PolicyDecision.Deny;
                }
                if (metadata.Scope == ToolOperationScope.Session)
                    return PolicyDecision.Allow;
                if (metadata.MutatesProject && !status.SandboxActive)
                {
                    reason = "安全模式下工程修改必须先创建并进入沙盒副本。";
                    return PolicyDecision.Deny;
                }
                reason = "安全模式下所有写操作必须人工批准。";
                return PolicyDecision.RequireApproval;

            case ExecutionMode.Assist:
                if (metadata.RiskLevel <= ToolRiskLevel.Medium) return PolicyDecision.Allow;
                reason = "辅助模式下高风险或关键操作必须经过人工审批。";
                return PolicyDecision.RequireApproval;

            case ExecutionMode.Auto:
                return PolicyDecision.Allow;

            default:
                reason = "未知执行模式，已按安全原则拒绝操作。";
                return PolicyDecision.Deny;
        }
    }
}

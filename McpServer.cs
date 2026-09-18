using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcpServer;

/// <summary>
/// MCP 服务器核心 — 标准 MCP JSON-RPC 2.0 协议 over stdin/stdout
/// </summary>
public partial class McpServer
{
    private readonly Lazy<PortalService> _tia = new(() => new PortalService());
    private readonly Dictionary<string, ToolDefinition> _tools = new();
    private readonly ServerRuntimeState _status;
    private readonly ToolRiskPolicy _riskPolicy = new();
    private readonly ChangeAuditService _changeAudit;
    private readonly EngineeringWorkflowService _engineeringWorkflow;
    private readonly ClientControlCoordinator _clientControl;
    private readonly SafeTiaOperationCoordinator _operationCoordinator = new SafeTiaOperationCoordinator();
    private readonly JobManager _jobs;
    private readonly RuntimeHealthMonitor _healthMonitor;
    public string Profile { get; private set; } = "all";
    private HashSet<string>? _whitelist;
    private HashSet<string>? _blacklist;

    public static readonly string Version = "4.4.5-v17fix1";
    private static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2024-11-05" };
    private string _negotiatedProtocolVersion = "2025-11-25";
    private static readonly bool DebugEnabled =
        Environment.GetEnvironmentVariable("TIA_MCP_DEBUG") is string s &&
        (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase));

    public McpServer(string? profile = null, ServerRuntimeState? status = null, string? clientHint = null)
    {
        _status = status ?? new ServerRuntimeState();
        _changeAudit = new ChangeAuditService(_status);
        _engineeringWorkflow = new EngineeringWorkflowService(AppContext.BaseDirectory);
        _clientControl = new ClientControlCoordinator(clientHint);
        _jobs = new JobManager(_status);
        _status.UpdateClientControlState(_clientControl.Refresh());
        Profile = profile?.ToLowerInvariant() ?? "all";
        LoadToolsFilter();
        RegisterAllTools();
        RegisterBuiltInTools();
        ValidateCoreToolExposure();
        _status.MarkServerStarted(Version, _negotiatedProtocolVersion, Profile, _tools.Count);
        _healthMonitor = new RuntimeHealthMonitor(_status, () => { TryRefreshTiaStatus(); });
        Log($"服务器初始化完成，可用工具 {_tools.Count} 个");
    }

    private void LoadToolsFilter()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools_filter.json");
        if (!File.Exists(path)) return;
        try
        {
            var cfg = JObject.Parse(File.ReadAllText(path));
            var mode = cfg["mode"]?.ToString() ?? "whitelist";
            var list = (cfg["tools"] as JArray)?.Select(t => t.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var profileCfg = cfg["profiles"]?[Profile] as JObject;
            if (profileCfg != null)
            {
                mode = profileCfg["mode"]?.ToString() ?? mode;
                list = (profileCfg["tools"] as JArray)?.Select(t => t.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? list;
            }
            if (list == null || list.Count == 0) return;
            if (mode.Equals("blacklist", StringComparison.OrdinalIgnoreCase))
                _blacklist = list;
            else
                _whitelist = list;
        }
        catch (Exception ex) { Log("工具过滤配置加载失败：" + ex.Message); }
    }

    private void ValidateCoreToolExposure()
    {
        var required = new[]
        {
            "get_xml_ir_capabilities", "compile_lad_ir", "validate_lad_ir", "get_lad_reference_profile",
            "apply_lad_ir", "compile_hmi_ir", "validate_hmi_ir", "apply_hmi_ir"
        };
        var missing = required.Where(name => !_tools.Values.Any(t =>
            string.Equals(t.Metadata.OriginalName, name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("核心 XML IR 工具未暴露，拒绝启动: " + string.Join(", ", missing));
    }

    public async Task RunAsync()
    {
        Log("通信服务器正在启动，版本 " + Version);
        // RC1: 禁止自动附加第一个 TIA 进程。必须由用户在工作台中明确选择 PID。

        try { Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = Encoding.UTF8; } catch { }

        try
        {
            using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); } catch { break; }
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                string result;
                try { result = DispatchMessage(line); }
                catch (Exception ex) { result = JsonRpcError(null, -32603, "Internal error: " + ex.Message); }

                if (!string.IsNullOrEmpty(result))
                    await writer.WriteLineAsync(result);
            }
        }
        finally
        {
            // 所有异常/退出路径都统一释放健康监视器、Job、TIA、控制租约和读写锁。
            _healthMonitor.Dispose();
            _jobs.Dispose();
            if (_tia.IsValueCreated) _tia.Value.Dispose();
            _clientControl.Dispose();
            _operationCoordinator.Dispose();
            _status.MarkServerStopped();
        }
    }

    private string DispatchMessage(string line)
    {
        JObject msg;
        try { msg = JObject.Parse(line); } catch { return JsonRpcError(null, -32700, "Parse error"); }

        _status.MarkRequest();
        var id = msg["id"];
        var method = msg["method"]?.ToString();
        if (msg["jsonrpc"]?.ToString() != "2.0")
            return JsonRpcError(id, -32600, "Invalid Request");

        return method switch
        {
            "ping" => JsonRpcResult(id, new { }),
            "notifications/cancelled" => string.Empty,
            "initialize" => HandleInitialize(id, msg["params"] as JObject),
            "notifications/initialized" => string.Empty,
            "tools/list" => HandleToolsList(id),
            "tools/call" => HandleToolsCall(id, msg["params"] as JObject),
            _ => JsonRpcError(id, -32601, "Method not found: " + method),
        };
    }

    private string HandleInitialize(JToken? id, JObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.ToString() ?? "";
        _negotiatedProtocolVersion = SupportedProtocolVersions.Contains(requested)
            ? requested
            : SupportedProtocolVersions[0];

        var clientInfo = parameters?["clientInfo"] as JObject;
        var clientName = clientInfo?["name"]?.ToString() ?? "unknown-client";
        var clientVersion = clientInfo?["version"]?.ToString() ?? "";
        _status.MarkClientInitialized(clientName, clientVersion, _negotiatedProtocolVersion);
        if (_clientControl.CurrentClientName == "未知客户端") _clientControl.UpdateClientName(clientName);
        _status.UpdateClientControlState(_clientControl.Refresh());

        return JsonRpcResult(id, new
        {
            protocolVersion = _negotiatedProtocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = $"tia-mcp-{Profile}", version = Version }
        });
    }

    private string HandleToolsList(JToken? id)
    {
        var toolList = _tools.Values.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema,
            annotations = new
            {
                title = t.Name,
                readOnlyHint = t.Metadata.ReadOnly,
                destructiveHint = t.Metadata.Destructive,
                idempotentHint = t.Metadata.Idempotent,
                openWorldHint = t.Metadata.OpenWorld,
                riskLevel = t.Metadata.RiskLevel.ToString(),
                longRunningHint = t.Metadata.LongRunning
            }
        }).ToList();
        return JsonRpcResult(id, new { tools = toolList });
    }

    /// <summary>动态切换 Profile 并清空/重建工具集</summary>
    private string SwitchProfile(JToken? args)
    {
        var newProfile = GetStringArg(args, "profile");
        if (string.IsNullOrEmpty(newProfile))
            return JsonConvert.SerializeObject(new { success = false, error = "必须指定 profile 参数" });

        var lower = newProfile!.ToLowerInvariant();
        var valid = new[] { "all", "project", "plc", "hmi", "network", "online", "advanced" };
        if (!valid.Contains(lower))
            return JsonConvert.SerializeObject(new { success = false, error = $"无效profile: '{newProfile}'" });

        _tools.Clear();
        Profile = lower;
        _whitelist = null;
        _blacklist = null;
        LoadToolsFilter();
        RegisterAllTools();
        RegisterBuiltInTools();
        ValidateCoreToolExposure();

        var names = _tools.Keys.ToList();
        _status.SetProfile(Profile, _tools.Count);
        Log($"工具集切换完成，可用工具 {_tools.Count} 个");
        return JsonConvert.SerializeObject(new { success = true, message = $"已切换到 {Profile}", profile = Profile, toolCount = _tools.Count, tools = names }, Formatting.Indented);
    }

    private string HandleToolsCall(JToken? id, JObject? parameters)
    {
        if (parameters == null) return JsonRpcError(id, -32602, "缺少调用参数");
        var name = parameters["name"]?.ToString();
        if (string.IsNullOrEmpty(name)) return JsonRpcError(id, -32602, "缺少工具名称");
        var args = parameters["arguments"] as JObject ?? new JObject();

        if (!_tools.TryGetValue(name!, out var tool))
            return JsonRpcError(id, -32602, $"未找到工具: {name}");

        if (!ToolSchemaValidator.TryValidate(tool.InputSchema, args, out var schemaError))
            return JsonRpcError(id, -32602, schemaError);

        // 统一安全链：Schema -> RiskCheck -> Permission -> ControlLock -> Audit -> Execute。
        var snapshot = _status.Snapshot();
        var decision = _riskPolicy.Evaluate(tool.Metadata, snapshot, out var policyReason);
        var approvalRequired = decision == PolicyDecision.RequireApproval;
        var approvalGranted = false;
        if (decision == PolicyDecision.Deny)
        {
            var error = $"执行策略已阻止本次操作：{policyReason}";
            _status.MarkToolFailed(tool.Name, error, tool.Description);
            return ToolErrorResult(id, error, tool.Metadata.RiskLevel.ToString());
        }

        var clientControl = _clientControl.Refresh();
        _status.UpdateClientControlState(clientControl);
        if (!tool.Metadata.ReadOnly && !clientControl.IsController)
        {
            var error = $"当前控制端：{clientControl.ControllerClientName}。本客户端（{clientControl.CurrentClientName}）为只读端，禁止并发修改 PLC/TIA 工程。";
            _status.MarkToolFailed(tool.Name, error, tool.Description);
            return ToolErrorResult(id, error, tool.Metadata.RiskLevel.ToString());
        }

        if (tool.Metadata.Destructive)
        {
            if (GetBoolArg(args, "dryRun") == true)
            {
                return JsonRpcResult(id, new
                {
                    content = new[] { new { type = "text", text = JsonConvert.SerializeObject(new { success = true, dryRun = true, tool = tool.Metadata.OriginalName, message = "仅预检，未执行删除/重置操作。正式执行需传入 confirm=true 且 confirmation 与工具名一致。" }, Formatting.Indented) } },
                    isError = false
                });
            }
            var confirmation = GetStringArg(args, "confirmation") ?? "";
            if (GetBoolArg(args, "confirm") != true ||
                (!string.Equals(confirmation, tool.Metadata.OriginalName, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(confirmation, tool.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var error = $"破坏性操作需要二次确认。请先使用 dryRun=true 预检；确认后传入 confirm=true, confirmation=\"{tool.Metadata.OriginalName}\"。";
                _status.MarkToolFailed(tool.Name, error, tool.Description);
                return ToolErrorResult(id, error, tool.Metadata.RiskLevel.ToString());
            }
        }

        if (approvalRequired)
        {
            var summary = "即将执行一项高风险工程操作。审批窗口不显示原始调用参数，以避免泄露密码、密钥或其他敏感信息。";
            var approved = _status.RequestApproval(tool.Name, tool.Description, tool.Metadata.RiskLevel, tool.Metadata.Scope, summary, TimeSpan.FromMinutes(3));
            if (!approved)
            {
                var error = _status.HasInteractiveUi
                    ? "危险操作未获得人工批准或审批超时。"
                    : "危险操作需要图形控制中心人工批准；当前服务器未启用图形界面。";
                _status.MarkToolFailed(tool.Name, error, tool.Description);
                return ToolErrorResult(id, error, tool.Metadata.RiskLevel.ToString());
            }
            approvalGranted = true;
        }

        // ★阶段2 一遍过★ 长任务默认异步提交；调用方传 wait=true 时同步执行并直接返回结果，
        // 消灭"返回 Queued 后进程退出任务即消亡"的陷阱（如 export_block_source）。
        if (tool.Metadata.LongRunning && GetBoolArg(args, "wait") != true)
        {
            var clonedArgs = (JObject)args.DeepClone();
            var jobId = _jobs.Submit(tool.Name, cancellationToken =>
            {
                var outcome = ExecuteToolCore(tool, clonedArgs, approvalRequired, approvalGranted, cancellationToken);
                if (!outcome.Success)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(outcome.Error) ? "工具返回失败状态。" : outcome.Error);
                return outcome.ResultText;
            });
            return JsonRpcResult(id, new
            {
                content = new[] { new { type = "text", text = JsonConvert.SerializeObject(new { success = true, job_id = jobId, state = "Queued", message = "长任务已提交。可传 wait=true 同步等待；或使用 get_job_status 查询。" }, Formatting.Indented) } },
                isError = false
            });
        }

        var directOutcome = ExecuteToolCore(tool, args, approvalRequired, approvalGranted, CancellationToken.None);
        return JsonRpcResult(id, new
        {
            content = new[] { new { type = "text", text = EnrichToolResult(directOutcome.DisplayText) } },
            isError = !directOutcome.Success
        });
    }

    // ════════════════════════════════════════════════════════════════
    // 阶段2机制：主动喊人 —— 失败结果按已知模式富化 needsHuman/hint
    // ════════════════════════════════════════════════════════════════

    private static readonly (string Pattern, bool NeedsHuman, string HowToHuman, string ResumeHint, string Hint)[] ErrorAdvisories =
    {
        ("was not found", true,
         "TIA GUI：HMI_1 → 连接 → 添加连接 → 伙伴 PLC_1（V17 Openness 无法创建 HMI 集成连接，这是本项目唯一必须人工的步骤）",
         "连接创建后重试原操作即可继续", ""),
        ("not supported in online mode", false, "", "",
         "当前处于在线模式，Openness 禁止修改：先调用 go_offline 再重试（AI 可自行处理）"),
        ("执行策略已阻止", false, "", "",
         "服务器策略为 Safe：将 bin/server_policy.json 的 defaultExecutionMode 改为 Auto 后重启服务器（AI 可自行处理）"),
        ("Password must not be empty", false, "", "",
         "compile_hardware 的已知博途限制：改用 compile_plc 即可"),
        ("未找到缓存的 PLCSIM 实例", false, "", "",
         "先调用 plcsim_create_instance 创建实例，plcsim_set_instance_power 上电后再重试"),
    };

    /// <summary>
    /// 对失败结果做"主动喊人"富化：命中已知模式时附加
    /// needsHuman（是否必须人工）/ howToHuman（人工操作步骤）/ resumeHint（完成后如何继续）/ hint（AI 可自行处理的提示）。
    /// </summary>
    internal static string EnrichToolResult(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("\"success\":false")) return text;
        try
        {
            var jo = JObject.Parse(text);
            if (jo["success"]?.Value<bool>() != false) return text;
            if (jo["needsHuman"] != null || jo["hint"] != null) return text;
            var err = jo["error"]?.ToString() ?? "";
            foreach (var adv in ErrorAdvisories)
            {
                if (err.IndexOf(adv.Pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                jo["needsHuman"] = adv.NeedsHuman;
                if (!string.IsNullOrEmpty(adv.HowToHuman)) jo["howToHuman"] = adv.HowToHuman;
                if (!string.IsNullOrEmpty(adv.ResumeHint)) jo["resumeHint"] = adv.ResumeHint;
                if (!string.IsNullOrEmpty(adv.Hint)) jo["hint"] = adv.Hint;
                break;
            }
            return jo.ToString(Formatting.None);
        }
        catch { return text; }
    }

    /// <summary>只读写进程内任务/状态内存、不触碰 TIA COM 的工具：允许绕过调度器线程直接执行，
    /// 保证调度器被长任务占用时任务状态仍可查询。新增此类工具时必须确认 Handler 无任何 TIA/COM 访问。</summary>
    private static readonly HashSet<string> NonDispatcherTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_job_status", "list_jobs"
    };

    private ToolExecutionOutcome ExecuteToolCore(ToolDefinition tool, JObject args, bool approvalRequired, bool approvalGranted, CancellationToken cancellationToken)
    {
        ChangeAuditContext? audit = null;
        var handlerStarted = false;
        _status.MarkToolStarted(tool.Name, tool.Description);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolExecutionOutcome? outcome = null;
            // 历史背景：LongRunning（job）工具曾在线程池直接执行以"绕过 STA"，
            // 但 TIA 对象 STA 绑定导致 Compile 被 COM 封送回 STA 线程挂死（TIA 编译完成但调用不返回）。
            // 修复：调度器线程已改 MTA（见 TiaStaDispatcher.cs），COM 调用不再跨公寓封送，
            // 所有工具统一经调度器串行执行。job 执行期间其他工具经读写锁排队，COM 不会并发，安全。
            var executeBody = new Action(() =>
            {
                using (tool.Metadata.ReadOnly ? _operationCoordinator.EnterRead(cancellationToken) : _operationCoordinator.EnterWrite(cancellationToken))
                using (tool.Metadata.TouchesOnlineDevice ? SafeOnlineExecutor.EnterAuthorizedScope(tool.Metadata) : null)
                {
                    if (_tia.IsValueCreated && !NonDispatcherTools.Contains(tool.Metadata.OriginalName)) UpdateTiaStatusFromServiceUnsafe();
                    var currentStatus = _status.Snapshot();
                    if (!tool.Metadata.ReadOnly)
                    {
                        audit = _changeAudit.Begin(tool.Name, tool.Description, tool.Metadata, args, currentStatus,
                            _tia.IsValueCreated ? _tia.Value : null, approvalRequired, approvalGranted);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    handlerStarted = true;
                    var resultText = tool.Handler(args);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShouldRefreshTiaStatus(tool.Metadata.OriginalName) || tool.Metadata.MutatesProject || tool.Metadata.TouchesOnlineDevice)
                        UpdateTiaStatusFromServiceUnsafe();

                    var parsedSuccess = TryReadSuccess(resultText, out var reportedSuccess, out var reportedError);
                    // Read-only diagnostic tools may legitimately return plain text. Any operation that writes,
                    // touches an online device or is destructive must return an explicit JSON boolean success field.
                    var requiresExplicitSuccess = !tool.Metadata.ReadOnly
                                                  && (tool.Metadata.MutatesProject || tool.Metadata.TouchesOnlineDevice
                                                      || tool.Metadata.Destructive || tool.Metadata.RiskLevel >= ToolRiskLevel.High);
                    var operationSuccess = parsedSuccess ? reportedSuccess : !requiresExplicitSuccess;
                    if (!parsedSuccess && requiresExplicitSuccess)
                        reportedError = "写操作返回值缺少布尔型 success 字段，已按失败处理。";
                    if (audit != null) _changeAudit.Complete(audit, operationSuccess, resultText, operationSuccess ? "" : reportedError);
                    string displayText;
                    try { displayText = JToken.Parse(resultText).ToString(Formatting.Indented); }
                    catch { displayText = resultText; }

                    if (operationSuccess)
                    {
                        _status.MarkToolCompleted(tool.Name, tool.Description);
                    }
                    else _status.MarkToolFailed(tool.Name, string.IsNullOrWhiteSpace(reportedError) ? "操作返回失败状态" : reportedError, tool.Description);
                    outcome = new ToolExecutionOutcome { Success = operationSuccess, ResultText = resultText, DisplayText = displayText, Error = reportedError };
                }
            });
            // ★修复★ 所有工具统一走调度器（MTA 串行线程）执行。
            // 曾将 LongRunning（job）工具直接在线程池执行"绕过 STA"：但 TIA 对象在 STA 线程
            // 创建后，Job 线程的 Compile 被 COM 封送回 STA 线程执行并挂死（TIA 编译完成但调用不返回），
            // STA 线程永久占用导致所有工具（含 get_job_status）超时。调度器已改为 MTA，
            // COM 调用直接执行不再封送，全部工具统一串行执行既安全又不再卡死。
            // ★修复★ 纯内存状态查询工具不经调度器执行：即使调度器线程被长任务
            // （如批量导出）占用，get_job_status/list_jobs 仍能立即响应，
            // 长任务不再"静默挂起"而是可持续观测状态。
            if (NonDispatcherTools.Contains(tool.Metadata.OriginalName))
                executeBody();
            else
                TiaStaDispatcher.Instance.Run(executeBody);
            return outcome!;
        }
        catch (OperationCanceledException)
        {
            var message = handlerStarted
                ? "取消请求到达时底层 TIA 操作已经开始，最终设备/工程状态需要恢复检查。"
                : "操作在进入底层 TIA Handler 前已取消。";
            if (audit != null) _changeAudit.Complete(audit, false, "", message);
            _status.MarkToolFailed(tool.Name, message, tool.Description);
            if (handlerStarted) throw new TiaOperationRecoveryRequiredException(message);
            throw;
        }
        catch (Exception ex)
        {
            if (audit != null) _changeAudit.Complete(audit, false, "", ex.Message);
            _status.MarkToolFailed(tool.Name, ex.Message, tool.Description);
            return new ToolExecutionOutcome
            {
                Success = false,
                Error = ex.Message,
                ResultText = JsonConvert.SerializeObject(new { success = false, error = ex.Message }),
                DisplayText = JsonConvert.SerializeObject(new { success = false, error = ex.Message }, Formatting.Indented)
            };
        }
    }

    private static bool TryReadSuccess(string resultText, out bool success, out string error)
    {
        success = true;
        error = "";
        try
        {
            var token = JObject.Parse(resultText ?? "{}");
            var successToken = token["success"];
            if (successToken == null || successToken.Type != JTokenType.Boolean) return false;
            success = successToken.Value<bool>();
            error = token["error"]?.ToString() ?? "";
            return true;
        }
        catch { return false; }
    }

    private static string ToolErrorResult(JToken? id, string error, string riskLevel) =>
        JsonRpcResult(id, new
        {
            content = new[] { new { type = "text", text = JsonConvert.SerializeObject(new { success = false, error, riskLevel }) } },
            isError = true
        });

    private static bool ShouldRefreshTiaStatus(string toolName)
    {
        switch (toolName)
        {
            case "list_tia_processes":
            case "attach_to_process":
            case "detach":
            case "get_tia_status":
            case "create_project":
            case "open_project":
            case "close_project":
                return true;
            default:
                return false;
        }
    }

    public bool TryRefreshTiaStatus()
    {
        // ★修复★ 原实现持有协调器读锁的同时同步等待调度器线程（Run）：
        // 调度器上正在执行的任务体在 EnterWrite 的 200ms 重试间隙里，读锁仍可被
        // 本方法抢入，形成「读锁等调度器 → 调度器等写锁 → 写锁等读锁释放」的
        // 永久死锁。健康监视器每 15 秒一跳，任何超过一个周期的长任务必然命中，
        // 表现为"首个长任务成功、后续任务与 get_job_status 全部静默挂起"。
        // 改为 Post（投递不等待）：不持任何锁、不阻塞监视器线程，状态更新
        // 仍由调度器串行执行，COM 亲和性不变。
        if (!_tia.IsValueCreated) return true;
        try
        {
            TiaStaDispatcher.Instance.Post(() =>
            {
                try { UpdateTiaStatusFromServiceUnsafe(); }
                catch { }
            });
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void UpdateTiaStatusFromServiceUnsafe()
    {
        try
        {
            var token = JObject.Parse(_tia.Value.GetTiaStatus());
            if (token["success"]?.Value<bool>() != true)
            {
                _status.UpdateTiaStatus(false, null, 0, "", "", token["error"]?.ToString() ?? "TIA 状态读取失败");
                return;
            }

            _status.UpdateTiaStatus(
                token["isConnected"]?.Value<bool>() == true,
                token["attachedProcessId"]?.Type == JTokenType.Integer ? token["attachedProcessId"]!.Value<int>() : (int?)null,
                token["runningCount"]?.Value<int>() ?? 0,
                token["connectedProject"]?.ToString() ?? "",
                token["connectedProjectPath"]?.ToString() ?? "");
            if (_tia.IsValueCreated)
                _status.SetSandboxStatus(_tia.Value.IsSandboxActive, _tia.Value.SandboxProjectPath, _tia.Value.SandboxOriginalProjectPath);
        }
        catch (Exception ex)
        {
            _status.UpdateTiaStatus(false, null, 0, "", "", ex.Message);
        }
    }

    private void RegisterBuiltInTools()
    {
        var switchTool = new ToolDefinition
        {
            Name = "switch_profile",
            Description = "切换服务器工具集。可选范围：全部、工程、可编程控制器、人机界面、网络、在线操作、高级功能。",
            InputSchema = Props(new Dictionary<string, object> { ["profile"] = EnumStrProp("目标工具集内部标识", "all", "project", "plc", "hmi", "network", "online", "advanced") }, new[] { "profile" }),
            Handler = args => SwitchProfile(args),
            Metadata = _riskPolicy.Describe("switch_profile")
        };
        _tools[switchTool.Name] = switchTool;

        var statusTool = new ToolDefinition
        {
            Name = "get_runtime_status",
            Description = "读取通信服务器、智能客户端、博途和安全策略状态，不返回敏感日志。",
            InputSchema = Props(new Dictionary<string, object>(), Array.Empty<string>()),
            Handler = _ => GetRuntimeStatusJson(),
            Metadata = _riskPolicy.Describe("get_runtime_status")
        };
        _tools[statusTool.Name] = statusTool;

        var sandboxTool = new ToolDefinition
        {
            Name = "create_safe_sandbox",
            Description = "把当前博途工程归档并恢复到独立目录，打开真实沙盒副本，原工程保持不变。",
            InputSchema = Props(new Dictionary<string, object>(), Array.Empty<string>()),
            Handler = _ => CreateSafeSandbox(),
            Metadata = _riskPolicy.Describe("create_safe_sandbox")
        };
        _tools[sandboxTool.Name] = sandboxTool;

        var leaveSandboxTool = new ToolDefinition
        {
            Name = "leave_safe_sandbox",
            Description = "保存并关闭沙盒工程，重新打开进入沙盒前的原工程。",
            InputSchema = Props(new Dictionary<string, object> { ["saveSandboxChanges"] = BoolProp("是否保存沙盒工程中的修改，默认保存") }, Array.Empty<string>()),
            Handler = args => LeaveSafeSandbox(GetBoolArg(args, "saveSandboxChanges") ?? true),
            Metadata = _riskPolicy.Describe("leave_safe_sandbox")
        };
        _tools[leaveSandboxTool.Name] = leaveSandboxTool;

        var getJobTool = new ToolDefinition
        {
            Name = "get_job_status",
            Description = "按 job_id 查询长任务状态、结果或错误。",
            InputSchema = Props(new Dictionary<string, object> { ["job_id"] = StrProp("任务标识") }, new[] { "job_id" }),
            Handler = args => _jobs.GetJson(GetStringArg(args, "job_id") ?? ""),
            Metadata = _riskPolicy.Describe("get_job_status")
        };
        _tools[getJobTool.Name] = getJobTool;

        var listJobsTool = new ToolDefinition
        {
            Name = "list_jobs",
            Description = "列出最近的长任务及其状态。",
            InputSchema = Props(new Dictionary<string, object> { ["limit"] = IntProp("返回数量，1-200") }, Array.Empty<string>()),
            Handler = args => JsonConvert.SerializeObject(new { success = true, jobs = _jobs.List(GetIntArg(args, "limit") ?? 50) }, Formatting.Indented),
            Metadata = _riskPolicy.Describe("list_jobs")
        };
        _tools[listJobsTool.Name] = listJobsTool;

        var cancelJobTool = new ToolDefinition
        {
            Name = "cancel_job",
            Description = "取消长任务。若底层 TIA API 无法立即中止，任务会进入 RecoveryRequired 状态。",
            InputSchema = Props(new Dictionary<string, object> { ["job_id"] = StrProp("任务标识") }, new[] { "job_id" }),
            Handler = args =>
            {
                var ok = _jobs.Cancel(GetStringArg(args, "job_id") ?? "", out var message);
                return JsonConvert.SerializeObject(new { success = ok, message });
            },
            Metadata = _riskPolicy.Describe("cancel_job")
        };
        _tools[cancelJobTool.Name] = cancelJobTool;

        // ★阶段2 主动喊人★ 列出当前必须由人工完成的事项（V17 Openness 无法自动化的步骤）
        var pendingHumanTool = new ToolDefinition
        {
            Name = "get_pending_human_actions",
            Description = "列出当前必须由人工完成的事项（V17 Openness 无法自动化的步骤，如创建 HMI 集成连接）。\n" +
                          "AI 在规划流程时应先调用本工具；有未完成事项时主动告知用户并给出操作步骤。",
            InputSchema = Props(new Dictionary<string, object>(), Array.Empty<string>()),
            Handler = _ =>
            {
                var items = new JArray();
                // 动态检查：HMI 集成连接是否已存在（已连工程时才检查）
                try
                {
                    var conns = _tia.Value.ListHmiConnections();
                    var obj = JObject.Parse(conns);
                    JArray? arr = null;
                    foreach (var prop in obj.Properties())
                    {
                        if (prop.Value is JArray a && a.Count > 0) { arr = a; break; }
                        if (prop.Value is JArray empty && prop.Name.ToLowerInvariant().Contains("connection")) { arr = empty; }
                    }
                    if (arr != null && arr.Count == 0)
                        items.Add(new JObject
                        {
                            ["item"] = "创建 HMI↔PLC 集成连接",
                            ["why"] = "V17 Openness 无法创建经典 HMI 集成连接（Connections 只读）",
                            ["howToHuman"] = "TIA GUI：HMI_1 → 连接 → 添加 → 伙伴 PLC_1",
                        });
                }
                catch { /* 未连接工程/服务不可用时跳过动态检查 */ }
                items.Add(new JObject
                {
                    ["item"] = "设置 HMI 起始画面（如尚未设置）",
                    ["why"] = "V17 Openness 不暴露起始画面接口",
                    ["howToHuman"] = "TIA GUI：HMI_1 → 运行系统设置 → 常规 → 起始画面",
                });
                return EnrichToolResult(JsonConvert.SerializeObject(new
                {
                    success = true,
                    pendingCount = items.Count,
                    pending = items,
                    note = "pendingCount>0 时请把对应事项转告用户并附 howToHuman"
                }, Formatting.Indented));
            },
            Metadata = _riskPolicy.Describe("get_pending_human_actions")
        };
        _tools[pendingHumanTool.Name] = pendingHumanTool;
    }

    public string CreateSafeSandbox(string? sandboxRoot = null)
    {
        using (_operationCoordinator.EnterWrite())
        {
            var result = TiaStaDispatcher.Instance.Run(() =>
            {
                var r = _tia.Value.CreateSafeSandbox(sandboxRoot ?? "");
                UpdateTiaStatusFromServiceUnsafe();
                return r;
            });
            return result;
        }
    }

    public string LeaveSafeSandbox(bool saveSandboxChanges = true)
    {
        using (_operationCoordinator.EnterWrite())
        {
            var result = TiaStaDispatcher.Instance.Run(() =>
            {
                var r = _tia.Value.LeaveSafeSandbox(saveSandboxChanges);
                UpdateTiaStatusFromServiceUnsafe();
                return r;
            });
            return result;
        }
    }

    public string CreateBeginnerProject(string projectPath, string projectName)
    {
        using (_operationCoordinator.EnterWrite())
        {
            var result = TiaStaDispatcher.Instance.Run(() =>
            {
                var r = _tia.Value.CreateProject(projectPath, projectName);
                UpdateTiaStatusFromServiceUnsafe();
                return r;
            });
            return result;
        }
    }

    public string OpenProjectFromUi(string projectPath)
    {
        using (_operationCoordinator.EnterWrite())
        {
            var result = TiaStaDispatcher.Instance.Run(() =>
            {
                var r = _tia.Value.OpenProject(projectPath);
                UpdateTiaStatusFromServiceUnsafe();
                return r;
            });
            return result;
        }
    }

    public string RestoreLatestSafetySnapshot()
    {
        using (_operationCoordinator.EnterWrite())
        {
            UpdateTiaStatusFromServiceUnsafe();
            var status = _status.Snapshot();
            if (status.SandboxActive)
                return JsonConvert.SerializeObject(new { success = false, error = "当前正在使用沙盒工程。请先退出沙盒，再执行恢复操作。" });
            if (string.IsNullOrWhiteSpace(status.TiaProjectName))
                return JsonConvert.SerializeObject(new { success = false, error = "请先打开需要恢复的工程。系统会按当前工程名称选择对应的安全快照，避免恢复错工程。" });

            var dir = _changeAudit.SnapshotsDirectory;
            if (!Directory.Exists(dir))
                return JsonConvert.SerializeObject(new { success = false, error = "没有找到安全快照目录。" });

            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(status.TiaProjectName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            var latest = Directory.GetFiles(dir, "*.zap*").Where(f => EnvironmentDiscoveryService.IsTiaProjectFile(f))
                .Where(f => Path.GetFileName(f).StartsWith(safeName + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(latest))
                return JsonConvert.SerializeObject(new { success = false, error = "没有找到当前工程对应的安全快照。请先查看变更记录，确认该工程曾经生成过安全快照。" });

            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "博途智能助手", "恢复工程");
            var result = TiaStaDispatcher.Instance.Run(() =>
            {
                var r = _tia.Value.RestoreSafetySnapshotAsCopy(latest, root);
                UpdateTiaStatusFromServiceUnsafe();
                return r;
            });
            return result;
        }
    }

    public string GetChangeReportsDirectory() => _changeAudit.ReportsDirectory;
    public string GetSafetySnapshotsDirectory() => _changeAudit.SnapshotsDirectory;
    public string GetTechnicalLogsDirectory() => _status.TechnicalLogDirectory;

    private string GetRuntimeStatusJson()
    {
        var s = _status.Snapshot();
        return JsonConvert.SerializeObject(new
        {
            success = true,
            server = new { running = s.ServerRunning, version = s.Version, protocol = s.ProtocolVersion, profile = s.Profile, toolCount = s.ToolCount, requests = s.RequestCount, toolCalls = s.ToolCallCount },
            aiClient = new { state = s.AiState.ToString(), name = s.ClientName, version = s.ClientVersion, activeTool = s.ActiveTool, lastTool = s.LastTool },
            tia = new { connected = s.TiaConnected, processId = s.TiaProcessId, runningProcessCount = s.TiaRunningProcessCount, projectName = s.TiaProjectName, projectPath = s.TiaProjectPath, error = s.TiaStatusError },
            sandbox = new { active = s.SandboxActive, sandboxProjectPath = s.SandboxProjectPath, originalProjectPath = s.SandboxOriginalProjectPath },
            policy = new { mode = s.ExecutionMode.ToString(), beginnerMode = s.BeginnerMode, autoApproveDangerous = s.AutoApproveDangerous, pendingApproval = s.PendingApproval?.ToolName },
            engineeringWorkflow = _engineeringWorkflow.GetRuntimeSummary(),
            lastError = s.LastError
        });
    }

    private static string JsonRpcResult(JToken? id, object result) => new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["result"] = JObject.FromObject(result) }.ToString(Formatting.None);
    private static string JsonRpcError(JToken? id, int code, string message) => new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull(), ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString(Formatting.None);

    // ════════════════════════════════════════════════════════════════
    // 工具注册 — 按 Profile 分组
    // ════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════
    // 工具注册 — 扁平清单（阶段1裁剪后仅保留实际使用的领域，见 BASELINE.md）
    // ════════════════════════════════════════════════════════════════

    private void RegisterAllTools()
    {
        RegisterPortalTools(this, _tia);                    // 连接/进程/状态
        RegisterProjectTools(this, _tia);                   // 工程/块管理
        RegisterPlcTools(this, _tia);                       // LAD 编程/变量/DB/导入导出
        RegisterXmlOrchestrationTools(this, _tia);          // LAD IR 编译与校验
        RegisterHmiTools(this, _tia);                       // HMI 画面/变量
        RegisterHmiExtendedTools(this, _tia);               // HMI 扩展（文本列表/脚本/画面模板，2026-08-30 从 Archive 捞回）
        RegisterHmiSetupTools(this, _tia);                  // 网络与 HMI 连接编排
        RegisterHardwareTools(this, _tia);                  // 设备组态
        RegisterDownloadTools(this, _tia);                  // 下载/上传/CPU 启停
        RegisterWatchTools(this, _tia);                     // 监视表
        RegisterPlcSimTools(this, _tia);                    // PLCSIM 仿真
        RegisterSimulationGateTools(this, _tia);            // 模拟门禁 + 一遍过写入
        RegisterPlcSoftwareExtendedTools(this, _tia);       // PLC 软件扩展（块图等）
        RegisterMoreTools(this, _tia);                      // 杂项
        RegisterEngineeringWorkflowTools(this, _engineeringWorkflow); // 自主工程工作流门禁
        Log($"工具注册完成，共 {_tools.Count} 个");
    }

    // ════════════════════════════════════════════════════════════════
    // 工具名缩写
    // ════════════════════════════════════════════════════════════════

    private const int MaxToolNameLength = 32;
    private static readonly (string From, string To)[] PrefixAbbr =
    {
        ("configure_download_", "cfgdl_"), ("configure_upload_", "cfgul_"), ("configure_online_", "cfgon_"),
        ("create_unified_hmi_", "cuhmi_"), ("delete_unified_hmi_", "duhmi_"), ("list_unified_hmi_", "luhmi_"),
        ("plcsim_advanced_", "psa_"), ("create_classic_hmi_screen_from_template", "cchmi_from_tpl"),
        ("generate_blocks_from_external_source", "genblks_from_src"), ("generate_block_from_external_source", "genblk_from_src"),
        ("create_industrial_project_skeleton", "create_proj_skeleton"), ("security_", "sec_"),
        ("multiuser_", "mu_"), ("safety_", "sf_"), ("plcsim_", "ps_"), ("library_", "lib_"), ("get_set_", "gset_"),
    };
    private static readonly (string From, string To)[] WordAbbr =
    {
        ("password","pwd"),("certificate","cert"),("management","mgr"),("identification","id"),
        ("components","comp"),("languages","lang"),("modifications","mods"),("runtime","rt"),
        ("block_binding","blk_bind"),("master_secret","msecret"),("memory_card","memcard"),
        ("system_data","sysdata"),("target_languages","tgt_lang"),("hmi_data","hmidata"),
        ("alarm_text_libs","alarm_txt"),("consistent_blocks","cons_blk"),("certificate_template","cert_tpl"),
        ("external_source","ext_src"),("discrete_alarm","disc_alarm"),("analog_alarm","ana_alarm"),
        ("project_skeleton","proj_skeleton"),("from_template","from_tpl"),("runtime_group","rt_grp"),
        ("watchtable_access_rules","wt_access"),("forcetable_access_rules","ft_access"),
        ("alarm_text_libs","alarm_txt"),("initialize_memory","init_mem"),
    };

    internal static string ShortenToolName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length <= MaxToolNameLength) return name;
        var result = name;
        foreach (var (from, to) in PrefixAbbr) { if (result.StartsWith(from)) { result = to + result.Substring(from.Length); break; } }
        if (result.Length > MaxToolNameLength)
            foreach (var (from, to) in WordAbbr) { if (result.Length <= MaxToolNameLength) break; result = result.Replace(from, to); }
        if (result.Length > MaxToolNameLength)
        {
            var hash = StableHash8(name);
            result = result.Substring(0, MaxToolNameLength - hash.Length - 1) + "_" + hash;
        }
        return result;
    }

    private static string StableHash8(string value)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            return BitConverter.ToString(bytes, 0, 4).Replace("-", "");
        }
    }

    internal void RegisterTool(string name, string description, object inputSchema, Func<JToken?, string> handler, ToolMetadata? explicitMetadata = null)
    {
        if (string.Equals(name, "batch_operation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "orchestrate_project", StringComparison.OrdinalIgnoreCase))
        {
            Log($"安全整改：复合工具 {name} 未接入原子级门禁，当前版本不予注册。");
            return;
        }
        if (_whitelist != null && !_whitelist.Contains(name)) return;
        if (_blacklist != null && _blacklist.Contains(name)) { Log("已按安全配置跳过一个禁用工具"); return; }
        var shortName = ShortenToolName(name);
        if (shortName != name) Log("已为一个超长工具标识生成稳定缩写");

        if (_tools.TryGetValue(shortName, out var existing))
        {
            if (string.Equals(existing.Metadata.OriginalName, name, StringComparison.Ordinal))
            {
                // 同一个原始 Tool 在基础/增强模块中重复注册时，后注册版本显式覆盖前者。
                // 这修复 RC1 自动产生 xxx2 工具名、导致文档和真实能力不一致的问题。
                Log("检测到重复工具定义，已使用后注册版本");
            }
            else
            {
                // 不同原始 Tool 缩写后偶然碰撞时，用原始名 SHA-256 生成稳定后缀。
                var suffix = StableHash8(name);
                var keep = Math.Max(1, MaxToolNameLength - suffix.Length - 1);
                shortName = shortName.Substring(0, Math.Min(keep, shortName.Length)) + "_" + suffix;
                Log("检测到工具缩写冲突，已生成稳定唯一标识");
            }
        }

        var metadata = explicitMetadata ?? _riskPolicy.Describe(name);
        metadata.OriginalName = name;
        if (metadata.RiskLevel == ToolRiskLevel.Unknown)
            Log($"工具 {name} 未能确定风险等级，已注册为 Unknown 并将在执行时拒绝。" );
        var effectiveDescription = metadata.Destructive
            ? description + "\n安全要求：支持 dryRun=true；正式执行必须提供 confirm=true 且 confirmation 与工具名一致。"
            : description;
        _tools[shortName] = new ToolDefinition
        {
            Name = shortName,
            Description = effectiveDescription,
            InputSchema = inputSchema,
            Handler = handler,
            Metadata = metadata
        };
    }

    internal static string? GetStringArg(JToken? args, string name)
    {
        if (args == null || !args.HasValues) return null;
        var p = args[name]; if (p == null) return null;
        return p.Type == JTokenType.String ? p.ToString() : p.HasValues ? p.ToString(Formatting.None) : null;
    }
    internal static int? GetIntArg(JToken? args, string name)
    {
        if (args == null || !args.HasValues) return null;
        var p = args[name];
        return p?.Type switch { JTokenType.Integer => p.Value<int>(), JTokenType.Float => (int)p.Value<double>(), JTokenType.String when int.TryParse(p.ToString(), out var v) => v, _ => null };
    }
    internal static bool? GetBoolArg(JToken? args, string name)
    {
        if (args == null || !args.HasValues) return null;
        var p = args[name];
        return p?.Type switch { JTokenType.Integer => p.Value<int>() != 0, JTokenType.Boolean => p.Value<bool>(), JTokenType.String when bool.TryParse(p.ToString(), out var v) => v, _ => null };
    }
    internal void Log(string message)
    {
        _status.AddLog(message);
        if (DebugEnabled) Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private sealed class ToolExecutionOutcome
    {
        public bool Success { get; set; }
        public string ResultText { get; set; } = "";
        public string DisplayText { get; set; } = "";
        public string Error { get; set; } = "";
    }

    private class ToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public object InputSchema { get; set; } = new { };
        public Func<JToken?, string> Handler { get; set; } = _ => "{}";
        public ToolMetadata Metadata { get; set; } = new ToolMetadata();
    }
}
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace TiaMcpServer
{
    /// <summary>
    /// 连接管理 + 所有 Service 共用的辅助方法。
    /// 其它 Service（Plc/Hmi/Project/Drive）作为 partial class 共享 _project 等状态。
    /// </summary>
    public partial class PortalService : IDisposable
    {
        private TiaPortal? _tiaPortal;
        private Project? _project;
        private int? _attachedProcessId;
        private readonly object _lock = new();
        /// <summary>TIA Portal 对话框自动确认事件处理委托引用（用于取消订阅）。</summary>
        private EventHandler<ConfirmationEventArgs>? _confirmationHandler;

        public bool IsConnected => _tiaPortal != null;
        public int? AttachedProcessId => _attachedProcessId;
        public string? ProjectName => _project?.Name;
        public string? ProjectPath => _project?.Path?.FullName;

        // ═════════════════════════════════════════════════════════════════════════════
        // JSON 辅助
        // ═════════════════════════════════════════════════════════════════════════════
        protected string Err(string msg) => JsonConvert.SerializeObject(new { success = false, error = msg });
        protected string Ok(string msg) => JsonConvert.SerializeObject(new { success = true, message = msg });

        // ═════════════════════════════════════════════════════════════════════════════
        // XML 导入统一防护（防"无效 XML 导入 → TIA 进程终止"）
        // 所有用户可控 XML 的 Import 入口必须先用良构校验拦截，
        // 返回可操作的错误（含下一步建议），避免坏 XML 到达 TIA Import。
        // ═════════════════════════════════════════════════════════════════════════════
        protected static string? ValidateImportXmlString(string? xml, string what)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return $"{what}：导入内容为空。请先提供有效的 TIA 导出 XML（可从 TIA GUI 导出，或先用 export_* 工具导出模板）。";
            try
            {
                System.Xml.Linq.XDocument.Parse(xml);
                return null;
            }
            catch (Exception ex)
            {
                return $"{what}：XML 不是良构文档（{ex.Message}）。已阻止导入以防止博途进程终止。" +
                       "请检查 XML 是否完整/编码是否为 UTF-8，或改用 TIA GUI 手工导入，或用 export_* 工具先导出合法模板再修改。";
            }
        }

        protected static string? ValidateImportXmlFile(string? filePath, string what)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return $"{what}：文件不存在（{filePath ?? "(空)"}）。请提供正确的导出 XML 文件路径。";
            try
            {
                System.Xml.Linq.XDocument.Load(filePath);
                return null;
            }
            catch (Exception ex)
            {
                return $"{what}：文件不是良构 XML（{ex.Message}）。已阻止导入以防止博途进程终止。" +
                       "请检查文件内容/编码（应 UTF-8），或改用 TIA GUI 手工导入。";
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 错误分类与恢复建议（C3）
        // 将原始异常归类为有限类别，并给出可操作的中文恢复建议，
        // 便于上层 AI Agent 决定是否重试以及如何引导用户修复。
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>错误类别枚举。Unknown 为兜底类别。</summary>
        protected enum ErrorType
        {
            BlockNotFound,      // 块/变量表/UDT 不存在
            BlockInconsistent,  // 块未编译（Inconsistent）
            VariableUndefined,  // 操作数未定义
            ImportFailed,       // XML Import 失败（可能 Dispose 项目）
            NetworkError,       // 网络/连接问题
            PermissionDenied,   // 权限不足
            Unknown             // 未知错误
        }

        /// <summary>
        /// 根据异常消息（含 InnerException 链）归类错误类别。
        /// 关键字匹配基于 TIA Portal Openness 实际抛出的中英文消息。
        /// </summary>
        protected static ErrorType ClassifyError(Exception ex)
        {
            if (ex == null) return ErrorType.Unknown;
            // 拼接整条异常链的消息，便于匹配被包装的底层错误
            var msg = GetFullExceptionMessage(ex);

            // 顺序很重要：先匹配更具体/更严重的类别
            // 权限类（放最前，避免被 "access" 误判为连接问题）
            if (msg.IndexOf("权限", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("Permission", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("access denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("没有所需", StringComparison.OrdinalIgnoreCase) >= 0)
                return ErrorType.PermissionDenied;

            // 网络连接类
            if (msg.IndexOf("RPC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("连接", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("通信", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("进程不可用", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("process is not available", StringComparison.OrdinalIgnoreCase) >= 0)
                return ErrorType.NetworkError;

            // Import 失败类（可能 Dispose 项目，需重新查找块）
            if (msg.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("导入", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("disposed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("Cannot access a disposed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("已释放", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("对象未释放", StringComparison.OrdinalIgnoreCase) >= 0)
                return ErrorType.ImportFailed;

            // 块不一致类
            if (msg.IndexOf("Inconsistent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("不一致", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("cannot be exported", StringComparison.OrdinalIgnoreCase) >= 0)
                return ErrorType.BlockInconsistent;

            // 变量未定义类
            if (msg.IndexOf("not defined", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("未定义", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("undefined", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("is not defined", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (msg.IndexOf("Tag", StringComparison.OrdinalIgnoreCase) >= 0 && msg.IndexOf("defined", StringComparison.OrdinalIgnoreCase) >= 0))
                return ErrorType.VariableUndefined;

            // 块/表/UDT 不存在类
            if (msg.IndexOf("未找到", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("找不到", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                msg.IndexOf("不存在", StringComparison.OrdinalIgnoreCase) >= 0)
                return ErrorType.BlockNotFound;

            return ErrorType.Unknown;
        }

        /// <summary>拼接异常链完整消息（ex -> inner -> inner...）。</summary>
        private static string GetFullExceptionMessage(Exception ex)
        {
            var sb = new StringBuilder(ex.Message ?? "");
            var inner = ex.InnerException;
            int guard = 0;
            while (inner != null && guard < 5)
            {
                if (!string.IsNullOrEmpty(inner.Message))
                    sb.Append(" -> ").Append(inner.Message);
                inner = inner.InnerException;
                guard++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 判断错误类别是否可重试。可重试 = 同一调用重试可能成功（无需用户改输入）。
        /// </summary>
        protected static bool IsRetryable(ErrorType type)
        {
            switch (type)
            {
                case ErrorType.BlockInconsistent:  // 编译后重试可能成功
                case ErrorType.ImportFailed:      // 重新查找块后重试可能成功
                case ErrorType.NetworkError:      // 网络恢复后重试可能成功
                    return true;
                case ErrorType.BlockNotFound:     // 需用户先创建块
                case ErrorType.VariableUndefined: // 需用户先声明变量
                case ErrorType.PermissionDenied:  // 需用户调整权限
                case ErrorType.Unknown:           // 保守起见不自动重试
                default:
                    return false;
            }
        }

        /// <summary>
        /// 生成对应类别的中文恢复建议。operation 用于在建议中体现具体操作上下文。
        /// </summary>
        protected static string GetRecoveryHint(ErrorType type, string operation)
        {
            var op = string.IsNullOrEmpty(operation) ? "操作" : operation;
            switch (type)
            {
                case ErrorType.BlockNotFound:
                    return $"请先使用 create_block 创建目标块/变量表，或检查名称拼写与大小写后重试{op}。若为多 PLC 项目，确认 plcName 参数指向正确的 PLC。";
                case ErrorType.BlockInconsistent:
                    return $"目标块处于未编译（Inconsistent）状态。请先调用 compile_block 编译该块，编译成功后再重试{op}。若编译本身失败，请查看编译错误信息修复逻辑。";
                case ErrorType.VariableUndefined:
                    return $"操作数未定义。请通过 get_block_interface_v2 检查块接口，确认变量已在对应 Section（Input/Output/Static/Temp）声明；中文变量名需确保拼写一致。声明后重新{op}。";
                case ErrorType.ImportFailed:
                    return $"XML 导入失败，旧块引用可能已 Dispose 失效。建议重新调用对应方法（会自动 FindBlock 获取新引用）后重试{op}。若仍失败，检查 XML 格式、UTF-8 BOM 编码以及块号是否冲突。";
                case ErrorType.NetworkError:
                    return $"网络/连接异常。请用 list_tia_processes 确认博途进程仍在运行，再调用 list_tia_processes 并使用 attach_to_process 明确选择 PID 后重试{op}。";
                case ErrorType.PermissionDenied:
                    return $"权限不足。请确认博途项目以可编程模式（带编程接口）打开，且当前用户对项目有写权限后重试{op}。";
                case ErrorType.Unknown:
                default:
                    return $"未知错误。建议先调用 compile_block 编译相关块后重试{op}；若仍失败，请查看完整异常信息定位问题。";
            }
        }

        /// <summary>
        /// 返回带分类与恢复建议的错误 JSON。
        /// 输出字段：success=false / error / errorType / recovery / retryable。
        /// 与 Err() 兼容（仍含 success 与 error 字段）。
        /// </summary>
        protected string ErrWithRecovery(Exception ex, string operation)
        {
            var type = ClassifyError(ex);
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = ex.Message,
                errorType = type.ToString(),
                recovery = GetRecoveryHint(type, operation),
                retryable = IsRetryable(type)
            });
        }

        protected static string FixComEncoding(string? text)
        {
            // 修复 COM → .NET 字符串传递时的 UTF-8→GBK 编码错乱
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                var gb2312Bytes = Encoding.Default.GetBytes(text);
                var utf8Text = Encoding.UTF8.GetString(gb2312Bytes);
                foreach (var c in utf8Text)
                    if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3000 && c <= 0x303F))
                        return utf8Text;
            }
            catch { }
            return text ?? "";
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 进程 / 连接（任务 6）
        // ═════════════════════════════════════════════════════════════════════════════
        public string ListTiaProcesses()
        {
            try
            {
                var processes = TiaPortal.GetProcesses();
                var result = processes.Select(p => new
                {
                    Id = p.Id,
                    ProjectPath = p.ProjectPath?.ToString() ?? "",
                    Mode = p.Mode.ToString()
                }).ToList();
                return JsonConvert.SerializeObject(new { success = true, count = result.Count, processes = result });
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        public string AttachToProcess(int processId)
        {
            lock (_lock)
            {
                try
                {
                    if (_sandboxActive) return Err("当前处于沙盒工程，请先退出沙盒后再切换博途连接。");
                    DisposeConnection();
                    var processes = TiaPortal.GetProcesses().ToList();
                    var process = processes.FirstOrDefault(p => p.Id == processId);
                    if (process == null) return Err($"未找到进程 ID: {processId}");

                    _tiaPortal = process.Attach();
                    _attachedProcessId = process.Id;
                    _project = _tiaPortal.Projects.FirstOrDefault();
                    // 防崩溃兜底：自动应答 TIA 确认弹窗
                    EnsureDialogSuppression();
                    // ★修复★ attach 后预热设备枚举：TIA COM 对象刚创建时首次枚举
                    // （GetAllDevices → _project.Devices）可能返回空（attach 后立即调用
                    // RequireClassicHmi 时实测复现），先触发一次枚举并短暂等待可稳定命中。
                    try
                    {
                        if (_project != null)
                        {
                            var _ = _project.Devices?.Count;
                            System.Threading.Thread.Sleep(300);
                        }
                    }
                    catch { }
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        message = "已成功连接到博途",
                        projectName = _project?.Name ?? "无项目",
                        projectPath = _project?.Path?.FullName ?? ""
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string AttachToFirst()
        {
            lock (_lock)
            {
                var processes = TiaPortal.GetProcesses().ToList();
                if (processes.Count == 0) return Err("没有运行中的博途实例");
                return AttachToProcess(processes[0].Id);
            }
        }

        public string Detach()
        {
            lock (_lock)
            {
                try
                {
                    if (_sandboxActive) return Err("当前处于沙盒工程，请先退出沙盒后再断开连接。");
                    DisposeConnection();
                    return Ok("已断开连接");
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        public string GetTiaStatus()
        {
            lock (_lock)
            {
                try
                {
                    var processes = TiaPortal.GetProcesses();
                    var running = processes
                        .Select(p => new { Id = p.Id, ProjectPath = p.ProjectPath?.ToString() ?? "" })
                        .ToList();
                    return JsonConvert.SerializeObject(new
                    {
                        success = true,
                        isConnected = IsConnected,
                        attachedProcessId = AttachedProcessId,
                        connectedProject = ProjectName ?? "",
                        connectedProjectPath = ProjectPath ?? "",
                        runningProcesses = running,
                        runningCount = running.Count
                    });
                }
                catch (Exception ex) { return Err(ex.Message); }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 对话框自动确认（TiaPortal.Confirmation 事件）
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 启用/禁用 TIA Portal 对话框自动确认（对话框抑制）。
        /// 工具层 set_dialog_suppression 传入的 enable 参数实际是 suppress 语义：
        ///   suppress=true(enable=true)  → 订阅 Confirmation 事件，自动应答弹窗（抑制对话框）
        ///   suppress=false(enable=false) → 取消订阅，恢复手动应答（启用对话框）
        /// 返回的 enabled 字段表示"对话框是否启用"：suppress=true 时 enabled=false（已抑制），
        /// suppress=false 时 enabled=true（已恢复）。
        /// result 可选值：Ok/Yes/YesToAll/Abort/Retry/Ignore/No/NoToAll/Cancel（默认 Ignore）。
        /// </summary>
        public string SetDialogSuppression(bool enable, string? result = null)
        {
            lock (_lock)
            {
                try
                {
                    if (_tiaPortal == null)
                        return Err("TIA Portal 未启动");

                    // 解析 result 参数为 ConfirmationResult 枚举（默认 Ignore）
                    ConfirmationResult cr = ConfirmationResult.Ignore;
                    if (!string.IsNullOrEmpty(result))
                    {
                        if (!Enum.TryParse(result, true, out cr))
                            return Err($"无效的 result 值: {result}。可选: {string.Join("/", Enum.GetNames(typeof(ConfirmationResult)))}");
                    }

                    // enable(=suppress)=true → 抑制对话框（订阅自动确认事件）
                    // enable(=suppress)=false → 启用对话框（取消订阅，恢复手动应答）
                    if (enable)
                    {
                        // 先取消旧订阅（避免重复订阅）
                        if (_confirmationHandler != null)
                        {
                            try { _tiaPortal.Confirmation -= _confirmationHandler; } catch { }
                            _confirmationHandler = null;
                        }

                        _confirmationHandler = (sender, e) =>
                        {
                            try
                            {
                                e.IsHandled = true;
                                e.Result = cr;
                            }
                            catch { }
                        };
                        _tiaPortal.Confirmation += _confirmationHandler;

                        // suppress=true → 对话框已被抑制（enabled=false）
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            enabled = false,
                            suppressed = true,
                            result = cr.ToString(),
                            message = $"已抑制对话框（自动应答结果: {cr}），所有 TIA Portal 弹窗将被自动应答"
                        });
                    }
                    else
                    {
                        if (_confirmationHandler != null)
                        {
                            try { _tiaPortal.Confirmation -= _confirmationHandler; } catch { }
                            _confirmationHandler = null;
                        }
                        // suppress=false → 对话框已启用（enabled=true）
                        return JsonConvert.SerializeObject(new
                        {
                            success = true,
                            enabled = true,
                            suppressed = false,
                            result = (string?)null,
                            message = "已启用对话框（恢复手动应答），TIA Portal 弹窗将需要手动确认"
                        });
                    }
                }
                catch (Exception ex) { return Err($"设置对话框抑制失败: {ex.Message}"); }
            }
        }

        /// <summary>
        /// 确保对话框自动应答已启用（防崩溃兜底）。
        /// 导入/操作失败弹出的错误确认框若无人应答，博途进程可能直接退出；
        /// 连接建立/项目创建/项目打开后自动订阅 Confirmation → Ok 应答。
        /// 用户可显式调用 set_dialog_suppression(suppress=false) 恢复手动应答。
        /// </summary>
        public void EnsureDialogSuppression()
        {
            lock (_lock)
            {
                if (_tiaPortal == null || _confirmationHandler != null) return;
                try
                {
                    _confirmationHandler = (sender, e) =>
                    {
                        try { e.IsHandled = true; e.Result = ConfirmationResult.Ok; } catch { }
                    };
                    _tiaPortal.Confirmation += _confirmationHandler;
                }
                catch { }
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 设备项遍历
        // ═════════════════════════════════════════════════════════════════════════════
        private void EnsureAttachedToExistingProject()
        {
            if (_project != null) return;

            var candidates = TiaPortal.GetProcesses()
                .Where(p => p.ProjectPath != null &&
                            !string.IsNullOrWhiteSpace(p.ProjectPath.ToString()))
                .ToList();

            if (candidates.Count == 1)
            {
                DisposeConnection();
                var process = candidates[0];
                _tiaPortal = process.Attach();
                _attachedProcessId = process.Id;
                _project = _tiaPortal.Projects.FirstOrDefault();
                return;
            }

            if (candidates.Count > 1)
            {
                var ids = string.Join(", ", candidates.Select(p => p.Id));
                throw new InvalidOperationException(
                    $"Multiple TIA Portal processes have open projects (PID: {ids}). " +
                    "Call list_tia_processes and attach_to_tia_process with a PID. " +
                    "Do not call open_project.");
            }
        }

        protected void RequireProject()
        {
            lock (_lock)
            {
                EnsureAttachedToExistingProject();
                if (_project == null)
                {
                    throw new InvalidOperationException(
                        "No TIA Portal project is connected. Open a project first. " +
                        "If TIA Portal is already running, call list_tia_processes and " +
                        "attach_to_tia_process. Do not call open_project.");
                }
            }
        }

        /// <summary>
        /// 递归收集项目下所有设备（含 DeviceUserGroup 子组中的设备）。
        /// 博途多站项目会把设备放在嵌套设备组里（如 SSJ/PLC、DDJ/HMI），
        /// 仅遍历 Project.Devices 会漏掉。
        /// 同时返回设备所在的组路径，便于 AI 理解项目结构。
        /// 注：DeviceUserGroup.Groups 是递归结构，需深度遍历。
        /// </summary>
        protected List<(Device Device, string GroupPath)> GetAllDevicesWithGroup()
        {
            var result = new List<(Device, string)>();
            if (_project == null) return result;

            // 根级设备
            foreach (var dev in _project.Devices)
                result.Add((dev, ""));

            // 递归设备组（DeviceUserGroup 有 Groups 子组属性）
            CollectDevicesInUserGroups(_project.DeviceGroups, "", result);
            return result;
        }

        /// <summary>获取项目下所有设备（不带组路径，便于简单场景使用）。</summary>
        protected List<Device> GetAllDevices()
            => GetAllDevicesWithGroup().Select(t => t.Device).ToList();

        /// <summary>
        /// 递归遍历 DeviceUserGroup 的 Groups 子组。
        /// 使用反射获取 Groups 属性（DeviceUserGroup 子类才有，基类 DeviceGroup 没有）。
        /// </summary>
        private static void CollectDevicesInUserGroups(
            System.Collections.IEnumerable groups, string parentPath,
            List<(Device, string)> result)
        {
            foreach (var g in groups)
            {
                var gName = g.GetType().GetProperty("Name")?.GetValue(g)?.ToString() ?? "?";
                var curPath = string.IsNullOrEmpty(parentPath) ? gName : parentPath + "/" + gName;

                // 收集该组直接包含的设备
                var devicesProp = g.GetType().GetProperty("Devices");
                if (devicesProp?.GetValue(g) is System.Collections.IEnumerable devs)
                {
                    foreach (var dev in devs)
                    {
                        if (dev is Device d)
                            result.Add((d, curPath));
                    }
                }

                // 递归子组（DeviceUserGroup.Groups）
                var subGroupsProp = g.GetType().GetProperty("Groups");
                if (subGroupsProp?.GetValue(g) is System.Collections.IEnumerable subGroups)
                {
                    CollectDevicesInUserGroups(subGroups, curPath, result);
                }
            }
        }

        /// <summary>按名称查找设备（递归遍历所有设备组）。</summary>
        protected Device? FindDeviceByName(string name)
        {
            foreach (var (dev, _) in GetAllDevicesWithGroup())
            {
                if (dev.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return dev;
            }
            return null;
        }

        /// <summary>递归展平设备项树。</summary>
        protected static IEnumerable<DeviceItem> EnumerateDeviceItems(DeviceItemComposition items)
        {
            foreach (var item in items)
            {
                yield return item;
                if (item.DeviceItems != null)
                {
                    foreach (var child in EnumerateDeviceItems(item.DeviceItems))
                        yield return child;
                }
            }
        }

        protected static List<object> GetDeviceItemsRecursive(DeviceItemComposition items)
        {
            var result = new List<object>();
            foreach (var item in items)
            {
                result.Add(new
                {
                    name = item.Name,
                    type = item.TypeIdentifier?.ToString() ?? "",
                    children = item.DeviceItems != null ? GetDeviceItemsRecursive(item.DeviceItems) : new List<object>()
                });
            }
            return result;
        }

        protected static DeviceItem? FindDeviceItemInDevice(DeviceItemComposition items, string name)
        {
            foreach (var item in items)
            {
                if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return item;
                var found = FindDeviceItemInDevice(item.DeviceItems, name);
                if (found != null) return found;
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // PLC 软件定位
        // ═════════════════════════════════════════════════════════════════════════════
        protected List<PlcSoftware> GetPlcSoftwareList()
        {
            var result = new List<PlcSoftware>();
            if (_project == null) return result;

            // ★递归遍历所有设备（含设备组中的设备），修复多站项目枚举为空的问题
            foreach (var device in GetAllDevices())
            {
                foreach (var di in EnumerateDeviceItems(device.DeviceItems))
                {
                    try
                    {
                        var swContainer = di.GetService<SoftwareContainer>();
                        if (swContainer?.Software is PlcSoftware plc)
                            result.Add(plc);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[tia-mcp] 枚举 PLC 软件异常 ({di.Name}): {ex.Message}");
                    }
                }
            }
            return result;
        }

        protected PlcSoftware GetPlcSoftware()
        {
            RequireProject();
            var plcList = GetPlcSoftwareList();
            // 多 PLC 项目时 FirstOrDefault 总是返回第一个，输出警告便于排查
            if (plcList.Count > 1)
            {
                Debug.WriteLine($"[tia-mcp] 警告: 项目包含 {plcList.Count} 个 PLC，GetPlcSoftware() 默认返回第一个 '{plcList[0].Name}'，建议调用方指定 plcName");
            }
            var plc = plcList.FirstOrDefault();
            if (plc == null) throw new InvalidOperationException("未找到 PLC 软件");
            return plc;
        }

        /// <summary>按名称查找 PLC 软件（不区分大小写）。返回 null 表示未找到。</summary>
        protected PlcSoftware? FindPlcByName(string? plcName)
        {
            if (string.IsNullOrEmpty(plcName)) return null;
            return GetPlcSoftwareList()
                .FirstOrDefault(p => p.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase));
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 块 / 变量表 / UDT 查找（递归）
        // ═════════════════════════════════════════════════════════════════════════════
        protected static List<PlcBlock> GetAllBlocks(PlcBlockGroup? group)
        {
            var result = new List<PlcBlock>();
            if (group == null) return result;
            if (group.Blocks != null) result.AddRange(group.Blocks);
            if (group.Groups != null)
                foreach (var sub in group.Groups) result.AddRange(GetAllBlocks(sub));
            // 遍历系统块组（IEC_TIMER/IEC_COUNTER 等系统类型 DB 存储在此）
            // PlcBlockSystemGroup 有 SystemBlockGroups 属性，PlcBlockGroup 没有
            var sysGroupsProp = group.GetType().GetProperty("SystemBlockGroups");
            if (sysGroupsProp != null)
            {
                var sysGroups = sysGroupsProp.GetValue(group) as IEnumerable;
                if (sysGroups != null)
                {
                    foreach (var sg in sysGroups)
                    {
                        var sgBlocksProp = sg?.GetType().GetProperty("Blocks");
                        var sgBlocks = sgBlocksProp?.GetValue(sg) as IEnumerable;
                        if (sgBlocks != null)
                            foreach (var b in sgBlocks)
                                if (b is PlcBlock pb) result.Add(pb);
                        // 递归子系统块组
                        var sgGroupsProp = sg?.GetType().GetProperty("Groups");
                        var sgGroups = sgGroupsProp?.GetValue(sg) as IEnumerable;
                        if (sgGroups != null)
                            foreach (var ssg in sgGroups)
                                if (ssg is PlcBlockGroup pbg) result.AddRange(GetAllBlocks(pbg));
                    }
                }
            }
            return result;
        }

        protected PlcBlock? FindBlock(string name, string? plcName = null)
        {
            // 指定了 plcName 时，只在该 PLC 中查找
            if (!string.IsNullOrEmpty(plcName))
            {
                var plc = FindPlcByName(plcName);
                if (plc == null) return null;
                return GetAllBlocks(plc.BlockGroup)
                    .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            // 未指定 plcName 时，收集所有 PLC 中的匹配块
            // 多个 PLC 存在同名块时抛错，避免误用第一个匹配
            var matches = new List<PlcBlock>();
            foreach (var plc in GetPlcSoftwareList())
            {
                var match = GetAllBlocks(plc.BlockGroup)
                    .FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match != null) matches.Add(match);
            }
            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];
            throw new InvalidOperationException($"未指定 plcName，且多个 PLC 存在同名块 '{name}'，请指定 plcName");
        }

        protected static List<PlcTagTable> GetAllTagTables(PlcTagTableGroup? group)
        {
            var result = new List<PlcTagTable>();
            if (group == null) return result;
            if (group.TagTables != null) result.AddRange(group.TagTables);
            if (group.Groups != null)
                foreach (var sub in group.Groups) result.AddRange(GetAllTagTables(sub));
            return result;
        }

        protected PlcTagTable? FindTagTable(string name, string? plcName = null)
        {
            if (!string.IsNullOrEmpty(plcName))
            {
                var plc = FindPlcByName(plcName);
                if (plc == null) return null;
                return GetAllTagTables(plc.TagTableGroup)
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            // 未指定 plcName 时，收集所有 PLC 中的匹配表
            // 多个 PLC 存在同名表时抛错，避免误用第一个匹配
            var matches = new List<PlcTagTable>();
            foreach (var plc in GetPlcSoftwareList())
            {
                var match = GetAllTagTables(plc.TagTableGroup)
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match != null) matches.Add(match);
            }
            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];
            throw new InvalidOperationException($"未指定 plcName，且多个 PLC 存在同名变量表 '{name}'，请指定 plcName");
        }

        protected static List<PlcType> GetAllTypes(PlcTypeGroup? group)
        {
            var result = new List<PlcType>();
            if (group == null) return result;
            if (group.Types != null) result.AddRange(group.Types);
            if (group.Groups != null)
                foreach (var sub in group.Groups) result.AddRange(GetAllTypes(sub));
            return result;
        }

        protected PlcType? FindUdt(string name, string? plcName = null)
        {
            if (!string.IsNullOrEmpty(plcName))
            {
                var plc = FindPlcByName(plcName);
                if (plc == null) return null;
                return GetAllTypes(plc.TypeGroup)
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            // 未指定 plcName 时，收集所有 PLC 中的匹配 UDT
            // 多个 PLC 存在同名 UDT 时抛错，避免误用第一个匹配
            var matches = new List<PlcType>();
            foreach (var plc in GetPlcSoftwareList())
            {
                var match = GetAllTypes(plc.TypeGroup)
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (match != null) matches.Add(match);
            }
            if (matches.Count == 0) return null;
            if (matches.Count == 1) return matches[0];
            throw new InvalidOperationException($"未指定 plcName，且多个 PLC 存在同名 UDT '{name}'，请指定 plcName");
        }

        protected static PlcBlockGroup? FindBlockGroup(PlcBlockGroup group, string name)
        {
            if (group.Groups == null) return null;
            foreach (var sub in group.Groups)
            {
                if (sub.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return sub;
                var found = FindBlockGroup(sub, name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>块类型短名（FB/FC/DB/OB，CLR 类型名最可靠）。</summary>
        protected static string GetBlockTypeName(PlcBlock block)
        {
            var clr = block.GetType().Name;
            if (clr.Equals("GlobalDB", StringComparison.OrdinalIgnoreCase)) return "DB";
            if (clr.Equals("FB", StringComparison.OrdinalIgnoreCase)) return "FB";
            if (clr.Equals("FC", StringComparison.OrdinalIgnoreCase)) return "FC";
            if (clr.Equals("OB", StringComparison.OrdinalIgnoreCase)) return "OB";
            if (clr.Equals("DB", StringComparison.OrdinalIgnoreCase)) return "DB";
            if (clr.Equals("InstanceDB", StringComparison.OrdinalIgnoreCase)) return "DB";
            if (clr.Equals("VariantDB", StringComparison.OrdinalIgnoreCase)) return "DB";

            try
            {
                var ti = ((IEngineeringObject)block).GetAttribute("TypeIdentifier")?.ToString();
                if (!string.IsNullOrEmpty(ti))
                {
                    var last = ti!.Split('.').Last();
                    if (last.IndexOf("GlobalDB", StringComparison.OrdinalIgnoreCase) >= 0) return "DB";
                    if (last.IndexOf("FB", StringComparison.OrdinalIgnoreCase) >= 0) return "FB";
                    if (last.IndexOf("FC", StringComparison.OrdinalIgnoreCase) >= 0) return "FC";
                    if (last.IndexOf("OB", StringComparison.OrdinalIgnoreCase) >= 0) return "OB";
                    if (last.IndexOf("DB", StringComparison.OrdinalIgnoreCase) >= 0) return "DB";
                }
            }
            catch { }
            return clr;
        }

        protected string GetParentPlcName(PlcBlock block)
        {
            foreach (var plc in GetPlcSoftwareList())
            {
                if (GetAllBlocks(plc.BlockGroup).Any(b => string.Equals(b.Name, block.Name, StringComparison.OrdinalIgnoreCase)))
                    return plc.Name;
            }
            return "";
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // 编译结果序列化
        // ═════════════════════════════════════════════════════════════════════════════
        protected string SerializeCompileResult(object? result, string targetName)
        {
            var errors = new List<object>();
            var warnings = new List<object>();
            var messages = new List<object>();
            if (result != null)
            {
                var rt = result.GetType();
                var msgList = rt.GetProperty("Messages")?.GetValue(result) as System.Collections.IEnumerable;
                if (msgList != null)
                {
                    foreach (var m in msgList)
                    {
                        FlattenCompilerMessages(m, errors, warnings);
                        messages.Add(SerializeCompilerMessage(m));
                    }
                }

                var errorCount = Convert.ToInt32(rt.GetProperty("ErrorCount")?.GetValue(result) ?? 0);
                var warningCount = Convert.ToInt32(rt.GetProperty("WarningCount")?.GetValue(result) ?? 0);
                var success = errorCount == 0;
                var summary = success
                    ? $"编译成功: {errorCount} 错误, {warningCount} 警告"
                    : $"编译失败: {errorCount} 错误, {warningCount} 警告";

                return JsonConvert.SerializeObject(new
                {
                    success,
                    targetName,
                    state = rt.GetProperty("State")?.GetValue(result)?.ToString() ?? "",
                    errorCount,
                    warningCount,
                    messages,
                    errors,
                    warnings,
                    summary
                });
            }
            return JsonConvert.SerializeObject(new { success = false, targetName, error = "编译结果为空" });
        }

        private static object SerializeCompilerMessage(object msg)
        {
            var children = new List<object>();
            var mt = msg.GetType();
            var childMsgs = mt.GetProperty("Messages")?.GetValue(msg) as System.Collections.IEnumerable;
            if (childMsgs != null)
                foreach (var child in childMsgs)
                    children.Add(SerializeCompilerMessage(child));
            return new
            {
                description = FixComEncoding(mt.GetProperty("Description")?.GetValue(msg)?.ToString() ?? ""),
                path = FixComEncoding(mt.GetProperty("Path")?.GetValue(msg)?.ToString() ?? ""),
                dateTime = mt.GetProperty("DateTime")?.GetValue(msg)?.ToString() ?? "",
                state = mt.GetProperty("State")?.GetValue(msg)?.ToString() ?? "",
                errorCount = Convert.ToInt32(mt.GetProperty("ErrorCount")?.GetValue(msg) ?? 0),
                warningCount = Convert.ToInt32(mt.GetProperty("WarningCount")?.GetValue(msg) ?? 0),
                messages = children
            };
        }

        private static void FlattenCompilerMessages(object msg, List<object> errors, List<object> warnings)
        {
            var mt = msg.GetType();
            var stateText = mt.GetProperty("State")?.GetValue(msg)?.ToString() ?? "";
            var errCount = Convert.ToInt32(mt.GetProperty("ErrorCount")?.GetValue(msg) ?? 0);
            var warnCount = Convert.ToInt32(mt.GetProperty("WarningCount")?.GetValue(msg) ?? 0);
            var isError = stateText.Equals("Error", StringComparison.OrdinalIgnoreCase) || errCount > 0;
            var isWarning = stateText.Equals("Warning", StringComparison.OrdinalIgnoreCase) || warnCount > 0;

            if (isError)
                errors.Add(SerializeCompilerMessage(msg));
            else if (isWarning)
                warnings.Add(SerializeCompilerMessage(msg));

            var childMsgs = mt.GetProperty("Messages")?.GetValue(msg) as System.Collections.IEnumerable;
            if (childMsgs != null)
                foreach (var child in childMsgs)
                    FlattenCompilerMessages(child, errors, warnings);
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // TO 参数查找
        // ═════════════════════════════════════════════════════════════════════════════
        protected static TechnologicalParameter? FindParameterRecursive(
            TechnologicalParameterComposition parameters, string name)
        {
            foreach (var p in parameters)
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        protected static Version ParseVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return new Version(3, 0);
            var s = version.Trim();
            var parts = s.Split('.');
            if (parts.Length == 1 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
                return new Version(major, 0);
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maj) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min))
                return new Version(maj, min);
            return new Version(3, 0);
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // Dispose
        // ═════════════════════════════════════════════════════════════════════════════
        private void DisposeConnection()
        {
            // 取消订阅 Confirmation 事件，避免事件泄漏
            if (_confirmationHandler != null && _tiaPortal != null)
            {
                try { _tiaPortal.Confirmation -= _confirmationHandler; } catch { }
                _confirmationHandler = null;
            }
            try { _tiaPortal?.Dispose(); }
            catch { }
            finally { _project = null; _tiaPortal = null; _attachedProcessId = null; }

            // 清理所有静态缓存，避免切换项目后跨项目数据泄漏
            try { ClearSecurityCache(); } catch { }
            try { ClearAllTransactions(); } catch { }
            try { ClearOnlineSessions(); } catch { }
            try { ClearDownloadConfigs(); } catch { }
            try { ClearAllLadCaches(); } catch { }
        }

        public void Dispose()
        {
            lock (_lock) DisposeConnection();
            GC.SuppressFinalize(this);
        }
    }
}

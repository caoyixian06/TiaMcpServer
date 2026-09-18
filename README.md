# TIA-MCP Copilot V4.3.0 — 西门子博途 V17 MCP 服务器（TIA Portal V17 MCP Server）

[English](README.en.md) | **简体中文**

基于 C# / .NET Framework 4.8 / 西门子博途 V17（TIA Portal V17 Openness）的 MCP stdio 服务器，带中文 WinForms 控制中心。

> **作者：曹义贤** · 邮箱 [3165352549@qq.com](mailto:3165352549@qq.com) · GitHub [@caoyixian06](https://github.com/caoyixian06)

V4.3.0 工程工作流与执行策略说明：

- 工程需求会话、需求冻结、计划生成/批准和阶段状态机作为可选能力保留，不再阻断普通工程工具调用。
- `server_policy.json` 当前关闭 engineering workflow，因此 `create_project`、硬件、PLC、HMI 和离线编程工具无需先完成阶段推进即可调用。
- 关闭工程工作流不等于关闭安全边界：工具风险分类、参数 Schema、控制端锁、在线授权、TIA 工程状态检查、审计以及删除/重置确认仍然生效。
- LAD、HMI、验证和部署规范仍可按主题小包加载；它们不再作为普通工具调用的强制前置步骤。
- `Safe / Assist / Auto` 三态 `ExecutionPolicy` 仍然有效；当前默认配置为 `Auto`，但 Unknown 风险工具和未接入安全分发器的复合工具仍会被拒绝。
- Tool 调用仍统一经过 `Schema -> RiskCheck -> Permission -> Read/Write Lock -> Audit -> Execute`。
- 在线写入、下载、上传、CPU 控制等仍必须进入 `SafeOnlineExecutor` 授权范围。
- 删除/重置类 Tool 仍支持 `dryRun`，正式执行必须显式二次确认。
- 长任务仍返回 `job_id`，支持 `get_job_status`、`list_jobs`、`cancel_job`；需要立即拿到结果时传 `wait=true`。

## 构建

必须在安装 TIA Portal、对应 Openness 和 .NET Framework 4.8 开发环境的 Windows 电脑上构建：

```powershell
.\一键发布.ps1
```

按工程版本构建：

```powershell
.\一键发布.ps1 -ProjectPath "G:\PLC工程\包装线.ap17"
```

底层脚本为 `scripts\构建V4.3.ps1`，会扫描环境变量、注册表和本地盘符，并根据工程扩展名选择 TIA 版本。

## 静态审计

在带 Python 3 的环境中可运行：

```text
python scripts/static_audit_v442.py
```

该脚本检查 JSON/XML、C# 分隔符、版本一致性、全部静态注册 Tool 的风险覆盖、工程工作流锚点、9 个按需规范包、统一安全链及 WinForms DPI 设置。它不能替代 C# 编译、TIA Openness 实机连接和 PLC 下载测试。

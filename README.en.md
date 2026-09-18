# TIA-MCP Copilot V4.3.0 — TIA Portal V17 MCP Server (西门子博途 V17)

**English** | [简体中文](README.md)

An MCP stdio server built on C# / .NET Framework 4.8 / Siemens TIA Portal V17 Openness, shipping with a Chinese-language WinForms control center.

> **Author: Cao Yixian (曹义贤)** · Email [3165352549@qq.com](mailto:3165352549@qq.com) · GitHub [@caoyixian06](https://github.com/caoyixian06)

V4.3.0 engineering workflow & execution policy notes:

- Engineering requirement sessions, requirement freezing, plan generation/approval, and the phase state machine are kept as optional capabilities and no longer block regular engineering tool calls.
- `server_policy.json` currently disables the engineering workflow, so `create_project`, hardware, PLC, HMI, and offline programming tools can be invoked without advancing workflow phases first.
- Disabling the engineering workflow does not disable safety boundaries: tool risk classification, parameter schema validation, control-end locks, online authorization, TIA engineering state checks, auditing, and delete/reset confirmations all remain in force.
- LAD, HMI, verification, and deployment standards can still be loaded as small topic-based packages; they are no longer mandatory prerequisites for regular tool calls.
- The three-state `ExecutionPolicy` (`Safe / Assist / Auto`) remains effective; the current default is `Auto`, but Unknown-risk tools and composite tools not wired into the safety dispatcher are still rejected.
- Every tool call still passes through the unified chain `Schema -> RiskCheck -> Permission -> Read/Write Lock -> Audit -> Execute`.
- Online writes, download, upload, and CPU control still must go through the `SafeOnlineExecutor` authorization scope.
- Delete/reset tools still support `dryRun`; actual execution always requires an explicit second confirmation.
- Long-running tasks still return a `job_id`, with `get_job_status`, `list_jobs`, and `cancel_job` available; pass `wait=true` when you need the result immediately.

## Build

Build on a Windows PC with TIA Portal V17, the matching Openness API, and the .NET Framework 4.8 developer environment installed:

```powershell
.\一键发布.ps1
```

Build against a specific project version:

```powershell
.\一键发布.ps1 -ProjectPath "G:\PLC工程\包装线.ap17"
```

The underlying script is `scripts\构建V4.3.ps1`; it scans environment variables, the registry, and local drives, and selects the TIA version based on the project file extension.

## Static Audit

With Python 3 available:

```text
python scripts/static_audit_v442.py
```

The script checks JSON/XML validity, C# delimiters, version consistency, risk coverage of all statically registered tools, engineering workflow anchors, the 9 on-demand standards packages, the unified safety chain, and WinForms DPI settings. It does not replace C# compilation, a real TIA Openness connection, or PLC download tests.

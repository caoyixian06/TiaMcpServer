# 重构基线（阶段 0）

> 日期：2026-08-16（会话时间线）
> 来源：`E:\TIA-MCP-V4.4.4-HMI-SOURCE-FIXED\src` 完整副本（排除 bin/obj）
> 基线提交：e5433d6「阶段0基线」

## 基线指标

| 指标 | 值 |
|---|---|
| 文件数 | 158 |
| 代码行数 | 83,198 |
| 构建 | Release + TiaPortalDir(V17) **0 错误**（仅既有 nullable 警告） |
| 冒烟测试 | tools/smoke.mjs **6/6 通过** |
| 工具数 | **235** |
| 冒烟对象 | TIA PID 26056 · 两层升降平台控制系统_V17（升降控制 FB 44 网络） |

## 冒烟覆盖

tools/list 计数、attach_to_process、get_project_info、list_blocks、
read_lad_network(升降控制 网1)、compile_plc(0 错误 0 警告)。

## 重构目标（用户需求）

1. **AI 一遍过**：统一工具输入格式、写后回读验证（verified）、导出不再有 Queued 陷阱
2. **少协助 + 主动喊人**：needsHuman 升级协议（reason/howToHuman/resumeHint）、get_pending_human_actions
3. **写入前必模拟**：verify_lad_simulation / write_lad_safe 门禁（node xml2net + lad-sim 静态执行）
4. **小白能看懂**：裁剪死重（约 45k 行未用域）、目录归位、中文说明书

## 阶段计划

- 阶段 1：裁剪（Archive 移出未用域 + 公共辅助抽取）→ 构建过 + 冒烟过
- 阶段 2：三大机制落地
- 阶段 3：结构归位（Core/Connection/Domains）
- 阶段 4：说明书进知识库

## 回归门（每个阶段提交前必须全绿）

```
dotnet build src.csproj -c Release -p:TiaPortalDir="C:\Program Files\Siemens\Automation\Portal V17" -p:TiaVersion=V17
node tools/smoke.mjs bin\Release\net48\TiaMcpServer.exe
```

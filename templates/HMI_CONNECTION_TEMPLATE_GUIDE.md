# Classic HMI 连接自动化说明（修正版）

## 1. 先区分两类连接

### 集成连接

HMI 和 PLC 位于同一个 TIA 项目内，并在“设备与网络”中建立 HMI connection。

- 不会出现在 `HmiTarget.Connections` 中。
- TIA Openness 不支持导出集成连接。
- 不能通过导出的连接 XML 复制或创建。
- `export_hmi_connection` 对此类连接返回明确失败是正确行为。

自动化方案：准备一个已经建立好集成连接的 `.ap19` / `.zap19` 种子工程。MCP 从该工程开始创建 PLC 变量、HMI 变量和画面。也可以在每个新工程中通过 TIA GUI 手工创建一次。

## 2. 非集成连接

非集成连接面向项目外部 PLC，通过通信驱动、IP、机架/插槽等参数寻址。

- 位于 `HmiTarget.Connections`。
- 支持 `Connection.Export()` 和 `ConnectionComposition.Import()`。
- 可以先手工创建一个非集成连接，再通过 `export_hmi_connection` 导出模板。
- HMI 变量通常使用绝对地址。

## 3. 推荐架构

需要同一项目内的 PLC 符号变量联动时，使用“种子工程 + 已有集成连接”。

只要求运行时通过 IP 访问 PLC，且可接受绝对地址时，使用非集成连接。

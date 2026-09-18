# HMI–PLC 集成连接修正说明

上一版错误地要求从手工配置成功的同项目 HMI–PLC 连接中导出 XML。该连接属于集成连接，西门子明确不支持导出。

本版修改：

1. `export_hmi_connection` 仅允许导出非集成连接，不再反射生成伪 XML。
2. `create_hmi_connection` 增加 `connectionMode`。
3. `integrated` 模式返回明确的 API 不支持结果，并提供种子工程方案。
4. `nonIntegrated` 模式仍使用 `HmiTarget.Connections` 的 Import/Export。
5. `setup_network_and_hmi_connection` 不再声称能自动创建 Classic HMI 集成连接。

推荐：建立一个最小 `.zap19` 种子工程，包含目标 PLC、目标 HMI、PROFINET 子网和一条集成 HMI 连接。后续自动化只克隆该工程并修改内容。

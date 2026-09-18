# HMI、连接和 LAD 定向修复测试

这些测试必须在可丢弃项目中执行。先用 PLCSIM，不要直接在生产 PLC 上验证。

## 0. 构建

在安装 TIA Portal V19 的 Windows 电脑执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Patched.ps1 `
  -TiaPortalDir "C:\Program Files\Siemens\Automation\Portal V19"
```

确认输出目录同时存在：

- `TiaMcpServer.exe`
- `tools_filter.json`

## 1. HMI—PLC 连接

### 1.1 集成连接

同一项目内的 Classic HMI↔PLC 集成连接不能通过 Openness 创建或导出。调用：

```json
{
  "subnetName": "PN/IE_1",
  "plcDeviceName": "PLC_1",
  "hmiDeviceName": "HMI_1",
  "plcIp": "192.168.0.1",
  "hmiIp": "192.168.0.2",
  "connectionName": "HMI_Connection",
  "connectionMode": "integrated"
}
```

预期：

- 子网和 IP 步骤可以成功。
- 连接步骤返回 `CLASSIC_HMI_INTEGRATED_CONNECTION_NOT_EXPOSED`。
- 总结果不得把网络成功误报成集成连接创建成功。
- 实际工程应从已经包含集成连接的 `.ap19/.zap19` 种子工程开始。

### 1.2 非集成连接

先在测试工程中手工创建一个面向外部 PLC 地址的非集成连接，再用 `export_hmi_connection` 导出模板。调用：

```json
{
  "subnetName": "PN/IE_1",
  "hmiDeviceName": "HMI_1",
  "plcIp": "192.168.0.1",
  "hmiIp": "192.168.0.2",
  "connectionName": "External_PLC",
  "connectionMode": "nonIntegrated",
  "connectionDriver": "SIMATIC S7 1200",
  "connectionTemplateFilePath": "D:\\TIA_MCP\\templates\\non_integrated_connection.xml"
}
```

预期：

- 连接位于 `HmiTarget.Connections`。
- 可以再次通过 `export_hmi_connection` 导出。
- HMI 变量使用绝对地址，不依赖项目内 PLC 符号链接。

## 2. HMI 绝对地址变量

调用 `import_hmi_tags_absolute`：

```json
{
  "connectionName": "HMI_Connection",
  "targetTableName": "MCP_Test_Tags",
  "tagsJson": "[{\"name\":\"启动按钮\",\"dataType\":\"Bool\",\"address\":\"%M0.0\"},{\"name\":\"当前速度\",\"dataType\":\"Int\",\"address\":\"%MW2\"},{\"name\":\"累计值\",\"dataType\":\"DInt\",\"address\":\"%MD4\"}]"
}
```

反向测试：把 `Int` 配成 `%M0.0`，必须在导入前失败，不能返回假成功。

## 3. HMI 画面自动布局

故意提交越界、字号过大和文本框过小的规格：

```json
{
  "hmiDeviceName": "HMI_1",
  "specJson": "{\"screenName\":\"MCP_Layout_Test\",\"backColor\":\"#F2F2F2\",\"items\":[{\"type\":\"text\",\"name\":\"Title\",\"left\":760,\"top\":10,\"width\":40,\"height\":18,\"text\":\"设备运行状态总览\",\"fontSize\":\"48\"},{\"type\":\"button\",\"name\":\"StartButton\",\"left\":790,\"top\":460,\"width\":140,\"height\":80,\"text\":\"启动设备\",\"fontSize\":\"36\"},{\"type\":\"text\",\"name\":\"StatusText\",\"left\":20,\"top\":20,\"width\":80,\"height\":20,\"text\":\"当前状态：自动运行\",\"fontSize\":\"28\"}]}"
}
```

通过条件：

- 程序优先读取目标 HMI 的实际画布尺寸。
- 所有控件最终 `left/top/width/height` 均在画布内。
- 文字控件自动扩展或降低字号，文本不再明显被裁切。
- 显著重叠会被移动到最近空闲位置，并在结果中给出 warning。
- 重名控件必须失败。

## 4. LAD 静态校验

先调用 `validate_lad_network`，提交明显错误网络：

```json
{
  "networkJson": "{\"variables\":[{\"name\":\"Start\",\"datatype\":\"Bool\",\"section\":\"Input\"},{\"name\":\"Motor\",\"datatype\":\"Bool\",\"section\":\"Output\"}],\"rung\":[{\"coil\":{\"coil\":\"Motor\"}},{\"contact\":{\"contact\":\"Start\"}},{\"coil\":{\"coil\":\"Motor\"}}]}"
}
```

必须报告：

- 线圈不是最后一个元素。
- 同一网络重复写入 `Motor`。

再测试缺少定时器引脚：

```json
{
  "networkJson": "{\"variables\":[{\"name\":\"Start\",\"datatype\":\"Bool\",\"section\":\"Input\"}],\"rung\":[{\"contact\":{\"contact\":\"Start\"}},{\"box\":{\"box\":\"TON\",\"pins\":{\"PT\":\"T#5s\"}}}]}"
}
```

必须报告缺少必要引脚，而不是直接生成 XML。

## 5. LAD 导入、编译和回滚

用一个可丢弃 FB 测试正确网络：

```json
{
  "blockName": "FB_MCP_Test",
  "compileAfter": true,
  "networkJson": "{\"title\":\"起保停\",\"variables\":[{\"name\":\"Start\",\"datatype\":\"Bool\",\"section\":\"Input\"},{\"name\":\"Stop\",\"datatype\":\"Bool\",\"section\":\"Input\"},{\"name\":\"Motor\",\"datatype\":\"Bool\",\"section\":\"Output\"}],\"rung\":[{\"branch\":{\"branch\":[[{\"contact\":{\"contact\":\"Start\"}}],[{\"contact\":{\"contact\":\"Motor\"}}]]}},{\"contact\":{\"contact\":\"Stop\",\"negated\":true}},{\"coil\":{\"coil\":\"Motor\"}}]}"
}
```

通过条件：

- 静态校验通过后才允许导入。
- 导入后立即编译。
- 编译失败时恢复原块 XML，并返回 `success=false`。
- 多个未显式命名的 TON/TOF/TP/CTU 等实例不得共享同一个固定默认实例。

## 6. 业务逻辑验证仍然必需

编译成功只能证明工程结构、类型和指令连接可被 TIA 接受，不能证明工艺逻辑正确。至少为每个块建立：

- 输入组合与期望输出表。
- 启动、停止、复位、掉电恢复等状态测试。
- 定时器和计数器边界测试。
- 互锁、急停和故障路径测试。
- PLCSIM 观察表或自动化测试记录。

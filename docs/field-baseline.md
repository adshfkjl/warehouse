# 现场 PLC 与仓储基线表

| 项目 | 内容 |
| --- | --- |
| 基线版本 | 0.1 |
| 建立任务 | Task 0.2 |
| 建立日期 | 2026-08-25 |
| 适用范围 | 第一阶段旧 PLC 接口兼容与模拟测试 |
| 文档状态 | `AGENT_VERIFIED`（仅表示来源和结构已核对） |
| 现场状态 | `HUMAN_PENDING`、`FIELD_PENDING` |

## 1. 使用边界和状态说明

本表只固化旧源码可证明的字段、地址和代码行为。旧源码不能证明现场当前配置；任何无法从源码或已签字现场记录确认的真实值均写为 `BLOCKED`，不得用于生产连接、设备动作或新 WMS 的默认配置。

状态含义：

- `SOURCE_VERIFIED`：可在引用源码中复核的代码事实，不代表现场已确认。
- `BLOCKED`：真实值或业务含义缺失，必须由责任人确认后才可解除。
- `HUMAN_PENDING`：需要现场负责人签字确认。
- `FIELD_PENDING`：需要在隔离测试环境或受控现场验证；本任务不连接生产设备。

本表引用的主要源码：

- `warehouse/PlcManagementService/Models/ModbusAddress.cs:3-30`
- `warehouse/PlcManagementService/Models/PlcConfiguration.cs:5-55`
- `warehouse/PlcManagementService/Services/ModbusService.cs:19-176,199-352,510-658`
- `warehouse/PlcManagementService/PLCService.cs:544-548,710-714`
- `warehouse/PLCManagment/API/Services/PlcService.cs:1388-1415,2456-2585`
- `warehouse/PLCManagment/API/Services/LocationCheckService.cs:34-55,97-116,143-176,196-216`
- `warehouse/PLCManagment/API/Models/LocationManagement.cs:7-22`
- `warehouse/PLCManagment/API/Models/Dtos/LocationStatusDto.cs:5-60`
- `warehouse/PlcManagementService/Configuration/AppSettings.cs:5-16`

## 2. PLC 连接和设备身份表

源码只有配置字段定义，没有当前生产配置记录。以下字段必须从现场配置导出或设备铭牌确认。

| 字段 | 当前值 | 来源/证据 | 责任人 | 确认方式 | 截止日期 | 阻塞影响 |
| --- | --- | --- | --- | --- | --- | --- |
| PLC 编号 `PlcId` | `BLOCKED` | 字段存在于 `PlcConfiguration`，源码未提供实例值 | `BLOCKED：现场负责人待指定` | 读取非生产配置快照并由现场负责人签字 | `BLOCKED：待负责人指定` | 无法将任务、库位和设备状态绑定到具体 PLC |
| PLC IP `IpAddress` | `BLOCKED` | `ModbusService.ConnectAsync` 使用配置字段连接；源码无现场 IP | `BLOCKED：现场负责人待指定` | 现场网络台账/设备配置导出，脱敏后归档 | `BLOCKED：待负责人指定` | 禁止连接设备，无法进行现场回归 |
| Modbus TCP 端口 `Port` | `BLOCKED` | 配置字段存在；源码无现场端口。测试代码中的 `502` 仅为样例，不是现场值 | `BLOCKED：现场负责人待指定` | 现场 PLC 通信配置和网络台账 | `BLOCKED：待负责人指定` | 无法建立通信；不得把样例 502 当默认生产端口 |
| Modbus Slave ID | `BLOCKED` | `ReadHoldingRegistersAsync` 和写操作均使用配置字段；源码无现场值。测试值 `1` 仅为样例 | `BLOCKED：现场负责人待指定` | PLC 通讯参数导出并现场负责人签字 | `BLOCKED：待负责人指定` | 可能读写错误从站，禁止自动试探 |
| 寄存器地址偏移 `RegisterAddrOffset` | `BLOCKED` | `GetRegisterAddress = base + offset`；配置字段存在但无实例值 | `BLOCKED：PLC/设备负责人待指定` | 对照 PLC 地址表和读回结果，在模拟器先验证 | `BLOCKED：待负责人指定` | 所有读写地址可能错位，禁止下发指令 |
| 设备类型 | `BLOCKED` | 源码只定义 PLC 配置和堆垛机相关字段，未给出设备型号/拓扑 | `BLOCKED：设备负责人待指定` | 设备清单、铭牌、控制柜图纸和现场签字 | `BLOCKED：待负责人指定` | 无法确定能力、风险等级和网关映射 |
| 连接方式 | `SOURCE_VERIFIED：Modbus TCP over TcpClient`；现场链路 `BLOCKED` | `TcpClient.ConnectAsync` + NModbus master | `BLOCKED：网络负责人待指定` | 网络拓扑和隔离测试环境确认 | `BLOCKED：待负责人指定` | 未确认隔离边界前不得连接生产网络 |
| PLC 数量及编号集合 | `BLOCKED` | 服务按 `ConcurrentDictionary<string, ModbusService>` 管理多个 PLC，但源码无清单 | `BLOCKED：设备负责人待指定` | 现场设备清单与 WMS 对照 | `BLOCKED：待负责人指定` | 无法建立完整设备覆盖范围 |

### 2.1 代码已知的通信参数（不是现场参数）

| 参数 | 代码值 | 证据 | 说明 |
| --- | --- | --- | --- |
| 单次连接超时 | `3000 ms` 默认 | `AppSettings.PlcConnectTimeout` | 可被配置覆盖；生产值仍为 `BLOCKED` |
| NModbus 读超时 | `1500 ms` | `ModbusService.ConnectAsync` | 代码设置值，需在模拟器和现场确认是否足够 |
| NModbus 写超时 | `1500 ms` | `ModbusService.ConnectAsync` | 同上 |
| NModbus transport retries | `3` | `ModbusService.ConnectAsync` | 仅为旧服务行为，不等于新 WMS 允许的业务重试次数 |
| 连接失败最大重试 | `5` 默认 | `MaxRetryCount` / `AppSettings` | 达到上限后状态为 `Failed` |
| 重连间隔 | `60000 ms` 默认 | `ReconnectInterval` / `AppSettings` | 代码注释为 1 分钟 |
| 心跳定时器 | `1000 ms` | `new Timer(1000)` | 发送失败会停止心跳并启动重连 |
| 读周期配置 | `1000 ms` 默认；快速读 `200 ms` 默认 | `AppSettings` | `ModbusService` 本身的读取调用不证明现场调度周期 |
| 数据库命令超时 | `10 s` 默认 | `AppSettings.DbCommandTimeout` | 不是 PLC 超时 |

## 3. Modbus 地址与信号表

### 3.1 地址规则

`ModbusService` 的批量读取地址使用：

```text
effectiveAddress = baseAddress + RegisterAddrOffset
```

它先计算所有读地址的最小值和最大值，再以一个 `ReadHoldingRegistersAsync(slaveId, start, count)` 读取连续区间（`ModbusService.cs:156-182,224-232`）。单寄存器和批量写入也在 `WriteRegisterAsync`/`WriteRegistersAsync` 中加偏移（`ModbusService.cs:620-662`）。

**风险：** `SendHeartbeat` 与 `AutoRunTask` 在 `ModbusService.cs:524,556` 直接写 `ModbusAddress.Heartbeat`，没有调用 `GetRegisterAddress`；心跳是否也应加偏移为 `BLOCKED`，在确认前不得修改旧时序或据此实现新网关。

### 3.2 上位机写入信号

| 用途 | 基址/地址 | 宽度 | 方向与类型 | 代码行为 | 现场确认 |
| --- | ---: | ---: | --- | --- | --- |
| 工作模式 | `22000` (`D22000`) | 1 word | 上位机 -> PLC，`ushort` | `0` 上架、`1` 下架、`2` 移库（入库/出库：`PLCService.cs:544,710`；移库：`PlcService.cs:2563`） | 现场模式码和是否还有其他模式：`BLOCKED` |
| 装载点/货架选择 | `22001` (`D22001`) | 1 word | 上位机 -> PLC，`ushort` | 写入装载点 `0` 或 `1` | 0/1 的物理位置含义：`BLOCKED` |
| 货架编号 | `22002` | 1 word | 上位机 -> PLC，`ushort` | 上架/下架时写 `bill.Shelf` | 货架编号范围与编码规则：`BLOCKED` |
| 储位编号 | `22003` | 1 word | 上位机 -> PLC，`ushort` | 上架/下架时写 `bill.Position` | 储位编码与 PLC 坐标映射：`BLOCKED` |
| 上架启动 | `22008` | 1 word | 上位机 -> PLC，`ushort` | 写入 `1` 启动上架 | 启动保持/复位时序：`BLOCKED` |
| 下架启动 | `22009` | 1 word | 上位机 -> PLC，`ushort` | 写入 `1` 启动下架 | 启动保持/复位时序：`BLOCKED` |
| 心跳 | `22027` (`D22027`) | 1 word | 上位机 -> PLC，`ushort` | 每秒写 `DateTime.Now.Second % 2`；偏移是否适用见风险 | 心跳值、地址和失联判据：`BLOCKED` |

### 3.3 PLC 读取信号

| 用途 | 基址 | 宽度 | 解析方式/代码行为 | 现场确认 |
| --- | ---: | ---: | --- | --- |
| PLC 状态 | `23000` (`D23000`) | 1 word | `1` -> `联机`，否则 `脱机` | 现场状态码全集：`BLOCKED` |
| 工作状态/急停 | `23001` (`D23001`) | 1 word | `4` -> `IsEmergencyStop=true` | 急停码、复位条件和其他状态码：`BLOCKED` |
| 任务应答/完成码 | `23002` (`D23002`) | 1 word | 原值保存到 `OperationResult`；旧业务存在 `0`/`6` 等判断，完整码表：`BLOCKED` | 必须提供码表、时序和清零规则 |
| 货叉状态 | `23003` (`D23003`) | 1 word | `1` -> 货叉状态为真 | 物理状态和安全含义：`BLOCKED` |
| 货架检测 | `23004` (`D23004`) | 1 word | 地址常量存在，但 `ModbusService` 当前批量读取列表未包含它 | 是否必须读取及含义：`BLOCKED` |
| 位置状态 | `23005` (`D23005`) | 1 word | 地址常量存在，但当前批量读取列表未包含它 | 是否必须读取及编码：`BLOCKED` |
| 复位完成 | `23010` (`D23010`) | 1 word | `1` -> `IsResetCompleted=true` | 复位指令地址、完成保持时间：`BLOCKED` |
| 入库完成 | `23012` (`D23012`) | 1 word | `1` -> `IsInboundCompleted=true` | 完成脉冲/电平及清零规则：`BLOCKED` |
| 出库完成 | `23013` (`D23013`) | 1 word | `1` -> `IsOutboundCompleted=true` | 完成脉冲/电平及清零规则：`BLOCKED` |
| 移库完成 | `23014` (`D23014`) | 1 word | `1` -> `IsRelocationCompleted=true` | 完成脉冲/电平及清零规则：`BLOCKED` |
| 报警信息 | `23019` (`D23019`) | 2 words | 低 word + 高 word<<16 组成 32 位位图；旧解码器定义报警位 1-27 | 报警位、复位和确认方式：`BLOCKED` |
| X 位置 | `23030` (`D23030`) | 2 words | IEEE-754 single，寄存器低 word 在前，转 `decimal` | 单位、坐标原点和精度：`BLOCKED` |
| Y 位置 | `23032` (`D23032`) | 2 words | 同上 | 单位、坐标原点和精度：`BLOCKED` |
| Z 位置 | `23034` (`D23034`) | 2 words | 同上 | 单位、坐标原点和精度：`BLOCKED` |
| X 速度 | `23036` (`D23036`) | 2 words | IEEE-754 single，转 `int` 到 `AuxSpeed` | 单位和精度：`BLOCKED` |
| Y 速度 | `23038` (`D23038`) | 2 words | IEEE-754 single，转 `int` 到 `MainSpeed` | 单位和精度：`BLOCKED` |
| 装载点 A 重量 | `23042` (`D23042`) | 1 word | `short(register)/100.00`，保存为 `BoxWeightA` | 重量单位、零点、量程和阈值：`BLOCKED` |
| 装载点 B 重量 | `23044` (`D23044`) | 1 word | `short(register)/100.00`，保存为 `BoxWeightB` | 重量单位、零点、量程和阈值：`BLOCKED` |

所有表中地址均为**基址**；实际地址是否加偏移必须以现场 `RegisterAddrOffset` 和模拟器契约测试确认。上述表不能作为生产写入授权。

### 3.4 信号待确认责任表

下表逐项覆盖 3.2 和 3.3 中标为 `BLOCKED` 的地址语义；在完成确认前，只允许在模拟 PLC 中使用。

| 未知信号/事项 | 当前状态 | 责任人 | 确认方式 | 截止日期 | 阻塞影响 |
| --- | --- | --- | --- | --- | --- |
| 22000 工作模式完整码表 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | PLC 地址表、旧时序和模拟器逐值测试 | `BLOCKED：待负责人指定` | 误写模式可能触发错误动作 |
| 22001 装载点 0/1 物理映射 | `BLOCKED` | `BLOCKED：设备负责人待指定` | 控制柜图纸、装载点台账和受控空载确认 | `BLOCKED：待负责人指定` | 可能把托盘送到错误装载点 |
| 22002/22003 货架与储位范围/映射 | `BLOCKED` | `BLOCKED：PLC 与仓储负责人待指定` | 库位主数据、设备坐标表和模拟器边界测试 | `BLOCKED：待负责人指定` | 位置错位可能造成设备及库存风险 |
| 22008 上架启动保持/清零 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | PLC 时序图、模拟器边沿测试和负责人签字 | `BLOCKED：待负责人指定` | 重复触发或无法启动上架 |
| 22009 下架启动保持/清零 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | PLC 时序图、模拟器边沿测试和负责人签字 | `BLOCKED：待负责人指定` | 重复触发或无法启动下架 |
| 22027 心跳地址偏移和值域 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | 对照读写程序、模拟器偏移测试；禁止生产试探 | `BLOCKED：待负责人指定` | 错误心跳可能导致误判离线或覆盖寄存器 |
| 23000 PLC 在线码 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | PLC 状态字典和断网模拟 | `BLOCKED：待负责人指定` | 无法可靠判断在线状态 |
| 23001 工作状态/急停完整码表 | `BLOCKED` | `BLOCKED：安全与 PLC 负责人待指定` | 安全回路图、PLC 状态字典和受控演练 | `BLOCKED：待负责人指定` | 急停和恢复判断错误会放大现场风险 |
| 23002 任务应答/完成码与清零 | `BLOCKED` | `BLOCKED：PLC 与设备负责人待指定` | PLC 程序、旧 API 监控和模拟器延迟/重复测试 | `BLOCKED：待负责人指定` | 无法安全判断成功、失败或物理未知 |
| 23003 货叉状态码 | `BLOCKED` | `BLOCKED：设备负责人待指定` | 设备状态字典和空载受控测试 | `BLOCKED：待负责人指定` | 可能在货叉未到位时更新库存 |
| 23004/23005 是否启用及编码 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | PLC 地址表与读回样本对照 | `BLOCKED：待负责人指定` | 状态采集缺失或错误映射 |
| 23010 复位完成语义 | `BLOCKED` | `BLOCKED：安全与 PLC 负责人待指定` | 复位时序、急停恢复演练和边沿测试 | `BLOCKED：待负责人指定` | 可能在未复位时放行任务 |
| 23012/23013/23014 完成信号电平/脉冲 | `BLOCKED` | `BLOCKED：PLC 与设备负责人待指定` | 各流程模拟器测试、PLC 时序图和现场签字 | `BLOCKED：待负责人指定` | 重复完成或漏完成，账实不一致 |
| 23019-23020 报警位字典与复位 | `BLOCKED` | `BLOCKED：安全与 PLC 负责人待指定` | 报警代码表、模拟器注入和受控报警确认 | `BLOCKED：待负责人指定` | 无法分级处置或错误自动恢复 |
| 23030-23040 坐标/速度单位与字序 | `BLOCKED` | `BLOCKED：设备负责人待指定` | PLC 数据类型说明、已知坐标样本和模拟器字序测试 | `BLOCKED：待负责人指定` | 位置/速度显示和边界校验错误 |
| 23042/23044 重量单位、零点和量程 | `BLOCKED` | `BLOCKED：计量与设备负责人待指定` | 仪表校准记录、砝码测试和 PLC 参数表 | `BLOCKED：待负责人指定` | 空闲/超重判断错误，可能重复装载 |

## 4. 入库、出库、移库、复位、急停和报警信号关系

| 流程 | 写入/读取信号 | 源码可确认行为 | 未确认项与阻塞影响 |
| --- | --- | --- | --- |
| 入库（上架） | 写 `22000=0`、`22001=装载点`、`22002=货架`、`22003=储位`、`22008=1`；读取 `23012` 和 `23002` | `PLCService.cs:710-714`；完成字段由 `ModbusService` 解析 | 完成码和清零/重复触发规则 `BLOCKED`；无法安全实现幂等完成 |
| 出库（下架） | 写 `22000=1`、`22001=装载点`、`22002=货架`、`22003=储位`、`22009=1`；读取 `23013` 和 `23002` | `PLCService.cs:544-548` | 同上；装载点空闲阈值还存在 1.5 与 2 的代码差异，现场值 `BLOCKED` |
| 移库 | `ModbusAddress` 没有独立命名常量，但旧 API 写 `22000=2`、`22004`-`22007` 参数并以 `22010=1/0` 触发 | `PlcService.cs:2456-2585`；`RelocationCompleted=23014` 另有完成信号 | 现场模式码、源/目标编码、保持/清零和完成语义均 `BLOCKED`；不得从入库/出库猜测 |
| 复位 | 读取 `23010=1` 表示复位完成 | `ModbusService.cs:283-286` | 复位命令地址、触发值、急停后复位条件 `BLOCKED` |
| 急停 | 读取 `23001=4` 表示急停 | `ModbusService.cs:265-268` | 急停物理回路、解除条件、WMS 任务处置 `BLOCKED`；软件不得代替急停 |
| 完成 | 读取 `23012/23013/23014=1` 及任务应答 `23002` | `ModbusService.cs:271-303` | 完成脉冲宽度、是否需要边沿和操作 ID 关联 `BLOCKED` |
| 报警 | 读取 `23019-23020` 组成 32 位位图 | `ModbusService.cs:307-311`；报警位 1-27 在同文件 `AlarmDecoder` | 报警位现场字典、确认/复位和恢复动作 `BLOCKED` |
| 心跳 | 写 `22027`，值按秒 0/1 交替 | `ModbusService.cs:517-556` | 偏移不一致、PLC 期望方向和超时判据 `BLOCKED`；禁止生产验证 |

## 5. 库位、托盘和装载点编码表

### 5.1 源码结构事实

| 对象 | 源码字段/行为 | 代码事实 | 现场真实值 |
| --- | --- | --- | --- |
| 库位主键 | `PLCID + Shelf + Position` | 查询 `LocationManagements` 使用 PLC、货架、位置；实体还保存 `Row`、`Lev` | `BLOCKED`：编号范围、排/层/列映射待确认 |
| 库位状态 | `ShelfStatus` | `0` 空、`1` 有货、`2` 停用、查询不到为 `-1`（`LocationCheckService.cs:97-116`） | `BLOCKED`：现场是否还有故障/锁定状态 |
| 库位托盘编码 | `LocationManagement.Tray` | 可空字符串，最大长度约束在盘点模型为 50；格式未由库位实体限定 | `BLOCKED`：编码模板、校验规则和唯一性 |
| 托盘/料箱编号 | `PalletCode`、`Tray`、`CurrentPalletNumber` | 装载点 0/1 以 `CurrentPalletNumber` 是否为空参与空闲判断 | `BLOCKED`：现场条码类型、长度和扫描规则 |
| 装载点编号 | `PLCLocationCode` / `LoadingPoint` | 接口允许 `0` 或 `1`；代码将 0 关联 `BoxWeightA`、1 关联 `BoxWeightB` | `BLOCKED`：0/1 的外/内物理位置和传感器对应关系 |
| 装载点托盘同步 | `LoadingPoint_Status.CurrentPalletNumber` | 出库完成后由旧服务同步托盘号；新 WMS 不得直接写旧表 | `BLOCKED`：现场同步时点和对账方式 |

### 5.2 库位编码待确认清单

| 待确认字段 | 当前值 | 责任人 | 确认方式 | 截止日期 | 阻塞影响 |
| --- | --- | --- | --- | --- | --- |
| PLC 编号格式和最大长度 | `BLOCKED` | `BLOCKED：仓储负责人待指定` | 导出现有库位主数据并抽样扫描 | `BLOCKED：待负责人指定` | 无法建立 WMS 库位唯一键 |
| 货架/排/层/列范围 | `BLOCKED` | `BLOCKED：仓储与设备负责人待指定` | 设备布局图 + 库位主数据核对 | `BLOCKED：待负责人指定` | 无法校验目标库位和防越界 |
| `Shelf`/`Position` 到 PLC 坐标的映射 | `BLOCKED` | `BLOCKED：PLC 负责人待指定` | 模拟器映射测试 + 现场负责人签字 | `BLOCKED：待负责人指定` | 下发错误位置可能导致设备风险 |
| 托盘编码格式、长度、大小写 | `BLOCKED` | `BLOCKED：仓储负责人待指定` | 条码规范、实物扫描和重复性检查 | `BLOCKED：待负责人指定` | 无法防止重复托盘和账实错配 |
| 托盘与库位唯一性 | `BLOCKED` | `BLOCKED：仓储负责人待指定` | 全库盘点导出与重复检测 | `BLOCKED：待负责人指定` | 无法安全锁定库存或移库 |
| 装载点 0/1 物理方向 | `BLOCKED` | `BLOCKED：设备负责人待指定` | 控制柜图纸 + 受控空载确认 | `BLOCKED：待负责人指定` | 装载点选择和重量对应可能反转 |

## 6. 重量、数量精度和业务阈值

| 项目 | 代码表现 | 基线状态 | 责任人/确认方式 | 截止日期 | 阻塞影响 |
| --- | --- | --- | --- | --- | --- |
| 重量寄存器原始单位 | 1 word signed value divided by `100.00` | `BLOCKED`：源码只有换算，没有物理单位 | `BLOCKED：计量负责人待指定`；校准砝码/PLC 手册/现场签字 | `BLOCKED：待负责人指定` | 不能确定 kg、g 或其他单位 |
| 重量小数精度 | `BoxWeightA/B` 数据库字段为 `decimal(10,2)`（API 模型） | `SOURCE_VERIFIED`；现场显示/业务精度 `BLOCKED` | `BLOCKED：仓储负责人待指定`；对照称重仪表和业务规则 | `BLOCKED：待负责人指定` | 阈值判断和库存重量可能误差 |
| 装载点空闲阈值 | `1.5`（`LocationCheckService` SQL） | `BLOCKED`：旧代码另有 `<2` 注释/自动选择逻辑 | `BLOCKED：仓储与设备负责人待指定`；现场空载/有载测试 | `BLOCKED：待负责人指定` | 误判空闲会造成重复装载或拒绝作业 |
| 箱体重量上限 | `BLOCKED` | 报警位有“箱1/箱2过重”，上限值未在两份源码中给出 | `BLOCKED：设备负责人待指定`；PLC 参数表和校准测试 | `BLOCKED：待负责人指定` | 无法配置超重拦截 |
| 数量单位 | `BLOCKED` | 旧模型有 `decimal Qty/Quantity`，未定义计量单位和换算 | `BLOCKED：仓储负责人待指定`；物料主数据和单据样例 | `BLOCKED：待负责人指定` | 无法安全实现收发存和盘点差异 |
| 数量小数位 | `BLOCKED` | 源码未定义统一精度 | `BLOCKED：仓储负责人待指定`；业务规则签字 | `BLOCKED：待负责人指定` | 不能确定舍入、超发和负库存规则 |

## 7. 并发、锁和超时基线

| 项目 | 源码可见行为 | 当前基线 | 责任人/确认方式 | 截止日期 | 阻塞影响 |
| --- | --- | --- | --- | --- | --- |
| 单个 `ModbusService` 通信并发 | `_modbusLock = new SemaphoreSlim(1,1)` | `SOURCE_VERIFIED：同一服务串行读写` | `BLOCKED：设备负责人待指定`；模拟器并发压测和现场能力确认 | `BLOCKED：待负责人指定` | 新网关若并发下发可能破坏旧时序 |
| PLC 连接并发 | API 连接管理按 PLC 建立锁；批量探测 `MaxDegreeOfParallelism=2` | `SOURCE_VERIFIED：探测并发上限 2`；生产允许并发 `BLOCKED` | `BLOCKED：设备负责人待指定`；设备数量和网络压测 | `BLOCKED：待负责人指定` | 影响调度器吞吐与网络负载 |
| 设备动作并发 | 旧服务按 PLC 管理自动任务缓存/处理集合；未给出动作容量 | `BLOCKED` | `BLOCKED：设备负责人待指定`；PLC/WCS 能力表和急停演练 | `BLOCKED：待负责人指定` | 未知时只能按每 PLC 单任务保守运行 |
| 读/写响应超时 | 连接 3000ms，NModbus 读写 1500ms | `SOURCE_VERIFIED`；现场可接受上限 `BLOCKED` | `BLOCKED：设备负责人待指定`；模拟器延迟注入 + 受控验证 | `BLOCKED：待负责人指定` | 误判超时或长时间占用资源 |
| 业务任务完成超时 | 源码没有统一任务超时常量 | `BLOCKED` | `BLOCKED：仓储与设备负责人待指定`；按最慢设备动作实测 | `BLOCKED：待负责人指定` | 无法定义自动失败、未知和人工接管边界 |
| 发送后网络超时重试 | 旧源码未证明按任务号去重/查询能力 | `BLOCKED：默认不得自动重试` | `BLOCKED：设备负责人待指定`；旧 API 契约探测和现场签字 | `BLOCKED：待负责人指定` | 自动重试可能导致重复物理动作；应进入 `PhysicalStateUnknown` |
| 心跳失联判据 | 代码发送周期约 1s，未定义 PLC 端判据 | `BLOCKED` | `BLOCKED：PLC 负责人待指定`；PLC 程序/参数表和断网模拟 | `BLOCKED：待负责人指定` | 无法安全判定在线、离线和恢复 |

## 8. 待确认事项总表

在以下事项全部完成现场负责人签字前，设备网关只能使用模拟 PLC 和脱敏开发配置：

1. PLC 编号、IP、端口、Slave ID、寄存器偏移和设备拓扑。
2. 22000-22009 与 22027 的偏移、保持、清零和触发时序，尤其是心跳偏移不一致问题。
3. 23001 急停码、23002 完成/应答码、23010/23012/23013/23014 完成信号的边沿/电平语义。
4. 报警位 1-27 的现场字典、复位和人工接管流程。
5. 装载点 0/1 的物理位置、托盘同步时点、空闲重量阈值和重量单位。
6. 库位、托盘、物料数量编码和精度规则。
7. 每台设备安全并发数、业务动作超时、重试能力和断网/断电恢复策略。

### 责任和门禁

| 门禁 | 当前状态 | 允许动作 |
| --- | --- | --- |
| Agent 文档验收 | `AGENT_VERIFIED` | 校验来源、格式、未知项标记；仅可继续不依赖现场真实值的文档/模拟器工作 |
| 现场负责人确认 | `HUMAN_PENDING` | 负责人逐项签字后，才可把对应 `BLOCKED` 值写入非生产配置 |
| 现场设备验证 | `FIELD_PENDING` | 受控窗口、回滚方案和安全负责人在场后验证；本任务不执行连接或动作 |

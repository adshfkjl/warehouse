# 旧系统仓储规则基线

本文档记录旧程序当前已经实现或部分实现的入库、出库、移库、自动运行和盘点规则，作为新 WMS 第一阶段的兼容参考。它不是新系统的最终业务规范；“旧系统缺陷/待确认”部分不得直接复制到新系统。

## 1. 适用范围和来源

旧程序目录：`warehouse/`。

主要来源：

- `warehouse/PLCManagment/API/Services/PlcService.cs`
- `warehouse/PLCManagment/API/Services/LocationCheckService.cs`
- `warehouse/PLCManagment/API/Services/InventoryCheckService.cs`
- `warehouse/PLCManagment/API/Controllers/PlcOperationsController.cs`
- `warehouse/PLCManagment/API/Controllers/DocumentOperationController.cs`
- `warehouse/PlcManagementService/PLCService.cs`
- `warehouse/PlcManagementService/Services/DatabaseService.cs`
- `warehouse/PlcManagementService/Models/ModbusAddress.cs`
- `warehouse/PLCManagment/API/Tests/InventoryCheckFeature.Tests.ps1`
- `warehouse/PLCManagment/API/Tests/OutboundPalletSync.Tests.ps1`
- `warehouse/PlcManagementService/Tests/PlcServiceAutoOutbound.Tests.ps1`

### 1.1 规则来源行号索引

以下行号以当前 `warehouse/` 参考源码为准；旧源码变更后必须重新核对，不能把行号当成稳定接口标识。

| 规则 | 来源 |
| --- | --- |
| 库位状态查询、`ShelfStatus` 映射和库位更新方法 | `warehouse/PLCManagment/API/Services/LocationCheckService.cs:97-123,153-189; warehouse/PLCManagment/API/Services/PlcService.cs:2789-2807` |
| 装载点空判断及重量阈值 `<1.5` | `warehouse/PLCManagment/API/Services/LocationCheckService.cs:34-55` |
| 基础按位置出库前置校验和重量阈值 `0.4` | `warehouse/PLCManagment/API/Services/PlcService.cs:659-720` |
| 基础出库寄存器时序 `22000/22001/22002/22003/22009` | `warehouse/PLCManagment/API/Services/PlcService.cs:742-753` |
| 普通 API 出库完成监控、出库前托盘号同步和 `400ms/750` 轮询 | `warehouse/PLCManagment/API/Controllers/PlcOperationsController.cs:539-624` |
| 业务出库入口及 ERP 回写分支 | `warehouse/PLCManagment/API/Services/PlcService.cs:784-917` |
| 单据上/下架 API 及服务调用 | `warehouse/PLCManagment/API/Controllers/DocumentOperationController.cs:32-108` |
| 单据上架存储过程和后续 PLC 调用 | `warehouse/PLCManagment/API/Services/DocumentInboundService.cs:24-116` |
| 单据下架存储过程和后续 PLC 调用 | `warehouse/PLCManagment/API/Services/DocumentDownLoadService.cs:24-92` |
| 基础按位置入库前置校验、设备忙登记后台任务、货叉校验和注释的重量判断 | `warehouse/PLCManagment/API/Services/PlcService.cs:1285-1415` |
| 按单据入库入口、重量阈值 `<=1` 和 PLC 参数写入 | `warehouse/PLCManagment/API/Services/PlcService.cs:2055-2184` |
| 基础入库寄存器时序 `22000/22001/22002/22003/22008` | `warehouse/PLCManagment/API/Services/PlcService.cs:1388-1415` |
| 移库前置校验、已知 `inShelf` 源库位缺陷和移库寄存器时序 | `warehouse/PLCManagment/API/Services/PlcService.cs:2456-2585` |
| 自动任务查询和 `OperationType` 分流 | `warehouse/PlcManagementService/PLCService.cs:398-458; warehouse/PlcManagementService/Services/DatabaseService.cs:32-40` |
| 自动下架装载点选择、重量 `2` 和托盘号判断 | `warehouse/PlcManagementService/PLCService.cs:462-501` |
| 自动下架寄存器、完成监控和托盘同步 | `warehouse/PlcManagementService/PLCService.cs:522-679` |
| 自动上架寄存器和旧单据回写 | `warehouse/PlcManagementService/PLCService.cs:683-893` |
| 盘点范围筛选、排序、任务明细初始状态和单 PLC 活动任务 | `warehouse/PLCManagment/API/Services/InventoryCheckService.cs:17-116,565-605` |
| 盘点逐条下架、装载点等待、完成判断和任务统计 | `warehouse/PLCManagment/API/Services/InventoryCheckService.cs:258-395,450-555` |
| 盘点逐条预约上架和重复预约限制 | `warehouse/PLCManagment/API/Services/InventoryCheckService.cs:133-216` |
| 盘点状态常量 | `warehouse/PLCManagment/API/Models/Dtos/InventoryCheckDto.cs:6-18` |
| 盘点 API 路由、取消和逐条上架入口 | `warehouse/PLCManagment/API/Controllers/InventoryCheckController.cs:22-120` |
| PLC 寄存器常量和返回状态地址 | `warehouse/PlcManagementService/Models/ModbusAddress.cs:3-30` |
| 出库前托盘号捕获和装载点同步测试 | `warehouse/PLCManagment/API/Tests/OutboundPalletSync.Tests.ps1:17-24` |

行号只用于本次基线审计和复核；新 WMS 的接口契约必须引用业务名称、版本和测试，而不是依赖旧源码行号。

## 2. 库位、托盘和装载点状态

### 2.1 库位状态

旧程序以 `LocationManagements.ShelfStatus` 表示库位状态：

| 值 | 含义 | 入库 | 出库 |
| --- | --- | --- | --- |
| `0` | 空 | 允许 | 拒绝 |
| `1` | 有货 | 拒绝 | 允许 |
| `2` | 停用 | 拒绝 | 拒绝 |
| `-1` | 不存在 | 拒绝 | 拒绝 |

入库还要求装载点有货；出库要求目标装载点为空、源库位有货、货叉为空且 PLC 当前任务空闲。

旧代码中多个入库、出库、移库入口的 `ShelfStatus` 更新被注释，不能假定设备指令成功后旧库位账一定已经变化。新 WMS 必须在设备完成确认后，以自己的业务事务更新库位、托盘和库存流水。

### 2.2 装载点判定

旧程序有多套判定阈值：

- `LocationCheckService.IsLoadingPointEmpty` 使用重量和托盘号判断空点，部分实现使用重量 `< 1.5`。
- 基础出库逻辑使用对应重量 `>= 0.4` 判定装载点有货。
- 自动下架选择装载点使用 `BoxWeightA/BoxWeightB < 2` 且 `LoadingPoint_Status.CurrentPalletNumber` 为空；两个重量都 `> 2` 或两个托盘号都不为空时跳过。
- 入库业务版本使用 `BoxWeightA/BoxWeightB <= 1` 且对应点为空判定装载点无货；另一个基础版本的重量判断被注释。

装载点 `0` 对应重量 A，装载点 `1` 对应重量 B。新系统必须把阈值配置化、统一单位，并在现场确认后写入设备契约；不能直接选择旧代码中的任意一个阈值。

## 3. 手工/API 入库规则

### 3.1 按位置入库

入口：`POST api/plc-operations/inbound`，最终调用 `InboundOperation(plcId, shelf, position, loadingPoint)`。

前置条件：

1. 目标库位存在且 `ShelfStatus == 0`。
2. 指定装载点必须有货。
3. PLC 存在、可连接，且货叉无货。
4. PLC `OperationResult == 0` 时直接下发；非零时部分实现会登记后台预上架任务，等待 PLC 空闲。

设备时序：设置 `22000 = 0`、`22002 = 货架`、`22003 = 储位`、`22001 = 装载点`，写 `22008 = 1` 触发，约 1.5 或 2.5 秒后写回 `22008 = 0`。

接口返回成功主要表示参数写入/触发成功，不等价于设备实际完成。业务单据入口还会写旧 ERP 单据操作结果，不能带入新 WMS。

### 3.2 按托盘和按单据入库

- `inbound-by-tray` 先用托盘号查询现有库位，再复用按位置入库；这要求托盘在库位记录中已经存在，语义与“装载点新托盘入库”并不一致。
- `inbound-by-bill-tray` 额外传递 `BillID/BillNO/ITM/QTY/REM`，旧服务会调用 ERP 业务回写逻辑。
- `DocumentOperationController` 的单据上架入口依赖 `WMS_上架处理` 等存储过程先分配设备参数，再调用 PLC。

新 WMS 应把“收货托盘待上架”和“已有托盘回库”拆成明确业务状态，不按旧接口名称推断库存已经入账。

## 4. 手工/API 出库规则

### 4.1 按位置出库

入口：`POST api/plc-operations/outbound`，最终调用 `OutboundOperation(plcId, shelf, position, loadingPoint)`。

前置条件：

1. 源库位存在且 `ShelfStatus == 1`。
2. 目标装载点为空。
3. PLC 存在并可连接，`OperationResult == 0`。
4. 货叉无货。
5. 目标装载点对应重量低于旧逻辑的占用阈值（基础入口使用 `0.4`）。

设备时序：设置 `22000 = 1`、`22002 = 货架`、`22003 = 储位`、`22001 = 装载点`，写 `22009 = 1` 触发，约 2.5 秒后写回 `22009 = 0`。

普通入口会启动后台完成监控：约每 400ms 读取一次状态，最多 750 次（约 5 分钟）；当前实现直接把 `OperationResult == 6` 或 `0` 视为完成，并将出库前捕获的托盘号写入 `LoadingPoint_Status.CurrentPalletNumber`。它没有记录“先观察到运行再回到 0”，与自动服务和盘点服务的完成判断不一致；该差异必须在新系统统一设计并现场确认。

### 4.2 按托盘和按单据出库

- `outbound-by-tray` 先按托盘号定位库位，再调用按位置出库。
- `outbound-by-bill-tray` 还会调用 `IBillOperationService.UpdateBillOperation` 更新 ERP 单据。
- 出库前必须捕获托盘号；设备完成后旧库位托盘字段可能已经被清空，不能在完成回调中再从库位读取托盘号。
- `DocumentOperationController` 的单据下架入口依赖 `WMS_下架处理` 存储过程。

新 WMS 应将“设备下架完成”“装载点确认”“出库复核”“库存扣减”拆成状态，不把旧接口的同步 `IsSuccess` 直接映射为库存扣减。

## 5. 移库规则

入口：`POST api/plc-operations/transfer` 和 `POST api/plc-operations/transfer-by-tray`。

按位置移库的预期规则：

1. 目标库位存在且为空。
2. 源库位存在且有货。
3. PLC 空闲，`OperationResult == 0`，货叉无货。
4. 源、目标位置定位到同一 PLC。

设备时序：设置 `22000 = 2`、`22004 = 源货架`、`22005 = 目标货架`、`22006 = 源储位`、`22007 = 目标储位`，写 `22010 = 1` 触发，约 2.5 秒后写回 `22010 = 0`。

旧实现缺陷和歧义：

- `TransferOperation` 的源库位检查错误地使用了 `inShelf` 配合源位置，而不是 `outShelf`；新系统禁止照搬。
- `transfer-by-tray` 要求源托盘和目标托盘都已存在，但按位置移库又要求目标库位为空，接口语义冲突，必须现场确认。
- 源库位置空、目标库位置货和库存位置更新代码大多被注释。
- 旧接口成功主要表示触发写入成功，不保证移库设备完成，也没有统一原子更新两侧库位和库存。

新 WMS 的移库必须锁定源库位、目标库位和托盘，设备完成后在单一事务中完成两侧库位、托盘位置和库存流水更新；目标托盘不得作为“空目标库位”的必填条件。

## 6. 自动入库、自动出库

Windows 服务每轮先读取 PLC 状态并写回旧数据库；只有 `AutoRun == true` 且 `OperationResult == 0` 才查询 `SDL_GetNextTask @PlcID` 获取一条自动任务。`OperationType == 0` 为上架，`OperationType == 1` 为下架。

自动下架：

- 读取 `LoadingPoint_Status` 的 0、1 号点。
- 两点重量都大于 2 或两个 `CurrentPalletNumber` 都不为空时不下发。
- 优先选择 0 号点（重量 A 小于 2 且托盘号为空），否则选择 1 号点（重量 B 小于 2 且托盘号为空）。
- 先写 `LocationOperationLogs` 获取 `OperationID`，再下发出库寄存器。
- 启动两个后台监控：一个写 ERP 单据完成时间，一个同步托盘到装载点；监控间隔 400ms，最大 750 次。

自动上架：

- 使用任务中的 `Shelf/Position/PLCLocationCode` 作为货架、储位和装载点。
- 记录操作日志后写入库寄存器；旧代码对装载点越界只记录警告，未统一拒绝。
- 通过 `UPDATE_BillDetail_Operation` 等旧存储过程回写业务单据。

新 WMS 不使用 `SDL_GetNextTask` 或 `UPDATE_BillDetail_Operation` 作为业务真相；自动调度改为 WMS 自有任务队列，设备结果通过兼容层回传。

## 7. 盘点范围和执行规则

入口：`POST api/inventory-check/outbound-range`。

范围解析：

1. PLC 编号转大写，只允许 `A1` 到 `A8`。
2. 托盘格式为 `prefix-number`；起止前缀必须相同，起始编号不得大于结束编号。
3. 从 `LocationManagements` 筛选指定 PLC、`ShelfStatus == 1`、托盘非空、前缀一致且编号位于闭区间的库位。
4. 按托盘编号、货架、储位升序生成明细；没有任何占用位置返回 404。
5. 同一 PLC 只能有一个活动盘点下架任务；创建新任务会取消旧的 Pending/Running 任务。

盘点下架：

- 任务状态为 `Pending -> Running -> Completed/CompletedWithErrors/Failed/Canceled`。
- 明细初始 `OutboundStatus = Pending`、`InboundStatus = NotReady`，并保存原始库位状态。
- 明细按保存后的数据库 ID 顺序执行；每条明细优先等待装载点 0，再等待装载点 1，两个都占用时持续轮询，不跳过货框。
- PLC 离线、连接失败或设备忙时每秒重试；下发成功后仍等待真实完成。
- 每 400ms 查询状态，最多约 5 分钟；当前盘点实现直接把 `OperationResult == 6` 或 `0` 视为完成，未记录运行进度；与自动服务的“观察到非零后才接受回到 `0`”实现不一致，不能直接继承。
- 完成后把出库前托盘号写入选定装载点；成功明细变为 `Succeeded/Ready`，失败明细变为 `Failed/NotReady`。
- 只要有失败，任务状态为 `CompletedWithErrors`；取消时任务和未完成明细标记为 `Canceled`。

盘点上架只能逐条预约：只有 `OutboundStatus == Succeeded` 且存在出库装载点的明细可以调用 `schedule-inbound`；已预约或运行中的明细不得重复预约。旧实现的 `Scheduled` 更接近“已发送入库指令”，不等价于 PLC 已完成。

## 8. 规则分类和新系统处理原则

### 保留为兼容基线

- PLC 编号、货架/储位/装载点参数含义和寄存器地址映射。
- 入库、出库、移库的设备工作模式及触发时序。
- `ShelfStatus` 的现场含义、托盘号格式和盘点范围排序。
- 装载点 0/1 与重量 A/B 的对应关系。
- 出库完成可能需要观察“运行后回到空闲”的现场兼容判断。

### 必须在新系统重设计

- 单据、库存、托盘和库位的独立数据模型及所有权。
- 任务幂等、资源锁、状态历史、超时、恢复和人工处置。
- 设备完成与业务完成的事务边界。
- 入库待上架、出库锁定/复核、移库原子更新、盘点差异调整。
- 装载点重量阈值、空点判定和完成码的统一配置与现场确认。

### 禁止继承

- `SDL_GetNextTask`、`WMS_上架处理`、`WMS_下架处理`、`UPDATE_BillDetail_Operation` 等 ERP/旧业务存储过程。
- PLC 服务直接修改 WMS 库存或业务单据。
- 旧代码中被注释的库位状态更新作为“已实现”规则。
- 移库源库位检查中的 `inShelf` 缺陷和按托盘移库的目标托盘语义。

## 9. 待现场确认项

- 各 PLC 实际 `OperationResult` 状态码的完整含义，尤其是 `0`、`4`、`6` 和异常码。
- 入库、出库和自动任务使用的重量阈值及重量单位。
- 装载点托盘号、重量和 `ShelfStatus` 的真实更新时间点。
- 设备完成信号是否稳定可读，是否必须保留“运行后回到 0”的兼容判断。
- 目标托盘是否允许在移库接口中出现，以及跨 PLC/跨巷道移库边界。
- PLC 忙时入库任务登记后由谁调度、取消和恢复。
- 盘点期间是否冻结相关出入库和移库任务。

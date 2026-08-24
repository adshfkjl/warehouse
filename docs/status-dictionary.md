# 第一版范围与状态词典

本词典冻结新 WMS 第一版的业务范围、入口优先级和状态语义。状态名称是业务/API/数据库之间的契约；实现时不得用一个通用的 `Succeeded` 覆盖取消、设备未知结果或人工处置。

## 1. 版本和门禁

| 项目 | 值 |
| --- | --- |
| 词典版本 | `0.1` |
| 对应设计书 | `PROJECT_DESIGN.md` 版本 `0.9` |
| 自动化状态 | `AGENT_VERIFIED` 待代码和契约测试实现后复核 |
| 业务确认 | `HUMAN_PENDING` |
| 现场设备确认 | `FIELD_PENDING` |

状态词典只冻结业务语义，不代表现场 PLC 的完成码已经确认。设备相关未知事实必须保持 `BLOCKED`。

## 2. 通用状态规则

1. 所有状态变化必须写状态历史，至少包含实体、前状态、后状态、操作者/系统身份、原因、错误码、幂等键和时间。
2. 终态不能被普通更新覆盖；需要更正时创建新的业务操作或人工处置记录。
3. `Canceled` 只表示业务取消。设备已经进入 `SentToPlc` 或 `Executing` 后只能请求停止，不能直接变为 `Canceled` 并释放物理资源。
4. `PhysicalStateUnknown` 表示设备可能已经动作但 WMS 无法确认；不得自动重试、释放库位/托盘/库存锁或扣减库存。
5. `ManualIntervention` 表示进入人工处置，不等于成功。只有填写物理位置、托盘、源/目标库位、设备状态、原因并通过二次授权后，才能产生“人工确认物理结果并结案”的专用操作。
6. `Succeeded` 只能由设备结果和业务提交事务共同证明；不能由接口 HTTP 200、寄存器写入成功或人工按钮单独产生。
7. `Exception` 是业务单据的异常态；设备任务的物理未知和人工处置仍使用独立状态，并关联异常工作项。

## 3. 入库单状态

### 3.1 状态集合

`Draft`、`Receiving`、`Received`、`PutawayQueued`、`Completed`、`Canceled`、`Exception`。

### 3.2 合法流转

```text
Draft -> Receiving -> Received -> PutawayQueued -> Completed
   |         |           |             |
   +------> Canceled     +----------> Exception
                                      |
                                      +------> PutawayQueued | Canceled
```

| 当前状态 | 允许的下一状态 | 约束 |
| --- | --- | --- |
| `Draft` | `Receiving`, `Canceled` | 未收货前可取消 |
| `Receiving` | `Receiving`, `Received`, `Canceled`, `Exception` | 数量不能超过单据明细；重复收货必须幂等 |
| `Received` | `PutawayQueued`, `Canceled`, `Exception` | 数量进入待入库库存，不等于库位实存 |
| `PutawayQueued` | `Completed`, `Exception`, `Canceled` | 只有设备任务未下发时允许直接取消 |
| `Exception` | `PutawayQueued`, `Canceled`, `ManualIntervention` | 处置动作必须留痕 |
| `Completed` | 无 | 如需更正，走库存调整或反向业务单据 |
| `Canceled` | 无 | 不得恢复原单直接继续执行 |

`ManualIntervention` 是跨实体人工处置标记；入库单进入该标记后必须有异常工作项和授权记录。

## 4. 出库单状态

### 4.1 状态集合

`Draft`、`Allocated`、`Locked`、`Picking`、`AwaitingReview`、`Completed`、`Canceled`、`Exception`。

### 4.2 合法流转

```text
Draft -> Allocated -> Locked -> Picking -> AwaitingReview -> Completed
  |          |          |          |              |
  +------> Canceled     +--------> Exception <-----+
```

| 当前状态 | 允许的下一状态 | 约束 |
| --- | --- | --- |
| `Draft` | `Allocated`, `Canceled` | 需求和明细校验通过后才能分配 |
| `Allocated` | `Locked`, `Draft`, `Canceled`, `Exception` | 分配不足不得伪造完成 |
| `Locked` | `Picking`, `Canceled`, `Exception` | 未下发设备任务时取消可释放锁 |
| `Picking` | `AwaitingReview`, `Exception` | 设备失败、超时、未知结果不得直接扣库存 |
| `AwaitingReview` | `Completed`, `Exception` | 必须确认装载点托盘号、重量和物理状态 |
| `Exception` | `Picking`, `AwaitingReview`, `Canceled`, `ManualIntervention` | 重新执行前必须完成对账 |
| `Completed` | 无 | 库存扣减和托盘流转已在短事务中提交 |
| `Canceled` | 无 | 取消不等于设备停止 |

## 5. 移库单状态

状态集合：`Draft`、`Allocated`、`Queued`、`Executing`、`Completed`、`Canceled`、`Exception`、`ManualIntervention`。

```text
Draft -> Allocated -> Queued -> Executing -> Completed
  |          |           |          |
  +------> Canceled      +------> Exception -> Allocated | ManualIntervention
```

- `Allocated` 必须同时锁定源库位、目标库位和托盘。
- `Queued` 未下发时可以取消并释放锁。
- `Executing` 取消只能转入设备任务的 `StopRequested`，不能直接释放资源。
- 目标库位为空是业务约束；目标托盘不是必填条件，禁止继承旧接口的目标托盘语义冲突。
- `Completed` 只在设备成功后以单一短事务更新两侧库位、托盘位置和库存流水。

## 6. 盘点任务和明细状态

### 6.1 盘点任务

状态集合：`Draft`、`Pending`、`Running`、`Completed`、`CompletedWithErrors`、`Failed`、`Canceled`、`Exception`。

```text
Draft -> Pending -> Running -> Completed
                         |-> CompletedWithErrors
                         |-> Failed | Canceled | Exception
```

同一 PLC 的活动盘点任务只能有一个。取消只影响未完成明细；已经下发设备任务的明细遵守设备停止和物理未知规则。

### 6.2 盘点明细

- `OutboundStatus`：`Pending` -> `Running` -> `Succeeded`/`Failed`/`Canceled`。
- `InboundStatus`：`NotReady` -> `Ready` -> `Scheduled` -> `Running` -> `Succeeded`/`Failed`；未完成下架的明细不得预约上架。
- `Scheduled` 只表示已预约/已发送，不表示 PLC 已完成。
- 盘点差异不得直接改余额；必须经过复盘、授权和库存调整流水。

## 7. 库存状态

状态集合：`Available`、`Locked`、`PendingInbound`、`PendingOutbound`、`Frozen`、`Exception`。

| 当前状态 | 合法操作/下一状态 | 说明 |
| --- | --- | --- |
| `PendingInbound` | `Available`, `Exception` | 收货后等待上架；设备未确认前不能算库位实存 |
| `Available` | `Locked`, `PendingOutbound`, `Frozen`, `Exception` | 增减必须有来源单据和流水 |
| `Locked` | `Available`, `PendingOutbound`, `Exception` | 只由持有锁的任务释放或转移 |
| `PendingOutbound` | `Available`, `Frozen`, `Exception` | 设备失败/未知不能直接扣减 |
| `Frozen` | `Available`, `Exception` | 盘点、质量或人工冻结期间禁止普通作业 |
| `Exception` | `Available`, `Frozen`, `Locked` | 需经异常处置或库存调整，不可静默修复 |

每次增加、减少、锁定、解锁、移库、冻结、解冻和调整都写不可篡改库存流水；余额必须可由流水重算。

## 8. 设备任务状态

### 8.1 状态集合

`Created`、`Allocated`、`Queued`、`Dispatching`、`SentToPlc`、`Executing`、`Succeeded`、`Failed`、`TimedOut`、`Canceled`、`CancelRequested`、`StopRequested`、`StopConfirmed`、`StopFailed`、`PhysicalStateUnknown`、`ManualIntervention`。

### 8.2 合法流转

```text
Created -> Allocated -> Queued -> Dispatching -> SentToPlc -> Executing -> Succeeded
   |          |          |           |             |            |
   +------> Canceled     +--------> Failed      Failed       Failed/TimedOut
                                                    |            |
                                                    +------> PhysicalStateUnknown

Queued/Dispatching -> Canceled
SentToPlc/Executing -> StopRequested -> StopConfirmed | StopFailed | PhysicalStateUnknown
PhysicalStateUnknown -> Executing | Succeeded | Failed | ManualIntervention
```

| 状态 | 语义和限制 |
| --- | --- |
| `Dispatching` | 已在短事务中记录发送尝试，设备结果尚未确认；服务崩溃或请求超时不得假设未发送 |
| `SentToPlc` | 旧接口接受了请求，但不等于物理动作完成 |
| `Executing` | 已观察到设备执行，资源保持锁定 |
| `TimedOut` | 达到软件等待上限；若无法按任务号查询，必须同时进入 `PhysicalStateUnknown` 处置 |
| `CancelRequested` | 业务请求取消，尚未证明设备停止；不释放物理资源 |
| `StopRequested` | 已向设备请求停止 |
| `StopConfirmed` | 设备明确确认停止，随后由业务决定释放或重建任务 |
| `StopFailed` | 停止未确认，保持资源并进入异常工作项 |
| `PhysicalStateUnknown` | 设备可能已动作；禁止自动重试、释放和库存提交 |
| `ManualIntervention` | 人工确认物理结果并结案的工作状态，不是成功别名 |

设备命令只有在设备具备任务号去重和查询能力且能力已确认时才可自动重试；否则 `Dispatching`/发送超时后的安全结果是 `PhysicalStateUnknown`。

## 9. 第一版范围

### 9.1 包含

- 仓库、库区、巷道、货架、库位、装载点、设备、物料、容器和托盘基础资料。
- 独立运行的入库、收货、待入库库存、库位分配和上架闭环。
- 独立运行的出库、库存分配/锁定、下架、装载点确认和复核闭环。
- 库内移库、托盘位置更新、盘点范围、盘点差异和库存流水。
- 现有 PLC API 兼容适配器、模拟 PLC、设备契约、任务调度、异常工作项、重启恢复和审计。
- 手工/PDA 建单、Excel 批量导入、版本化本地 API、用户角色和高风险操作授权。
- 基础库存、任务、设备在线率、报警、盘点差异和操作审计报表。

### 9.2 排除或后置

- 采购、销售、生产、财务结算、成本、发票和 CRM/供应商业务。
- ERP/MES 数据库直连、旧 ERP 存储过程作为业务真相、外部系统直接改库存。
- 在没有现场确认的情况下重定义 PLC 寄存器、设备时序、完成码、重量阈值或急停流程。
- 跨仓网络调度、自动路径优化、机器人群控和高级预测分析；这些另立需求和计划。

## 10. 入口优先级和数据所有权

1. **手工/PDA（优先级 1）**：用于现场即时作业，调用与其他入口相同的 WMS 应用服务；PDA 不直接控制寄存器。
2. **Excel 导入（优先级 2）**：用于批量建单，必须先完成模板版本、整批校验、错误报告和文件摘要幂等；成功导入只创建 WMS 单据，不直接下发 PLC。
3. **外部 API/消息（优先级 3，可选）**：使用版本化契约、来源标识和幂等键；外部不可用时 WMS 仍可独立运行，失败进入待同步队列。

优先级只决定同一资源发生竞争时的入队顺序，不允许绕过库存锁、权限或任务状态机。WMS 自有业务单据、库存、托盘、库位和任务是唯一业务真相；外部来源不能直接覆盖本地状态。相同业务单号或幂等键冲突必须返回可诊断错误，不能重复创建或重复下发设备任务。

## 11. 门禁

- `AGENT_VERIFIED`：词典、范围和合法流转已文档化并通过静态审查。
- `HUMAN_PENDING`：负责人尚未确认第一版业务范围、状态含义和入口优先级。
- `FIELD_PENDING`：真实 PLC、急停、断网、断电、账实和恢复演练尚未执行。
- 只有负责人签字后才能追加 `HUMAN_CONFIRMED`；真实设备回归和账实核对完成后才能追加 `FIELD_VERIFIED`。

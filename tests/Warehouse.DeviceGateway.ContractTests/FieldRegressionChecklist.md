# 现场设备回归清单

本清单是 Task 7.2 的现场验证模板。当前只允许执行“模拟 PLC/模拟网关”部分；真实设备项目全部保持 `BLOCKED` 或 `FIELD_PENDING`，不得由自动化测试冒充现场签字。

## A. 模拟回归（可由 Agent 验证）

| 编号 | 场景 | 期望结果 | 自动化证据 | 状态 |
| --- | --- | --- | --- | --- |
| A-01 | Accepted/Executing/Succeeded | 任务按合法状态迁移，设备结果版本单调 | `TaskSchedulerTests`、设备契约测试 | `AGENT_VERIFIED` |
| A-02 | `Offline` | 能力不足时不自动重试，任务失败/异常路径可诊断 | `TaskSchedulerTests`、模拟网关场景 | `AGENT_VERIFIED` |
| A-03 | 超时/查询不到结果 | `TimedOut -> PhysicalStateUnknown`，不释放未确认资源 | `TaskRecoveryTests` | `AGENT_VERIFIED` |
| A-04 | 任务号查询能力 | Worker 重启查询原任务，不重复提交 | `TaskRecoveryTests` | `AGENT_VERIFIED` |
| A-05 | 重复提交 | 同幂等键返回同一结果，不产生第二次物理动作 | `SimulatedDeviceGatewayTests`、任务并发测试 | `AGENT_VERIFIED` |
| A-06 | 轮询与回调重复/乱序 | 相同或旧结果版本被去重，不覆盖新状态 | `TaskSchedulerTests`、Inbox 契约测试 | `AGENT_VERIFIED` |
| A-07 | 停止请求成功/失败 | 仅 `StopConfirmed` 才能进入取消；失败/未知保持资源 | 任务取消和设备契约测试 | `AGENT_VERIFIED` |
| A-08 | 入库/出库/移库模拟闭环 | 设备确认前不错误改变库存，成功后按业务短事务处理 | 各业务集成测试 | `AGENT_VERIFIED` |

执行命令：

```powershell
dotnet build Warehouse.Wms.sln --no-restore
dotnet test tests/Warehouse.DeviceGateway.ContractTests/Warehouse.DeviceGateway.ContractTests.csproj --no-build --no-restore
dotnet test tests/Warehouse.Wms.UnitTests/Warehouse.Wms.UnitTests.csproj --no-build --no-restore --filter "FullyQualifiedName~Tasks|FullyQualifiedName~Inbound|FullyQualifiedName~Outbound|FullyQualifiedName~Relocation"
dotnet test tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~Tasks|FullyQualifiedName~Inbound|FullyQualifiedName~Outbound|FullyQualifiedName~Relocation"
```

## B. 现场回归（必须人工执行）

| 编号 | 场景 | 前置安全条件 | 证据 | 当前状态 |
| --- | --- | --- | --- | --- |
| F-01 | 只读 PLC 状态/报警/装载点 | 网络隔离、无写入权限 | 状态快照、负责人签字 | `FIELD_PENDING` |
| F-02 | 单库区单托盘入库 | 急停可用、人员隔离、双跑无差异 | 任务号、托盘/库位对账 | `BLOCKED` |
| F-03 | 单托盘出库和复核 | 装载点确认、重量校验 | 复核单、设备日志 | `BLOCKED` |
| F-04 | 单托盘移库 | 源/目标库位和跨设备能力已确认 | 位置对账、状态历史 | `BLOCKED` |
| F-05 | 急停/停止请求 | 安全负责人在场，设备规程批准 | 急停记录、停止结果 | `BLOCKED` |
| F-06 | 断网/PLC 离线/发送超时 | 已批准网络隔离窗口 | 最后任务号、未知处置记录 | `BLOCKED` |
| F-07 | 断电/PLC 重启/WMS 重启 | 备份完成、回滚负责人在场 | 恢复日志、账实核对 | `BLOCKED` |
| F-08 | 报警复位和人工接管 | 报警字典、复位权限和二次授权已确认 | 报警位、原因、库存校正流水 | `BLOCKED` |

现场测试不得修改旧 PLC 寄存器、时序、完成码或报警逻辑。任何结果不确定、托盘位置不明、库位双占用、重复动作或账实差异都必须停止并转人工处置；未完成负责人签字前，不能把状态改为 `FIELD_VERIFIED`。

## C. 签字栏

| 角色 | 姓名 | 日期/窗口 | 签字 | 备注 |
| --- | --- | --- | --- | --- |
| 现场负责人 | `BLOCKED` | `BLOCKED` | `BLOCKED` | 批准/停止/回滚 |
| 仓储负责人 | `BLOCKED` | `BLOCKED` | `BLOCKED` | 账实和托盘库位 |
| 设备/PLC 负责人 | `BLOCKED` | `BLOCKED` | `BLOCKED` | 设备安全和报警 |
| WMS 运维负责人 | `BLOCKED` | `BLOCKED` | `BLOCKED` | 服务、数据库和证据 |
| 安全/质量负责人 | `BLOCKED` | `BLOCKED` | `BLOCKED` | 隔离和人员安全 |

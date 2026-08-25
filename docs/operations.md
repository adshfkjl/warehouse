# 运维与恢复演练

本文档对应 Task 7.1。当前报表和健康检查可以在开发环境独立运行；报表读取的是注入的内存快照，不能当作 SQL Server 生产报表。真实数据库、PLC 和消息总线接入前，所有演练必须使用测试数据库、模拟网关和临时目录。

## 启动和健康检查

```powershell
dotnet restore Warehouse.Wms.sln
dotnet build Warehouse.Wms.sln --no-restore
dotnet run --project src/Warehouse.Wms.Api/Warehouse.Wms.Api.csproj --urls http://127.0.0.1:5080
```

检查：

```powershell
Invoke-WebRequest http://127.0.0.1:5080/health/live
Invoke-WebRequest http://127.0.0.1:5080/health/ready
Invoke-WebRequest http://127.0.0.1:5080/api/reports/health
```

`/api/reports/health` 返回 API、数据库、Worker、设备网关和 Outbox/Inbox 五项状态。开发默认值表示数据库探针和模拟网关可用；没有真实数据库或 PLC 时不得把该结果解释为现场健康。

报表接口：

```text
GET /api/reports/inventory
GET /api/reports/locations/utilization
GET /api/reports/inbound-outbound
GET /api/reports/transfers
GET /api/reports/pallets/{palletCode}
GET /api/reports/stocktaking-differences
GET /api/reports/device-alarms
```

## 日常故障判断

| 状态 | 含义 | 处理 |
| --- | --- | --- |
| Healthy | 组件探针成功，且没有消息重放记录 | 继续观察日志和任务状态 |
| Degraded | Worker 心跳过期或发现消息重放 | 暂停自动重试，按幂等键、消息摘要和结果版本核对 |
| Unhealthy | 数据库或设备网关探针失败 | 不下发新 PLC 任务；保留未确认任务和资源锁，转人工处置 |

任务处于 `TimedOut`、`PhysicalStateUnknown` 时，不得重新下发或释放物理资源。先查询设备任务号；旧接口不能查询时，登记人工物理确认。消息重放只能通过 Inbox 的消息 ID、幂等键和结果版本去重。

## 无现场设备恢复演练

从项目根目录执行：

```powershell
.\scripts\recovery-drill.ps1
```

脚本只运行本地构建、自动化测试、迁移脚本生成和临时目录备份恢复，不读取生产连接串，不连接 PLC。演练证据写入临时输出目录，包含：

- 数据库迁移脚本可生成；
- 文件级备份可恢复；
- Worker 重启不会重复提交设备任务；
- 模拟设备离线、超时和未知结果进入对应状态；
- Outbox/Inbox 重复消息不重复处理。

## 数据库备份与恢复（现场前置）

生产数据库命令必须由数据库负责人按环境连接串执行。先生成并审阅幂等迁移脚本，再在维护窗口备份和恢复：

```powershell
dotnet ef migrations script --idempotent `
  --project src/Warehouse.Wms.Infrastructure/Warehouse.Wms.Infrastructure.csproj `
  --startup-project src/Warehouse.Wms.Api/Warehouse.Wms.Api.csproj `
  --output .artifacts/warehouse-migrations.sql
```

恢复完成后依次检查迁移版本、库位/托盘唯一约束、库存流水数量、任务状态历史和 Outbox/Inbox 未处理消息。数据库恢复不等于 PLC 物理恢复；对 `PhysicalStateUnknown` 任务仍需设备对账或人工确认。

## 门禁

- `AGENT_VERIFIED`：本地命令、模拟设备测试和恢复脚本通过。
- `HUMAN_PENDING`：运维负责人尚未审阅报表口径、备份保留期和恢复责任人。
- `FIELD_PENDING`：未执行真实 PLC 离线、急停、断电、网络中断和现场账实核对。

没有 `HUMAN_CONFIRMED` 和 `FIELD_VERIFIED` 证据时，不得把本演练当作生产切换批准。

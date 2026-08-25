# 外部集成契约

Task 8.1 定义 `/api/integrations/v1` 的可插拔边界。集成默认关闭，WMS 手工建单、库存、任务和盘点 API 不依赖外部系统。

## 消息约定

每个请求必须包含 `source`、`version`、`idempotencyKey` 和 `payload`；可选 `correlationId` 用于关联外部单号。当前只接受 `version: "v1"`。WMS 计算原始 `payload` 的 SHA-256 摘要并写入 Outbox，便于重试和审计。相同幂等键只产生一条待发布消息。

## 端点

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| POST | `/api/integrations/v1/inbound-notices` | 入库通知 |
| POST | `/api/integrations/v1/outbound-requests` | 出库请求 |
| POST | `/api/integrations/v1/cancel-requests` | 取消请求 |
| POST | `/api/integrations/v1/status-queries` | 状态查询 |
| POST | `/api/integrations/v1/result-callbacks` | 设备/外部结果回传 |
| POST | `/api/integrations/v1/inventory-sync` | 库存同步 |

接收端只验证契约、计算摘要并写入 Outbox，不直接调用 PLC、不直接修改库存，也不把外部回传视为设备完成。发布器必须根据幂等键、消息版本和结果版本去重；消息重试不能重复创建 WMS 业务单据或设备任务。Task 9.3 已将 SQL Server `IIntegrationOutbox` 接入 `Wms:PersistenceMode=SqlServer` 组合，并复用 Task 9.2A 的短事务消息仓储；同一幂等键且摘要一致时安全重放，摘要冲突拒绝。

关闭 `Wms:ExternalIntegrationsEnabled` 时端点返回 `404 INTEGRATION_DISABLED`，不会写入 Outbox；本地手工流程仍可用。`InMemory` 持久化模式继续使用内存 Outbox，适用于无数据库开发和契约测试。SQL Server outbox 已完成入口接入，但发布 Worker、外部系统真实联调、生产备份策略和 HUMAN/FIELD 门禁仍是后续工作。

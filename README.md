# 立体仓库管理系统

面向自动化立体仓库的独立 WMS，覆盖入库、出库、移库、盘点、库存、库位、托盘、设备任务和异常处置。系统以作业处理为中心，同时提供轻量实时监控、统计分析和库位点位查询。

## 系统能力

- 入库建单、收货、托盘绑定、库位分配和上架
- 出库建单、库存分配、下架、装载点确认和复核
- 移库、托盘位置更新和库存流水
- 全库、库区、物料、批次、托盘和库位盘点
- 库存余额、库存流水、库位容量和托盘位置查询
- 设备任务队列、状态跟踪、超时、取消、停止和异常处置
- 物理状态未知、人工确认和库存校正审计
- 用户、角色、仓库范围权限和高风险操作授权
- Excel 入库/出库导入，支持模板校验、错误报告和幂等处理
- 周期统计、趋势图表、任务 KPI 和异常统计
- 按仓库、库区、巷道、货架、层和库位查看实时点位物品

## 设计原则

- 不依赖 ERP、MES 或其他上游系统即可完成仓储作业。
- WMS 是业务单据、库存、托盘、库位和任务的唯一业务真相。
- 设备连接和现场时序由设备网关负责，业务模块不直接写 PLC 寄存器。
- 默认使用模拟设备，真实设备适配器必须通过部署配置显式启用。
- 库存和任务变化可追溯，关键操作保留状态历史、幂等记录和审计信息。
- 统计和点位页面是只读查询，不直接修改库存或触发设备动作。

## 系统组成

```text
浏览器 / PDA
       |
       v
WMS Web 与 API
       |
       +-- WMS 数据库（SQL Server）
       |
       +-- 设备网关（模拟设备或现有 PLC 接口适配器）
```

主要项目：

| 项目 | 用途 |
| --- | --- |
| `src/Warehouse.Wms.Web` | 管理工作台和 PDA 入口 |
| `src/Warehouse.Wms.Api` | WMS 业务 API、认证和健康检查 |
| `src/Warehouse.Wms.Application` | 入库、出库、库存、盘点、任务和报表应用服务 |
| `src/Warehouse.Wms.Domain` | 业务实体、状态机和值对象 |
| `src/Warehouse.Wms.Infrastructure` | SQL Server、后台 Worker 和外部适配器 |
| `src/Warehouse.DeviceGateway` | 模拟设备和现有 PLC 接口兼容层 |

## 本地运行

### 环境要求

- Windows 或 Linux
- .NET 8 SDK
- Docker Desktop（用于本地 SQL Server）

### 启动开发数据库

```powershell
docker compose -f docker-compose.dev.yml up -d
```

### 启动 API

```powershell
dotnet run --project src/Warehouse.Wms.Api --launch-profile http
```

API 默认地址：<http://localhost:5054>

健康检查：<http://localhost:5054/health/live>

### 启动管理界面

```powershell
dotnet run --project src/Warehouse.Wms.Web --urls http://localhost:5055
```

管理界面：<http://localhost:5055>

Web 已内置固定 YARP 2.2.0 同源代理：浏览器只访问相对 `/api/...` 和 `/health/api/...` 路径，生产部署必须配置受信任的 `ApiProxy:UpstreamBaseUrl`（生产 loopback 还需显式 `AllowLoopbackUpstream=true`）。本地预览时 Web 和 API 分别运行，Web 的 `/health/web/live` 与代理后的 `/health/api/live` 用于区分两层健康状态。

固定 `5054`/`5055` 只服务于本地开发双进程冒烟；自动化代理验收始终让临时 API 和 Web 分别监听 Kestrel `127.0.0.1:0`，覆盖真实同源 HTTP 转发而不占用开发端口。

### 数据库迁移

开发数据库连接和迁移方式见 [`docs/development.md`](docs/development.md)。数据库结构通过 EF Core 迁移管理，不需要手工修改表结构。

## 使用文档

- [用户操作说明](docs/user-guide.md)
- [运行与运维说明](docs/operations.md)
- [接口契约](docs/integration-contract.md)
- [现场基线说明](docs/field-baseline.md)
- [试运行手册](docs/pilot-runbook.md)
- [回滚手册](docs/rollback-runbook.md)
- [系统设计书](PROJECT_DESIGN.md)

## 设备与现场边界

开发环境不连接真实 PLC，也不修改现场控制程序。现有 PLC 的连接方式、寄存器定义、库位和设备时序仅通过兼容适配器接入；在接口契约、模拟设备和现场回归验证完成前，不启用真实设备配置。

正式部署前还需要完成业务负责人确认、真实设备回归、异常恢复演练和库存账实核对。

## 许可证

许可证和部署授权以项目所有者发布的版本为准。

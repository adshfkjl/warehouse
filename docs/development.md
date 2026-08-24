# 本地开发与质量门禁

## 适用范围

本文件只描述本地开发环境。默认使用模拟设备网关，不连接生产 PLC、生产数据库或 ERP。示例密码只用于本机 Docker 容器，不能用于任何共享或生产环境。

## 前置条件

- .NET 8 SDK
- PowerShell 7（Windows PowerShell 5.1 也可运行基础脚本）
- Docker Desktop 及 Compose（执行 SQL Server 数据库迁移和集成测试时需要）
- EF Core CLI `dotnet-ef` 8.x（可使用仓库本地 `.codex-tools/dotnet-ef`，或安装到用户工具目录）

## 配置

复制 `src/Warehouse.Wms.Api/appsettings.example.json` 为本地未提交的配置，或通过环境变量设置：

```powershell
$env:Wms__DeviceGatewayMode = "Simulator"
$env:Wms__ExternalIntegrationsEnabled = "false"
```

ERP 连接串可以缺省。未配置真实 PLC 时必须保持 `Simulator`；不要把真实地址、密码或密钥写入仓库。

## 快速验证

在仓库根目录执行：

```powershell
dotnet restore Warehouse.Wms.sln
dotnet build Warehouse.Wms.sln --no-restore
dotnet test Warehouse.Wms.sln --no-build --no-restore
pwsh -File scripts/verify.ps1
```

`verify.ps1` 固定按 restore、build、test、迁移检查、健康检查、旧目录保护的顺序执行。编译或测试失败会以退出码 `1` 终止；当前尚无迁移时数据库检查标记为“不适用”并继续健康检查；存在迁移但缺少 Docker、Docker daemon 或 EF CLI 时记录 `BLOCKED`，以退出码 `2` 结束，不能被当作迁移通过。

## 数据库与迁移

持久化任务完成后，启动仅绑定本机回环地址的开发数据库：

```powershell
docker compose -f docker-compose.dev.yml up -d
dotnet ef database update --project src/Warehouse.Wms.Infrastructure --startup-project src/Warehouse.Wms.Api
```

开发数据库凭据和端口只在 `docker-compose.dev.yml` 中定义。清理本机数据：

```powershell
docker compose -f docker-compose.dev.yml down -v
```

Task 3.1 已包含 `src/Warehouse.Wms.Infrastructure/Migrations/` 首个基础资料迁移。验证脚本会检查迁移目录；执行数据库更新仍需要本地 Docker SQL Server、可用的 `dotnet-ef` 和开发连接串。缺少这些条件时必须标记为 `BLOCKED`，不得通过创建空迁移或跳过实际迁移执行伪造成功。

## 健康检查

启动 API（不需要数据库、PLC 或 ERP）：

```powershell
dotnet run --project src/Warehouse.Wms.Api --no-launch-profile
```

然后确认 `/health/live` 和 `/health/ready` 返回 HTTP 200。`verify.ps1` 会自动使用本机临时端口执行同样的检查并在结束时停止 API 进程。

## 旧系统保护

`warehouse/` 目录是只读参考源码。质量门禁会执行 `git diff --name-only -- warehouse`；该命令必须无输出。任何 PLC 时序、寄存器或旧接口的修改都必须另立任务、先完成契约和回滚验证。

# 立体仓库管理项目

面向自动化立体仓库的 PLC 通信、库存作业与后台管理项目，包含 ASP.NET Core API 和 Windows PLC 服务两个部分。

## 项目结构

| 目录 | 说明 |
| --- | --- |
| `PLCManagment/` | 仓库管理后端，包含 ASP.NET Core API、业务服务、EF Core 数据访问、数据库脚本及测试 |
| `PlcManagementService/` | Windows PLC 通信服务，负责 Modbus 通信、状态监控和数据库交互 |

## 技术环境

- Windows
- .NET SDK 9.0.203（API 目标框架为 .NET 8.0）
- .NET Framework 4.8（Windows 服务）
- SQL Server
- Visual Studio 2022

## 本地配置

仓库不包含真实数据库密码和 JWT 密钥。首次运行前，请复制配置模板并填写本地参数：

```powershell
Copy-Item PLCManagment/API/appsettings.example.json PLCManagment/API/appsettings.json
Copy-Item PlcManagementService/App.example.config PlcManagementService/App.config
```

需要配置的主要内容：

- SQL Server 地址、数据库名、用户名和密码
- JWT 签名密钥
- PLC/Modbus 连接参数

请勿将真实凭据提交到 Git。

## 运行 API

```powershell
dotnet restore PLCManagment/API/PLCManagement.API.csproj
dotnet run --project PLCManagment/API/PLCManagement.API.csproj
```

启动后可根据本地配置访问 Swagger 页面。

## Windows 服务

使用 Visual Studio 打开 `PlcManagementService/PlcManagementService.sln`，还原 NuGet 包后编译。部署前请确认 `App.config` 中的数据库和 PLC 参数正确。

## 数据库

数据库脚本位于 `PLCManagment/Database/`。执行脚本前请先备份目标数据库，并按环境顺序核对脚本内容。

## 安全说明

日志、构建产物、历史压缩包、本地配置及依赖缓存已通过 `.gitignore` 排除。

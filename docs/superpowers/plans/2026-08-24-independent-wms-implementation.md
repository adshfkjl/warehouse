# 独立立体仓库 WMS Implementation Plan

> **For agentic workers:** When available, use `superpowers:subagent-driven-development` or `superpowers:executing-plans`; otherwise follow the single-Task protocol in this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在保留现场 PLC 接口稳定性的前提下，建设一个不依赖 ERP/MES、能够独立完成仓储全流程的模块化单体 WMS。

**Architecture:** 新 WMS 拥有基础资料、业务单据、库存账、任务调度、权限和报表。第一版 `Warehouse.DeviceGateway` 是 WMS Worker 内加载的基础设施类库，通过 HTTP 调用独立运行的旧 PLC API；它不是第三个独立服务，也不拥有业务库存。WMS 不直接写寄存器、不访问 ERP 数据库。PLC 执行期间不持有数据库长事务，设备结果通过能力分级的幂等策略、短事务状态提交、Outbox/Inbox、资源锁和重启对账回写 WMS。

**Tech Stack:** .NET 8 ASP.NET Core、EF Core、SQL Server、现有 PLC 接口兼容层、Modbus/PLC 模拟器、REST API、浏览器/PDA 前端。

---

## 一、执行方式和 Agent 协议

这份文件是总路线图，不允许 Agent 从第一项连续执行到现场上线。每次只执行一个 `Task`，完成后暂停，提交证据并等待人工批准进入下一项。

### 1.1 单 Task 执行协议

每个 Task 开始时必须：

1. 阅读本计划、`AGENTS.md`、相关设计章节和前置 Task 的验收证据。
2. 检查依赖、未确认的现场事实、数据库和工具版本；依赖未满足时停止，不得假设。
3. 先写或补充测试，再实现最小功能；不顺手重构无关模块。
4. 只修改本 Task 的文件范围；旧系统 `warehouse/` 只读。
5. 运行该 Task 明确列出的命令，记录退出码、测试数和失败信息。
6. 更新设计书、迁移、API 契约、测试和操作说明，使其保持一致。
7. 不自动进入下一 Task，不自动执行现场操作，不自动修改旧 PLC 时序。

每个 Task 结束时必须输出：

```text
任务编号：
修改文件：
数据库迁移：
新增或修改测试：
执行命令及结果：
门禁状态：AGENT_VERIFIED / HUMAN_CONFIRMED / FIELD_VERIFIED / BLOCKED
是否满足验收条件：
旧系统是否被修改：
已知风险：
需要人工确认：
建议下一步：
```

### 1.2 门禁定义

- `AGENT_VERIFIED`：代码、迁移、静态检查和自动化测试已由 Agent 运行并取得证据。
- `HUMAN_CONFIRMED`：负责人确认业务规则、状态含义、页面行为、权限和操作说明。
- `FIELD_VERIFIED`：在真实 PLC/设备上完成受控回归、断网/重启/异常恢复和账实核对。
- `BLOCKED`：依赖现场事实、设备响应或人工签字，Agent 不得自行推断继续。

阶段 0 和阶段 7.2 的现场事实必须由人员确认。没有现场签字、设备回归记录或责任人批准时，状态只能是 `BLOCKED`，不能标记为完成。

### 1.3 统一本地验收命令

所有“构建通过”必须至少执行：

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

数据库和 API 验收必须执行：

```powershell
docker compose -f docker-compose.dev.yml up -d
dotnet ef database update --project src/Warehouse.Wms.Infrastructure --startup-project src/Warehouse.Wms.Api
dotnet run --project src/Warehouse.Wms.Api
```

并验证 `/health/live` 和 `/health/ready` 返回 HTTP 200；未配置 PLC 时使用模拟网关；未配置 ERP 连接串时仍可启动；集成测试使用独立测试数据库；`git diff --name-only -- warehouse` 无输出。

### 1.4 阶段依赖

```text
阶段0 现场/范围冻结
  -> 阶段1 新系统骨架和质量门禁
  -> 阶段2 设备契约、旧接口适配器和模拟器
  -> 阶段3 基础资料、托盘和库存账
  -> 阶段4 任务状态机、调度器、锁和恢复
  -> 阶段5 入库 / 出库 / 移库业务闭环
  -> 阶段6 盘点、权限、审计和最小界面
  -> 阶段7 运维、报表、现场试运行
  -> 阶段8 可选外部集成
```

阶段 5 不得提前自建临时任务状态机；所有设备任务必须复用阶段 4 的状态机和调度器。

## 二、范围、数据和设备边界

### 2.1 第一版范围

第一版只覆盖：基础资料、收货/上架、下架/复核、库存、移库、盘点、任务调度、异常、权限、审计、必要报表和 PLC 兼容调用。

第一版不实现采购、销售、生产、财务结算、成本核算和供应商/客户业务流程。系统内部手工建单是主入口，Excel 导入和 API 是辅助入口；Excel 入库/出库导入必须完成 Task 5.1A 后才算第一版具备该入口。

### 2.2 数据所有权

| 数据 | 唯一业务所有者 |
| --- | --- |
| 物料、库区、货架、库位、托盘、业务单据、库存余额 | WMS |
| 业务任务、幂等键、状态历史、资源锁、异常和审计 | WMS |
| PLC 原始状态、寄存器、设备报警和现场时序 | 设备网关/旧 PLC 接口 |
| ERP/MES 数据 | 外部集成适配器，不是 WMS 运行前提 |

PLC/WCS 不得直接写 WMS 库存或业务单据。库存变化只能由 WMS 业务命令和库存流水产生。

### 2.3 旧程序保护规则

- `D:\projects\warehouse\warehouse` 只作为参考源码和现场基线来源。
- 新代码只能放在 `src/`、`tests/` 和 `docs/` 等新系统目录。
- 新 WMS 只能通过 `IWarehouseDeviceGateway` 调用旧 PLC API；业务层不得出现寄存器地址、NModbus 或 ERP 字段。
- 未完成契约测试、模拟器测试和现场回归前，不修改旧接口的指令时序。

### 2.4 第一版设备网关部署形态

- `src/Warehouse.DeviceGateway/` 编译为 WMS Worker 加载的类库，不创建 `Warehouse.DeviceGateway.Api` 或独立网关进程。
- `LegacyPlcApiGateway` 只通过 HTTP 调用独立运行的旧 PLC API；旧 PLC API 继续负责连接、寄存器和现场时序。
- 网关不拥有独立业务数据库；幂等命令、设备观察、任务状态和 Outbox/Inbox 持久化在 WMS 数据库。
- 网关健康检查作为 WMS 的依赖健康检查，同时检查旧 PLC API 的 HTTP 可达性和协议探测结果。
- 将来若拆为独立网关服务，必须另立计划，增加独立 API/Worker、部署、持久化、契约和恢复任务；本计划不实施该拆分。

## 三、阶段 0：现场和范围冻结

### Task 0.1：固化旧程序仓储规则

**Files:**
- Modify: `docs/legacy-warehouse-rules.md`
- Modify: `PROJECT_DESIGN.md` section `6.16`
- Reference: `warehouse/PLCManagment/API/Services/PlcService.cs`
- Reference: `warehouse/PLCManagment/API/Services/InventoryCheckService.cs`
- Reference: `warehouse/PlcManagementService/PLCService.cs`

- [x] 核对入库、出库、移库、自动任务和盘点范围规则，并为每条规则标注来源文件和行号。
- [x] 将不同重量阈值、完成码、移库缺陷和 ERP 依赖列入“现场确认/禁止继承”清单。
- [x] 更新设计书版本、日期和需求变更记录。
- [x] 运行 `rg -n "ERP|SDL_GetNextTask|WMS_上架处理|WMS_下架处理|UPDATE_BillDetail_Operation" docs PROJECT_DESIGN.md`，确认禁止继承项已登记。

**验收:** `AGENT_VERIFIED`；规则盘点文档完成，未确认事实没有被写成确定性业务规则。现场负责人未确认前不得进入 Task 0.2 的现场冻结门禁。

**执行记录（2026-08-25）:**

- 修改：`docs/legacy-warehouse-rules.md`、`PROJECT_DESIGN.md`、本计划文件。
- 验证：源码行号范围和关键规则关键字抽查通过；ERP/旧存储过程检索通过；`git diff --check` 通过；`git diff --name-only -- warehouse` 无输出。
- 审查修正：补充普通 API 出库监控、自动下架第二监控、按单据入库、单据服务、盘点状态常量及库位状态更新的源码行号，并修正文档对 `OperationResult == 0` 完成判断的描述。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（重量阈值、完成码、更新时间点等业务含义待负责人确认）；`FIELD_PENDING`（真实 PLC、账实和恢复演练未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 0.2：建立现场基线表

**Files:**
- Create: `docs/field-baseline.md`
- Reference: `warehouse/PlcManagementService/Models/ModbusAddress.cs`
- Reference: `warehouse/PlcManagementService/Services/ModbusService.cs`

- [x] 建立 PLC 编号、IP、端口、Slave ID、地址偏移、设备类型和装载点表。
- [x] 建立入库、出库、移库、复位、急停、完成、报警和心跳信号表。
- [x] 建立库位编码、托盘编码、重量单位、数量精度、并发数和超时表。
- [x] 对每个未知项填写责任人、确认方式、截止日期和阻塞影响；未知项使用 `BLOCKED`。

**验收:** `AGENT_VERIFIED` 只能表示文档结构和来源齐全；只有现场负责人签字后才追加 `HUMAN_CONFIRMED`。签字缺失时不允许 Agent 继续设备实现。

**执行记录（2026-08-25）:**

- 修改：`docs/field-baseline.md`、`PROJECT_DESIGN.md`、本计划文件。
- 验证：旧寄存器常量、连接参数、地址偏移、状态解析、心跳、读写超时、报警码、移库硬编码地址和库位/托盘字段来源已核对；来源矩阵覆盖 24 个基线主题；所有未确认现场参数均标记为 `BLOCKED`；未连接真实 PLC。
- 审查修正：补充旧 API 移库的模式 2、22004-22007/22010 写入来源，并保留心跳写入未显式加偏移的风险说明。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（设备清单、编码、计量和信号语义待负责人确认）；`FIELD_PENDING`（真实设备和恢复演练未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 0.3：冻结第一版范围和状态词典

**Files:**
- Modify: `PROJECT_DESIGN.md`
- Create: `docs/status-dictionary.md`

- [x] 定义入库、出库、移库、盘点、库存和设备任务的状态集合及合法流转。
- [x] 定义第一版包含和排除的业务类型。
- [x] 定义手工建单、Excel 导入和外部 API 的优先级。
- [x] 为取消、未知设备结果和人工结案保留独立状态，禁止用 `Succeeded` 代替。

**验收:** `AGENT_VERIFIED` 后由负责人完成 `HUMAN_CONFIRMED`；任何待开发功能都能映射到状态词典或被明确排除。

**执行记录（2026-08-25）:**

- 修改：`docs/status-dictionary.md`、`PROJECT_DESIGN.md`、本计划文件。
- 验证：状态集合、合法流转、取消/停止/物理未知/人工处置语义、第一版范围和入口优先级均已记录；`Dispatching` 已作为发送尝试状态纳入任务流转；未把 `Succeeded` 用作取消或人工结案别名。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（负责人尚未签字确认第一版范围和状态含义）；`FIELD_PENDING`（真实设备恢复和账实演练未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 四、阶段 1：新系统骨架和质量门禁

### Task 1.1：创建解决方案和项目边界

**Files:**
- Create: `src/Warehouse.Wms.Api/`
- Create: `src/Warehouse.Wms.Application/`
- Create: `src/Warehouse.Wms.Domain/`
- Create: `src/Warehouse.Wms.Infrastructure/`
- Create: `src/Warehouse.DeviceGateway/`
- Create: `tests/Warehouse.Wms.UnitTests/`
- Create: `tests/Warehouse.Wms.IntegrationTests/`
- Create: `tests/Warehouse.DeviceGateway.ContractTests/`

- [x] 创建 `Warehouse.Wms.sln` 和上述项目，所有命名空间统一使用 `Warehouse.Wms.*`。
- [x] 让 Domain 不引用 EF Core、数据库、HTTP、PLC 或 UI。
- [x] 让 Application 只依赖 Domain 和抽象接口。
- [x] 让 DeviceGateway 只暴露设备任务和设备状态接口。
- [x] 添加不含真实密钥、ERP 连接串和生产 PLC 地址的配置模板。
- [x] 运行统一本地验收命令和 `dotnet list package --vulnerable`。

**验收:** `AGENT_VERIFIED`；无数据库、无 PLC、无 ERP 时 API 可启动健康检查；旧 `warehouse/` 目录无变更。

**执行记录（2026-08-25）：**

- 修改：`Warehouse.Wms.sln`、`src/Warehouse.Wms.Api/`、`src/Warehouse.Wms.Application/`、`src/Warehouse.Wms.Domain/`、`src/Warehouse.Wms.Infrastructure/`、`src/Warehouse.DeviceGateway/`、`tests/`、`PROJECT_DESIGN.md`。
- 验证：`dotnet restore Warehouse.Wms.sln`（退出码 0）；`dotnet build Warehouse.Wms.sln --no-restore`（退出码 0，0 警告、0 错误）；`dotnet test Warehouse.Wms.sln --no-build`（退出码 0，3 个测试程序集共 3 个测试通过；集成测试程序集当前无测试，后续由集成任务补充）；`dotnet list Warehouse.Wms.sln package --vulnerable`（未发现漏洞包）；API `http://127.0.0.1:5087/health/live`、`/health/ready`、`/` 均返回 200；`git diff --check` 通过；`git diff --name-only -- warehouse` 无输出。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（项目边界和健康检查行为待负责人确认）；`FIELD_PENDING`（真实 PLC、账实和恢复演练未执行）。
- 已知风险：集成测试项目尚无测试用例；设备网关尚未实现真实协议适配；根目录 `.gitignore` 保留任务开始前的用户删除状态，未纳入本 Task 提交。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 1.2：建立本地环境和质量门禁

**Files:**
- Create: `.editorconfig`
- Create: `Directory.Build.props`
- Create: `docker-compose.dev.yml`
- Create: `docs/development.md`
- Create: `scripts/verify.ps1`

- [x] 在 `scripts/verify.ps1` 中按顺序执行 restore、build、test、迁移检查、健康检查和旧目录保护检查。
- [x] 配置测试失败即失败、编译警告策略和独立测试数据库。
- [x] 在 `docs/development.md` 写明模拟网关默认启用和 ERP 连接串可缺省。
- [x] 运行 `pwsh -File scripts/verify.ps1`，记录所有命令退出码为 0。

**验收:** `AGENT_VERIFIED`；新开发者可在空环境按文档运行统一验证脚本。

**执行记录（2026-08-25）：**

- 修改：`.editorconfig`、`Directory.Build.props`、`docker-compose.dev.yml`、`docs/development.md`、`scripts/verify.ps1`、`tests/Warehouse.Wms.UnitTests/QualityGateConfigurationTests.cs`、`PROJECT_DESIGN.md`。
- TDD：先以缺失文件和验证脚本结构测试得到预期失败；实现后脚本顺序、必需文件和 PowerShell 迁移判断回归测试通过。
- 验证：`dotnet restore Warehouse.Wms.sln`（退出码 0）；`dotnet build Warehouse.Wms.sln --no-restore`（退出码 0，0 警告、0 错误）；`dotnet test Warehouse.Wms.sln --no-build --no-restore`（退出码 0，Unit 5、DeviceGateway 1 通过，集成测试程序集当前无测试）；`dotnet list Warehouse.Wms.sln package --vulnerable`（退出码 0，未发现漏洞包）；`pwsh -NoProfile -File scripts/verify.ps1`（退出码 0，按 restore/build/test/migration/health/protection 顺序执行，健康端点 200，旧目录保护通过）；`docker compose config` 未执行，因本机未安装 Docker CLI。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（本地开发门禁和数据库模板待负责人确认）；`FIELD_PENDING`（真实数据库、PLC、账实和恢复演练未执行）。
- 已知风险：Docker CLI、Docker daemon、EF CLI 当前不可用；当前无迁移，因此脚本将数据库检查标记为不适用，后续持久化任务必须在有 Docker/EF 的环境重新执行迁移门禁；集成测试项目尚无测试用例。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 五、阶段 2：设备契约、旧接口适配器和模拟器

### Task 2.1：定义设备操作和值对象

**Files:**
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceTask.cs`
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceOperationResult.cs`
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceTaskState.cs`
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceCapability.cs`
- Create: `src/Warehouse.Wms.Application/Devices/IWarehouseDeviceGateway.cs`
- Test: `tests/Warehouse.DeviceGateway.ContractTests/DeviceTaskContractTests.cs`

- [x] 定义 `SubmitInboundAsync`、`SubmitOutboundAsync`、`SubmitTransferAsync`、`GetStatusAsync` 和 `TestConnectionAsync`。
- [x] 请求必须包含幂等键、WMS 任务号、设备编号、源/目标库位、装载点和协议版本。
- [x] 设备结果必须区分 `Accepted`、`Executing`、`Succeeded`、`Failed`、`TimedOut`、`Offline`、`Unknown`。
- [x] 将设备接口方法命名为 `RequestStopAsync`；未下发任务的业务取消由 WMS 状态机处理，不调用设备停止。
- [x] 定义 `DeviceCapability`：`TaskKeyDeduplication`、`TaskQuery`、`StopControl`、`CompletionCallback`。
- [x] 定义设备结果观察对象，包含设备任务号、结果版本、来源（`Polling`/`Callback`）和观测时间。
- [x] 为缺失幂等身份、结果观察和能力组合写契约测试，再实现最小契约。

**验收:** `AGENT_VERIFIED`；WMS 抽象中不出现寄存器地址、NModbus、ERP 表或存储过程名称，并明确旧接口能力不足时不得宣称“恰好执行一次”。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.Wms.Domain/Devices/DeviceTask.cs`、`DeviceTaskState.cs`、`DeviceCapability.cs`、`DeviceOperationResult.cs`、`src/Warehouse.Wms.Application/Devices/IWarehouseDeviceGateway.cs`、`tests/Warehouse.DeviceGateway.ContractTests/DeviceTaskContractTests.cs` 及相关项目引用、设计书和状态词典。
- TDD：契约测试先因设备领域类型和网关接口缺失而失败；实现最小值对象、状态/能力枚举和接口后通过。
- 验证：契约测试 7 个通过；全量 UnitTests 5 个通过；解决方案构建 0 警告、0 错误；未连接 PLC、ERP 或数据库；旧 `warehouse/` 目录未修改。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（设备契约字段和状态语义待负责人确认）；`FIELD_PENDING`（旧接口任务号去重/查询、停止和回调能力未在现场确认）。
- 已知风险：当前契约只定义抽象和值对象，尚未实现模拟网关或旧 API 适配；没有现场能力确认时，发送后超时仍必须进入 `PhysicalStateUnknown`，不得自动重发。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 2.2：实现模拟设备网关

**Files:**
- Create: `src/Warehouse.DeviceGateway/SimulatedDeviceGateway.cs`
- Create: `src/Warehouse.DeviceGateway/Simulation/SimulationScenario.cs`
- Test: `tests/Warehouse.DeviceGateway.ContractTests/SimulatedDeviceGatewayTests.cs`

- [x] 支持可配置延迟、成功、失败、离线、超时、报警、重复请求和服务重启恢复。
- [x] 模拟器必须先记录命令幂等键，再返回结果；具备任务号去重能力时重复命令不能产生第二次物理动作。
- [x] 模拟停止流程返回 `StopConfirmed`、`StopFailed` 或 `PhysicalStateUnknown`；未下发命令由 WMS 直接取消。
- [x] 模拟器分别覆盖“支持任务号去重/查询”和“不支持任务号去重/查询”两种设备能力。
- [x] 编写每个结果的契约测试并运行测试项目。

**验收:** `AGENT_VERIFIED`；无真实 PLC 可以稳定复现成功、失败、超时和未知结果。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.DeviceGateway/Simulation/SimulationScenario.cs`、`src/Warehouse.DeviceGateway/SimulatedDeviceGateway.cs`、项目引用和 `tests/Warehouse.DeviceGateway.ContractTests/SimulatedDeviceGatewayTests.cs`，并同步设计书和计划。
- TDD：先添加模拟网关契约测试，因实现类型缺失而失败；实现后先通过 18 个基础场景测试，再补充无去重、无查询和无停止能力的失败测试并修正实现，最终契约测试 21 个通过。
- 验证：`dotnet restore Warehouse.Wms.sln`（退出码 0）；`dotnet build Warehouse.Wms.sln --no-restore`（退出码 0，0 警告、0 错误）；`dotnet test Warehouse.Wms.sln --no-build --no-restore`（退出码 0，DeviceGateway 21、Unit 5 通过，集成测试程序集当前无测试）；`dotnet list Warehouse.Wms.sln package --vulnerable`（退出码 0，未发现漏洞）；未连接 PLC、ERP 或数据库；旧 `warehouse/` 目录未修改。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（模拟结果和停止语义待负责人确认）；`FIELD_PENDING`（真实设备能力、任务号去重/查询、停止和恢复未验证）。
- 已知风险：模拟器状态为进程内共享存储，真实 WMS 的持久化幂等、Outbox/Inbox 和重启对账由后续任务实现；模拟器不代表旧 PLC 的现场能力。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 2.3：实现旧 PLC API 适配器

**Files:**
- Create: `src/Warehouse.DeviceGateway/LegacyPlcApiGateway.cs`
- Create: `src/Warehouse.DeviceGateway/Legacy/LegacyPlcClient.cs`
- Create: `src/Warehouse.DeviceGateway/Legacy/LegacyCapabilityProbe.cs`
- Test: `tests/Warehouse.DeviceGateway.ContractTests/LegacyPlcApiGatewayTests.cs`
- Modify: `docs/field-baseline.md`

- [x] 将旧 API 的入库、出库、移库、状态读取和连接测试映射到设备契约。
- [x] 将 HTTP 错误、PLC 忙、离线、超时和完成状态转换为统一结果。
- [x] 探测并记录旧 API 是否接受 WMS 任务号、是否支持按任务号查询、是否支持停止和是否提供回调；未知能力标记为 `BLOCKED`。
- [x] 适配器只调用旧设备能力，不调用 ERP 单据查询、库存回写或业务存储过程。
- [x] 旧接口明确支持任务号去重且可按任务号查询时，才允许同一命令自动重试。
- [x] 请求发送后超时且无法按任务号查询时，立即返回 `PhysicalStateUnknown`，禁止自动重试、释放资源或再次下发，直到设备对账或人工确认。
- [x] 为调用超时、幂等能力未确认时不自动重试、旧 API 返回“触发成功但未完成”和能力不足场景写测试。
- [x] 运行契约测试并检查 `git diff --name-only -- warehouse` 为空。

**验收:** `AGENT_VERIFIED`；模拟器和旧 API 适配器都通过同一组契约测试，并输出旧接口能力矩阵。只有现场确认具备任务号去重/查询时，相关能力才可标记 `HUMAN_CONFIRMED`；否则统一采用超时未知策略。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.DeviceGateway/LegacyPlcApiGateway.cs`、`src/Warehouse.DeviceGateway/Legacy/LegacyPlcClient.cs`、`src/Warehouse.DeviceGateway/Legacy/LegacyCapabilityProbe.cs`、`tests/Warehouse.DeviceGateway.ContractTests/LegacyPlcApiGatewayTests.cs`、`docs/field-baseline.md`，并同步设计书和状态词典。
- TDD/契约测试：覆盖入库、出库、移库路由映射，连接测试，在线执行状态轮询，HTTP 503/504，PLC 忙，发送超时、停止不支持、非法位置/装载点、非法或缺少 `isSuccess` 的成功响应、状态版本递增；旧接口触发成功保持 `Accepted`，不伪造完成。
- 验证：`dotnet restore Warehouse.Wms.sln`、`dotnet build Warehouse.Wms.sln --no-restore`、`dotnet test Warehouse.Wms.sln --no-build --no-restore`、`dotnet list Warehouse.Wms.sln package --vulnerable` 和 `git diff --check` 均通过；设备网关契约测试 37 个通过；未连接 PLC、ERP 或数据库；`git diff --name-only -- warehouse` 无输出。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（旧 API 结果语义和参数需负责人确认）；`FIELD_PENDING`（任务号去重/查询、停止、回调、超时恢复能力未现场验证）。
- 已知风险：旧 API 成功只证明触发请求被接受；发送后超时不可安全重发，必须进入 `PhysicalStateUnknown` 并等待设备对账或人工确认。`GetStatusAsync` 只有显式 `TaskQuery` 能力和已确认 PLC 编号同时提供时启用，且旧状态接口参数按 PLC 编号处理，不能据此宣称任务号查询。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 六、阶段 3：基础资料、托盘和库存账

### Task 3.1：建立基础资料实体和迁移

**Files:**
- Create: `src/Warehouse.Wms.Domain/MasterData/Warehouse.cs`
- Create: `src/Warehouse.Wms.Domain/MasterData/Location.cs`
- Create: `src/Warehouse.Wms.Domain/MasterData/LoadingPoint.cs`
- Create: `src/Warehouse.Wms.Domain/MasterData/Material.cs`
- Create: `src/Warehouse.Wms.Domain/MasterData/Pallet.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Persistence/WarehouseDbContext.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Persistence/Migrations/`
- Test: `tests/Warehouse.Wms.UnitTests/MasterData/`

- [x] 建立仓库、库区、巷道、货架、库位、装载点、设备、物料、容器和托盘实体。
- [x] 为库位编码、托盘编码、设备编号和装载点建立唯一约束。
- [x] 为库位增加容量、重量、尺寸、禁用、锁定和现场映射字段。
- [x] 为托盘增加版本号和当前归属状态，禁止同一托盘双占用。
- [x] 写实体约束测试，生成第一版迁移和开发样例种子。

**验收:** `AGENT_VERIFIED`；数据库可独立建立基础资料，不读取 ERP 表。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.Wms.Domain/MasterData/` 下仓库、库区、巷道、货架、库位、装载点、设备、物料、容器和托盘实体；`src/Warehouse.Wms.Infrastructure/Persistence/WarehouseDbContext.cs`、设计时工厂、`src/Warehouse.Wms.Infrastructure/Migrations/` 首个迁移和模型快照；测试项目 EF Core 引用及 `tests/Warehouse.Wms.UnitTests/MasterData/MasterDataModelTests.cs`；同步 `PROJECT_DESIGN.md`。
- TDD：先添加并运行基础资料模型测试，确认实体/DbContext/EF 引用缺失导致编译失败；随后实现构造参数校验、SQL Server 模型唯一索引、托盘当前库位/装载点过滤唯一索引、互斥检查约束、并发版本和开发样例种子，模型测试 4 个通过。
- 验证：`dotnet restore Warehouse.Wms.sln`、`dotnet build Warehouse.Wms.sln --no-restore`、`dotnet test Warehouse.Wms.sln --no-build --no-restore`、`dotnet list Warehouse.Wms.sln package --vulnerable`、`git diff --check`；使用本地 `.codex-tools/dotnet-ef` 生成 `InitialMasterData` 迁移；未连接 PLC、ERP、生产数据库或 Docker SQL Server。
- 自动化状态：`AGENT_VERIFIED`（迁移文件已生成，未执行数据库更新）。
- 外部门禁：`HUMAN_PENDING`（编码层级、容量和现场映射字段语义需负责人确认）；`FIELD_PENDING`（真实库位/托盘归属与现场基线尚未核对）。
- 已知风险：当前迁移目标为 SQL Server；本地未运行 Docker/SQL Server，迁移执行和样例种子落库尚未现场验证；托盘当前库位和装载点均有过滤唯一索引，并通过互斥检查约束禁止同时归属，后续业务服务仍需使用并发版本提交。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 3.2：建立库存流水和余额

**Files:**
- Create: `src/Warehouse.Wms.Domain/Inventory/InventoryBalance.cs`
- Create: `src/Warehouse.Wms.Domain/Inventory/InventoryTransaction.cs`
- Create: `src/Warehouse.Wms.Domain/Inventory/InventoryStatus.cs`
- Create: `src/Warehouse.Wms.Application/Inventory/InventoryService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Inventory/InventoryServiceTests.cs`

- [x] 定义可用、锁定、待入库、待出库、冻结和异常库存状态。
- [x] 每次增加、减少、锁定、解锁、移库和调整都写不可篡改流水。
- [x] 用幂等键和并发版本防止重复扣减、负库存和重复流水。
- [x] 将来源单据、任务号、操作人、原因和时间写入流水。
- [x] 编写重复请求、超量扣减、并发锁定、事务回滚和从流水重算余额测试。

**验收:** `AGENT_VERIFIED`；库存余额可从流水重算，控制器不能直接修改库存数量。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.Wms.Domain/Inventory/InventoryStatus.cs`、`InventoryBalance.cs`、`InventoryTransaction.cs`、`src/Warehouse.Wms.Application/Inventory/InventoryService.cs`、`tests/Warehouse.Wms.UnitTests/Inventory/InventoryServiceTests.cs`，并同步设计书、状态词典和项目计划。
- TDD：先添加库存服务测试并确认领域类型和服务缺失导致编译失败；实现增加、减少、锁定、解锁、移库、调整、幂等键冲突、负库存防护、并发锁定、失败回滚和流水重算后，库存测试 8 个通过。
- 验证：`dotnet restore Warehouse.Wms.sln`、`dotnet build Warehouse.Wms.sln --no-restore`、`dotnet test Warehouse.Wms.sln --no-build --no-restore`、`dotnet list Warehouse.Wms.sln package --vulnerable` 和 `git diff --check` 均通过；未连接 PLC、ERP 或生产数据库；`git diff --name-only -- warehouse` 无输出。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（库存锁定的业务粒度和调整授权规则需负责人确认）；`FIELD_PENDING`（真实库位、托盘和账实核对尚未执行）。
- 已知风险：当前服务以内存状态验证领域事务和流水重算，持久化表、Outbox/Inbox 和数据库并发提交由后续基础设施/任务调度任务承接；冻结和异常状态只能由后续授权处置流程改变。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 七、阶段 4：任务状态机、调度器、资源锁和恢复

### Task 4.1：定义任务状态和状态历史

**Files:**
- Create: `src/Warehouse.Wms.Domain/Tasks/TaskState.cs`
- Create: `src/Warehouse.Wms.Domain/Tasks/WarehouseTask.cs`
- Create: `src/Warehouse.Wms.Domain/Tasks/TaskStateHistory.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Tasks/TaskStateMachineTests.cs`

- [x] 定义 `Created`、`Allocated`、`Queued`、`Dispatching`、`SentToPlc`、`Executing`、`Succeeded`、`Failed`、`TimedOut`、`Canceled`、`CancelRequested`、`StopRequested`、`StopConfirmed`、`StopFailed`、`PhysicalStateUnknown` 和 `ManualIntervention`。
- [x] 为每条状态迁移定义前置状态、操作者、原因、错误码和时间。
- [x] 禁止从 `SentToPlc`/`Executing` 直接把任务标记为物理成功的取消结果。
- [x] 编写非法跳转、重复完成、重复取消和未知结果测试。

**验收:** `AGENT_VERIFIED`；状态机能表达“取消未下发”和“设备可能仍在动作”两种不同语义。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.Wms.Domain/Tasks/TaskState.cs`、`WarehouseTask.cs`、`TaskStateHistory.cs`、`tests/Warehouse.Wms.UnitTests/Tasks/TaskStateMachineTests.cs`，并同步 `PROJECT_DESIGN.md`、`docs/status-dictionary.md`。
- TDD：先添加状态集合、正常执行链、历史字段、取消/停止、超时/物理未知、终态和非法迁移测试；确认任务领域类型缺失导致测试编译失败后，实现 16 状态迁移矩阵、UTC 历史记录、版本递增和终态保护，7 个测试通过。
- 验证：`dotnet test tests/Warehouse.Wms.UnitTests/Warehouse.Wms.UnitTests.csproj --filter FullyQualifiedName~TaskStateMachineTests --no-restore` 通过；随后执行全量构建、全量测试、漏洞扫描、`git diff --check` 和旧目录保护；未连接 PLC、ERP、生产数据库或执行后续任务。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（状态流转和人工处置语义需负责人确认）；`FIELD_PENDING`（现场设备停止、超时和物理未知恢复尚未验证）。
- 已知风险：状态历史目前为领域内存集合，持久化映射和并发提交属于 Task 4.2；`ManualIntervention` 保持终态，人工确认物理结果并结案的专用服务属于 Task 4.4；当前未连接真实设备。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 4.2：实现幂等、资源锁和短事务提交

**Files:**
- Create: `src/Warehouse.Wms.Domain/Tasks/TaskIdempotencyKey.cs`
- Create: `src/Warehouse.Wms.Domain/Tasks/ResourceLock.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Persistence/OutboxMessage.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Persistence/InboxMessage.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Tasks/TaskConcurrencyTests.cs`

- [x] 为任务命令、设备提交和设备回调建立唯一幂等键。
- [x] 为库位、托盘、装载点和设备建立带过期时间的资源锁及乐观版本号。
- [x] 定义每次状态变化使用独立短数据库事务的边界；禁止事务跨越 PLC 下发、等待和轮询过程。
- [x] 定义用 Outbox 发布设备命令、用 Inbox 去重设备结果消息的实体和状态；结果来源可以是轮询或可选回调。
- [x] 以并发分配、消息抢占/发布、重复结果和锁过期恢复契约测试验证上述规则。

**验收:** `AGENT_VERIFIED`；具备任务号去重/查询能力的设备命令可安全重试；不具备该能力的超时命令进入 `PhysicalStateUnknown` 且不得自动重试，数据库不存在跨 PLC 长事务。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.Wms.Domain/Tasks/TaskIdempotencyKey.cs`、`src/Warehouse.Wms.Domain/Tasks/ResourceLock.cs`、`src/Warehouse.Wms.Infrastructure/Persistence/OutboxMessage.cs`、`src/Warehouse.Wms.Infrastructure/Persistence/InboxMessage.cs`、`tests/Warehouse.Wms.IntegrationTests/Tasks/TaskConcurrencyTests.cs`，并同步 `PROJECT_DESIGN.md`、`docs/status-dictionary.md`。
- TDD：先编写幂等摘要冲突、资源锁并发/租约/版本冲突、Outbox 抢占/发布与租约恢复、Inbox 重复结果测试；确认实体缺失导致测试编译失败后，实现最小领域/持久化行为并使 6 个测试通过。
- 验证：`dotnet test tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj --filter FullyQualifiedName~TaskConcurrencyTests --no-restore` 通过（6/6）；尚未连接 SQL Server、PLC、ERP 或生产服务，未执行后续 Task。
- 自动化状态：`AGENT_VERIFIED`（当前实体契约和并发行为）；外部门禁：`FIELD_PENDING`（真实设备任务号去重/查询能力和重启对账尚未现场确认）。
- 已知风险：本 Task 未修改 `WarehouseDbContext` 或生成迁移，实体的唯一索引、并发令牌和事务提交将在后续持久化接入/调度 Task 中映射；测试使用内存对象，不能证明 SQL Server 隔离级别或跨进程竞争已现场验证。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 4.3：实现任务调度器和恢复 Worker

**Files:**
- Create: `src/Warehouse.Wms.Application/Tasks/TaskScheduler.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Background/TaskWorker.cs`
- Create: `src/Warehouse.Wms.Application/Tasks/DeviceResultProcessor.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Tasks/TaskSchedulerTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Tasks/TaskRecoveryTests.cs`

- [x] 实现同一设备串行、优先级、有限重试、超时和资源锁释放。
- [x] 只从 Outbox 取待发送命令，发送成功后短事务写 `SentToPlc`。
- [x] 第一版以 `GetStatusAsync` 定时轮询为主；若旧接口支持回调，回调先写 Inbox，不直接更新业务状态。
- [x] 轮询和回调都转换为同一个 `DeviceObservation`，按设备任务号、结果版本和幂等键去重后交给 `DeviceResultProcessor`。
- [x] Worker 重启后先查询设备状态和任务幂等键，再决定重试、等待或进入 `PhysicalStateUnknown`；旧接口没有任务号查询能力时不得自动重试超时命令。
- [x] 设备离线、未知结果和无法对账时进入异常队列，不自动释放库存锁。
- [x] 测试杀进程、恢复、重复发送、轮询/回调同时到达、超时和人工接管。

**验收:** `AGENT_VERIFIED`；服务重启不会重复下发，也不会丢失未完成任务。

**执行记录（2026-08-25）：**

- 修改：`src/Warehouse.Wms.Application/Tasks/TaskScheduler.cs`、`DeviceResultProcessor.cs`、`src/Warehouse.Wms.Infrastructure/Background/TaskWorker.cs`、`src/Warehouse.Wms.Domain/Tasks/TaskState.cs`、`tests/Warehouse.Wms.UnitTests/Tasks/TaskSchedulerTests.cs`、`tests/Warehouse.Wms.IntegrationTests/Tasks/TaskRecoveryTests.cs`，并同步设计书和状态词典。
- TDD：先添加同设备串行/优先级、未知结果不重试、轮询/回调去重和 Worker 重启恢复测试，确认调度器与 Worker 缺失导致编译失败；实现有限重试能力门禁、设备结果统一处理、Inbox 风格版本去重和查询能力不足时的物理未知恢复。
- 验证：Task 4.3 单元测试 6 个、恢复集成测试 8 个通过；随后执行全量 restore/build/test、漏洞扫描、`git diff --check` 和旧目录保护；未连接 SQL Server、PLC、ERP 或生产服务。
- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（重试、优先级和异常队列业务策略需负责人确认）；`FIELD_PENDING`（真实 PLC 查询、重启和物理未知恢复尚未现场验证）。
- 已知风险：当前调度状态和 Worker 队列使用共享内存状态；Task 4.2 的 Outbox/Inbox 仍是 Infrastructure 中的实体契约，尚未接入 Application 调度器、WarehouseDbContext、SQL Server 迁移或跨进程持久化；Worker 是可调用的轮询类，尚未注册为 API 宿主后台服务；未连接真实设备。
- 审查处置：已补充 `Dispatching` 重启恢复、提交响应幂等键校验、轮询未知/离线结果转物理未知、查询异常保护和重复任务命令冲突检测；Outbox/Inbox 持久化接入和宿主注册保留为后续 Task，不将其标记为已完成。
- 验收补充：发送调用开始前取消会回队列；调用开始后的取消或异常以物理未知处理；结果处理拒绝不匹配的设备任务号并忽略旧结果版本。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 4.4：实现取消、停止和人工结案协议

**Files:**
- Modify: `src/Warehouse.Wms.Domain/Tasks/TaskState.cs`
- Create: `src/Warehouse.Wms.Application/Authorization/ICurrentUser.cs`
- Create: `src/Warehouse.Wms.Application/Authorization/IRiskAuthorizationService.cs`
- Create: `src/Warehouse.Wms.Application/Tasks/TaskCancellationService.cs`
- Create: `src/Warehouse.Wms.Application/Tasks/PhysicalResultConfirmationService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Tasks/TaskCancellationTests.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Authorization/FakeRiskAuthorizationServiceTests.cs`

- [x] `Created/Allocated/Queued` 只能取消未下发任务并释放资源；`Dispatching` 仅在设备调用尚未开始时回到 `Queued`。
- [x] `SentToPlc/Executing` 只能提交停止请求，等待 `StopConfirmed`、`StopFailed` 或 `PhysicalStateUnknown`。
- [x] `PhysicalStateUnknown` 不得直接释放库位、托盘和库存锁。
- [x] 将“人工完成”命名为“人工确认物理结果并结案”，强制调用 `IRiskAuthorizationService` 二次授权，并填写原因、设备状态、托盘实际位置、源/目标库位核对和库存校正流水。
- [x] 先用测试实现提供 `ICurrentUser` 和 `IRiskAuthorizationService`；Task 6.3 再接入真实用户、角色和 JWT，不得因此跳过权限校验。
- [x] 测试取消竞态、停止失败、人工确认缺字段和重复确认。

**验收:** `AGENT_VERIFIED`；人工结案不是简单把状态改为 `Succeeded`，并能完整审计。

**Task 4.4 执行证据（2026-08-25）：** 新增取消/停止服务、物理结果确认服务、当前用户和二次授权契约；WMS 单元测试 12 个 Task 4.4 用例通过，全单元测试 44 个通过，解决方案构建 0 警告/0 错误。停止响应增加设备任务幂等键校验，不匹配时进入物理未知。当前仍未接入持久化资源锁释放实现、真实用户/JWT 或现场 PLC 停止确认，分别留待后续基础设施、权限和现场门禁。

### Task 4.5：异常工作项和处置中心

**Files:**
- Create: `src/Warehouse.Wms.Domain/Exceptions/ExceptionWorkItem.cs`
- Create: `src/Warehouse.Wms.Domain/Exceptions/ExceptionType.cs`
- Create: `src/Warehouse.Wms.Application/Exceptions/ExceptionWorkItemService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/ExceptionsController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Exceptions/ExceptionWorkItemTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Exceptions/ExceptionIdempotencyTests.cs`

- [x] 定义异常来源、类型、严重程度、关联任务、设备任务号和当前物理状态。
- [x] 记录受影响的托盘、库位、装载点、库存余额和资源锁。
- [x] 支持重试、请求停止、重新分配、人工确认物理结果和库存校正等处置动作；每个动作通过动作执行器契约复用任务状态机和权限抽象。
- [x] 强制记录操作人、原因、处理前后状态、设备观察和审计信息。
- [x] 用 `source + externalKey + taskId` 合并重复异常，重复告警不得创建多个活动工作项。
- [x] 测试超时未知、重复告警、处置竞态边界、权限拒绝、锁未释放保护和关闭后再次告警。

**验收:** `AGENT_VERIFIED`；阶段 5 的“进入异常处置”均有真实实体、服务、接口和测试承接；未解决的物理未知任务不能被异常中心直接改成成功。

**Task 4.5 执行证据（2026-08-25）：** 新增异常工作项领域实体、来源/类型/严重程度/物理状态和资源快照；应用服务提供 `source + externalKey + taskId` 合并、关闭后重开、动作授权和未知状态保护；API 契约位于 `api/exceptions`。异常仓储和动作执行器当前为显式内存实现，尚未接入 SQL Server、Outbox/Inbox、真实用户/JWT 或现场设备对账，不能作为生产完成证据。异常单元测试 6 个、幂等集成测试 1 个通过；额外验证未知物理状态不得以 `Closed + Unknown` 关闭；解决方案构建无警告/错误。

## 八、阶段 5：入库、出库和移库业务闭环

### Task 5.1：定义入库状态和建单

**Files:**
- Create: `src/Warehouse.Wms.Domain/Inbound/InboundOrder.cs`
- Create: `src/Warehouse.Wms.Domain/Inbound/InboundLine.cs`
- Create: `src/Warehouse.Wms.Domain/Inbound/InboundState.cs`
- Create: `src/Warehouse.Wms.Application/Inbound/InboundOrderService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Inbound/InboundOrderTests.cs`

- [x] 定义 `Draft`、`Receiving`、`Received`、`PutawayQueued`、`Completed`、`Canceled` 和 `Exception`。
- [x] 实现手工建单、明细收货、批次/有效期、托盘绑定和重量采集。
- [x] 收货数量只能进入待入库库存，不得直接变为库位实存。
- [x] 测试部分收货、重复收货、数量超限、托盘重复绑定和取消。

**验收:** `AGENT_VERIFIED`；可无 ERP 创建入库单和待上架库存。

**Task 5.1 执行证据（2026-08-25）：** 新增入库单/明细领域模型和内存 `InboundOrderService`；实现状态迁移、手工建单、部分/全量收货、批次/有效期、托盘编码/ID 唯一绑定、重量采集、幂等重放和超量拒绝。每次收货生成 `PendingInboundInventory`，状态为 `PendingInbound` 且 `LocationId = null`，未调用 `InventoryService` 增加库位实存；`Exception` 状态入库单拒绝新收货。新增 10 个单元测试，覆盖状态、部分收货、待入库边界、重复键、摘要冲突、超量、托盘重复绑定、取消、异常状态和非法明细/数量；Task 5.1 定向测试 10 个通过，现有 WMS 单元测试共 60 个通过。当前服务与收货记录为内存契约，SQL Server 持久化、库位分配和设备上架由后续 Task 5.2/5.3 完成。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（入库状态、批次/有效期、重量和取消语义待负责人确认）；`FIELD_PENDING`（真实托盘、设备和账实流程未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 5.1A：实现 Excel 入库/出库导入

**Files:**
- Create: `src/Warehouse.Wms.Application/Import/SpreadsheetImportService.cs`
- Create: `src/Warehouse.Wms.Application/Import/ImportErrorReport.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/ImportsController.cs`
- Create: `docs/templates/inbound-import.xlsx`
- Create: `docs/templates/outbound-import.xlsx`
- Test: `tests/Warehouse.Wms.UnitTests/Import/SpreadsheetImportTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Import/SpreadsheetImportApiTests.cs`

- [x] 为入库和出库分别定义模板列、版本号、必填字段、数量精度、批次/有效期和托盘规则。
- [x] 导入前完成整批校验，返回带行号、列名、错误码和修复建议的错误报告；任何错误行不得部分入账。
- [x] 使用文件摘要加来源幂等键防止同一文件和同一业务单据重复创建。
- [x] 导入成功只创建 WMS 单据，不直接提交 PLC 指令；后续流程复用 Task 5.2/5.4。
- [x] 测试模板版本错误、重复文件、部分错误、数量超限、重复托盘和成功导入。

**验收:** `AGENT_VERIFIED`；Excel 导入与手工建单使用相同领域服务，错误报告可下载，导入不依赖 ERP。

**Task 5.1A 执行证据（2026-08-25）：** 新增版本 `1.0` 的入库/出库 `.xlsx` 模板、`SpreadsheetImportService`、CSV 错误报告下载 API 和导入控制器。导入按来源键和文件 SHA-256 摘要幂等；模板版本、列顺序、必填字段、GUID、数量精度、批次/有效期、托盘重复和数量上限在创建单据前整批校验，错误行不会部分创建。入库复用 `InboundOrderService` 生成待入库收货记录，出库复用 `OutboundAllocationService` 只创建 `Draft` 单据；服务没有设备网关依赖，不提交 PLC。新增单元及 API 控制器测试，当前导入记录和错误报告为内存实现，真实数据库持久化仍待后续任务。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（模板列、数量精度和导入操作待负责人确认）；`FIELD_PENDING`（真实设备和账实流程未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 5.2：实现入库库位分配和上架任务

**Files:**
- Create: `src/Warehouse.Wms.Application/Inbound/PutawayAllocationService.cs`
- Create: `src/Warehouse.Wms.Application/Inbound/PutawayTaskService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/InboundController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Inbound/PutawayAllocationTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Inbound/InboundApiTests.cs`

- [x] 过滤停用、故障、锁定、占用、尺寸不符和重量超限库位。
- [x] 支持自动推荐和人工指定两条路径，保存推荐原因。
- [x] 创建任务时锁定待入库库存、托盘、目标库位和装载点。
- [x] 通过阶段 4 调度器提交设备网关，不在业务代码中写寄存器。
- [x] 测试目标库位冲突、装载点无货、设备离线和重复提交。

**验收:** `AGENT_VERIFIED`；模拟网关下可提交一条上架任务，库存仍保持待入库直到设备结果确认。

**Task 5.2 执行证据（2026-08-25）：** 新增 `PutawayAllocationService`、`PutawayTaskService` 和入库上架 API 契约；自动推荐/人工指定均校验库位启用、故障、锁定、占用、容量、尺寸和重量，保存推荐原因；提交前校验装载点有货并创建待入库库存、托盘、目标库位和装载点资源锁。设备任务统一通过阶段 4 `TaskScheduler` 提交，设备结果确认前不改变 `PendingInbound` 库存；重复幂等请求直接返回已记录结果，不重复下发设备命令。当前实现和锁为内存契约，SQL Server 持久化、设备结果落账和真实 API 依赖注入留待后续任务。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（库位推荐优先级、尺寸/重量业务阈值和上架操作语义待负责人确认）；`FIELD_PENDING`（真实装载点、库位占用和 PLC 上架回归未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 5.3：实现入库设备结果和库存提交

**Files:**
- Modify: `src/Warehouse.Wms.Application/Inbound/PutawayTaskService.cs`
- Create: `src/Warehouse.Wms.Application/Inbound/InboundReconciliationService.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Inbound/PutawayCompletionTests.cs`

- [x] 收到设备成功后用短事务更新任务、托盘归属、库位状态、库存余额和库存流水。
- [x] 设备失败或超时保持待入库库存并进入异常处置，不释放未确认的物理锁。
- [x] 设备未知结果先查询状态和对账，再决定成功、失败或人工处置。
- [x] 所有结果处理通过幂等键，重复回调不重复入账。
- [x] 测试成功、失败、未知结果、重复回调和服务重启。

**验收:** `AGENT_VERIFIED`；数据库事务只覆盖本次状态提交，不跨 PLC 执行过程。

**Task 5.3 执行证据（2026-08-25）：** 新增 `InboundReconciliationService` 并扩展上架任务关联待入库库存；设备成功结果通过 `InventoryService` 的幂等操作在短事务中增加目标库位库存流水，随后释放待入库库存、托盘、库位和装载点资源锁；失败、超时和物理未知只更新任务/处置状态，不增加库存或释放未确认锁。重复成功结果返回已完成结果且不新增流水；同一入库单的全部待上架任务确认后才转为 `Completed`。当前对账和锁为内存契约，SQL Server 事务、托盘/库位持久化归属和真实设备状态查询留待后续任务。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（成功落账、失败处置和多托盘完成语义待负责人确认）；`FIELD_PENDING`（真实设备结果、账实核对、重启恢复未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 5.4：定义出库状态、分配和库存锁定

**Files:**
- Create: `src/Warehouse.Wms.Domain/Outbound/OutboundOrder.cs`
- Create: `src/Warehouse.Wms.Domain/Outbound/OutboundLine.cs`
- Create: `src/Warehouse.Wms.Domain/Outbound/OutboundState.cs`
- Create: `src/Warehouse.Wms.Application/Outbound/OutboundAllocationService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Outbound/OutboundAllocationTests.cs`

- [x] 定义 `Draft`、`Allocated`、`Locked`、`Picking`、`AwaitingReview`、`Completed`、`Canceled` 和 `Exception`。
- [x] 支持确定性库存顺序、指定批次、指定托盘和指定库位；FEFO 等日期排序待库存有效期字段接入后启用。
- [x] 先锁定库存和托盘，再创建下架任务；不得负库存或重复分配。
- [x] 测试缺货、部分出库、并发锁定、重复请求和取消未下发任务。

**验收:** `AGENT_VERIFIED`；出库分配可独立运行，不依赖 ERP。

**Task 5.4 执行证据（2026-08-25）：** 新增出库单/明细/状态领域模型和 `OutboundAllocationService`；可按批次、托盘、库位筛选 `Available` 库存并按确定性顺序分配，支持部分数量。分配创建库存余额、托盘和库位资源锁，同一幂等键重放返回原分配，冲突资源拒绝并发分配；未在本 Task 扣减库存或下发设备。当前资源锁为内存契约，FEFO 需要库存有效期字段，设备下架和复核留待 Task 5.5。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（FIFO/FEFO 优先级、部分出库和锁定语义待负责人确认）；`FIELD_PENDING`（真实托盘、库位和设备下架未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 5.5：实现出库设备执行、装载点确认和复核

**Files:**
- Create: `src/Warehouse.Wms.Application/Outbound/OutboundTaskService.cs`
- Create: `src/Warehouse.Wms.Application/Outbound/OutboundReviewService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/OutboundController.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Outbound/OutboundWorkflowTests.cs`

- [x] 出库下发前捕获托盘号、锁定装载点，并记录设备任务幂等键。
- [x] 设备完成后先确认装载点托盘号、重量和物理状态，再进入复核。
- [x] 复核通过后用短事务扣减库存、完成托盘流转和释放资源；差异进入异常流程。
- [x] 设备失败、超时、取消和未知结果不得直接扣减库存。
- [x] 测试模拟 PLC 成功、装载点占用、托盘号变化、复核差异、重复回调和重启。

**验收:** `AGENT_VERIFIED`；模拟 PLC 可完成出库闭环，业务库存扣减不依赖旧 API 的同步 `IsSuccess`。

**Task 5.5 执行证据（2026-08-25）：** 新增 `OutboundTaskService`、`OutboundReviewService`、出库复核 API 和 5 个集成场景。出库下发前校验装载点空闲并锁定装载点，设备命令通过统一 `TaskScheduler` 提交；设备成功只进入 `AwaitingReview`，复核确认托盘号、重量和物理状态后才以幂等短事务扣减库存并释放资源。设备失败、超时、物理未知、装载点占用和复核差异均不扣减库存；重复复核不产生第二条库存流水。当前服务和锁为内存契约，SQL Server 持久化、真实装载点读数、托盘实体归属和现场回归留待后续任务。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（复核字段、重量容差和出库完成语义待负责人确认）；`FIELD_PENDING`（真实 PLC、装载点托盘号和账实核对未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 5.6：实现移库和托盘位置原子更新

**Files:**
- Create: `src/Warehouse.Wms.Domain/Relocation/RelocationOrder.cs`
- Create: `src/Warehouse.Wms.Application/Relocation/RelocationService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/RelocationController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Relocation/RelocationServiceTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Relocation/RelocationWorkflowTests.cs`

- [x] 校验源库位有货、目标库位为空、托盘归属正确、同一设备和资源锁可用。
- [x] 目标托盘不是空目标库位的必填条件；跨 PLC/跨巷道能力按现场确认结果保持配置化待确认。
- [x] 通过任务调度器提交 `SubmitTransferAsync`，不复制旧代码的 `inShelf` 源库位缺陷。
- [x] 设备成功后用单一短事务更新源库位、目标库位、托盘位置和库存流水。
- [x] 测试目标占用、源无货、跨设备拒绝、重复移库、失败、未知结果和重启。

**验收:** `AGENT_VERIFIED`；一次移库不产生双占用、丢托盘或重复库存流水。

**Task 5.6 执行证据（2026-08-25）：** 新增 `RelocationOrder`、`RelocationService`、移库 API 和 4 个单元/集成场景。服务校验源库存、目标空位、物料/托盘归属、数量重量和资源冲突，复用阶段 4 调度器提交 `SubmitTransferAsync`；成功结果以幂等 `InventoryService.MoveAsync` 更新源/目标库存流水并释放锁，失败/物理未知保持账面不变和资源锁，重复完成不新增流水。跨 PLC/跨巷道和真实托盘位置更新未凭空实现，保持 `FIELD_PENDING`。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（目标托盘和移库完成语义待负责人确认）；`FIELD_PENDING`（真实设备、跨设备能力和账实位置核对未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 九、阶段 6：盘点、权限、审计和最小界面

### Task 6.1：实现盘点范围和盘点任务

**Files:**
- Create: `src/Warehouse.Wms.Domain/Stocktaking/StocktakingTask.cs`
- Create: `src/Warehouse.Wms.Application/Stocktaking/StocktakingService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/StocktakingController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Stocktaking/StocktakingRangeTests.cs`

- [x] 支持全库、库区、物料、批次、托盘范围和动态抽盘入口。
- [x] 兼容旧基线的 `A1-A8`、`prefix-number` 闭区间和确定性排序，范围解析保持配置化边界。
- [x] 盘点下架复用任务调度器，逐条等待装载点，不跳过占用货框。
- [x] 盘点任务保存账面数、实盘数、出库装载点、明细状态和状态历史。
- [x] 测试空范围、重复任务、装载点串行、设备任务排队和服务重启契约。

**验收:** `AGENT_VERIFIED`；盘点下架不直接覆盖库存，设备结果可恢复。

**Task 6.1 执行证据（2026-08-25）：** 新增 `StocktakingTask`、`StocktakingItem`、`StocktakingService` 和盘点 API；支持全库/库区/物料/批次/托盘筛选及旧基线 `A1-A8`、`prefix-number` 闭区间，结果按位置、物料、托盘确定性排序。任务保存账面数量/重量、实盘数量/重量、差异状态和装载点；设备辅助盘点复用 `TaskScheduler`，每个明细逐条占用装载点，未完成盘点不允许结案。盘点服务不直接修改库存，差异审批和调整流水留待 Task 6.2。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（范围业务含义、动态抽盘和差异完成语义待负责人确认）；`FIELD_PENDING`（真实盘点、设备下架和账实核对未执行）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 6.2：实现盘点差异、复盘和调整审批

**Files:**
- Create: `src/Warehouse.Wms.Application/Stocktaking/StocktakingDifferenceService.cs`
- Create: `src/Warehouse.Wms.Domain/Stocktaking/StocktakingAdjustment.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Stocktaking/StocktakingDifferenceTests.cs`

- [x] 支持明盘、盲盘、复盘、差异原因和冻结策略。
- [x] 差异必须经授权确认后生成库存调整流水，不能直接更新余额。
- [x] 对移动托盘的上架预约逐条幂等执行，区分“已发送”和“设备完成”。
- [x] 测试重复确认、未授权调整、差异复盘和调整回滚。

**验收:** `AGENT_VERIFIED`；差异有完整审批、流水和审计记录。

**Task 6.2 执行证据（2026-08-25）：** 新增 `StocktakingAdjustment`、`StocktakingDifferenceService` 和差异单元测试。服务支持明盘/盲盘操作员视图、复盘替换、`FreezeOnDifference` 冻结策略、差异原因、二次授权和审计轨迹；授权通过后唯一调用 `InventoryService.AdjustAsync`，以调整 ID 作为幂等作用域，重复确认不重复写库存流水，库存调整失败保持 `Approved` 且不产生部分流水，后续可重试。移动托盘上架预约按任务/明细幂等创建，状态严格区分 `Reserved`、`Sent`、`Completed`。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（明盘/盲盘和冻结策略的现场操作含义待负责人确认）；`FIELD_PENDING`（真实盘点、设备发送/完成和账实核对未执行）。
- 已知限制：当前差异工作项、冻结标记和上架预约为内存实现；尚未接入 SQL Server 持久化、真实库位 ID 映射、Outbox/Inbox 和现场设备回执。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 6.3：补全认证、权限和审计

**Files:**
- Create: `src/Warehouse.Wms.Application/Identity/`
- Create: `src/Warehouse.Wms.Api/Controllers/UsersController.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/RolesController.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Identity/AuthorizationTests.cs`

- [x] 实现 JWT Bearer、刷新令牌、退出、密码修改和账号禁用。
- [x] 实现用户、角色、功能权限和仓库范围授权。
- [x] 对库存调整、任务取消、停止请求、人工物理结果确认和差异审批增加二次权限。
- [x] 将登录、权限变更、设备任务和高风险操作写入审计日志。

**验收:** `AGENT_VERIFIED`；未授权用户无法执行高风险操作，审计可追溯。

**Task 6.3 执行证据（2026-08-25）：** 新增 `IIdentityService`、`InMemoryIdentityService`、身份契约和 API 用户/角色控制器；开发 API 接入 JWT Bearer、`ICurrentUser` 仓库范围和统一异常状态码。实现 PBKDF2 密码哈希、JWT 访问令牌、一次性刷新令牌、注销、改密、账号禁用、角色分配、权限授予、仓库范围校验和高风险二次授权；`IAuditLog` 记录登录、刷新、注销、改密、禁用、角色/权限变更、设备任务和高风险授权。身份集成测试 6 个通过，API 健康检查为 200，匿名高风险接口为 401。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（角色矩阵、仓库范围和高风险操作审批人待负责人确认）；`FIELD_PENDING`（真实用户目录、密钥轮换和现场登录验证未执行）。
- 已知限制：当前用户、刷新令牌和审计为内存实现；尚未接入 SQL Server、分布式注销、密码找回通知和生产密钥管理。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 6.4：实现最小可用管理界面

**Files:**
- Create: `src/Warehouse.Wms.Web/`
- Create: `docs/user-guide.md`
- Test: `tests/Warehouse.Wms.IntegrationTests/Web/`

- [x] 实现工作台、基础资料、入库、出库、库存、任务、盘点和异常页面。
- [x] PDA 页面只负责扫码和业务操作，不在浏览器实现 PLC 控制时序。
- [x] 展示任务状态历史、取消/停止语义、未知状态和人工确认所需字段。
- [x] 编写操作员和主管的关键流程、权限和异常处置说明。

**验收:** `HUMAN_CONFIRMED` 前必须由负责人按操作说明走通核心流程；没有人工确认不能标记阶段完成。

**Task 6.4 执行证据（2026-08-25）：** 新增 `Warehouse.Wms.Web` 独立静态 Web 项目，提供工作台、入库、出库、库存/库位、设备任务、盘点、异常中心和 PDA 扫码入口；导航、筛选、库位热力、任务分栏、异常处置入口和 PDA 提交提示均可在无 ERP/PLC 环境下运行。前端脚本只保存页面筛选/最近视图并提交业务操作提示，不包含寄存器、PLC 写入或设备时序。新增 `ManagementWebTests` 检查页面模块、PDA 壳和控制边界；测试通过。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（负责人尚未按 [`docs/user-guide.md`](../../../docs/user-guide.md) 完成人工入库、出库、移库、盘点差异、异常和权限流程）；`FIELD_PENDING`（真实设备和现场网络未验证）。
- 已知限制：当前 Web 是静态 API 壳，尚未接入完整 API 数据查询、真实登录页、持久化用户目录和现场设备状态；页面不能替代现场试运行。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 6.5：冻结作业优先工作台和实时查询交互设计

**前置条件:** Task 6.4 的页面壳和旧程序功能盘点已完成；本 Task 只产出设计规格和实施拆分，不修改 Web 代码、不接入真实设备。

**Files:**
- Create: `docs/superpowers/specs/2026-08-25-wms-statistics-point-view-design.md`
- Modify: `PROJECT_DESIGN.md`
- Modify: `docs/user-guide.md`（实现阶段补充操作说明）

- [x] 固化“作业优先 + 轻量实时监控”工作台：核心作业待办、快捷入口、任务队列为主区域，PLC/设备/装载点/告警为只读辅助区域。
- [x] 固化角色化入口、任务状态、数据新鲜度、异常和物理未知展示，不允许监控卡片直接写 PLC 或修改库存。
- [x] 固化按仓库/库区/巷道/货架/层/库位查看实时物品的二维点位视图、详情抽屉、托盘反向定位和过期/离线/锁定显示。
- [x] 固化统计页面的 KPI、趋势、利用率、任务状态和盘点差异图表，以及小时/日/周/月周期、幂等汇总、失败保留上次成功结果和明细跳转规则。
- [x] 形成后续实现 Task 的文件范围、接口契约、读模型边界和测试清单。

**验收:** `AGENT_VERIFIED`；设计书、交互规格和实施计划一致，未提前修改 Web 代码或声称真实点位/统计口径已确认。

**执行证据（2026-08-26）:** 已新增 [`docs/superpowers/specs/2026-08-25-wms-statistics-point-view-design.md`](../../../docs/superpowers/specs/2026-08-25-wms-statistics-point-view-design.md)，并同步更新 `PROJECT_DESIGN.md` 版本 3.16 和需求变更记录。视觉 companion 已确认工作台采用“作业优先 + 轻量实时监控”；统计和点位实现暂不执行，保留后续 Task。主代理复核确认设计规格、项目设计书和本计划一致，Task 6.6 代理在设计确认前已停止，未产生代码改动。

- 自动化状态：`AGENT_VERIFIED`（文档一致性和范围检查通过）。
- 外部门禁：`HUMAN_PENDING`（统计口径、角色指标、数据新鲜度阈值和点位状态颜色语义待负责人确认）；`FIELD_PENDING`（真实点位、设备报警和现场数据刷新能力未验证）。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 6.6：实现统计分析与实时点位查询（后续执行）

**前置条件:** Task 6.5 通过设计验收；Task 9.x 的业务账和消息边界保持通过；不得连接生产 PLC 或生产数据库。

**目标:** 实现统计汇总、可视化读模型、仓库点位查询和管理 Web 交互；所有查询只读，不改变库存、任务或 PLC 时序。

**允许修改范围:**
- `src/Warehouse.Wms.Application/Reports/`、`src/Warehouse.Wms.Application/Warehouse/`
- `src/Warehouse.Wms.Infrastructure/Reports/`、`src/Warehouse.Wms.Infrastructure/Warehouse/`
- `src/Warehouse.Wms.Api/Controllers/ReportsController.cs` 及点位查询控制器
- `src/Warehouse.Wms.Web/` 统计、工作台和点位视图页面
- `tests/Warehouse.Wms.UnitTests/Reports/`、`tests/Warehouse.Wms.IntegrationTests/Reports/`、`tests/Warehouse.Wms.IntegrationTests/Web/`
- `PROJECT_DESIGN.md`、`docs/user-guide.md`、必要的 API 契约和运维说明

**必须完成:**
- [x] 建立库存、入库、出库、移库、盘点差异、设备任务成功率和异常数量的周期汇总模型；周期、范围、来源版本和幂等键唯一。
- [x] 增加可配置小时/日/周/月统计调度；重复执行安全重放，失败不覆盖上次成功汇总，并提供数据生成时间和新鲜度。
- [x] 增加 KPI、趋势、利用率、任务状态分布和盘点差异只读 API，支持按仓库/库区/时间/任务类型筛选和跳转明细。
- [x] 增加按仓库/库区/巷道/货架/层/库位筛选的点位快照 API，显示托盘、物料、批次/有效期、数量、重量、锁定、任务和设备观察信息。
- [x] 增加二维货架网格、库位详情抽屉和托盘反向定位；轮询和未来回调统一使用观察版本/Inbox 去重，过期或物理未知不得显示为空闲。
- [x] 补充统计幂等、失败保留、点位版本、空间筛选、权限、只读边界和页面跳转测试；不得直接写寄存器或修改 `warehouse/`。

**验收:** `AGENT_VERIFIED`；模拟数据下图表、周期汇总、点位查询和页面交互通过，API 无 ERP/真实 PLC 仍可启动；`HUMAN_PENDING`/`FIELD_PENDING` 保留真实统计口径、刷新阈值、点位映射和设备报警语义确认。

**Task 6.6 执行证据（2026-08-26）：** 新增统计契约、内存周期调度器、统计摘要/趋势控制器、点位查询契约和内存点位读模型；批次使用稳定 SHA-256 幂等 ID，同周期不同来源版本拒绝，失败不覆盖上次成功，按仓库范围返回最新成功批次。点位按 `SourceVersion` 去重，支持库区/巷道/货架/层/库位/状态/物料/托盘筛选，详情和托盘位置只读；过期显示 `Stale`，`PhysicalUnknown` 原样保留。Web 工作台新增统计分析导航、KPI/趋势/任务状态、周期刷新和库存点位筛选/详情，动态字段转义后再渲染。新增 9 个统计/点位单元测试和 2 个 API 集成测试。主代理独立复验：定向统计/点位测试 5 通过；完整解决方案 restore/build 通过（0 警告、0 错误），全量测试在 SQL 环境下为 Unit 142、Integration 70、Device Contract 37 全部通过；Docker SQL 迁移已是最新，`scripts/verify.ps1` 退出码 0，健康端点均返回 200，旧目录保护通过。自动化状态：`AGENT_VERIFIED`。统计和点位读模型仍为内存实现，真实统计口径、刷新阈值、点位映射和设备报警语义保留 `HUMAN_PENDING`/`FIELD_PENDING`。

- 自动化状态：`AGENT_VERIFIED`（实现代理与主代理定向复测通过，待最终质量门禁复验）。
- 外部门禁：`HUMAN_PENDING`（统计口径、角色指标、数据新鲜度阈值和点位状态颜色语义待负责人确认）；`FIELD_PENDING`（真实点位、设备报警和现场数据刷新能力未验证）。
- 已知限制：当前统计和点位读模型为内存实现，周期调度器是可配置应用边界，尚未接入 SQL 投影、独立后台 Worker、真实库存流水聚合或设备回调；Web 仍为静态壳，通过 API 读取模拟数据。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 十、阶段 7：报表、运维和现场试运行

### Task 7.1：实现报表、健康检查和恢复演练

**Files:**
- Create: `src/Warehouse.Wms.Api/Controllers/ReportsController.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Health/`
- Create: `docs/operations.md`
- Create: `scripts/recovery-drill.ps1`

- [x] 实现库存、库位利用率、入出库、移库、托盘追踪、盘点差异和设备报警报表。
- [x] 增加 API、数据库、Worker、设备网关和 Outbox/Inbox 健康检查。
- [x] 演练数据库恢复、服务重启、模拟 PLC 离线、超时、未知结果和消息重放。
- [x] 运行统一本地验收命令、迁移命令、健康检查和恢复脚本。

**验收:** `AGENT_VERIFIED`；运维人员可判断各组件健康并完成无现场设备的恢复演练。

**Task 7.1 执行证据（2026-08-25）：** 新增 `ReportsController` 和 `IReportsReadModel` 内存只读快照，覆盖库存、库位利用率、入库/出库、移库、托盘追踪、盘点差异和设备报警；新增 `WarehouseHealthCheckService` 聚合 API、数据库、Worker、设备网关和 Outbox/Inbox 状态，并在 API DI 中注册开发安全默认实现。新增 [`docs/operations.md`](../../../docs/operations.md) 和 [`scripts/recovery-drill.ps1`](../../../scripts/recovery-drill.ps1)，脚本只生成迁移脚本、执行模拟任务恢复/离线/超时/物理未知/消息重放测试，并在临时目录执行文件级备份恢复，不连接生产数据库或现场 PLC。报表/健康单元与集成测试通过。后续修复 `32f2f09` 使 `scripts/verify.ps1` 自动发现 Docker Desktop 标准路径、等待容器内 `sqlcmd SELECT 1` 成功，并让 `WarehouseDbContextFactory` 使用 `ConnectionStrings__WmsDb`；在本机 Docker Desktop 29.2.0 上完整运行 `dotnet ef database update`、健康检查和旧目录保护，质量门禁退出码为 0。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（报表口径、数据库备份保留期、恢复责任人和告警阈值尚未由运维负责人确认）；`FIELD_PENDING`（真实数据库恢复、PLC 离线/断电/急停、现场账实和网络恢复未执行）。
- 已知限制：报表读模型、健康探针默认值、Outbox/Inbox 重放计数和恢复证据为开发内存/临时文件契约；尚未接入 SQL 报表投影、真实 Worker 心跳存储、生产监控和真实设备报警采集。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 7.2：编写现场试运行和回滚手册

**Files:**
- Create: `docs/pilot-runbook.md`
- Create: `docs/rollback-runbook.md`
- Create: `tests/Warehouse.DeviceGateway.ContractTests/FieldRegressionChecklist.md`

- [x] 明确只读监控、单库区试点、双跑、人工对账和切换顺序。
- [x] 明确急停、断网、断电、PLC 重启、服务重启、任务未知结果和人工接管步骤。
- [x] 明确成功条件、停止条件、回滚条件、责任人、停机窗口和证据留存位置。
- [x] 先在模拟 PLC 和测试库完成回归，再由现场人员执行受控设备测试。
- [x] 未完成现场签字、真实设备回归和账实核对时，状态保持 `BLOCKED`。

**验收:** `FIELD_VERIFIED`；负责人签署试点结果和回滚演练，未签署不得切换生产主作业，不得修改旧 PLC 时序。

**Task 7.2 执行证据（2026-08-25）：** 新增 [`docs/pilot-runbook.md`](../../../docs/pilot-runbook.md)、[`docs/rollback-runbook.md`](../../../docs/rollback-runbook.md) 和 [`tests/Warehouse.DeviceGateway.ContractTests/FieldRegressionChecklist.md`](../../../tests/Warehouse.DeviceGateway.ContractTests/FieldRegressionChecklist.md)。文档冻结只读监控、单库区双跑、受控试点、人工对账、急停/断网/断电/PLC 重启/服务重启、任务未知结果和人工接管流程，并定义成功、停止、回滚条件、责任人、停机窗口和证据留存。回归清单把模拟 Accepted/Executing/Succeeded、Offline、超时/物理未知、重启查询、幂等重放和停止结果映射到现有自动化测试；真实 PLC 和现场回归未执行。

- 自动化状态：`AGENT_VERIFIED`（文档和模拟回归清单已完成；不代表现场设备验证）。
- 外部门禁：`HUMAN_PENDING`（负责人尚未确认试点范围、报警/完成码语义、停机窗口和签字责任）；`FIELD_PENDING`/`BLOCKED`（真实 PLC、急停、断网、断电、服务重启、账实核对和回滚签字未执行）。
- 已知限制：手册中的生产数据库恢复、现场物理停止和人工接管步骤是待签字作业流程；不能用模拟网关、自动化测试或源码默认值替代现场证据。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 十一、阶段 8：可选外部集成

### Task 8.1：定义版本化外部接口

**Files:**
- Create: `src/Warehouse.Wms.Application/Integrations/`
- Create: `src/Warehouse.Wms.Infrastructure/Integrations/`
- Create: `docs/integration-contract.md`
- Test: `tests/Warehouse.Wms.IntegrationTests/Integrations/`

- [x] 定义版本化入库通知、出库请求、取消请求、状态查询、结果回传和库存同步接口。
- [x] 每个请求包含来源、版本、幂等键和原始报文摘要。
- [x] 用 Outbox/待同步队列处理失败重试，不能重复执行 PLC 作业。
- [x] 关闭集成适配器后验证手工建单和本地仓储作业仍正常运行。

**验收:** `AGENT_VERIFIED`；ERP/MES 是可插拔客户端，不是 WMS 启动和运行前提。

**Task 8.1 执行证据（2026-08-25）：** 新增 Application 集成契约和命令服务、Infrastructure 开发内存 Outbox、API `/api/integrations/v1/*` 六类端点、集成契约文档及单元/集成测试。请求强制来源、`v1`、幂等键和原始 payload SHA-256 摘要；重复键返回 `AlreadyQueued`，不会生成第二条 Outbox，也不会直接创建 WMS 单据或 PLC 任务。集成默认关闭时返回 `404 INTEGRATION_DISABLED`，本地流程不受影响。生产启用前仍需接入 SQL Server Outbox 发布器和真实外部系统契约回归。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（外部字段映射、调用方身份和发布 SLA 尚未确认）；`FIELD_PENDING`（真实 ERP/MES 未连接，未执行现场联调）。
- 已知限制：当前 Outbox 为开发内存实现，未连接外部系统，不代表生产消息可靠性或业务字段映射已确认。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 8.2：修复 API 运行时组合和控制器依赖门禁

**Files:**
- Modify: `src/Warehouse.Wms.Api/Program.cs`
- Modify: `tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj`
- Create: `tests/Warehouse.Wms.IntegrationTests/Composition/ApiCompositionTests.cs`
- Modify: `PROJECT_DESIGN.md`

- [x] 默认注册 `SimulatedDeviceGateway`，通过 `IWarehouseDeviceGateway` 注入统一任务调度器；不连接真实 PLC。
- [x] 注册入库、出库、移库、盘点、异常、报表和导入控制器所需的完整应用服务组合。
- [x] 依赖当前 HTTP 用户的服务使用请求作用域，避免单例捕获 scoped 依赖。
- [x] 通过真实 `WebApplicationFactory` 启动 API，解析所有 MVC 控制器，并断言网关实现为模拟网关。

**验收:** `AGENT_VERIFIED`；API 在无 ERP、无真实 PLC 和无生产连接串时可启动，所有控制器构造依赖可解析；真实设备适配器未被默认启用。

**Task 8.2 执行证据（2026-08-25）：** `Program.cs` 新增模拟网关、任务调度器、入库/出库/移库/盘点/异常及相关应用服务注册；`ExceptionWorkItemService`、`StocktakingDifferenceService` 和人工确认服务按请求作用域注册。新增 `ApiCompositionTests` 使用 `WebApplicationFactory<Program>` 启动 API，检查 `/health/live`、解析所有控制器并断言 `IWarehouseDeviceGateway` 为 `SimulatedDeviceGateway`。先运行测试确认缺少网关注册的预期失败，再完成最小 DI 修复后通过。

- 自动化状态：`AGENT_VERIFIED`。2026-08-25 主代理复测：`dotnet restore Warehouse.Wms.sln`、`dotnet build Warehouse.Wms.sln --no-restore -m:1 -nodeReuse:false`、`dotnet test Warehouse.Wms.sln --no-build --no-restore` 和 `pwsh -NoProfile -File scripts/verify.ps1` 全部退出码为 0；95 个单元测试、35 个集成测试、37 个设备契约测试通过，Docker SQL Server 迁移已是最新，`/health/live` 与 `/health/ready` 返回 200，旧 `warehouse/` 保护检查通过。
- 外部门禁：`HUMAN_PENDING`（生产部署配置和真实设备启用审批待负责人确认）；`FIELD_PENDING`（真实 PLC 未连接，未执行现场回归）。
- 已知限制：当前部分业务服务仍使用开发内存实现，默认候选库位/装载点为空；本 Task 只保证运行时依赖可解析，不替代业务数据初始化和现场验证。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

## 十二、持续建设任务（第一版生产化缺口）

### Task 9.1：库存账 SQL Server 持久化

**前置条件:** Task 3.2、4.2 和 8.2 已达到 `AGENT_VERIFIED`；开发 SQL Server 容器可用；不得连接生产数据库。

**目标:** 将库存余额、库存流水和库存操作幂等键从仅内存契约提升为可恢复的 SQL Server 业务账，同时保留无数据库单元/设备契约测试所需的显式内存实现。

**允许修改范围:**
- `src/Warehouse.Wms.Application/Inventory/` 的存储抽象和服务组合
- `src/Warehouse.Wms.Infrastructure/Persistence/` 的 EF 实体、仓储、DbContext 和迁移
- `src/Warehouse.Wms.Api/Program.cs`、开发配置和健康探针组合
- `tests/Warehouse.Wms.UnitTests/Inventory/`、`tests/Warehouse.Wms.IntegrationTests/Inventory/`
- `PROJECT_DESIGN.md`、本计划和必要的操作说明

**必须完成:**
- [x] 定义 `IInventoryLedgerStore` 或等价边界；内存实现只用于显式开发/测试模式。
- [x] 增加余额、流水和幂等记录的 SQL Server 映射、唯一索引、数量/重量精度和乐观版本约束。
- [x] 每次库存操作在一个短事务内完成幂等校验、余额更新和流水写入；摘要冲突拒绝，重复键返回原事务。
- [x] 通过 `Wms:PersistenceMode=SqlServer` 显式启用持久化；没有连接串时默认模拟/内存模式仍可启动，生产配置禁止隐式回退。
- [x] 增加迁移、空库建库、服务重启重载、重复操作、并发版本冲突、负库存和流水重算测试。
- [x] 不把 PLC 调用或设备等待放入数据库事务，不修改 `warehouse/`。

**验收:** `AGENT_VERIFIED`；定向单元/集成测试、完整构建测试、Docker 迁移和健康检查通过；SQL Server 重启后库存账可重建，API 在显式 SQL 模式下不使用内存库存。

**外部门禁:** `HUMAN_PENDING`（生产数据库保留策略、并发隔离级别和运维责任待确认）；`FIELD_PENDING`（真实 PLC 与现场账实仍未验证）。

**Task 9.1 执行证据（2026-08-25）：** 新增 `IInventoryLedgerStore`、SQL Server EF 余额/流水实体、唯一 `BalanceKey` 和幂等索引、串行化短事务及乐观版本检查；迁移 `20260825133933_InventoryLedgerPersistence` 可从空库建立表。API 默认 `InMemory`，显式 `Wms:PersistenceMode=SqlServer` 时强制要求 `ConnectionStrings:WmsDb` 并注入 SQL 存储。补充了持久化快照重启、幂等重放、摘要冲突、负库存保护和合法负调整测试；Docker SQL Server 实测定向集成测试 1/1 通过，SQL 模式 API 健康端点 200/200。完整构建通过；全量测试 99 个单元、36 个集成和 37 个设备契约通过，未配置 SQL 测试连接时仅 1 个 SQL fixture 按配置动态跳过。旧 `warehouse/` 未修改。

### Task 9.2A：Outbox/Inbox SQL Server 持久化基础设施

**前置条件:** Task 4.2、4.3、8.1 和 9.1 已达到 `AGENT_VERIFIED`；开发 SQL Server 容器可用；不得连接生产数据库；本 Task 不接入调度器或现场 Worker。

**目标:** 将 Outbox/Inbox 消息实体从内存契约提升为可跨进程恢复的 SQL Server 消息账，保留消息去重、租约抢占、发布/处理和失败重试的短事务边界。

**允许修改范围:**
- `src/Warehouse.Wms.Infrastructure/Persistence/` 的消息映射、仓储、DbContext 和迁移
- `tests/Warehouse.Wms.IntegrationTests/Tasks/` 的 SQL Server 持久化测试
- `PROJECT_DESIGN.md`、本计划和必要的消息运维说明

**必须完成:**
- [x] 为 Outbox/Inbox 增加 EF Core 表映射、字段长度/时间类型、唯一业务索引和乐观并发版本字段。
- [x] 定义最小 `IOutboxMessageStore`/`IInboxMessageStore` 边界，覆盖入队幂等、claim 租约恢复、publish/process、fail 回退和重复消息去重。
- [x] 每个消息操作使用独立 SQL Server 短事务；设备调用、等待、轮询和回调不得处于事务中。
- [x] 生成可从现有迁移链和空开发库执行的 `OutboxInboxPersistence` 迁移。
- [x] 使用 `WMS_SQLSERVER_TEST_CONNECTION` 动态控制 SQL 集成测试；未配置时跳过，配置 Docker SQL Server 时覆盖重启/租约/版本冲突/重复消息行为。
- [x] 不修改 `warehouse/`，不把消息仓储接入后续调度器流程。

**验收:** `AGENT_VERIFIED`；消息重复入队不会产生第二条记录，旧租约可被新 Worker 抢占，错误 Worker 或过期 claim 不能发布/处理，失败可重试，旧 Inbox 结果不会覆盖较新结果；迁移、编译和定向 SQL 测试通过。

**Task 9.2A 执行证据（2026-08-25）：** 新增 `MessagePersistence.cs`，提供 `SqlServerMessageStore`、`IOutboxMessageStore` 和 `IInboxMessageStore`；`WarehouseDbContext` 映射 `OutboxMessages`/`InboxMessages`，建立 Outbox 幂等键、Inbox 消息 ID及幂等键+结果版本唯一索引、状态查询索引和 `Version` 并发令牌；生成迁移 `20260825140514_OutboxInboxPersistence`。新增 `MessagePersistenceTests` 覆盖 Outbox 重复入队、过期 claim 恢复、错误 Worker 拒绝、失败回退、Inbox 重复/旧版本去重、新版本处理及状态操作。先按 TDD 观察测试因仓储缺失而失败，完成实现后构建通过；设置 Docker SQL Server 连接时定向测试 3/3 通过，未设置连接时 3 项动态跳过。未修改调度器、PLC、ERP 或旧 `warehouse/`，未进行现场确认。

**已知风险:** 消息仓储已具备持久化和短事务契约，但当前 API/Worker 尚未将现有内存集成 Outbox 或调度器队列切换到该仓储；SQL Server 跨进程高并发、生产备份保留策略和真实设备消息语义仍需后续 Task 与人工/现场门禁确认。

### Task 9.3：外部集成 Outbox SQL 适配器与运行时组合

**前置条件:** Task 8.1、8.2、9.1 和 9.2A 已达到 `AGENT_VERIFIED`；开发 SQL Server 容器可用；不得连接生产数据库；本 Task 不实现发布 Worker，不修改 PLC 调度器。

**目标:** 将 `/api/integrations/v1` 的外部消息入口在 SQL Server 模式下切换为持久化 Outbox，复用 Task 9.2A 的 `IOutboxMessageStore`/`SqlServerMessageStore`；保留显式内存模式和集成关闭时的独立运行能力。

**允许修改范围:**
- `src/Warehouse.Wms.Infrastructure/Integrations/` 的 SQL 集成 Outbox 适配器及内存实现一致性修复
- `src/Warehouse.Wms.Infrastructure/Persistence/InventoryPersistenceServiceCollectionExtensions.cs` 的 SQL 组合注册
- `src/Warehouse.Wms.Api/Program.cs` 的 InMemory/SqlServer 运行时选择
- `tests/Warehouse.Wms.UnitTests/Integrations/`、`tests/Warehouse.Wms.IntegrationTests/Composition/` 的适配器和组合测试
- `PROJECT_DESIGN.md`、`docs/integration-contract.md`、`docs/status-dictionary.md`、本计划

**必须完成:**
- [x] SQL 集成 Outbox 委托 `IOutboxMessageStore` 入队，映射 `IntegrationMessage` 到 `OutboxMessage`，并保持 `Queued`/`AlreadyQueued` 结果语义。
- [x] 同一幂等键且报文摘要一致时安全重放；摘要冲突拒绝；`ExternalIntegrationsEnabled=false` 时不调用 outbox、不落库且本地 WMS 不受影响。
- [x] SQL 持久化组合注册 `SqlServerMessageStore`、`IOutboxMessageStore`、`IInboxMessageStore` 和 SQL 集成 Outbox；InMemory 模式继续注册内存实现。
- [x] 增加适配器映射、摘要冲突和 API/服务组合选择测试；不得连接生产。
- [x] 不实现发布 Worker，不接入调度器，不修改 PLC 或旧 `warehouse/`。

**验收:** `AGENT_VERIFIED`；定向单元和 API 组合测试通过，SQL 模式解析出 SQL 消息仓储与 SQL 集成 Outbox，内存模式和集成关闭行为保持通过；完整构建/测试和适用质量门禁通过。

**Task 9.3 执行证据（2026-08-25）：** 新增 `SqlServerIntegrationOutbox` 和共享集成消息序列化/摘要校验，内部复用 `IOutboxMessageStore`；SQL 持久化组合注册 `SqlServerMessageStore` 及 Outbox/Inbox 接口，API 仅在 `Wms:PersistenceMode=SqlServer` 时选择 SQL 集成 Outbox，默认 InMemory 模式保持原内存实现。新增 SQL 适配器映射、同键摘要冲突和 API SQL 组合测试；集成关闭路径仍在调用服务层提前返回 `Disabled`，不写 Outbox。定向集成单元测试 6 项、API 组合测试 4 项通过；未实现发布 Worker、未接入调度器，未修改 PLC 或旧 `warehouse/`。完整构建与全量测试待主代理独立复验。

**自动化状态:** `AGENT_VERIFIED`（待主代理独立复验）。
**外部门禁:** `HUMAN_PENDING`（生产集成启用和消息运维策略待负责人确认）；`FIELD_PENDING`（真实外部系统、设备消息语义和现场恢复未验证）。
**已知风险:** SQL outbox 已接入入口但尚无发布 Worker；跨进程发布、消息失败重试与外部系统回执仍需后续任务验证。摘要冲突由 SQL 消息仓储拒绝，生产幂等键范围和保留策略仍需确认。

### Task 9.4：设备命令 Outbox 前置持久化边界

**前置条件:** Task 4.3、9.2A 和 9.3 已达到 `AGENT_VERIFIED`；开发 SQL Server 容器可用；不得连接生产 PLC 或数据库。

**目标:** 调度器调用设备网关前先通过可选 Outbox 写入并 claim 设备命令；设备等待期间不持有数据库事务；确定性响应发布成功，失败或物理未知命令不得自动重发。

**允许修改范围:**
- `src/Warehouse.Wms.Application/Tasks/TaskCommandOutbox.cs`
- `src/Warehouse.Wms.Application/Tasks/TaskScheduler.cs`
- `src/Warehouse.Wms.Infrastructure/Persistence/SqlServerTaskCommandOutbox.cs`
- `src/Warehouse.Wms.Infrastructure/Persistence/InventoryPersistenceServiceCollectionExtensions.cs`
- `src/Warehouse.Wms.Api/Program.cs`
- `tests/Warehouse.Wms.UnitTests/Tasks/TaskSchedulerTests.cs`
- `PROJECT_DESIGN.md`、本计划和必要的状态说明

**必须完成:**
- [x] 设备调用前以任务幂等键写入并抢占 Outbox，claim、设备调用、发布/失败的顺序可测试。
- [x] 设备调用期间不持有数据库事务；Accepted、Executing 或 Succeeded 等确定性响应标记 Published。
- [x] 超时、异常、响应幂等键不匹配和 PhysicalStateUnknown 标记失败且 `nextAttemptAt=DateTimeOffset.MaxValue`，不得自动重发未知命令。
- [x] 无 Outbox 的内存测试和现有调度器构造方式保持兼容；SQL 模式注册 `SqlServerTaskCommandOutbox`。
- [x] 不实现完整任务实体 SQL 持久化、跨进程发布 Worker、重启后的命令恢复或现场 PLC 验证。

**验收:** `AGENT_VERIFIED`；TaskScheduler 定向测试、完整构建、全量测试、Docker 迁移、健康检查和旧目录保护通过。

**Task 9.4 执行证据（2026-08-25）：** 新增 `ITaskCommandOutbox`、命令序列化和 SQL 适配器；`TaskScheduler` 支持设备调用前 claim、确定性响应后 Published、失败/物理未知不可重发，并通过 `Wms:SchedulerWorkerId` 注入 Worker 标识。新增顺序和未知命令不重试测试。主代理独立验证：`dotnet restore Warehouse.Wms.sln`、`dotnet build Warehouse.Wms.sln --no-restore -m:1 -nodeReuse:false`、`dotnet test Warehouse.Wms.sln --no-build --no-restore`、Docker SQL Server 迁移、`pwsh -NoProfile -File scripts/verify.ps1` 全部通过；104 个单元、38 个集成（4 个 SQL fixture 未配置连接时跳过）和 37 个设备契约测试通过；健康端点 200；`git diff --name-only -- warehouse` 为空。

- 自动化状态：`AGENT_VERIFIED`。
- 外部门禁：`HUMAN_PENDING`（任务命令重试策略、生产 Worker 标识和消息保留待运维确认）；`FIELD_PENDING`（真实旧 PLC 任务号去重/查询、断网/重启和物理结果未验证）。
- 已知限制：任务实体、资源锁和命令发布 Worker 尚未完成 SQL 持久化/跨进程恢复；未知命令仍需设备对账或人工确认后处置。
- 旧系统：`warehouse/` 仅作只读参考，未修改。

### Task 9.5：任务实体、状态历史、资源锁和任务幂等键 SQL 持久化

**前置条件:** Task 4.1、4.2、4.3、4.4、9.1、9.2A、9.3 和 9.4 已达到 `AGENT_VERIFIED`；开发 SQL Server 容器可用；不得连接生产 PLC、生产数据库或 ERP。

**目标:** 将任务实体、任务状态历史、资源锁和任务幂等键从仅内存领域契约提升为可跨进程恢复的 SQL Server 业务真相，同时保留显式 InMemory 实现供单元/契约测试使用。

**允许修改范围:**
- `src/Warehouse.Wms.Application/Tasks/` 的任务/状态历史/资源锁持久化抽象和服务组合
- `src/Warehouse.Wms.Infrastructure/Persistence/` 的任务、状态历史、资源锁、幂等键 EF 映射、仓储、DbContext 和迁移
- `src/Warehouse.Wms.Api/Program.cs`、开发配置和健康探针组合
- `tests/Warehouse.Wms.UnitTests/Tasks/`、`tests/Warehouse.Wms.IntegrationTests/Tasks/`
- `PROJECT_DESIGN.md`、本计划和必要的运维/恢复说明

**必须完成:**
- [x] 定义 `ITaskPersistenceStore`、`IResourceLockStore` 或等价边界；内存实现只能通过显式测试/开发模式注册。
- [x] 增加 `WarehouseTask`、`TaskStateHistory`、`ResourceLock` 和 `TaskIdempotencyKey` 的 EF Core 映射、字段长度、UTC 时间、枚举转换、唯一索引和乐观版本约束。
- [x] 以任务号、资源业务键（`ResourceType + ResourceId`）和幂等唯一键建立数据库唯一约束；任务状态历史按任务和版本可追溯，禁止静默覆盖。
- [x] 创建任务、状态迁移及历史追加、幂等键登记、资源锁获取/续租/释放分别使用独立短事务；版本冲突、错误 owner/token、摘要冲突和活动锁冲突必须拒绝。
- [x] 支持租约到期后的安全抢占、续租和释放；未过期锁不得被新任务取得，过期或已释放锁不得被旧 owner 续租。
- [x] 服务重启后从 SQL 恢复未完成任务、状态历史、有效资源锁和幂等记录；不得在恢复时重复下发 PLC 命令或重复释放资源。
- [x] 增加迁移、空库建库、重启恢复、重复创建/重放、摘要冲突、锁并发/租约/版本冲突、状态历史完整性和任务状态版本冲突测试。
- [x] 不把 PLC 调用、设备等待、轮询或回调放入数据库事务，不修改 `warehouse/`。

**验收:** `AGENT_VERIFIED`（持久化基础设施）；定向单元/SQL 集成测试、完整构建测试和 Docker 迁移通过；SQL Server 重启后任务、历史、有效锁和幂等记录可恢复。调度器/业务服务消费该接口及未完成任务恢复仍由 Task 9.6 验收，完成前不得声称生产调度已脱离内存状态。

**外部门禁:** `HUMAN_PENDING`（任务保留、锁租约时长、并发隔离级别和运维清理策略待负责人确认）；`FIELD_PENDING`（真实 PLC 重启/断网、物理资源对账和现场恢复未验证）。

**执行记录（2026-08-26）:** terra 按 TDD 新增任务/锁持久化边界、SQL 仓储、EF 迁移和单元/SQL 集成测试。主代理修复导航 Include、EF 版本原值保存、生产模式隐式回退校验、测试数据库隔离和锁 token 强制校验；随后补充 Production 拒绝显式 InMemory 的配置门禁并实测启动失败；Docker SQL Server 实测迁移与重启恢复 1/1 通过；单元 116、集成 45（含 5 个 SQL 场景）、设备契约 37 通过；构建 0 警告/0 错误；`scripts/verify.ps1` 在 Development 配置下通过，健康端点 200，旧目录保护通过。自动化状态：`AGENT_VERIFIED`。已知限制：`TaskScheduler` 及业务闭环仍使用进程内调度状态，留待 Task 9.6；真实 PLC/现场恢复为 `FIELD_PENDING`。提交：`d49b6c0`、`92b74bc` 及配置门禁修复提交。推送 `target/agent/wms-implementation` 于 2026-08-26 因 GitHub 连接重置/无法连接失败，状态 `PUSH_PENDING`，不阻塞后续任务。

### Task 9.6：任务持久化运行时接入和重启恢复 Worker

**前置条件:** Task 9.5 已达到 `AGENT_VERIFIED`；不得连接生产 PLC/数据库；Task 9.5 的提交当前已记录为 `PUSH_PENDING`，不影响本地继续执行。

**目标:** 将 `TaskScheduler`、入库/出库/移库/盘点业务服务的任务创建、状态迁移、幂等登记和资源锁操作接入 `ITaskPersistenceStore`/`IResourceLockStore`，并实现服务重启后从 SQL 恢复未完成任务而不重复下发设备命令。

**必须完成:**
- [x] 调度器不再以 `TaskSchedulerState.Requests` 作为 SQL 模式业务真相；内存状态只作短期调度缓存。重启时恢复执行中设备占用，避免同设备队列并发。
- [x] 任务入队、Dispatching/SentToPlc/Executing/终态迁移、设备结果和异常处置均提交 SQL 状态历史与版本；工作流引用、调度快照和恢复状态同步持久化。
- [x] 业务锁获取/续租/释放使用持久化锁，设备等待期间不持有数据库事务。出库装载点锁和入库成功释放均通过 `IResourceLockStore`。
- [x] Worker 启动扫描未完成任务、对账 Outbox/Inbox 和设备任务号能力；无法确认的任务进入 `PhysicalStateUnknown`，不得重复下发。
- [x] 增加 API SQL 组合、跨进程调度、服务重启、并发版本冲突和设备命令不重复发送测试；Docker SQL Server 定向场景通过。

**验收:** `AGENT_VERIFIED`；SQL 模式业务闭环和重启恢复测试通过，内存模式仅在显式开发/测试配置可用；真实 PLC 恢复仍需 `FIELD_PENDING`。

**执行记录（2026-08-26）：** terra 修复有效工作流快照被启动恢复误阻塞的问题：有效 JSON 对象标记 `RecoveredFromSnapshot`，缺失或非法快照标记 `BlockedMissingBusinessState`；调度器通过 SQL `ResourceLock(ResourceType=Device)` 实现跨进程同设备租约，成功/明确失败释放，执行中或物理未知保留；Worker 启动恢复和盘点完成后的持久化装载点锁释放均有测试。新增/更新迁移 `20260825184844_TaskDispatchContextModel`、`20260825191213_WorkflowRecoveryContext`。主代理独立验证：构建 0 警告/0 错误；Unit 131、Integration 43（无 SQL 环境时 7 项跳过）、设备契约 37；Docker SQL Server 下任务持久化/重启/跨进程设备租约 3 项、消息持久化 3 项通过；`warehouse/` 无变化。当前状态 `DONE_WITH_CONCERNS`：任务元数据和设备调度可恢复，但入库、出库、移库、盘点业务聚合仍以进程内字典/对象为主，尚无完整 SQL 投影和跨进程重建；API 仍需从基础资料提供出库装载点，不能据此宣称生产业务闭环已完成。真实 PLC 恢复继续为 `FIELD_PENDING`。本地提交 `c350414` 已生成；向 `origin`/`target` 的推送因 GitHub 网络连接未完成，状态 `PUSH_PENDING`，不阻塞本地继续执行。

### Task 9.7A：业务聚合持久化基础设施与入库/出库恢复

**前置条件:** Task 9.6 的调度/设备恢复子集已通过主代理构建、测试、Docker SQL 重启/租约验收，且 sol 审查无 P0；Task 9.6 的业务聚合缺口正是本 Task 的范围；基础资料持久化边界已确定；不得连接生产 PLC、生产数据库或 ERP。

**目标:** 建立业务聚合快照、状态历史、幂等和关联任务的 SQL/InMemory 对等存储边界，并首先将入库、收货/上架和出库、分配/复核上下文接入；Worker 重启或跨进程启动时按版本重建这两类聚合，不得以仅恢复任务元数据代替业务恢复。

**必须完成:**

- [x] 为入库单/收货/上架、出库单/分配/复核及其明细定义持久化实体、状态历史、幂等键和关联任务引用；移库/盘点在 Task 9.7B 完成。
- [x] 为业务服务提供 SQL/InMemory 对等存储边界；所有库存变化继续通过库存账短事务完成，禁止设备等待处于事务中。
- [x] Worker 启动按入库/出库业务引用和快照版本重建聚合；缺失、冲突或校验失败进入异常工作项，不得自动释放资源或重复结算。
- [x] 增加 SQL 重启、跨进程并发、版本冲突、重复回放、锁释放和任务结果幂等测试，覆盖入库和出库闭环。

**验收:** `AGENT_VERIFIED`；SQL 空库迁移、API 组合、完整构建/测试、Docker 重启恢复和入库/出库业务闭环证据齐全。真实 PLC 和现场账实继续由 `FIELD_PENDING` 门禁管理。

**已知风险：** 当前 SQL 设备租约已验证顺序争抢和重启恢复；真实同时并发 `DispatchNextAsync` 仍可能触发 SQL Server deadlock victim，Task 9.7 的持久化实现必须增加死锁重试/锁顺序设计和并发验收，不得将顺序争抢测试当作完全并发安全证明。

**执行记录（2026-08-26）：** terra 完成业务快照通用存储、入库订单/明细/收货/PendingInbound 稳定身份恢复与失败回滚、上架任务及库位分配/设备上下文/资源锁引用恢复、出库任务/分配/复核恢复和 Worker 启动恢复顺序。主代理独立验证：解决方案构建 0 警告/0 错误；Unit 136 通过；入库/出库/上架/任务恢复定向测试 13 通过；Docker SQL Server 下业务快照测试 2 通过，空库迁移完成，`dotnet ef migrations has-pending-model-changes` 无变化；`git diff --check` 通过；`git diff --quiet -- warehouse` 通过。缺失设备任务号转入 `PhysicalStateUnknown`，不合成任务号；Worker 按 `TaskType=Putaway` 查找 `PutawayTask` 快照，缺失/非法业务快照阻断调度；开发装载点 ID 与 SQL seed 保持一致。自动化状态：`AGENT_VERIFIED`。外部门禁：`HUMAN_PENDING`、`FIELD_PENDING`（真实 PLC、现场账实和恢复演练未执行）。已知风险：两个 API composition 测试仍硬编码 `Server=127.0.0.1,1`，属于测试夹具环境失败；SQL 同时并发调度 deadlock、跨进程复核原子边界和真实主数据装载点读取留待后续持久化/主数据任务。旧 `warehouse/` 未见 diff，本 Task 未修改其内容。

### Task 9.7B：移库/盘点业务聚合持久化与恢复

**前置条件:** Task 9.7A 达到 `AGENT_VERIFIED`；不得连接生产 PLC、生产数据库或 ERP。

**目标:** 将移库单、盘点单及明细、设备结果、库存结算和资源锁上下文提升为 SQL 事实，并在 Worker 重启或跨进程启动时重建聚合。

**必须完成:**

- [x] 为移库单、盘点单及其明细定义版本化业务快照、状态和关联设备任务/资源锁引用；通用 `BusinessWorkflows` 表承载 SQL 事实，内存实现继续用于开发/测试。
- [x] 接入 `RelocationService`、`StocktakingService`、盘点差异/上架预约和锁释放的 SQL/InMemory 对等存储边界。
- [x] Worker 启动重建移库/盘点聚合；快照 JSON 非法或不完整时跳过业务重建并由任务恢复门禁标记为 `BlockedMissingBusinessState`，不重复发送设备命令。
- [x] 增加稳定身份、重复提交/重复完成、资源锁上下文和重启恢复测试；SQL 专用实体拆分、跨进程并发和 Docker SQL 验收仍待独立门禁。

**验收:** `AGENT_VERIFIED`；SQL 空库迁移、API 组合、完整构建/测试、Docker 重启恢复和移库/盘点业务闭环证据齐全。真实 PLC 和现场账实继续由 `FIELD_PENDING` 门禁管理。

**执行记录（2026-08-26，terra）：** 新增移库和盘点业务快照，保存订单/任务/明细稳定 ID、状态、设备任务号、优先级/尝试次数和资源锁引用；`RelocationService`、`StocktakingService` 支持 `RestoreAsync`，盘点差异与上架预约保存可重放状态；Worker 启动顺序增加移库/盘点恢复，并通过 scoped recovery scope 恢复盘点差异服务，任务缺失快照映射到阻塞门禁。新增移库/盘点恢复单元测试和 Docker SQL 移库重启测试；业务快照 SQL 保存增加 deadlock/唯一键竞争有限重试，并新增跨进程同版本写入测试。完整构建 0 警告/0 错误，Unit 139 通过，移库集成 2 通过，SQL 盘点锁释放 1 通过，SQL 业务快照 3 通过，SQL 移库重启 1 通过，EF 模型无待迁移变更。当前仍有风险：业务事实使用通用快照而非独立 EF 表，真实 PLC/现场账实保持 `FIELD_PENDING`。

### Task 9.8A：业务快照 SQL 并发与 API 组合夹具硬化

**前置条件：** Task 9.7A/9.7B 的通用业务快照已可用；不得连接生产 PLC、生产数据库或 ERP。

**允许修改范围：** `SqlServerBusinessWorkflowStore`、业务快照 SQL 集成测试、API composition 测试夹具、本文档和项目设计书；不拆分专用业务实体，不修改 PLC/设备时序和旧 `warehouse/`。

**必须完成：**

- [x] 对 `SaveAsync`、`RegisterIdempotencyAsync` 及必要的读取/删除边界增加有限、可取消的 deadlock/唯一键竞争重试；不得吞掉业务版本冲突或摘要冲突。
- [x] 同一聚合同一版本并发写入最终只有一个成功，另一方明确返回 `BusinessWorkflowConcurrencyException` 或等价冲突；历史版本不得重复或静默覆盖。
- [x] 增加 SQL 集成测试覆盖同版本写入、同幂等键同摘要重放、同幂等键不同摘要拒绝、竞争重试上限和取消传播。
- [x] API composition SQL 测试只能使用 `WMS_SQLSERVER_TEST_CONNECTION` 或明确的无 SQL 夹具；未配置 SQL 时稳定跳过，不得回退到伪造连接。

**验收：** `AGENT_VERIFIED`；构建、完整单元/集成测试和 SQL 夹具检查通过，真实 PLC/现场账实保持 `FIELD_PENDING`。

**外部门禁：** `HUMAN_PENDING`（并发策略和运维告警尚未由负责人确认）；`FIELD_PENDING`（真实 SQL 生产拓扑、PLC 和现场恢复未验证）。

**执行记录（2026-08-26）：** terra 完成 SQL 业务快照保存、幂等注册和幂等删除的最多 3 次 deadlock/唯一键竞争重试，重试由取消令牌控制且不吞掉版本/摘要冲突；新增同版本并发写入并断言最终版本/单条历史、幂等竞争重放/摘要冲突、确定性重试上限和中途取消测试；API composition SQL 测试改为仅使用 `WMS_SQLSERVER_TEST_CONNECTION`，未配置时稳定跳过。主代理复核提交 `8274f44`、`6f87f2c`：构建 0 警告/0 错误；Unit 139 通过；Integration 63 通过（SQL 环境全量运行）；设备契约 37 通过；Docker SQL 迁移、健康检查和 `scripts/verify.ps1` 质量门禁通过；`warehouse/` 无 tracked diff。自动化状态：`AGENT_VERIFIED`。外部门禁：`HUMAN_PENDING`（生产并发策略和告警待确认）；`FIELD_PENDING`（真实 SQL 拓扑、PLC 与现场账实未验证）。

### Task 9.8B：装载点基础资料目录与出库运行时读取

**前置条件：** Task 3.1 基础资料迁移、Task 5.4 出库任务和 Task 9.8A 已达到 `AGENT_VERIFIED`；不得连接生产 PLC、生产数据库或 ERP。

**目标：** 消除 API 对单个装载点 GUID 的硬编码。出库服务通过目录抽象读取 WMS 自有 `LoadingPoints` 基础资料；SQL 模式从 `WarehouseDbContext` 查询，开发/契约模式仅使用显式样例目录，不把现场映射或旧 PLC 地址写成已确认事实。

**允许修改范围：** `src/Warehouse.Wms.Application/Outbound/` 的目录契约和出库服务、`src/Warehouse.Wms.Infrastructure/Persistence/` 的基础资料读取适配器、`src/Warehouse.Wms.Api/Program.cs` 的 DI 组合、出库 API/组合测试、`PROJECT_DESIGN.md`、本计划；不得修改 PLC/设备时序或旧 `warehouse/`。

**必须完成：**

- [x] 定义 `ILoadingPointCatalog` 或等价只读边界，返回编码、禁用/锁定/占用/故障状态及稳定 ID；目录查询必须支持取消。
- [x] SQL 模式目录从 `LoadingPoints` 查询，不使用固定 GUID；开发/契约模式注册显式样例目录，且 SQL 连接缺失时不得隐式连接生产或伪造 SQL。
- [x] `OutboundTaskService` 提交任务时通过目录解析请求装载点，保留资源锁和可用性校验；删除 `Program.cs` 中的固定装载点实例。
- [x] 增加内存单元、SQL 读取和 API composition 测试，验证 seed 装载点可被请求、未知/禁用装载点被拒绝、取消令牌传播及无 SQL 环境稳定跳过。
- [x] 不修改 PLC、ERP 或旧 `warehouse/`；数据库模型无变化时不得生成无意义迁移。

**验收：** `AGENT_VERIFIED`；构建、定向/完整测试、Docker SQL 读取和质量门禁通过。真实装载点编码、报警语义和现场可用性继续保持 `HUMAN_PENDING`/`FIELD_PENDING`。

**外部门禁：** `HUMAN_PENDING`（装载点业务状态和统计口径待负责人确认）；`FIELD_PENDING`（真实点位映射、设备报警和 PLC 现场验证未执行）。

**执行记录（2026-08-26）：** 已新增 `ILoadingPointCatalog`、显式内存目录和 `SqlServerLoadingPointCatalog`；SQL 模式通过 `WarehouseDbContext.LoadingPoints` 读取 seed 装载点，API 不再硬编码装载点集合；占用从 `Pallets.CurrentLoadingPointId` 派生，未知运行时故障按不可用处理，开发模式显式注入模拟状态。已增加内存目录、SQL seed 读取、目录状态、出库提交缺失/故障拒绝、取消 token 观测和 SQL 托盘占用查询测试。sol 复审无 P0/P1，上一轮 P2 证据缺口已由提交 `1dcb625` 修正；主代理独立验证：Unit 142、Integration 67、设备契约 37 通过，构建 0 警告/错误，Docker SQL 迁移/占用读取、健康检查和 `scripts/verify.ps1` 质量门禁通过，`warehouse/` 无 tracked diff，无模型变化因此未生成迁移。自动化状态：`AGENT_VERIFIED`。外部门禁：`HUMAN_PENDING`（装载点业务状态和统计口径待确认）；`FIELD_PENDING`（真实点位映射、设备报警和 PLC 现场验证未执行）。提交 `1dcb625` 推送因 GitHub 连接重置失败，状态 `PUSH_PENDING`，不影响本地继续执行。

### Task 9.8C：SQL 调度器跨进程并发与死锁恢复

**前置条件：** Task 9.6、9.7B 和 9.8A 已达到 `AGENT_VERIFIED`；不得连接生产 PLC、生产数据库或 ERP。

**目标：** 在 Docker SQL Server 中验证两个或多个 Worker 同时 `DispatchNextAsync` 时的设备资源锁争抢、deadlock 重试、唯一结果和任务状态一致性；实现必须保持短事务，不得重复下发设备命令。

**必须完成：**

- [x] 增加可重复的跨进程并发测试，覆盖相同设备不同任务、不同设备并发和任务结果回写。
- [x] 对设备租约抢占/调度操作增加有限可取消重试或稳定的锁顺序；业务版本冲突和物理未知不得被重试吞掉。
- [x] 证明同一设备同一时刻最多一个 Worker 持有有效锁，失败 Worker 不产生第二次 PLC 提交。
- [x] 记录 deadlock/锁争抢观测，测试未配置 SQL 时稳定跳过，不伪造连接。

**验收：** `AGENT_VERIFIED`；Docker SQL 并发测试、完整质量门禁和旧目录保护通过。真实 PLC 结果、现场设备串行能力和生产隔离级别保持 `FIELD_PENDING`。

**执行记录（2026-08-26）：** 已完成 SQL 调度器跨进程并发测试与硬化：新增同设备并发租约唯一提交、不同设备并发提交、物理未知不重试及无 SQL 稳定跳过测试；`SqlServerTaskPersistenceStore` 对任务创建、状态历史和设备租约中的 deadlock、锁超时及唯一键竞争最多重试 3 次并支持取消，版本/业务冲突保持明确抛出；记录 `DeadlockRetryCount` 与 `LockContentionCount` 观测。Docker SQL 实跑：Task 9.8C 定向场景 6/6 通过，完整集成测试 70/70 通过；未配置 `WMS_SQLSERVER_TEST_CONNECTION` 时稳定跳过且不伪造连接。主代理质量门禁 restore/build/test、迁移、健康检查和旧目录保护均通过。自动化状态：`AGENT_VERIFIED`。真实 PLC、生产 SQL 隔离级别和现场账实仍为 `FIELD_PENDING`。

### Task 9.9：统计与点位读模型 SQL 持久化和周期 Worker

**前置条件：** Task 6.6、Task 9.8C 已达到 `AGENT_VERIFIED`；不得连接生产 PLC、生产数据库或 ERP。

**目标：** 将统计批次、统计指标和点位快照从进程内临时状态提升为可重建的 SQL 只读投影，并由可托管的后台 Worker 按小时/日/周/月周期生成；不改变库存、任务或 PLC 的业务所有权。

**允许修改范围：** `src/Warehouse.Wms.Domain/Reports/`、`src/Warehouse.Wms.Domain/Warehouse/`、`src/Warehouse.Wms.Application/Reports/`、`src/Warehouse.Wms.Application/Warehouse/`、`src/Warehouse.Wms.Infrastructure/Reports/`、`src/Warehouse.Wms.Infrastructure/Warehouse/`、`src/Warehouse.Wms.Infrastructure/Persistence/`、`src/Warehouse.Wms.Api/Program.cs`、相关控制器、迁移、单元/集成测试、`PROJECT_DESIGN.md`、本计划和 `docs/user-guide.md`；不得修改 PLC/设备时序或旧 `warehouse/`。

**必须完成：**

- [x] 为统计批次、指标明细、点位快照定义 EF Core 实体、版本/新鲜度字段、来源版本和统计/点位唯一幂等键；迁移可从空库执行。
- [x] 实现 SQL 读模型仓储和内存测试替身；重复批次安全重放，同周期不同来源版本拒绝，失败不得覆盖最近成功结果。
- [x] 将统计与点位查询 API 接入 SQL 模式读模型；SQL 模式不得隐式回退到内存，开发/契约模式仍可显式使用内存替身。
- [x] 增加可取消、可观测的周期 Worker；重启后能继续生成未完成周期，不重复写入已成功批次。
- [x] 点位快照按 `SourceVersion` 去重，保留 `PhysicalUnknown`、锁定、离线和过期状态；查询只读，不写库存、任务或 PLC。
- [x] 增加 SQL 并发、幂等、失败保留、重启恢复、权限和 API composition 测试；无 SQL 环境稳定跳过，不伪造连接。

**验收：** `AGENT_VERIFIED`；Docker SQL 空库迁移、统计/点位 SQL 查询、周期 Worker 重启和完整质量门禁通过。真实统计口径、刷新阈值、点位映射和设备报警语义继续保持 `HUMAN_PENDING`/`FIELD_PENDING`。

**执行记录（2026-08-26）：** 已完成 SQL 统计批次/趋势/任务状态和点位快照实体、唯一约束及 `StatisticsPointReadModels` 迁移；SQL 模式注册 `SqlServerStatisticsService` 与 `SqlServerPointReadModel`，内存模式保持显式替身；新增可取消、记录异常的 `StatisticsWorkerHostedService`，重复周期安全重放、点位按版本取最高观察并保留物理未知/过期状态。无 `WMS_SQLSERVER_TEST_CONNECTION` 时 SQL 集成测试稳定跳过；Docker SQL 实跑和完整质量门禁由主代理执行，真实统计口径与现场刷新阈值保持 HUMAN/FIELD_PENDING。
**审查修复（2026-08-26）：** Worker 按 `RunAt` 仅生成已完成周期并注入 `IStatisticsSource`，源不可用时不写入成功批次；点位 QueryAsync 将取消令牌传递至 EF 查询，点位 Upsert 与统计批次写入分别对唯一键及 deadlock 做有限、可取消重试，非唯一数据库异常继续抛出；补充 Worker RunAt/源不可用、取消和 SQL 集成覆盖（无 SQL 时稳定跳过）。
**统计源补全（2026-08-26）：** 新增 `SqlServerStatisticsSource`，从 WMS 自有 `InventoryBalances`、`InventoryTransactions`、`TaskStateHistories` 和 `Locations` 事实表计算 KPI、趋势、任务状态、异常数量和库位利用率；空事实集返回 `null`，任务成功率无终态时为 0%；SQL 模式仅注册真实源，InMemory/契约模式保留不可用替身；补充非零事实聚合单测和 SQL 集成测试（无 SQL 稳定跳过）。
**统计口径修正（2026-08-26）：** 任务事实按 `WarehouseTask.Id` 最新状态去重，避免状态迁移重复计数；库位利用率改为有库位库存数量除以库位容量总和，补充重复历史与容量利用率测试。
**仓库范围补全（2026-08-26）：** `StatisticsScheduleOptions.WarehouseCode` 支持可配置范围，Worker 写入带范围的批次；`SqlServerStatisticsSource` 按 Location→Rack→Aisle→Zone→Warehouse 关系过滤库存、流水和容量并返回范围，Program 仅 SQL 模式注入真实源；新增 Worker 范围单测。
**移库范围修复与最终验收（2026-08-26）：** 仓库范围下的 `Move` 流水同时按 `SourceLocationId`/`DestinationLocationId` 过滤，避免 `LocationId` 为空导致移库趋势丢失；Worker 在统计源已带范围时不再无条件覆盖为空。主代理独立验证：`dotnet restore`、`dotnet build` 0 警告/错误；全量测试 Unit 148 通过/2 跳过、Integration 50 通过/22 跳过、Device Contract 37 通过；显式 SQL 连接下报表集成测试 6/6 通过；Docker SQL 迁移已是最新，健康端点均返回 200，`scripts/verify.ps1` 质量门禁通过，`warehouse/` 无 tracked diff。统计终态口径、任务仓库归属和大数据量 SQL 下推聚合保留 `HUMAN_PENDING`/后续 Task。

### Task 9.10：统计任务仓库归属与终态 KPI 口径硬化

**前置条件：** Task 9.9 达到 `AGENT_VERIFIED`；不得连接生产 PLC、生产数据库或 ERP；不得修改旧 `warehouse/`、PLC 协议或设备时序。

**目标：** 修复统计源中任务范围和成功率口径：从任务持久化的设备调度上下文解析源/目标库位，沿 Location→Rack→Aisle→Zone→Warehouse 关系确定任务仓库归属；统计成功率、任务状态分布和异常数只使用周期内已确定的终态任务，未完成任务不进入成功率分母；指定仓库范围时无法解析归属的任务不得混入该仓库批次。

**允许修改范围：** `src/Warehouse.Wms.Application/Reports/`、`src/Warehouse.Wms.Infrastructure/Reports/`、必要的统计契约/测试、`PROJECT_DESIGN.md`、本计划和 `docs/user-guide.md`；如确需调整任务调度上下文，只能修改新 WMS 代码及对应迁移/测试，不得修改 PLC 或旧 `warehouse/`。

**必须完成：**

- [ ] 扩展任务统计事实，保留任务 ID、当前状态、更新时间和可解析的源/目标库位标识；非法或缺失调度上下文不得猜测仓库。
- [ ] SQL 模式按库位层级解析任务仓库归属；无仓库范围时保留所有可读任务，有仓库范围时只保留至少一个关联位置属于该仓库且归属可确定的任务。
- [ ] 成功率分母只包含 `Succeeded`、`Failed`、`TimedOut`、`Canceled`、`ManualIntervention` 等终态；未完成任务不降低成功率；异常数只按每个任务当前终态计数一次。
- [ ] 增加内存聚合和 SQL 集成测试：重复状态历史/任务去重、未完成任务不进分母、跨仓库任务不混入范围批次、缺失上下文不猜测、终态异常只计数一次。
- [ ] 保持统计批次幂等、失败保留、Worker 取消传播和 SQL/InMemory 显式模式；不得把测试空数据改成伪造成功率。

**验收：** `AGENT_VERIFIED`；统计定向测试、SQL 报表集成测试、完整 build/test、Docker 迁移、健康检查和 `warehouse/` 保护通过。大数据量服务端聚合优化另列后续 Task；真实统计阈值和现场设备状态继续保持 `HUMAN_PENDING`/`FIELD_PENDING`。

## 十三、阶段门禁和最终标准

### 13.1 阶段门禁

- **门禁 A：** Task 0.1-0.3 的规则、范围和现场基线由 Agent 整理并由负责人确认；未确认项为 `BLOCKED`。
- **门禁 B：** 阶段 1-2 的骨架、模拟器、适配器和契约测试为 `AGENT_VERIFIED`。
- **门禁 C：** 阶段 3 的基础资料、库存余额和流水可独立运行，迁移和并发测试通过。
- **门禁 D：** 阶段 4 的任务状态机、幂等、短事务、锁、取消、恢复和异常工作项测试通过。
- **门禁 E：** 阶段 5-6 在模拟 PLC 下完成入库、出库、移库、盘点、权限和异常闭环。
- **门禁 F：** 阶段 7.1 的本地恢复演练通过；阶段 7.2 必须 `FIELD_VERIFIED`。
- **门禁 G：** 阶段 8 集成关闭时，WMS 仍能独立运行。

### 13.2 第一版完成标准

第一版只有在以下证据全部存在时才算完成：

- 设计书、代码、数据库迁移、API 契约、自动化测试和操作说明一致。
- `scripts/verify.ps1` 退出码为 0，健康检查返回 200，迁移可重复执行。
- 无 ERP 连接串、无真实 PLC 时可用模拟网关完成核心流程。
- 手工和 Excel 入库/出库、复核、移库和盘点差异处理可独立完成；Excel 导入具备模板版本、整批校验、错误报告和幂等。
- PLC 离线、服务重启、超时和未知结果有可验证恢复路径。
- 取消、停止、物理状态未知和人工确认均不会错误释放库存或把任务伪装成成功。
- 现场试点、账实核对、回滚演练和负责人签字完成。
- 旧 `warehouse/` 目录没有被新系统修改。

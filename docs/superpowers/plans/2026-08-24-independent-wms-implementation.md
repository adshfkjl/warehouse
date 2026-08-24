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
- 验证：旧寄存器常量、连接参数、地址偏移、状态解析、心跳、读写超时和报警码来源已核对；所有未确认现场参数均标记为 `BLOCKED`；未连接真实 PLC。
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

- [ ] 创建 `Warehouse.Wms.sln` 和上述项目，所有命名空间统一使用 `Warehouse.Wms.*`。
- [ ] 让 Domain 不引用 EF Core、数据库、HTTP、PLC 或 UI。
- [ ] 让 Application 只依赖 Domain 和抽象接口。
- [ ] 让 DeviceGateway 只暴露设备任务和设备状态接口。
- [ ] 添加不含真实密钥、ERP 连接串和生产 PLC 地址的配置模板。
- [ ] 运行统一本地验收命令和 `dotnet list package --vulnerable`。

**验收:** `AGENT_VERIFIED`；无数据库、无 PLC、无 ERP 时 API 可启动健康检查；旧 `warehouse/` 目录无变更。

### Task 1.2：建立本地环境和质量门禁

**Files:**
- Create: `.editorconfig`
- Create: `Directory.Build.props`
- Create: `docker-compose.dev.yml`
- Create: `docs/development.md`
- Create: `scripts/verify.ps1`

- [ ] 在 `scripts/verify.ps1` 中按顺序执行 restore、build、test、迁移检查、健康检查和旧目录保护检查。
- [ ] 配置测试失败即失败、编译警告策略和独立测试数据库。
- [ ] 在 `docs/development.md` 写明模拟网关默认启用和 ERP 连接串可缺省。
- [ ] 运行 `pwsh -File scripts/verify.ps1`，记录所有命令退出码为 0。

**验收:** `AGENT_VERIFIED`；新开发者可在空环境按文档运行统一验证脚本。

## 五、阶段 2：设备契约、旧接口适配器和模拟器

### Task 2.1：定义设备操作和值对象

**Files:**
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceTask.cs`
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceOperationResult.cs`
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceTaskState.cs`
- Create: `src/Warehouse.Wms.Domain/Devices/DeviceCapability.cs`
- Create: `src/Warehouse.Wms.Application/Devices/IWarehouseDeviceGateway.cs`
- Test: `tests/Warehouse.DeviceGateway.ContractTests/DeviceTaskContractTests.cs`

- [ ] 定义 `SubmitInboundAsync`、`SubmitOutboundAsync`、`SubmitTransferAsync`、`GetStatusAsync` 和 `TestConnectionAsync`。
- [ ] 请求必须包含幂等键、WMS 任务号、设备编号、源/目标库位、装载点和协议版本。
- [ ] 设备结果必须区分 `Accepted`、`Executing`、`Succeeded`、`Failed`、`TimedOut`、`Offline`、`Unknown`。
- [ ] 将设备接口方法命名为 `RequestStopAsync`；未下发任务的业务取消由 WMS 状态机处理，不调用设备停止。
- [ ] 定义 `DeviceCapability`：`TaskKeyDeduplication`、`TaskQuery`、`StopControl`、`CompletionCallback`。
- [ ] 定义设备结果观察对象，包含设备任务号、结果版本、来源（`Polling`/`Callback`）和观测时间。
- [ ] 为重复提交、超时、离线和未知结果写失败测试，再实现最小契约。

**验收:** `AGENT_VERIFIED`；WMS 抽象中不出现寄存器地址、NModbus、ERP 表或存储过程名称，并明确旧接口能力不足时不得宣称“恰好执行一次”。

### Task 2.2：实现模拟设备网关

**Files:**
- Create: `src/Warehouse.DeviceGateway/SimulatedDeviceGateway.cs`
- Create: `src/Warehouse.DeviceGateway/Simulation/SimulationScenario.cs`
- Test: `tests/Warehouse.DeviceGateway.ContractTests/SimulatedDeviceGatewayTests.cs`

- [ ] 支持可配置延迟、成功、失败、离线、超时、报警、重复请求和服务重启恢复。
- [ ] 模拟器必须先记录命令幂等键，再返回结果，重复命令不能产生第二次物理动作。
- [ ] 模拟停止流程返回 `StopConfirmed`、`StopFailed` 或 `PhysicalStateUnknown`；未下发命令由 WMS 直接取消。
- [ ] 模拟器分别覆盖“支持任务号去重/查询”和“不支持任务号去重/查询”两种设备能力。
- [ ] 编写每个结果的契约测试并运行测试项目。

**验收:** `AGENT_VERIFIED`；无真实 PLC 可以稳定复现成功、失败、超时和未知结果。

### Task 2.3：实现旧 PLC API 适配器

**Files:**
- Create: `src/Warehouse.DeviceGateway/LegacyPlcApiGateway.cs`
- Create: `src/Warehouse.DeviceGateway/Legacy/LegacyPlcClient.cs`
- Create: `src/Warehouse.DeviceGateway/Legacy/LegacyCapabilityProbe.cs`
- Test: `tests/Warehouse.DeviceGateway.ContractTests/LegacyPlcApiGatewayTests.cs`
- Modify: `docs/field-baseline.md`

- [ ] 将旧 API 的入库、出库、移库、状态读取和连接测试映射到设备契约。
- [ ] 将 HTTP 错误、PLC 忙、离线、超时和完成状态转换为统一结果。
- [ ] 探测并记录旧 API 是否接受 WMS 任务号、是否支持按任务号查询、是否支持停止和是否提供回调；未知能力标记为 `BLOCKED`。
- [ ] 适配器只调用旧设备能力，不调用 ERP 单据查询、库存回写或业务存储过程。
- [ ] 旧接口明确支持任务号去重且可按任务号查询时，才允许同一命令自动重试。
- [ ] 请求发送后超时且无法按任务号查询时，立即返回 `PhysicalStateUnknown`，禁止自动重试、释放资源或再次下发，直到设备对账或人工确认。
- [ ] 为调用超时、重复幂等键、旧 API 返回“触发成功但未完成”和能力不足场景写测试。
- [ ] 运行契约测试并检查 `git diff --name-only -- warehouse` 为空。

**验收:** `AGENT_VERIFIED`；模拟器和旧 API 适配器都通过同一组契约测试，并输出旧接口能力矩阵。只有现场确认具备任务号去重/查询时，相关能力才可标记 `HUMAN_CONFIRMED`；否则统一采用超时未知策略。

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

- [ ] 建立仓库、库区、巷道、货架、库位、装载点、设备、物料、容器和托盘实体。
- [ ] 为库位编码、托盘编码、设备编号和装载点建立唯一约束。
- [ ] 为库位增加容量、重量、尺寸、禁用、锁定和现场映射字段。
- [ ] 为托盘增加版本号和当前归属状态，禁止同一托盘双占用。
- [ ] 写实体约束测试，生成第一版迁移和开发样例种子。

**验收:** `AGENT_VERIFIED`；数据库可独立建立基础资料，不读取 ERP 表。

### Task 3.2：建立库存流水和余额

**Files:**
- Create: `src/Warehouse.Wms.Domain/Inventory/InventoryBalance.cs`
- Create: `src/Warehouse.Wms.Domain/Inventory/InventoryTransaction.cs`
- Create: `src/Warehouse.Wms.Domain/Inventory/InventoryStatus.cs`
- Create: `src/Warehouse.Wms.Application/Inventory/InventoryService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Inventory/InventoryServiceTests.cs`

- [ ] 定义可用、锁定、待入库、待出库、冻结和异常库存状态。
- [ ] 每次增加、减少、锁定、解锁、移库和调整都写不可篡改流水。
- [ ] 用幂等键和并发版本防止重复扣减、负库存和重复流水。
- [ ] 将来源单据、任务号、操作人、原因和时间写入流水。
- [ ] 编写重复请求、超量扣减、并发锁定、事务回滚和从流水重算余额测试。

**验收:** `AGENT_VERIFIED`；库存余额可从流水重算，控制器不能直接修改库存数量。

## 七、阶段 4：任务状态机、调度器、资源锁和恢复

### Task 4.1：定义任务状态和状态历史

**Files:**
- Create: `src/Warehouse.Wms.Domain/Tasks/TaskState.cs`
- Create: `src/Warehouse.Wms.Domain/Tasks/WarehouseTask.cs`
- Create: `src/Warehouse.Wms.Domain/Tasks/TaskStateHistory.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Tasks/TaskStateMachineTests.cs`

- [ ] 定义 `Created`、`Allocated`、`Queued`、`Dispatching`、`SentToPlc`、`Executing`、`Succeeded`、`Failed`、`TimedOut`、`Canceled`、`CancelRequested`、`StopRequested`、`StopConfirmed`、`StopFailed`、`PhysicalStateUnknown` 和 `ManualIntervention`。
- [ ] 为每条状态迁移定义前置状态、操作者、原因、错误码和时间。
- [ ] 禁止从 `SentToPlc`/`Executing` 直接把任务标记为物理成功的取消结果。
- [ ] 编写非法跳转、重复完成、重复取消和未知结果测试。

**验收:** `AGENT_VERIFIED`；状态机能表达“取消未下发”和“设备可能仍在动作”两种不同语义。

### Task 4.2：实现幂等、资源锁和短事务提交

**Files:**
- Create: `src/Warehouse.Wms.Domain/Tasks/TaskIdempotencyKey.cs`
- Create: `src/Warehouse.Wms.Domain/Tasks/ResourceLock.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Persistence/OutboxMessage.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Persistence/InboxMessage.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Tasks/TaskConcurrencyTests.cs`

- [ ] 为任务命令、设备提交和设备回调建立唯一幂等键。
- [ ] 为库位、托盘、装载点和设备建立带过期时间的资源锁及乐观版本号。
- [ ] 每次状态变化使用独立短数据库事务；禁止事务跨越 PLC 下发、等待和轮询过程。
- [ ] 用 Outbox 发布设备命令，用 Inbox 去重设备结果消息；结果来源可以是轮询或可选回调。
- [ ] 测试并发分配、服务重启、重复轮询结果、重复回调、消息重复和锁过期恢复。

**验收:** `AGENT_VERIFIED`；具备任务号去重/查询能力的设备命令可安全重试；不具备该能力的超时命令进入 `PhysicalStateUnknown` 且不得自动重试，数据库不存在跨 PLC 长事务。

### Task 4.3：实现任务调度器和恢复 Worker

**Files:**
- Create: `src/Warehouse.Wms.Application/Tasks/TaskScheduler.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Background/TaskWorker.cs`
- Create: `src/Warehouse.Wms.Application/Tasks/DeviceResultProcessor.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Tasks/TaskSchedulerTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Tasks/TaskRecoveryTests.cs`

- [ ] 实现同一设备串行、优先级、有限重试、超时和资源锁释放。
- [ ] 只从 Outbox 取待发送命令，发送成功后短事务写 `SentToPlc`。
- [ ] 第一版以 `GetStatusAsync` 定时轮询为主；若旧接口支持回调，回调先写 Inbox，不直接更新业务状态。
- [ ] 轮询和回调都转换为同一个 `DeviceObservation`，按设备任务号、结果版本和幂等键去重后交给 `DeviceResultProcessor`。
- [ ] Worker 重启后先查询设备状态和任务幂等键，再决定重试、等待或进入 `PhysicalStateUnknown`；旧接口没有任务号查询能力时不得自动重试超时命令。
- [ ] 设备离线、未知结果和无法对账时进入异常队列，不自动释放库存锁。
- [ ] 测试杀进程、恢复、重复发送、轮询/回调同时到达、超时和人工接管。

**验收:** `AGENT_VERIFIED`；服务重启不会重复下发，也不会丢失未完成任务。

### Task 4.4：实现取消、停止和人工结案协议

**Files:**
- Modify: `src/Warehouse.Wms.Domain/Tasks/TaskState.cs`
- Create: `src/Warehouse.Wms.Application/Authorization/ICurrentUser.cs`
- Create: `src/Warehouse.Wms.Application/Authorization/IRiskAuthorizationService.cs`
- Create: `src/Warehouse.Wms.Application/Tasks/TaskCancellationService.cs`
- Create: `src/Warehouse.Wms.Application/Tasks/PhysicalResultConfirmationService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Tasks/TaskCancellationTests.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Authorization/FakeRiskAuthorizationServiceTests.cs`

- [ ] `Created/Allocated/Queued` 只能取消未下发任务并释放资源。
- [ ] `SentToPlc/Executing` 只能提交停止请求，等待 `StopConfirmed`、`StopFailed` 或 `PhysicalStateUnknown`。
- [ ] `PhysicalStateUnknown` 不得直接释放库位、托盘和库存锁。
- [ ] 将“人工完成”命名为“人工确认物理结果并结案”，强制调用 `IRiskAuthorizationService` 二次授权，并填写原因、设备状态、托盘实际位置、源/目标库位核对和库存校正流水。
- [ ] 先用测试实现提供 `ICurrentUser` 和 `IRiskAuthorizationService`；Task 6.3 再接入真实用户、角色和 JWT，不得因此跳过权限校验。
- [ ] 测试取消竞态、停止失败、人工确认缺字段和重复确认。

**验收:** `AGENT_VERIFIED`；人工结案不是简单把状态改为 `Succeeded`，并能完整审计。

### Task 4.5：异常工作项和处置中心

**Files:**
- Create: `src/Warehouse.Wms.Domain/Exceptions/ExceptionWorkItem.cs`
- Create: `src/Warehouse.Wms.Domain/Exceptions/ExceptionType.cs`
- Create: `src/Warehouse.Wms.Application/Exceptions/ExceptionWorkItemService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/ExceptionsController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Exceptions/ExceptionWorkItemTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Exceptions/ExceptionIdempotencyTests.cs`

- [ ] 定义异常来源、类型、严重程度、关联任务、设备任务号和当前物理状态。
- [ ] 记录受影响的托盘、库位、装载点、库存余额和资源锁。
- [ ] 支持重试、请求停止、重新分配、人工确认物理结果和库存校正等处置动作；每个动作复用任务状态机和权限抽象。
- [ ] 强制记录操作人、原因、处理前后状态、设备观察和审计信息。
- [ ] 用 `source + externalKey + taskId` 合并重复异常，重复告警不得创建多个活动工作项。
- [ ] 测试超时未知、重复告警、处置竞态、权限拒绝、锁未释放和关闭后再次告警。

**验收:** `AGENT_VERIFIED`；阶段 5 的“进入异常处置”均有真实实体、服务、接口和测试承接；未解决的物理未知任务不能被异常中心直接改成成功。

## 八、阶段 5：入库、出库和移库业务闭环

### Task 5.1：定义入库状态和建单

**Files:**
- Create: `src/Warehouse.Wms.Domain/Inbound/InboundOrder.cs`
- Create: `src/Warehouse.Wms.Domain/Inbound/InboundLine.cs`
- Create: `src/Warehouse.Wms.Domain/Inbound/InboundState.cs`
- Create: `src/Warehouse.Wms.Application/Inbound/InboundOrderService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Inbound/InboundOrderTests.cs`

- [ ] 定义 `Draft`、`Receiving`、`Received`、`PutawayQueued`、`Completed`、`Canceled` 和 `Exception`。
- [ ] 实现手工建单、明细收货、批次/有效期、托盘绑定和重量采集。
- [ ] 收货数量只能进入待入库库存，不得直接变为库位实存。
- [ ] 测试部分收货、重复收货、数量超限、托盘重复绑定和取消。

**验收:** `AGENT_VERIFIED`；可无 ERP 创建入库单和待上架库存。

### Task 5.1A：实现 Excel 入库/出库导入

**Files:**
- Create: `src/Warehouse.Wms.Application/Import/SpreadsheetImportService.cs`
- Create: `src/Warehouse.Wms.Application/Import/ImportErrorReport.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/ImportsController.cs`
- Create: `docs/templates/inbound-import.xlsx`
- Create: `docs/templates/outbound-import.xlsx`
- Test: `tests/Warehouse.Wms.UnitTests/Import/SpreadsheetImportTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Import/SpreadsheetImportApiTests.cs`

- [ ] 为入库和出库分别定义模板列、版本号、必填字段、数量精度、批次/有效期和托盘规则。
- [ ] 导入前完成整批校验，返回带行号、列名、错误码和修复建议的错误报告；任何错误行不得部分入账。
- [ ] 使用文件摘要加来源幂等键防止同一文件和同一业务单据重复创建。
- [ ] 导入成功只创建 WMS 单据，不直接提交 PLC 指令；后续流程复用 Task 5.2/5.4。
- [ ] 测试模板版本错误、重复文件、部分错误、数量超限、重复托盘和成功导入。

**验收:** `AGENT_VERIFIED`；Excel 导入与手工建单使用相同领域服务，错误报告可下载，导入不依赖 ERP。

### Task 5.2：实现入库库位分配和上架任务

**Files:**
- Create: `src/Warehouse.Wms.Application/Inbound/PutawayAllocationService.cs`
- Create: `src/Warehouse.Wms.Application/Inbound/PutawayTaskService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/InboundController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Inbound/PutawayAllocationTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Inbound/InboundApiTests.cs`

- [ ] 过滤停用、故障、锁定、占用、尺寸不符和重量超限库位。
- [ ] 支持自动推荐和人工指定两条路径，保存推荐原因。
- [ ] 创建任务时锁定待入库库存、托盘、目标库位和装载点。
- [ ] 通过阶段 4 调度器提交设备网关，不在业务代码中写寄存器。
- [ ] 测试目标库位冲突、装载点无货、设备离线和重复提交。

**验收:** `AGENT_VERIFIED`；模拟网关下可提交一条上架任务，库存仍保持待入库直到设备结果确认。

### Task 5.3：实现入库设备结果和库存提交

**Files:**
- Modify: `src/Warehouse.Wms.Application/Inbound/PutawayTaskService.cs`
- Create: `src/Warehouse.Wms.Application/Inbound/InboundReconciliationService.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Inbound/PutawayCompletionTests.cs`

- [ ] 收到设备成功后用短事务更新任务、托盘归属、库位状态、库存余额和库存流水。
- [ ] 设备失败或超时保持待入库库存并进入异常处置，不释放未确认的物理锁。
- [ ] 设备未知结果先查询状态和对账，再决定成功、失败或人工处置。
- [ ] 所有结果处理通过幂等键，重复回调不重复入账。
- [ ] 测试成功、失败、未知结果、重复回调和服务重启。

**验收:** `AGENT_VERIFIED`；数据库事务只覆盖本次状态提交，不跨 PLC 执行过程。

### Task 5.4：定义出库状态、分配和库存锁定

**Files:**
- Create: `src/Warehouse.Wms.Domain/Outbound/OutboundOrder.cs`
- Create: `src/Warehouse.Wms.Domain/Outbound/OutboundLine.cs`
- Create: `src/Warehouse.Wms.Domain/Outbound/OutboundState.cs`
- Create: `src/Warehouse.Wms.Application/Outbound/OutboundAllocationService.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Outbound/OutboundAllocationTests.cs`

- [ ] 定义 `Draft`、`Allocated`、`Locked`、`Picking`、`AwaitingReview`、`Completed`、`Canceled` 和 `Exception`。
- [ ] 支持 FIFO、FEFO、指定批次、指定托盘和指定库位。
- [ ] 先锁定库存和托盘，再创建下架任务；不得负库存或重复分配。
- [ ] 测试缺货、部分出库、并发锁定、重复请求和取消未下发任务。

**验收:** `AGENT_VERIFIED`；出库分配可独立运行，不依赖 ERP。

### Task 5.5：实现出库设备执行、装载点确认和复核

**Files:**
- Create: `src/Warehouse.Wms.Application/Outbound/OutboundTaskService.cs`
- Create: `src/Warehouse.Wms.Application/Outbound/OutboundReviewService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/OutboundController.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Outbound/OutboundWorkflowTests.cs`

- [ ] 出库下发前捕获托盘号、锁定装载点，并记录设备任务幂等键。
- [ ] 设备完成后先确认装载点托盘号、重量和物理状态，再进入复核。
- [ ] 复核通过后用短事务扣减库存、完成托盘流转和释放资源；差异进入异常流程。
- [ ] 设备失败、超时、取消和未知结果不得直接扣减库存。
- [ ] 测试模拟 PLC 成功、装载点占用、托盘号变化、复核差异、重复回调和重启。

**验收:** `AGENT_VERIFIED`；模拟 PLC 可完成出库闭环，业务库存扣减不依赖旧 API 的同步 `IsSuccess`。

### Task 5.6：实现移库和托盘位置原子更新

**Files:**
- Create: `src/Warehouse.Wms.Domain/Relocation/RelocationOrder.cs`
- Create: `src/Warehouse.Wms.Application/Relocation/RelocationService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/RelocationController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Relocation/RelocationServiceTests.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Relocation/RelocationWorkflowTests.cs`

- [ ] 校验源库位有货、目标库位为空、托盘归属正确、同一设备和资源锁可用。
- [ ] 目标托盘不是空目标库位的必填条件；跨 PLC/跨巷道能力按现场确认结果实现。
- [ ] 通过任务调度器提交 `SubmitTransferAsync`，不复制旧代码的 `inShelf` 源库位缺陷。
- [ ] 设备成功后用单一短事务更新源库位、目标库位、托盘位置和库存流水。
- [ ] 测试目标占用、源无货、跨设备拒绝、重复移库、失败、未知结果和重启。

**验收:** `AGENT_VERIFIED`；一次移库不产生双占用、丢托盘或重复库存流水。

## 九、阶段 6：盘点、权限、审计和最小界面

### Task 6.1：实现盘点范围和盘点任务

**Files:**
- Create: `src/Warehouse.Wms.Domain/Stocktaking/StocktakingTask.cs`
- Create: `src/Warehouse.Wms.Application/Stocktaking/StocktakingService.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/StocktakingController.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Stocktaking/StocktakingRangeTests.cs`

- [ ] 支持全库、库区、物料、批次、托盘范围和动态抽盘。
- [ ] 兼容旧基线的 `A1-A8`、`prefix-number` 闭区间和确定性排序，但范围规则配置化。
- [ ] 盘点下架复用任务调度器，逐条等待装载点，不跳过占用货框。
- [ ] 盘点任务保存账面数、实盘数、出库装载点、明细状态和状态历史。
- [ ] 测试空范围、重复任务、取消、设备离线、重试和服务重启。

**验收:** `AGENT_VERIFIED`；盘点下架不直接覆盖库存，设备结果可恢复。

### Task 6.2：实现盘点差异、复盘和调整审批

**Files:**
- Create: `src/Warehouse.Wms.Application/Stocktaking/StocktakingDifferenceService.cs`
- Create: `src/Warehouse.Wms.Domain/Stocktaking/StocktakingAdjustment.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Stocktaking/StocktakingDifferenceTests.cs`

- [ ] 支持明盘、盲盘、复盘、差异原因和冻结策略。
- [ ] 差异必须经授权确认后生成库存调整流水，不能直接更新余额。
- [ ] 对移动托盘的上架预约逐条幂等执行，区分“已发送”和“设备完成”。
- [ ] 测试重复确认、未授权调整、差异复盘和调整回滚。

**验收:** `AGENT_VERIFIED`；差异有完整审批、流水和审计记录。

### Task 6.3：补全认证、权限和审计

**Files:**
- Create: `src/Warehouse.Wms.Application/Identity/`
- Create: `src/Warehouse.Wms.Api/Controllers/UsersController.cs`
- Create: `src/Warehouse.Wms.Api/Controllers/RolesController.cs`
- Test: `tests/Warehouse.Wms.IntegrationTests/Identity/AuthorizationTests.cs`

- [ ] 实现 JWT Bearer、刷新令牌、退出、密码修改和账号禁用。
- [ ] 实现用户、角色、功能权限和仓库范围授权。
- [ ] 对库存调整、任务取消、停止请求、人工物理结果确认和差异审批增加二次权限。
- [ ] 将登录、权限变更、设备任务和高风险操作写入审计日志。

**验收:** `AGENT_VERIFIED`；未授权用户无法执行高风险操作，审计可追溯。

### Task 6.4：实现最小可用管理界面

**Files:**
- Create: `src/Warehouse.Wms.Web/`
- Create: `docs/user-guide.md`
- Test: `tests/Warehouse.Wms.IntegrationTests/Web/`

- [ ] 实现工作台、基础资料、入库、出库、库存、任务、盘点和异常页面。
- [ ] PDA 页面只负责扫码和业务操作，不在浏览器实现 PLC 控制时序。
- [ ] 展示任务状态历史、取消/停止语义、未知状态和人工确认所需字段。
- [ ] 编写操作员和主管的关键流程、权限和异常处置说明。

**验收:** `HUMAN_CONFIRMED` 前必须由负责人按操作说明走通核心流程；没有人工确认不能标记阶段完成。

## 十、阶段 7：报表、运维和现场试运行

### Task 7.1：实现报表、健康检查和恢复演练

**Files:**
- Create: `src/Warehouse.Wms.Api/Controllers/ReportsController.cs`
- Create: `src/Warehouse.Wms.Infrastructure/Health/`
- Create: `docs/operations.md`
- Create: `scripts/recovery-drill.ps1`

- [ ] 实现库存、库位利用率、入出库、移库、托盘追踪、盘点差异和设备报警报表。
- [ ] 增加 API、数据库、Worker、设备网关和 Outbox/Inbox 健康检查。
- [ ] 演练数据库恢复、服务重启、模拟 PLC 离线、超时、未知结果和消息重放。
- [ ] 运行统一本地验收命令、迁移命令、健康检查和恢复脚本。

**验收:** `AGENT_VERIFIED`；运维人员可判断各组件健康并完成无现场设备的恢复演练。

### Task 7.2：编写现场试运行和回滚手册

**Files:**
- Create: `docs/pilot-runbook.md`
- Create: `docs/rollback-runbook.md`
- Create: `tests/Warehouse.DeviceGateway.ContractTests/FieldRegressionChecklist.md`

- [ ] 明确只读监控、单库区试点、双跑、人工对账和切换顺序。
- [ ] 明确急停、断网、断电、PLC 重启、服务重启、任务未知结果和人工接管步骤。
- [ ] 明确成功条件、停止条件、回滚条件、责任人、停机窗口和证据留存位置。
- [ ] 先在模拟 PLC 和测试库完成回归，再由现场人员执行受控设备测试。
- [ ] 未完成现场签字、真实设备回归和账实核对时，状态保持 `BLOCKED`。

**验收:** `FIELD_VERIFIED`；负责人签署试点结果和回滚演练，未签署不得切换生产主作业，不得修改旧 PLC 时序。

## 十一、阶段 8：可选外部集成

### Task 8.1：定义版本化外部接口

**Files:**
- Create: `src/Warehouse.Wms.Application/Integrations/`
- Create: `src/Warehouse.Wms.Infrastructure/Integrations/`
- Create: `docs/integration-contract.md`
- Test: `tests/Warehouse.Wms.IntegrationTests/Integrations/`

- [ ] 定义版本化入库通知、出库请求、取消请求、状态查询、结果回传和库存同步接口。
- [ ] 每个请求包含来源、版本、幂等键和原始报文摘要。
- [ ] 用 Outbox/待同步队列处理失败重试，不能重复执行 PLC 作业。
- [ ] 关闭集成适配器后验证手工建单和本地仓储作业仍正常运行。

**验收:** `AGENT_VERIFIED`；ERP/MES 是可插拔客户端，不是 WMS 启动和运行前提。

## 十二、阶段门禁和最终标准

### 12.1 阶段门禁

- **门禁 A：** Task 0.1-0.3 的规则、范围和现场基线由 Agent 整理并由负责人确认；未确认项为 `BLOCKED`。
- **门禁 B：** 阶段 1-2 的骨架、模拟器、适配器和契约测试为 `AGENT_VERIFIED`。
- **门禁 C：** 阶段 3 的基础资料、库存余额和流水可独立运行，迁移和并发测试通过。
- **门禁 D：** 阶段 4 的任务状态机、幂等、短事务、锁、取消、恢复和异常工作项测试通过。
- **门禁 E：** 阶段 5-6 在模拟 PLC 下完成入库、出库、移库、盘点、权限和异常闭环。
- **门禁 F：** 阶段 7.1 的本地恢复演练通过；阶段 7.2 必须 `FIELD_VERIFIED`。
- **门禁 G：** 阶段 8 集成关闭时，WMS 仍能独立运行。

### 12.2 第一版完成标准

第一版只有在以下证据全部存在时才算完成：

- 设计书、代码、数据库迁移、API 契约、自动化测试和操作说明一致。
- `scripts/verify.ps1` 退出码为 0，健康检查返回 200，迁移可重复执行。
- 无 ERP 连接串、无真实 PLC 时可用模拟网关完成核心流程。
- 手工和 Excel 入库/出库、复核、移库和盘点差异处理可独立完成；Excel 导入具备模板版本、整批校验、错误报告和幂等。
- PLC 离线、服务重启、超时和未知结果有可验证恢复路径。
- 取消、停止、物理状态未知和人工确认均不会错误释放库存或把任务伪装成成功。
- 现场试点、账实核对、回滚演练和负责人签字完成。
- 旧 `warehouse/` 目录没有被新系统修改。

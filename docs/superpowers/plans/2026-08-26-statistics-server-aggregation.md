# Statistics Server Aggregation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 SQL 统计事实聚合下推到 SQL Server，降低大数据量周期统计的传输和内存开销，同时保持现有统计结果和业务边界不变。

**Architecture:** `SqlServerStatisticsSource` 负责可翻译的 SQL 聚合；`StatisticsAggregation.Build` 继续作为唯一 KPI/任务口径合并器。任务调度上下文因需要 JSON 解析而只读取周期内必要字段，并在应用层解析仓库归属。

**Tech Stack:** .NET 8、EF Core、SQL Server、xUnit、Docker SQL Server。

---

### Task 9.11：统计事实 SQL 聚合优化

**Files:**
- Modify: `src/Warehouse.Wms.Infrastructure/Reports/SqlServerStatisticsSource.cs`
- Test: `tests/Warehouse.Wms.UnitTests/Reports/StatisticsPointPersistenceTests.cs` 或同目录新增聚合单元测试
- Test: `tests/Warehouse.Wms.IntegrationTests/Reports/StatisticsPointSqlPersistenceTests.cs`
- Modify: `docs/superpowers/plans/2026-08-24-independent-wms-implementation.md`
- Modify: `PROJECT_DESIGN.md`

- [x] **Step 1: Write failing tests for SQL aggregate semantics**

  增加测试数据和断言，覆盖：空事实返回 `null`；零容量利用率为 `0`；入库/出库/移库数量与现有契约一致；仓库范围的 Move 同时按源库位和目标库位纳入；任务成功率仍只使用终态任务。

- [x] **Step 2: Run focused SQL tests and record the baseline**

  Run:

  ```powershell
  $env:WMS_SQLSERVER_TEST_CONNECTION='Server=127.0.0.1,14333;User Id=sa;Password=WmsDevOnly!123;TrustServerCertificate=True;Encrypt=False'
  dotnet test tests/Warehouse.Wms.IntegrationTests/Warehouse.Wms.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~StatisticsPointSqlPersistenceTests
  ```

  Expected: new aggregate assertions fail before the implementation changes.

- [x] **Step 3: Implement bounded SQL aggregation**

  Replace full fact collection in `SqlServerStatisticsSource.BuildAsync` with SQL projections for balance totals, transaction totals and location capacity/occupancy. Keep task context selection bounded by period and candidate warehouse locations, and pass all sums into the existing `StatisticsAggregation.Build` without changing its terminal-task logic.

- [x] **Step 4: Add a bounded-load integration assertion**

  Seed a large number of balances and transactions in a disposable Docker database, run `BuildAsync`, and assert the returned KPI totals. Instrument the query or use an EF command interceptor to assert that balance and transaction entity rows are not materialized as full fact collections.

- [x] **Step 5: Run focused and full verification**

  Run the focused SQL test, then:

  ```powershell
  dotnet restore Warehouse.Wms.sln
  dotnet build Warehouse.Wms.sln --no-restore -m:1 -nodeReuse:false
  dotnet test Warehouse.Wms.sln --no-build --no-restore
  dotnet ef database update --project src/Warehouse.Wms.Infrastructure --startup-project src/Warehouse.Wms.Api --connection $env:WMS_SQLSERVER_TEST_CONNECTION
  pwsh -NoProfile -File scripts/verify.ps1
  ```

  Expected: 0 build errors, 0 test failures, migration current, both health checks 200, quality gate exit code 0, and no `warehouse/` diff.

- [x] **Step 6: Update execution evidence and commit**

  Record the exact test counts, SQL migration result, quality gate result, known `HUMAN_PENDING`/`FIELD_PENDING` items and any push failure in the main implementation plan. Commit only the Task 9.11 files with message `feat(wms): push statistics aggregation to sql`.

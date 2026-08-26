using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Infrastructure.Persistence;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.IntegrationTests.Reports;

public sealed class StatisticsPointSqlPersistenceTests
{
    [SqlServerFact]
    public async Task Sql_read_models_survive_restart_and_keep_versions()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsReadModels_{Guid.NewGuid():N}" };
        using var factory = new PooledFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();
        var statistics = new SqlServerStatisticsService(factory); var start = new DateTimeOffset(2026,8,26,0,0,0,TimeSpan.Zero);
        var request = new StatisticsBatchRequest(StatisticsPeriod.Day, start, start.AddDays(1), "v1", Kpi: new StatisticsKpi(1, 2, 3, 4, 5, 6, 7, 8, 9));
        var first = await statistics.GenerateAsync(request);
        Assert.Equal(first.BatchId, (await new SqlServerStatisticsService(factory).GenerateAsync(request)).BatchId);
        var points = new SqlServerPointReadModel(factory); var now=DateTimeOffset.UtcNow;
        await points.UpsertAsync(new WarehousePointSnapshot("WH","Z","A","R",1,"L","PhysicalUnknown","P",null,null,null,0,0,now,2,true,"unknown",null,null));
        Assert.Equal("PhysicalUnknown", Assert.Single(points.Query()).Status);
    }

    [SqlServerFact]
    public async Task Sql_statistics_source_generates_nonzero_kpi_from_wms_facts()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsStatsFacts_{Guid.NewGuid():N}" };
        using var factory = new PooledFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.InventoryBalances.Add(new InventoryBalanceEntity { Id = Guid.NewGuid(), BalanceKey = "fact-1", MaterialId = Guid.NewGuid(), Quantity = 12, WeightKg = 24, Status = InventoryStatus.Available, Version = 1 });
            await db.SaveChangesAsync();
        }
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var request = await new SqlServerStatisticsSource(factory).BuildAsync(StatisticsPeriod.Day, start, start.AddDays(1), "v1");
        Assert.NotNull(request);
        Assert.Equal(12, request!.Kpi!.InventoryQuantity);
    }

    [SqlServerFact]
    public async Task Sql_statistics_source_scopes_terminal_tasks_by_resolved_dispatch_location()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsStatsTasks_{Guid.NewGuid():N}" };
        using var factory = new PooledFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Tasks.Add(CreateTask("STAT-SUCCEEDED", TaskState.Succeeded, "{\"SourceLocation\":\"DEV-A01-R01-001\",\"DestinationLocation\":null}", now));
            db.Tasks.Add(CreateTask("STAT-FAILED", TaskState.Failed, "{\"SourceLocation\":\"DEV-A01-R01-001\"}", now));
            db.Tasks.Add(CreateTask("STAT-EXECUTING", TaskState.Executing, "{\"DestinationLocation\":\"DEV-A01-R01-001\"}", now));
            db.Tasks.Add(CreateTask("STAT-UNKNOWN", TaskState.Failed, "[]", now));
            await db.SaveChangesAsync();
        }
        var request = await new SqlServerStatisticsSource(factory, "DEV").BuildAsync(StatisticsPeriod.Day, now.AddMinutes(-1), now.AddMinutes(1), "v1");
        Assert.NotNull(request);
        Assert.Equal(50, request!.Kpi!.TaskSuccessRatePercent);
        Assert.Equal(1, request.Kpi.ExceptionCount);
        Assert.DoesNotContain(request.TaskStates!, x => x.State == nameof(TaskState.Executing));
    }

    private static WarehouseTask CreateTask(string number, TaskState target, string context, DateTimeOffset at)
    {
        var task = new WarehouseTask(number, "Putaway", at);
        task.SetDispatchContext(context);
        task.TransitionTo(TaskState.Allocated, "test", "allocated", occurredAt: at);
        task.TransitionTo(TaskState.Queued, "test", "queued", occurredAt: at);
        if (target == TaskState.Failed) { task.TransitionTo(TaskState.Failed, "test", "failed", occurredAt: at); return task; }
        task.TransitionTo(TaskState.Dispatching, "test", "dispatching", occurredAt: at);
        task.TransitionTo(TaskState.SentToPlc, "test", "sent", occurredAt: at);
        task.TransitionTo(target, "test", target.ToString(), occurredAt: at);
        return task;
    }
    private sealed class PooledFactory(DbContextOptions<WarehouseDbContext> options) : IDbContextFactory<WarehouseDbContext>, IDisposable
    { public WarehouseDbContext CreateDbContext()=>new(options); public ValueTask<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken=default)=>new(new WarehouseDbContext(options)); public void Dispose() { } }
    private sealed class SqlServerFactAttribute : FactAttribute { public SqlServerFactAttribute(){ if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION"))) Skip="Set WMS_SQLSERVER_TEST_CONNECTION to run SQL Server read-model tests."; } }
}

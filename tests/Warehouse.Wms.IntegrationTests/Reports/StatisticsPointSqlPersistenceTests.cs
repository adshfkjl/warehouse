using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Infrastructure.Persistence;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Domain.Tasks;
using WarehouseEntity = Warehouse.Wms.Domain.MasterData.Warehouse;

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
        var developmentLocationId = Guid.Parse("00000000-0000-0000-0000-000000000005");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Tasks.Add(CreateTask("STAT-SUCCEEDED", TaskState.Succeeded, $"{{\"SourceLocation\":\"{developmentLocationId:D}\",\"DestinationLocation\":null}}", now));
            db.Tasks.Add(CreateTask("STAT-FAILED", TaskState.Failed, "{\"SourceLocation\":\"DEV-A01-R01-001\"}", now));
            db.Tasks.Add(CreateTask("STAT-EXECUTING", TaskState.Executing, "{\"DestinationLocation\":\"DEV-A01-R01-001\"}", now));
            db.Tasks.Add(CreateTask("STAT-UNKNOWN", TaskState.Failed, "[]", now));
            db.Tasks.Add(CreateTask("STAT-BAD-LOCATION", TaskState.Failed, "{\"SourceLocation\":\"NO-SUCH-LOCATION\"}", now));
            await db.SaveChangesAsync();
        }
        var request = await new SqlServerStatisticsSource(factory, "DEV").BuildAsync(StatisticsPeriod.Day, now.AddMinutes(-1), now.AddMinutes(1), "v1");
        Assert.NotNull(request);
        Assert.Equal(50, request!.Kpi!.TaskSuccessRatePercent);
        Assert.Equal(1, request.Kpi.ExceptionCount);
        Assert.Collection(request.TaskStates!.OrderBy(x => x.State),
            state => { Assert.Equal(nameof(TaskState.Failed), state.State); Assert.Equal(1, state.Count); },
            state => { Assert.Equal(nameof(TaskState.Succeeded), state.State); Assert.Equal(1, state.Count); });
    }

    [SqlServerFact]
    public async Task Sql_statistics_source_aggregates_balance_and_transaction_facts_in_the_database()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsStatsAggregate_{Guid.NewGuid():N}" };
        var interceptor = new StatisticsFactQueryInterceptor();
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        using var factory = new PooledFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 64; index++)
            {
                db.InventoryBalances.Add(new InventoryBalanceEntity
                {
                    Id = Guid.NewGuid(), BalanceKey = $"aggregate-balance-{index}", MaterialId = Guid.NewGuid(),
                    Quantity = 2, WeightKg = 3, Status = InventoryStatus.Available, Version = 1
                });
                db.InventoryTransactions.Add(CreateTransaction($"aggregate-in-{index}", InventoryTransactionType.Increase, 2, start.AddHours(1)));
                db.InventoryTransactions.Add(CreateTransaction($"aggregate-out-{index}", InventoryTransactionType.Decrease, 1, start.AddHours(2)));
                db.InventoryTransactions.Add(CreateTransaction($"aggregate-move-{index}", InventoryTransactionType.Move, 3, start.AddHours(3)));
            }
            await db.SaveChangesAsync();
        }

        interceptor.Clear();
        var request = await new SqlServerStatisticsSource(factory).BuildAsync(StatisticsPeriod.Day, start, start.AddDays(1), "v1");

        Assert.NotNull(request);
        Assert.Equal(128, request!.Kpi!.InventoryQuantity);
        Assert.Equal(192, request.Kpi.InventoryWeightKg);
        Assert.Equal(128, request.Kpi.InboundQuantity);
        Assert.Equal(64, request.Kpi.OutboundQuantity);
        Assert.Equal(192, request.Kpi.TransferQuantity);
        var factQueries = interceptor.ReaderCommands
            .Where(command => command.Contains("[InventoryBalances]", StringComparison.Ordinal) || command.Contains("[InventoryTransactions]", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(factQueries);
        Assert.All(factQueries, command => Assert.Contains("SUM(", command, StringComparison.OrdinalIgnoreCase));
    }

    [SqlServerFact]
    public async Task Sql_statistics_source_scopes_outbound_and_move_transactions_by_either_location_and_resolves_guid_tasks()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsStatsScope_{Guid.NewGuid():N}" };
        using var factory = new PooledFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var developmentLocationId = Guid.Parse("00000000-0000-0000-0000-000000000005");
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var otherWarehouse = new WarehouseEntity("STATS-OTHER", "Other statistics warehouse");
            var otherZone = new Zone(otherWarehouse.Id, "Z", "Other zone");
            var otherAisle = new Aisle(otherZone.Id, "A", "Other aisle");
            var otherRack = new Rack(otherAisle.Id, "R", "Other rack");
            var otherLocation = new Location(otherRack.Id, "STATS-OTHER-001", 10, 1000, 1000, 1000, 1000);
            db.AddRange(otherWarehouse, otherZone, otherAisle, otherRack, otherLocation);
            db.InventoryTransactions.AddRange(
                CreateTransaction("scope-outbound-in", InventoryTransactionType.Decrease, 4, start.AddHours(1), locationId: developmentLocationId),
                CreateTransaction("scope-outbound-out", InventoryTransactionType.Decrease, 9, start.AddHours(1), locationId: otherLocation.Id),
                CreateTransaction("scope-move-source", InventoryTransactionType.Move, 3, start.AddHours(2), sourceLocationId: developmentLocationId, destinationLocationId: otherLocation.Id),
                CreateTransaction("scope-move-destination", InventoryTransactionType.Move, 5, start.AddHours(2), sourceLocationId: otherLocation.Id, destinationLocationId: developmentLocationId));
            db.Tasks.Add(CreateTask("scope-guid-task", TaskState.Succeeded, $"{{\"SourceLocation\":\"{developmentLocationId:D}\"}}", start.AddHours(3)));
            await db.SaveChangesAsync();
        }

        var request = await new SqlServerStatisticsSource(factory, "DEV").BuildAsync(StatisticsPeriod.Day, start, start.AddDays(1), "v1");

        Assert.NotNull(request);
        Assert.Equal(4, request!.Kpi!.OutboundQuantity);
        Assert.Equal(8, request.Kpi.TransferQuantity);
        Assert.Equal(100, request.Kpi.TaskSuccessRatePercent);
        Assert.Equal(0, request.Kpi.ExceptionCount);
    }

    private static InventoryTransactionEntity CreateTransaction(string key, InventoryTransactionType type, decimal quantity, DateTimeOffset occurredAt, Guid? locationId = null, Guid? sourceLocationId = null, Guid? destinationLocationId = null) => new()
    {
        Id = Guid.NewGuid(), IdempotencyKey = key, Type = type, MaterialId = Guid.NewGuid(), Quantity = quantity,
        WeightKg = quantity, StatusBefore = InventoryStatus.Available, StatusAfter = InventoryStatus.Available,
        Fingerprint = key, OccurredAt = occurredAt, LocationId = locationId, SourceLocationId = sourceLocationId, DestinationLocationId = destinationLocationId
    };

    private static WarehouseTask CreateTask(string number, TaskState target, string context, DateTimeOffset at)
    {
        var task = new WarehouseTask(number, "Putaway", at);
        task.SetDispatchContext(context);
        task.TransitionTo(TaskState.Allocated, "test", "allocated", occurredAt: at);
        task.TransitionTo(TaskState.Queued, "test", "queued", occurredAt: at);
        if (target == TaskState.Failed) { task.TransitionTo(TaskState.Failed, "test", "failed", occurredAt: at); return task; }
        task.TransitionTo(TaskState.Dispatching, "test", "dispatching", occurredAt: at);
        task.TransitionTo(TaskState.SentToPlc, "test", "sent", occurredAt: at);
        if (target == TaskState.Succeeded)
        {
            task.TransitionTo(TaskState.Executing, "test", "executing", occurredAt: at);
            task.TransitionTo(TaskState.Succeeded, "test", "succeeded", occurredAt: at);
            return task;
        }
        if (target == TaskState.Executing)
        {
            task.TransitionTo(TaskState.Executing, "test", "executing", occurredAt: at);
            return task;
        }
        throw new ArgumentOutOfRangeException(nameof(target));
    }
    private sealed class PooledFactory(DbContextOptions<WarehouseDbContext> options) : IDbContextFactory<WarehouseDbContext>, IDisposable
    { public WarehouseDbContext CreateDbContext()=>new(options); public ValueTask<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken=default)=>new(new WarehouseDbContext(options)); public void Dispose() { } }
    private sealed class SqlServerFactAttribute : FactAttribute { public SqlServerFactAttribute(){ if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION"))) Skip="Set WMS_SQLSERVER_TEST_CONNECTION to run SQL Server read-model tests."; } }
    private sealed class StatisticsFactQueryInterceptor : DbCommandInterceptor
    {
        public List<string> ReaderCommands { get; } = [];
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            ReaderCommands.Add(command.CommandText);
            return base.ReaderExecuted(command, eventData, result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            ReaderCommands.Add(command.CommandText);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
        public void Clear() => ReaderCommands.Clear();
    }
}

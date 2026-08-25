using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.MasterData;

public sealed class LoadingPointCatalogSqlTests
{
    [SqlServerFact]
    public async Task Sql_catalog_reads_seed_loading_point_without_fixed_api_id()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"WmsLoadingPointCatalog_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var points = await new SqlServerLoadingPointCatalog(factory, new UnknownLoadingPointRuntimeStatus()).GetAsync();

        var point = Assert.Single(points);
        Assert.Equal("LP-DEV-01", point.LoadingPoint.Code);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000006"), point.LoadingPoint.Id);
        Assert.True(point.IsFaulted);
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL Server loading point catalog tests.";
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options) : IDbContextFactory<WarehouseDbContext>
    {
        public WarehouseDbContext CreateDbContext() => new(options);
        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WarehouseDbContext(options));
    }
}

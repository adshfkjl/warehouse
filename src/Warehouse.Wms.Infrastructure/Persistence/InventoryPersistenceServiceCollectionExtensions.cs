using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Inventory;

namespace Warehouse.Wms.Infrastructure.Persistence;

public static class InventoryPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerInventoryPersistence(
        this IServiceCollection services,
        string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A SQL Server connection string is required.", nameof(connectionString));
        }

        services.AddPooledDbContextFactory<WarehouseDbContext>(options => options.UseSqlServer(connectionString));
        services.AddSingleton<IInventoryLedgerStore, SqlServerInventoryLedgerStore>();
        return services;
    }
}

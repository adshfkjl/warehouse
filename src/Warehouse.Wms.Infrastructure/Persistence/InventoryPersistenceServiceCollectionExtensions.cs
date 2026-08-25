using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Infrastructure.Integrations;

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
        services.AddSingleton<SqlServerMessageStore>();
        services.AddSingleton<IOutboxMessageStore>(sp => sp.GetRequiredService<SqlServerMessageStore>());
        services.AddSingleton<IInboxMessageStore>(sp => sp.GetRequiredService<SqlServerMessageStore>());
        services.AddSingleton<ITaskCommandOutbox, SqlServerTaskCommandOutbox>();
        services.AddSingleton<IIntegrationOutbox, SqlServerIntegrationOutbox>();
        return services;
    }
}

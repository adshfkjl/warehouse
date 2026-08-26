using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Infrastructure.Integrations;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;
using Warehouse.Wms.Application.Points;

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
        services.AddSingleton<SqlServerTaskPersistenceStore>();
        services.AddSingleton<ITaskPersistenceStore>(sp => sp.GetRequiredService<SqlServerTaskPersistenceStore>());
        services.AddSingleton<IResourceLockStore>(sp => sp.GetRequiredService<SqlServerTaskPersistenceStore>());
        services.AddSingleton<IBusinessWorkflowStore, SqlServerBusinessWorkflowStore>();
        services.AddSingleton<IIntegrationOutbox, SqlServerIntegrationOutbox>();
        services.AddSingleton<IStatisticsService, SqlServerStatisticsService>();
        services.AddSingleton<SqlServerPointReadModel>();
        services.AddSingleton<IPointReadModel>(sp => sp.GetRequiredService<SqlServerPointReadModel>());
        return services;
    }
}

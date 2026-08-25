using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.DeviceGateway;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Integrations;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Composition;

public sealed class ApiCompositionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiCompositionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
    }

    [Fact]
    public async Task Api_starts_resolves_every_controller_and_uses_the_simulated_gateway()
    {
        using var client = _factory.CreateClient();
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IWarehouseDeviceGateway>();
        Assert.IsType<SimulatedDeviceGateway>(gateway);

        var descriptors = scope.ServiceProvider
            .GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(item => item.ControllerTypeInfo.AsType())
            .Distinct()
            .ToArray();

        Assert.NotEmpty(descriptors);
        foreach (var controllerType in descriptors)
        {
            var controller = ActivatorUtilities.CreateInstance(scope.ServiceProvider, controllerType);
            Assert.NotNull(controller);
        }
    }

    [Fact]
    public void Api_defaults_to_in_memory_inventory_without_a_database_store()
    {
        using var scope = _factory.Services.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<IInventoryLedgerStore>());
        Assert.IsType<InventoryService>(scope.ServiceProvider.GetRequiredService<InventoryService>());
        Assert.Null(scope.ServiceProvider.GetService<IOutboxMessageStore>());
        Assert.IsType<InMemoryIntegrationOutbox>(scope.ServiceProvider.GetRequiredService<IIntegrationOutbox>());
    }

    [SqlServerFact]
    public void SqlServer_persistence_mode_uses_sql_message_store_and_integration_outbox()
    {
        var connection = TestSqlConnection();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Wms:PersistenceMode", "SqlServer");
            builder.UseSetting("ConnectionStrings:WmsDb", connection);
        });
        using var scope = factory.Services.CreateScope();

        Assert.IsType<SqlServerMessageStore>(scope.ServiceProvider.GetRequiredService<IOutboxMessageStore>());
        Assert.IsType<SqlServerIntegrationOutbox>(scope.ServiceProvider.GetRequiredService<IIntegrationOutbox>());
        Assert.IsType<SqlServerLoadingPointCatalog>(scope.ServiceProvider.GetRequiredService<ILoadingPointCatalog>());
    }

    [SqlServerFact]
    public async Task SqlServer_mode_with_integrations_disabled_does_not_touch_the_outbox()
    {
        var connection = TestSqlConnection();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Wms:PersistenceMode", "SqlServer");
            builder.UseSetting("ConnectionStrings:WmsDb", connection);
        });
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IIntegrationCommandService>();
        using var payload = JsonDocument.Parse("{\"sku\":\"MAT-01\"}");

        var result = await service.EnqueueAsync(
            IntegrationMessageType.InboundNotice,
            new ExternalIntegrationRequest("ERP", "v1", "disabled-001", null, payload.RootElement.Clone()));

        Assert.Equal("Disabled", result.Status);
    }

    private static string TestSqlConnection()
        => Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")
            ?? throw new InvalidOperationException("WMS_SQLSERVER_TEST_CONNECTION is required for SQL Server composition tests.");

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL Server composition tests.";
        }
    }
}

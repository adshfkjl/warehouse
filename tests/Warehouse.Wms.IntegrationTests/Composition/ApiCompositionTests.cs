using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.DeviceGateway;
using Warehouse.Wms.Application.Inventory;

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
    }
}

using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Outbound;

namespace Warehouse.Wms.UnitTests.Outbound;

public sealed class OutboundAllocationTests
{
    [Fact]
    public async Task Allocates_fifo_source_and_locks_inventory_pallet_and_location()
    {
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var location = Guid.NewGuid();
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(material, pallet, location, "B-01", 5m, 10m, new InventoryOperationContext("seed"));
        var service = new OutboundAllocationService(inventory);
        var order = service.Create("OB-001");
        var line = order.AddLine(new OutboundLine(material, 2m, "B-01"));
        var allocation = service.Allocate(new OutboundAllocationRequest("alloc-001", order.OrderNumber, line.Id, 2m));

        Assert.Equal(OutboundState.Locked, order.State);
        Assert.Equal(2m, allocation.Quantity);
        var resourceTypes = allocation.ResourceLocks.Select(item => item.ResourceType).ToArray();
        Assert.Equal(3, resourceTypes.Length);
        Assert.Contains("Inventory", resourceTypes);
        Assert.Contains("Location", resourceTypes);
        Assert.Contains("Pallet", resourceTypes);
    }

    [Fact]
    public async Task Rejects_duplicate_resource_lock_and_replays_same_idempotency_key()
    {
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var location = Guid.NewGuid();
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(material, pallet, location, null, 2m, 2m, new InventoryOperationContext("seed-2"));
        var service = new OutboundAllocationService(inventory);
        var firstOrder = service.Create("OB-002");
        var firstLine = firstOrder.AddLine(new OutboundLine(material, 1m));
        var first = service.Allocate(new OutboundAllocationRequest("alloc-002", firstOrder.OrderNumber, firstLine.Id, 1m));
        Assert.Same(first, service.Allocate(new OutboundAllocationRequest("alloc-002", firstOrder.OrderNumber, firstLine.Id, 1m)));

        var secondOrder = service.Create("OB-003");
        var secondLine = secondOrder.AddLine(new OutboundLine(material, 1m));
        Assert.Throws<InvalidOperationException>(() => service.Allocate(new OutboundAllocationRequest("alloc-003", secondOrder.OrderNumber, secondLine.Id, 1m)));
    }
}

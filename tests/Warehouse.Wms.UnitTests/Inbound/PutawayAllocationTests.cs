using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;

namespace Warehouse.Wms.UnitTests.Inbound;

public sealed class PutawayAllocationTests
{
    [Fact]
    public void Automatic_recommendation_filters_operational_and_capacity_constraints()
    {
        var rackId = Guid.NewGuid();
        var disabled = new Location(rackId, "A-00", 1, 100m, 1000m, 1000m, 1000m);
        var locked = new Location(rackId, "A-01", 1, 100m, 1000m, 1000m, 1000m);
        var occupied = new Location(rackId, "A-02", 1, 100m, 1000m, 1000m, 1000m);
        var tooSmall = new Location(rackId, "A-03", 1, 100m, 10m, 1000m, 1000m);
        var tooHeavy = new Location(rackId, "A-04", 1, 5m, 1000m, 1000m, 1000m);
        var valid = new Location(rackId, "A-05", 1, 100m, 1000m, 1000m, 1000m);

        var service = new PutawayAllocationService([
            new PutawayLocationCandidate(disabled, IsDisabled: true),
            new PutawayLocationCandidate(locked, IsLocked: true),
            new PutawayLocationCandidate(occupied, OccupiedUnits: 1),
            new PutawayLocationCandidate(tooSmall),
            new PutawayLocationCandidate(tooHeavy),
            new PutawayLocationCandidate(valid)]);
        var pending = Pending(weightKg: 10m);

        var result = service.Allocate(pending, new PutawayAllocationRequest(
            pending.Id, 500m, 500m, 500m, pending.WeightKg));

        Assert.Equal(valid.Id, result.LocationId);
        Assert.Contains("自动推荐", result.RecommendationReason);
        Assert.Contains("尺寸", result.RecommendationReason);
    }

    [Fact]
    public void Manual_location_is_validated_and_reason_is_persisted()
    {
        var rackId = Guid.NewGuid();
        var requested = new Location(rackId, "B-01", 1, 100m, 1000m, 1000m, 1000m);
        var service = new PutawayAllocationService([new PutawayLocationCandidate(requested)]);
        var pending = Pending();

        var result = service.Allocate(pending, new PutawayAllocationRequest(
            pending.Id, 500m, 500m, 500m, pending.WeightKg, requested.Id));

        Assert.Equal(requested.Id, result.LocationId);
        Assert.StartsWith("人工指定", result.RecommendationReason);
        Assert.Same(result, service.Get(pending.Id));
    }

    [Fact]
    public void A_location_cannot_be_allocated_twice_until_the_first_task_releases_it()
    {
        var location = new Location(Guid.NewGuid(), "C-01", 1, 100m, 1000m, 1000m, 1000m);
        var service = new PutawayAllocationService([new PutawayLocationCandidate(location)]);
        var first = Pending();
        var second = Pending();

        service.Allocate(first, new PutawayAllocationRequest(first.Id, 100m, 100m, 100m, 1m));

        Assert.Throws<InvalidOperationException>(() => service.Allocate(
            second, new PutawayAllocationRequest(second.Id, 100m, 100m, 100m, 1m, location.Id)));
    }

    private static PendingInboundInventory Pending(decimal weightKg = 1m) => new(
        Guid.NewGuid(), Guid.NewGuid(), "IB-001", Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "PALLET-001", null, null, 1m, weightKg,
        InventoryStatus.PendingInbound, null, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
}

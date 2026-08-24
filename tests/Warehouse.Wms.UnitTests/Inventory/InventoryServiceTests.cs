using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.UnitTests.Inventory;

public sealed class InventoryServiceTests
{
    [Fact]
    public async Task Increase_creates_available_balance_and_a_traceable_transaction()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var context = Context("receive-001", "receipt-001", "task-001", "operator-001", "收货");

        var transaction = await service.IncreaseAsync(
            materialId, Guid.NewGuid(), Guid.NewGuid(), "B-001", 10m, 125m, context);

        Assert.Equal(InventoryTransactionType.Increase, transaction.Type);
        Assert.Equal("receipt-001", transaction.SourceDocumentId);
        Assert.Equal("task-001", transaction.TaskNumber);
        Assert.Equal("operator-001", transaction.OperatorId);
        Assert.Equal("收货", transaction.Reason);
        var balance = Assert.Single(service.GetBalances());
        Assert.Equal(10m, balance.Quantity);
        Assert.Equal(125m, balance.WeightKg);
        Assert.Equal(InventoryStatus.Available, balance.Status);
    }

    [Fact]
    public async Task Repeating_an_idempotency_key_returns_the_original_transaction_without_duplicate_quantity()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var context = Context("idem-001");

        var first = await service.IncreaseAsync(materialId, palletId, locationId, null, 5m, 50m, context);
        var second = await service.IncreaseAsync(materialId, palletId, locationId, null, 5m, 50m, context);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.GetTransactions());
        Assert.Equal(5m, Assert.Single(service.GetBalances()).Quantity);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_operation_is_rejected()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var context = Context("idem-conflict");
        await service.IncreaseAsync(materialId, palletId, locationId, null, 5m, 50m, context);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IncreaseAsync(materialId, palletId, locationId, null, 6m, 60m, context));

        Assert.Equal(5m, Assert.Single(service.GetBalances()).Quantity);
        Assert.Single(service.GetTransactions());
    }

    [Fact]
    public async Task Overdraw_is_rejected_and_leaves_balance_and_ledger_unchanged()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await service.IncreaseAsync(materialId, palletId, locationId, null, 10m, 100m, Context("add-001"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecreaseAsync(materialId, palletId, locationId, null, 11m, 110m, Context("remove-001")));

        Assert.Equal(10m, Assert.Single(service.GetBalances()).Quantity);
        Assert.Single(service.GetTransactions());
    }

    [Fact]
    public async Task Concurrent_lock_requests_allow_only_one_physical_lock()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await service.IncreaseAsync(materialId, palletId, locationId, null, 10m, 100m, Context("add-001"));

        var attempts = await Task.WhenAll(
            Task.Run(() => CaptureAsync(() => service.LockAsync(materialId, palletId, locationId, null, 10m, Context("lock-001")))),
            Task.Run(() => CaptureAsync(() => service.LockAsync(materialId, palletId, locationId, null, 10m, Context("lock-002")))));

        Assert.Equal(1, attempts.Count(result => result is not null));
        Assert.Equal(1, attempts.Count(result => result is InvalidOperationException));
        Assert.Equal(InventoryStatus.Locked, Assert.Single(service.GetBalances()).Status);
    }

    [Fact]
    public async Task Unlock_and_adjustment_are_recorded_and_rebuildable()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await service.IncreaseAsync(materialId, palletId, locationId, null, 10m, 100m, Context("add-001"));
        await service.LockAsync(materialId, palletId, locationId, null, 10m, Context("lock-001"));
        await service.UnlockAsync(materialId, palletId, locationId, null, 10m, Context("unlock-001"));
        var adjustment = await service.AdjustAsync(materialId, palletId, locationId, null, 2m, 20m, Context("adjust-001", reason: "盘盈"));

        Assert.Equal(InventoryTransactionType.Adjustment, adjustment.Type);
        Assert.Equal(12m, service.GetBalance(materialId, palletId, locationId, null)!.Quantity);
        Assert.Equal(4, service.GetTransactions().Count);
        Assert.Equal(12m, service.RebuildBalances().Single().Quantity);
    }

    [Fact]
    public async Task Move_writes_one_transaction_and_rebuilds_the_same_balances_from_the_ledger()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var sourceLocationId = Guid.NewGuid();
        var destinationLocationId = Guid.NewGuid();
        await service.IncreaseAsync(materialId, palletId, sourceLocationId, "B-001", 5m, 50m, Context("add-001"));

        var move = await service.MoveAsync(
            materialId, palletId, sourceLocationId, destinationLocationId, "B-001", 5m, 50m, Context("move-001"));

        Assert.Equal(InventoryTransactionType.Move, move.Type);
        Assert.Equal(0m, service.GetBalance(materialId, palletId, sourceLocationId, "B-001")!.Quantity);
        Assert.Equal(5m, service.GetBalance(materialId, palletId, destinationLocationId, "B-001")!.Quantity);
        var rebuilt = service.RebuildBalances();
        Assert.Equal(
            service.GetBalances().OrderBy(x => x.Key).Select(ToSnapshot),
            rebuilt.OrderBy(x => x.Key).Select(ToSnapshot));
    }

    [Fact]
    public async Task Failed_move_rolls_back_without_creating_a_partial_transaction()
    {
        var service = new InventoryService();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var sourceLocationId = Guid.NewGuid();
        var destinationLocationId = Guid.NewGuid();
        await service.IncreaseAsync(materialId, palletId, sourceLocationId, null, 5m, 50m, Context("add-001"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveAsync(
            materialId, palletId, sourceLocationId, destinationLocationId, null, 6m, 60m, Context("move-failed")));

        Assert.Single(service.GetTransactions());
        Assert.Equal(5m, service.GetBalance(materialId, palletId, sourceLocationId, null)!.Quantity);
        Assert.Null(service.GetBalance(materialId, palletId, destinationLocationId, null));
    }

    private static InventoryOperationContext Context(
        string idempotencyKey,
        string? sourceDocumentId = null,
        string? taskNumber = null,
        string? operatorId = null,
        string? reason = null) =>
        new(idempotencyKey, sourceDocumentId, taskNumber, operatorId, reason);

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static (string Key, decimal Quantity, decimal WeightKg, InventoryStatus Status) ToSnapshot(InventoryBalance balance) =>
        (balance.Key, balance.Quantity, balance.WeightKg, balance.Status);
}

using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Stocktaking;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;

namespace Warehouse.Wms.UnitTests.Stocktaking;

public sealed class StocktakingDifferenceTests
{
    [Fact]
    public async Task Difference_requires_second_authorization_before_applying_inventory_adjustment()
    {
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var stocktaking = RunningStocktaking(materialId, palletId, 10m, 100m);
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(materialId, palletId, null, null, 10m, 100m, Context("seed"));
        var authorization = new RecordingAuthorization(false);
        var service = new StocktakingDifferenceService(stocktaking, inventory, new TestUser("operator"), authorization);
        var item = stocktaking.Get("ST-001").Items.Single();
        stocktaking.RecordCount("ST-001", item.Id, 8m, 80m);

        var adjustment = service.CreateDifference("ST-001", item.Id, StocktakingCountMode.Open, "破损");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApproveAndApplyAsync(adjustment.Id, "主管拒绝"));

        Assert.Equal(StocktakingAdjustmentState.PendingReview, adjustment.State);
        Assert.Empty(inventory.GetTransactions().Where(t => t.Type == InventoryTransactionType.Adjustment));
        Assert.True(authorization.Calls > 0);
    }

    [Fact]
    public async Task Authorized_adjustment_writes_one_ledger_entry_and_repeated_confirmation_is_idempotent()
    {
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var stocktaking = RunningStocktaking(materialId, palletId, 10m, 100m);
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(materialId, palletId, null, null, 10m, 100m, Context("seed"));
        var service = new StocktakingDifferenceService(stocktaking, inventory, new TestUser("supervisor"), new RecordingAuthorization(true));
        var item = stocktaking.Get("ST-001").Items.Single();
        stocktaking.RecordCount("ST-001", item.Id, 8m, 80m);
        var adjustment = service.CreateDifference("ST-001", item.Id, StocktakingCountMode.Open, "盘亏");

        var first = await service.ApproveAndApplyAsync(adjustment.Id, "确认盘亏");
        var second = await service.ApproveAndApplyAsync(adjustment.Id, "重复确认");

        Assert.Equal(StocktakingAdjustmentState.Applied, first.State);
        Assert.Same(first, second);
        Assert.Single(inventory.GetTransactions().Where(t => t.Type == InventoryTransactionType.Adjustment));
        Assert.Equal(8m, inventory.GetBalances().Single().Quantity);
    }

    [Fact]
    public void Blind_count_hides_book_values_from_operator_but_keeps_them_for_approval()
    {
        var stocktaking = RunningStocktaking(Guid.NewGuid(), Guid.NewGuid(), 10m, 100m);
        var service = NewService(stocktaking, new InventoryService(), new RecordingAuthorization(true));
        var item = stocktaking.Get("ST-001").Items.Single();
        stocktaking.RecordCount("ST-001", item.Id, 9m, 90m);

        var adjustment = service.CreateDifference("ST-001", item.Id, StocktakingCountMode.Blind, "复核记录");
        var view = service.GetOperatorView(adjustment.Id);

        Assert.Null(view.BookQuantity);
        Assert.Null(view.BookWeightKg);
        Assert.Equal(10m, adjustment.BookQuantity);
        Assert.Equal(100m, adjustment.BookWeightKg);
    }

    [Fact]
    public void Recount_replaces_initial_difference_and_does_not_create_a_second_adjustment()
    {
        var stocktaking = RunningStocktaking(Guid.NewGuid(), Guid.NewGuid(), 10m, 100m);
        var service = NewService(stocktaking, new InventoryService(), new RecordingAuthorization(true));
        var item = stocktaking.Get("ST-001").Items.Single();
        stocktaking.RecordCount("ST-001", item.Id, 8m, 80m);
        var adjustment = service.CreateDifference("ST-001", item.Id, StocktakingCountMode.Open, "初盘差异");

        service.RequestRecount(adjustment.Id, "需要复盘");
        service.RecordRecount(adjustment.Id, 10m, 100m, "复盘无差异");

        Assert.Equal(StocktakingAdjustmentState.Rejected, adjustment.State);
        Assert.Equal(10m, adjustment.FinalQuantity);
        Assert.Single(service.Adjustments);
    }

    [Fact]
    public async Task Negative_balance_failure_rolls_back_adjustment_state_and_ledger()
    {
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var stocktaking = RunningStocktaking(materialId, palletId, 10m, 100m);
        var inventory = new InventoryService();
        var service = NewService(stocktaking, inventory, new RecordingAuthorization(true));
        var item = stocktaking.Get("ST-001").Items.Single();
        stocktaking.RecordCount("ST-001", item.Id, 8m, 80m);
        var adjustment = service.CreateDifference("ST-001", item.Id, StocktakingCountMode.Open, "盘亏");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAndApplyAsync(adjustment.Id, "确认"));

        Assert.Equal(StocktakingAdjustmentState.Approved, adjustment.State);
        Assert.Empty(inventory.GetTransactions());
    }

    [Fact]
    public async Task Approved_adjustment_can_retry_after_the_inventory_balance_becomes_available()
    {
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var stocktaking = RunningStocktaking(materialId, palletId, 10m, 100m);
        var inventory = new InventoryService();
        var service = NewService(stocktaking, inventory, new RecordingAuthorization(true));
        var item = stocktaking.Get("ST-001").Items.Single();
        stocktaking.RecordCount("ST-001", item.Id, 8m, 80m);
        var adjustment = service.CreateDifference("ST-001", item.Id, StocktakingCountMode.Open, "盘亏");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAndApplyAsync(adjustment.Id, "首次确认"));
        await inventory.IncreaseAsync(materialId, palletId, null, null, 10m, 100m, Context("seed-after-failure"));
        var applied = await service.ApproveAndApplyAsync(adjustment.Id, "重试确认");

        Assert.Equal(StocktakingAdjustmentState.Applied, applied.State);
        Assert.Single(inventory.GetTransactions().Where(t => t.Type == InventoryTransactionType.Adjustment));
    }

    [Fact]
    public void Reservation_is_idempotent_and_distinguishes_sent_from_completed()
    {
        var stocktaking = RunningStocktaking(Guid.NewGuid(), Guid.NewGuid(), 1m, 1m);
        var service = NewService(stocktaking, new InventoryService(), new RecordingAuthorization(true));
        var item = stocktaking.Get("ST-001").Items.Single();

        var first = service.ReservePutaway("ST-001", item.Id, "LP-01", "DEV-01");
        var duplicate = service.ReservePutaway("ST-001", item.Id, "LP-01", "DEV-01");
        service.MarkPutawaySent(first.Id, "device-task-1");

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(PutawayReservationState.Sent, first.State);
        Assert.NotEqual(PutawayReservationState.Completed, first.State);
        service.MarkPutawayCompleted(first.Id);
        Assert.Equal(PutawayReservationState.Completed, first.State);
    }

    private static StocktakingService RunningStocktaking(Guid materialId, Guid palletId, decimal quantity, decimal weight)
    {
        var service = new StocktakingService([
            new StocktakingInventoryItem("A1-01", "Z1", "MAT-01", null, "PAL-01", materialId, palletId, quantity, weight, InventoryStatus.Available)]);
        service.Create(new StocktakingRequest("ST-001"));
        service.Start("ST-001");
        return service;
    }

    private static StocktakingDifferenceService NewService(StocktakingService stocktaking, InventoryService inventory, RecordingAuthorization authorization)
        => new(stocktaking, inventory, new TestUser("operator"), authorization);

    private static InventoryOperationContext Context(string key)
        => new(key, taskNumber: "ST-001", operatorId: "operator", reason: "test");

    private sealed record TestUser(string UserId) : ICurrentUser;

    private sealed class RecordingAuthorization(bool result) : IRiskAuthorizationService
    {
        public int Calls { get; private set; }

        public Task<bool> AuthorizeAsync(string operation, ICurrentUser user, string taskNumber, string reason, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}

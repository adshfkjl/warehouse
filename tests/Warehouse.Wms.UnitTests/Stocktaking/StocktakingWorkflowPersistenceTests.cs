using Warehouse.Wms.Application.Stocktaking;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;

namespace Warehouse.Wms.UnitTests.Stocktaking;

public sealed class StocktakingWorkflowPersistenceTests
{
    [Fact]
    public async Task Restore_rehydrates_stocktaking_items_and_recorded_count()
    {
        var store = new InMemoryBusinessWorkflowStore();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var first = new StocktakingService([
            new StocktakingInventoryItem("A1-01", "A", "M-01", "B-01", "P-01", material, pallet, 3m, 4m, InventoryStatus.Available)],
            workflowStore: store);
        var task = first.Create(new StocktakingRequest("ST-PERSIST-1"));
        first.Start(task.TaskNumber);
        var item = task.Items.Single();
        first.RecordCount(task.TaskNumber, item.Id, 2m, 3m);

        var restarted = new StocktakingService([
            new StocktakingInventoryItem("A1-01", "A", "M-01", "B-01", "P-01", material, pallet, 3m, 4m, InventoryStatus.Available)],
            workflowStore: store.CreateRestartedInstance());
        await restarted.RestoreAsync();
        var restored = restarted.Get(task.TaskNumber);

        Assert.Equal(task.Id, restored.Id);
        Assert.Equal(StocktakingState.Running, restored.State);
        Assert.Equal(item.Id, restored.Items.Single().Id);
        Assert.Equal(2m, restored.Items.Single().ActualQuantity);
        Assert.Equal(StocktakingItemState.Difference, restored.Items.Single().State);
    }
}

using Warehouse.Wms.Application.Inventory;

namespace Warehouse.Wms.UnitTests.Inventory;

public sealed class InventoryPersistenceContractTests
{
    [Fact]
    public void Inventory_ledger_store_exposes_snapshot_and_append_contracts()
    {
        var contract = typeof(InventoryService).Assembly
            .GetType("Warehouse.Wms.Application.Inventory.IInventoryLedgerStore");

        Assert.NotNull(contract);
        Assert.True(contract!.IsInterface);
        Assert.Contains(contract.GetMethods(), method => method.Name == "LoadSnapshotAsync");
        Assert.Contains(contract.GetMethods(), method => method.Name == "AppendAsync");
    }
}

using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Tasks;

namespace Warehouse.Wms.UnitTests.Inbound;

public sealed class InboundWorkflowPersistenceTests
{
    [Fact]
    public async Task Receive_is_replayed_after_service_restart_without_second_receipt()
    {
        var store = new InMemoryBusinessWorkflowStore();
        var material = Guid.NewGuid();
        var first = new InboundOrderService(store);
        var order = first.Create("IN-PERSIST-001", [new InboundLineRequest(material, 5m)]);
        var request = new InboundReceiptRequest("receipt-persist-001", 5m, PalletCode: "PLT-PERSIST-001");
        var receipt = first.Receive(order.OrderNumber, order.Lines[0].Id, request);

        var restarted = new InboundOrderService(store.CreateRestartedInstance());
        await restarted.RestoreAsync();
        var replay = restarted.Receive("IN-PERSIST-001", request);

        Assert.Equal(receipt.Quantity, replay.Quantity);
        Assert.Equal(order.Id, replay.InboundOrderId);
        Assert.Equal(order.Lines[0].Id, replay.InboundLineId);
        Assert.Equal(receipt.ReceiptId, replay.ReceiptId);
        Assert.Equal(receipt.Inventory.Id, replay.Inventory.Id);
        Assert.Equal("IN-PERSIST-001", restarted.Get("IN-PERSIST-001").OrderNumber);
        Assert.Single(restarted.PendingInboundInventory);
        Assert.Single(restarted.Get("IN-PERSIST-001").Lines[0].Receipts);
    }

    [Fact]
    public async Task Restore_preserves_order_line_and_receipt_ids()
    {
        var store = new InMemoryBusinessWorkflowStore();
        var material = Guid.NewGuid();
        var first = new InboundOrderService(store);
        var order = first.Create("IN-STABLE-ID", [new InboundLineRequest(material, 1m)]);
        var line = order.Lines.Single();
        var receipt = first.Receive(order.OrderNumber, line.Id, new InboundReceiptRequest("receipt-stable-id", 1m, PalletCode: "PLT-STABLE"));

        var restarted = new InboundOrderService(store.CreateRestartedInstance());
        await restarted.RestoreAsync();
        var restored = restarted.Get(order.OrderNumber);

        Assert.Equal(order.Id, restored.Id);
        Assert.Equal(line.Id, restored.Lines.Single().Id);
        Assert.Equal(receipt.ReceiptId, restored.Lines.Single().Receipts.Single().Id);
    }
}

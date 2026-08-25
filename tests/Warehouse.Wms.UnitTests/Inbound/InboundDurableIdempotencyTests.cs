using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Tasks;

namespace Warehouse.Wms.UnitTests.Inbound;

public sealed class InboundDurableIdempotencyTests
{
    [Fact]
    public async Task Durable_receipt_key_blocks_new_receipt_when_business_snapshot_is_missing()
    {
        var store = new InMemoryBusinessWorkflowStore();
        await store.RegisterIdempotencyAsync("inbound-receipt", "receipt-orphan", "IN-ORPHAN|1|0|-|-|-|-", "InboundOrder", "IN-ORPHAN");
        var service = new InboundOrderService(store);
        var order = service.Create("IN-ORPHAN", [new InboundLineRequest(Guid.NewGuid(), 1m)]);

        var request = new InboundReceiptRequest("receipt-orphan", 1m);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Task.Run(() => service.Receive(order.OrderNumber, order.Lines.Single().Id, request)));
    }
}

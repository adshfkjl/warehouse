using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.UnitTests.Inbound;

public sealed class InboundOrderTests
{
    [Fact]
    public void New_order_is_draft_and_can_be_built_manually()
    {
        var order = new InboundOrder(" IB-001 ");
        var line = order.AddLine(new InboundLine(Guid.NewGuid(), 10m, " B-01 ", new DateOnly(2027, 1, 31)));

        Assert.Equal("IB-001", order.OrderNumber);
        Assert.Equal(InboundState.Draft, order.State);
        Assert.Equal(10m, line.OrderedQuantity);
        Assert.Equal("B-01", line.BatchNumber);
        Assert.Equal(new DateOnly(2027, 1, 31), line.ExpirationDate);
        Assert.Single(order.Lines);
    }

    [Fact]
    public async Task Partial_receipt_enters_receiving_and_creates_pending_inbound_only()
    {
        var service = new InboundOrderService();
        var materialId = Guid.NewGuid();
        var order = service.Create("IB-002", [new InboundLineRequest(materialId, 10m)]);
        var line = order.Lines.Single();

        var result = await service.ReceiveAsync(
            order.OrderNumber,
            line.Id,
            new InboundReceiptRequest("receipt-001", 4m, 42.5m, "LOT-1", new DateOnly(2027, 2, 1), "PALLET-1"));

        Assert.Equal(InboundState.Receiving, order.State);
        Assert.Equal(4m, line.ReceivedQuantity);
        Assert.Equal(42.5m, line.ReceivedWeightKg);
        Assert.Equal(InventoryStatus.PendingInbound, result.Inventory.Status);
        Assert.Null(result.Inventory.LocationId);
        Assert.Single(service.PendingInboundInventory);
        Assert.Equal(4m, service.PendingInboundInventory.Single().Quantity);
        Assert.Null(service.PendingInboundInventory.Single().LocationId);
    }

    [Fact]
    public async Task Full_receipt_moves_to_received_but_does_not_claim_a_location()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-003", [new InboundLineRequest(Guid.NewGuid(), 5m)]);

        await service.ReceiveAsync(order.OrderNumber, order.Lines.Single().Id,
            new InboundReceiptRequest("receipt-002", 5m, 10m, "LOT-2", null, "PALLET-2"));

        Assert.Equal(InboundState.Received, order.State);
        Assert.Null(service.PendingInboundInventory.Single().LocationId);
        Assert.Equal(InventoryStatus.PendingInbound, service.PendingInboundInventory.Single().Status);
    }

    [Fact]
    public async Task Repeating_the_same_receipt_key_is_idempotent_and_does_not_double_receive()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-004", [new InboundLineRequest(Guid.NewGuid(), 10m)]);
        var request = new InboundReceiptRequest("receipt-003", 3m, 6m, "LOT-3", null, "PALLET-3");

        var first = await service.ReceiveAsync(order.OrderNumber, order.Lines.Single().Id, request);
        var second = await service.ReceiveAsync(order.OrderNumber, order.Lines.Single().Id, request);

        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(3m, order.Lines.Single().ReceivedQuantity);
        Assert.Single(service.PendingInboundInventory);
    }

    [Fact]
    public async Task Reusing_a_receipt_key_with_different_payload_is_rejected()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-005", [new InboundLineRequest(Guid.NewGuid(), 10m)]);
        var line = order.Lines.Single();
        await service.ReceiveAsync(order.OrderNumber, line.Id,
            new InboundReceiptRequest("receipt-004", 3m, 6m, "LOT-4", null, "PALLET-4"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveAsync(order.OrderNumber, line.Id,
            new InboundReceiptRequest("receipt-004", 4m, 8m, "LOT-4", null, "PALLET-4")));
    }

    [Fact]
    public async Task Over_receipt_is_rejected_without_changing_pending_inventory()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-006", [new InboundLineRequest(Guid.NewGuid(), 5m)]);
        var line = order.Lines.Single();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReceiveAsync(order.OrderNumber, line.Id,
            new InboundReceiptRequest("receipt-005", 6m, 1m)));

        Assert.Equal(0m, line.ReceivedQuantity);
        Assert.Empty(service.PendingInboundInventory);
    }

    [Fact]
    public async Task A_pallet_cannot_be_bound_to_two_receipts()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-007", [
            new InboundLineRequest(Guid.NewGuid(), 2m),
            new InboundLineRequest(Guid.NewGuid(), 2m)
        ]);
        var firstLine = order.Lines[0];
        var secondLine = order.Lines[1];

        await service.ReceiveAsync(order.OrderNumber, firstLine.Id,
            new InboundReceiptRequest("receipt-006", 1m, 1m, null, null, "PALLET-6"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveAsync(order.OrderNumber, secondLine.Id,
            new InboundReceiptRequest("receipt-007", 1m, 1m, null, null, "PALLET-6")));
    }

    [Fact]
    public async Task Canceling_a_draft_order_is_terminal_and_prevents_receiving()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-008", [new InboundLineRequest(Guid.NewGuid(), 1m)]);

        service.Cancel(order.OrderNumber, "operator-1", "cancelled before receiving");

        Assert.Equal(InboundState.Canceled, order.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveAsync(order.OrderNumber,
            order.Lines.Single().Id, new InboundReceiptRequest("receipt-008", 1m, 1m)));
    }

    [Fact]
    public async Task A_line_receipt_requires_a_known_line_and_positive_quantity()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-009", [new InboundLineRequest(Guid.NewGuid(), 1m)]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReceiveAsync(order.OrderNumber, Guid.NewGuid(),
            new InboundReceiptRequest("receipt-009", 1m, 1m)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReceiveAsync(order.OrderNumber,
            order.Lines.Single().Id, new InboundReceiptRequest("receipt-010", 0m, 1m)));
    }

    [Fact]
    public async Task Exception_order_cannot_accept_a_new_receipt()
    {
        var service = new InboundOrderService();
        var order = service.Create("IB-010", [new InboundLineRequest(Guid.NewGuid(), 1m)]);
        order.TransitionTo(InboundState.Receiving, "operator-1", "receiving started");
        service.MarkException(order.OrderNumber, "operator-1", "quality hold");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReceiveAsync(
            order.OrderNumber,
            order.Lines.Single().Id,
            new InboundReceiptRequest("receipt-011", 1m)));

        Assert.Equal(0m, order.Lines.Single().ReceivedQuantity);
        Assert.Empty(service.PendingInboundInventory);
    }
}

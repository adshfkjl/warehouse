using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Inbound;

namespace Warehouse.Wms.UnitTests.Inbound;

public sealed class InboundWorkflowFailureTests
{
    [Fact]
    public void Receive_persistence_failure_rolls_back_memory_and_allows_retry()
    {
        var store = new FailOnSaveStore();
        var service = new InboundOrderService(store);
        var order = service.Create("IN-ROLLBACK", [new InboundLineRequest(Guid.NewGuid(), 2m, "ORIGINAL-BATCH")]);
        var line = order.Lines.Single();
        var request = new InboundReceiptRequest("receipt-rollback", 1m, PalletCode: "PLT-ROLLBACK");

        store.FailNextSave = true;
        Assert.Throws<InvalidOperationException>(() => service.Receive(order.OrderNumber, line.Id, request));

        var afterFailure = service.Get(order.OrderNumber);
        Assert.Equal(InboundState.Draft, afterFailure.State);
        Assert.Equal(0m, afterFailure.Lines.Single().ReceivedQuantity);
        Assert.Empty(afterFailure.Lines.Single().Receipts);
        Assert.Empty(service.PendingInboundInventory);

        var retry = service.Receive(order.OrderNumber, line.Id, request);
        Assert.Equal(1m, retry.Quantity);
        Assert.Single(service.PendingInboundInventory);
    }

    private sealed class FailOnSaveStore : IBusinessWorkflowStore
    {
        private readonly InMemoryBusinessWorkflowStore _inner = new();
        public bool FailNextSave { get; set; }

        public Task<BusinessWorkflowSnapshot?> GetAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default)
            => _inner.GetAsync(aggregateType, aggregateKey, cancellationToken);
        public Task<IReadOnlyList<BusinessWorkflowSnapshot>> GetByTypeAsync(string aggregateType, CancellationToken cancellationToken = default)
            => _inner.GetByTypeAsync(aggregateType, cancellationToken);
        public Task<IReadOnlyList<BusinessWorkflowStateHistory>> GetHistoryAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default)
            => _inner.GetHistoryAsync(aggregateType, aggregateKey, cancellationToken);
        public Task<BusinessWorkflowIdempotencyResult> RegisterIdempotencyAsync(string scope, string key, string requestHash, string? aggregateType = null, string? aggregateKey = null, CancellationToken cancellationToken = default)
            => _inner.RegisterIdempotencyAsync(scope, key, requestHash, aggregateType, aggregateKey, cancellationToken);
        public Task<BusinessWorkflowIdempotency?> GetIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default)
            => _inner.GetIdempotencyAsync(scope, key, cancellationToken);
        public Task RemoveIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default)
            => _inner.RemoveIdempotencyAsync(scope, key, cancellationToken);

        public Task SaveAsync(BusinessWorkflowSnapshot snapshot, int expectedVersion, string? reason = null, string? operatorId = null, CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new InvalidOperationException("simulated persistence failure");
            }

            return _inner.SaveAsync(snapshot, expectedVersion, reason, operatorId, cancellationToken);
        }
    }
}

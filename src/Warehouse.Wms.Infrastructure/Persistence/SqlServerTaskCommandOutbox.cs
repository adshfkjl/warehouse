using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.Infrastructure.Persistence;

/// <summary>
/// Maps scheduler commands to the durable generic Outbox store. Each store
/// call is a short transaction; the PLC call happens after this adapter
/// returns and is never enclosed by the store transaction.
/// </summary>
public sealed class SqlServerTaskCommandOutbox(IOutboxMessageStore messageStore) : ITaskCommandOutbox
{
    public async Task<TaskCommandOutboxClaim?> EnqueueAndClaimAsync(
        DeviceTask task,
        DeviceOperationKind operationKind,
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var message = new OutboxMessage(
            "DeviceCommand",
            "WarehouseTask",
            task.WmsTaskId,
            task.IdempotencyKey,
            TaskCommandOutboxSerializer.Serialize(task, operationKind));
        await messageStore.EnqueueAsync(message, cancellationToken);
        var claimed = await messageStore.ClaimAsync(
            task.IdempotencyKey,
            workerId,
            claimedAt,
            leaseDuration,
            cancellationToken);
        return claimed is null
            ? null
            : new TaskCommandOutboxClaim(claimed.Id, claimed.IdempotencyKey, workerId);
    }

    public Task MarkPublishedAsync(
        TaskCommandOutboxClaim claim,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return messageStore.MarkPublishedAsync(claim.MessageId, claim.WorkerId, publishedAt, cancellationToken);
    }

    public Task MarkFailedAsync(
        TaskCommandOutboxClaim claim,
        string failure,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return messageStore.MarkFailedAsync(
            claim.MessageId,
            claim.WorkerId,
            failure,
            failedAt,
            nextAttemptAt,
            cancellationToken);
    }
}

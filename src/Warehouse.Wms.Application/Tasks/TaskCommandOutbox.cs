using System.Text.Json;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.Application.Tasks;

/// <summary>
/// The smallest scheduler-facing claim. Persistence details remain outside
/// Application so a device call never owns a database transaction.
/// </summary>
public sealed record TaskCommandOutboxClaim(Guid MessageId, string IdempotencyKey, string WorkerId);

public interface ITaskCommandOutbox
{
    Task<TaskCommandOutboxClaim?> EnqueueAndClaimAsync(
        DeviceTask task,
        DeviceOperationKind operationKind,
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(
        TaskCommandOutboxClaim claim,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        TaskCommandOutboxClaim claim,
        string failure,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);
}

public static class TaskCommandOutboxSerializer
{
    public static string Serialize(DeviceTask task, DeviceOperationKind operationKind)
    {
        ArgumentNullException.ThrowIfNull(task);
        return JsonSerializer.Serialize(new
        {
            operation = operationKind.ToString(),
            idempotencyKey = task.IdempotencyKey,
            wmsTaskId = task.WmsTaskId,
            deviceId = task.DeviceId,
            sourceLocation = task.SourceLocation,
            destinationLocation = task.DestinationLocation,
            loadingPoint = task.LoadingPoint,
            protocolVersion = task.ProtocolVersion
        });
    }
}

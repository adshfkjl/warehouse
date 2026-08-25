using Warehouse.Wms.Domain.Tasks;
using System.Text.Json;

namespace Warehouse.Wms.Application.Tasks;

public sealed record WorkflowRecoveryResult(
    string TaskNumber,
    string WorkflowKind,
    string WorkflowReference,
    WorkflowRecoveryStatus Status);

/// <summary>
/// Restores durable workflow metadata without inventing missing order or
/// inventory state. A task whose business aggregate is not available in the
/// current process is explicitly marked blocked for operator reconciliation.
/// </summary>
public sealed class WorkflowRecoveryService(ITaskPersistenceStore store)
{
    private static readonly TaskState[] OpenStates =
        [TaskState.Created, TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing];

    public async Task<IReadOnlyList<WorkflowRecoveryResult>> RecoverAsync(string workflowKind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowKind)) throw new ArgumentException("A workflow kind is required.", nameof(workflowKind));
        var tasks = await store.GetTasksByStatesAsync(OpenStates, cancellationToken);
        var results = new List<WorkflowRecoveryResult>();
        foreach (var task in tasks.Where(item => string.Equals(item.WorkflowKind, workflowKind.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(task.WorkflowReference) || string.IsNullOrWhiteSpace(task.WorkflowSnapshotJson))
            {
                await store.MarkWorkflowRecoveryBlockedAsync(task.TaskNumber, "WORKFLOW_SNAPSHOT_MISSING", cancellationToken);
                results.Add(new WorkflowRecoveryResult(task.TaskNumber, task.WorkflowKind!, task.WorkflowReference ?? string.Empty, WorkflowRecoveryStatus.BlockedMissingBusinessState));
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(task.WorkflowSnapshotJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Workflow snapshot must be an object.");
                await store.MarkWorkflowRecoveredAsync(task.TaskNumber, cancellationToken);
                results.Add(new WorkflowRecoveryResult(task.TaskNumber, task.WorkflowKind!, task.WorkflowReference!, WorkflowRecoveryStatus.RecoveredFromSnapshot));
            }
            catch (JsonException)
            {
                await store.MarkWorkflowRecoveryBlockedAsync(task.TaskNumber, "WORKFLOW_SNAPSHOT_INVALID", cancellationToken);
                results.Add(new WorkflowRecoveryResult(task.TaskNumber, task.WorkflowKind!, task.WorkflowReference!, WorkflowRecoveryStatus.BlockedMissingBusinessState));
            }
        }

        return results;
    }
}

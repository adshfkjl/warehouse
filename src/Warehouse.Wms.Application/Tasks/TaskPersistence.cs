using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Tasks;

public sealed record TaskCreateResult(WarehouseTask Task, bool Replayed);

public sealed record TaskIdempotencyRegistrationResult(TaskIdempotencyKey Key, bool Replayed);

public interface ITaskPersistenceStore
{
    Task<TaskCreateResult> CreateTaskAsync(WarehouseTask task, CancellationToken cancellationToken = default);
    Task<WarehouseTask?> GetTaskAsync(string taskNumber, CancellationToken cancellationToken = default);
    Task UpdateWorkflowContextAsync(string taskNumber, string workflowKind, string workflowReference, string snapshotJson, WorkflowRecoveryStatus recoveryStatus = WorkflowRecoveryStatus.Pending, CancellationToken cancellationToken = default);
    Task MarkWorkflowRecoveryBlockedAsync(string taskNumber, string reason, CancellationToken cancellationToken = default);
    Task MarkWorkflowRecoveredAsync(string taskNumber, CancellationToken cancellationToken = default);
    Task UpdateDispatchContextAsync(string taskNumber, string contextJson, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WarehouseTask>> GetTasksByStatesAsync(IReadOnlyCollection<TaskState> states, CancellationToken cancellationToken = default);
    Task<WarehouseTask> TransitionTaskAsync(string taskNumber, int expectedVersion, TaskState nextState, string operatorName, string reason, string? errorCode = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default);
    Task<TaskIdempotencyRegistrationResult> RegisterIdempotencyKeyAsync(TaskIdempotencyKey key, CancellationToken cancellationToken = default);
    Task<TaskIdempotencyKey?> GetIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default);
}

public interface IResourceLockStore
{
    Task<ResourceLock> AcquireResourceLockAsync(string resourceType, string resourceId, string ownerTaskNumber, DateTimeOffset acquiredAt, TimeSpan leaseDuration, Guid? lockToken = null, CancellationToken cancellationToken = default);
    Task<ResourceLock> RenewResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset renewedAt, TimeSpan leaseDuration, Guid lockToken, CancellationToken cancellationToken = default);
    Task ReleaseResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset releasedAt, Guid lockToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResourceLock>> GetActiveResourceLocksAsync(DateTimeOffset? at = null, CancellationToken cancellationToken = default);
}

public sealed class InMemoryTaskPersistenceStore : ITaskPersistenceStore, IResourceLockStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WarehouseTask> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskIdempotencyKey> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ResourceLock> _locks = [];

    public Task<TaskCreateResult> CreateTaskAsync(WarehouseTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_tasks.TryGetValue(task.TaskNumber, out var existing))
            {
                if (!string.Equals(existing.TaskType, task.TaskType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Task number '{task.TaskNumber}' is already used by another task.");
                }

                return Task.FromResult(new TaskCreateResult(existing, true));
            }

            _tasks.Add(task.TaskNumber, task);
            return Task.FromResult(new TaskCreateResult(task, false));
        }
    }

    public Task<WarehouseTask?> GetTaskAsync(string taskNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _tasks.TryGetValue(taskNumber.Trim(), out var task);
            return Task.FromResult(task);
        }
    }

    public Task UpdateWorkflowContextAsync(string taskNumber, string workflowKind, string workflowReference, string snapshotJson, WorkflowRecoveryStatus recoveryStatus = WorkflowRecoveryStatus.Pending, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskNumber.Trim(), out var task)) throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
            task.SetWorkflowContext(workflowKind, workflowReference, snapshotJson);
            if (recoveryStatus == WorkflowRecoveryStatus.BlockedMissingBusinessState) task.MarkWorkflowRecoveryBlocked();
            return Task.CompletedTask;
        }
    }

    public Task MarkWorkflowRecoveryBlockedAsync(string taskNumber, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskNumber.Trim(), out var task)) throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
            task.MarkWorkflowRecoveryBlocked();
            return Task.CompletedTask;
        }
    }

    public Task MarkWorkflowRecoveredAsync(string taskNumber, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskNumber.Trim(), out var task)) throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
            task.MarkWorkflowRecovered();
            return Task.CompletedTask;
        }
    }

    public Task UpdateDispatchContextAsync(string taskNumber, string contextJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskNumber.Trim(), out var task)) throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
            task.SetDispatchContext(contextJson);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<WarehouseTask>> GetTasksByStatesAsync(IReadOnlyCollection<TaskState> states, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var snapshot = _tasks.Values.Where(task => states.Contains(task.State)).ToArray();
            return Task.FromResult<IReadOnlyList<WarehouseTask>>(snapshot);
        }
    }

    public Task<WarehouseTask> TransitionTaskAsync(string taskNumber, int expectedVersion, TaskState nextState, string operatorName, string reason, string? errorCode = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskNumber.Trim(), out var task))
            {
                throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
            }

            if (task.Version != expectedVersion)
            {
                throw new InvalidOperationException($"Task '{task.TaskNumber}' version conflict; expected {expectedVersion}, actual {task.Version}.");
            }

            task.TransitionTo(nextState, operatorName, reason, errorCode, occurredAt);
            return Task.FromResult(task);
        }
    }

    public Task<TaskIdempotencyRegistrationResult> RegisterIdempotencyKeyAsync(TaskIdempotencyKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_idempotency.TryGetValue(key.UniqueKey, out var existing))
            {
                existing.EnsureRequestMatches(key.Scope, key.Key, key.RequestHash);
                return Task.FromResult(new TaskIdempotencyRegistrationResult(existing, true));
            }

            _idempotency.Add(key.UniqueKey, key);
            return Task.FromResult(new TaskIdempotencyRegistrationResult(key, false));
        }
    }

    public Task<TaskIdempotencyKey?> GetIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _idempotency.TryGetValue(TaskIdempotencyKey.BuildUniqueKey(scope, key), out var value);
            return Task.FromResult(value);
        }
    }

    public Task<ResourceLock> AcquireResourceLockAsync(string resourceType, string resourceId, string ownerTaskNumber, DateTimeOffset acquiredAt, TimeSpan leaseDuration, Guid? lockToken = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var existing = _locks.Values.SingleOrDefault(x => string.Equals(x.ResourceKey, ResourceLock.BuildResourceKey(resourceType, resourceId), StringComparison.Ordinal) && x.ReleasedAt is null);
            if (existing is not null)
            {
                if (existing.IsActive(acquiredAt))
                {
                    throw new InvalidOperationException($"Resource '{existing.ResourceKey}' is already locked.");
                }

                existing.Release(existing.OwnerTaskNumber, existing.Version, acquiredAt, existing.LockToken);
            }

            var created = new ResourceLock(resourceType, resourceId, ownerTaskNumber, acquiredAt, leaseDuration, lockToken);
            _locks.Add(created.Id, created);
            return Task.FromResult(created);
        }
    }

    public Task<ResourceLock> RenewResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset renewedAt, TimeSpan leaseDuration, Guid lockToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var resourceLock = GetLock(lockId);
            resourceLock.Renew(ownerTaskNumber, expectedVersion, renewedAt, leaseDuration, lockToken);
            return Task.FromResult(resourceLock);
        }
    }

    public Task ReleaseResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset releasedAt, Guid lockToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            GetLock(lockId).Release(ownerTaskNumber, expectedVersion, releasedAt, lockToken);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<ResourceLock>> GetActiveResourceLocksAsync(DateTimeOffset? at = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var snapshot = _locks.Values.Where(x => x.IsActive(at)).ToArray();
            return Task.FromResult<IReadOnlyList<ResourceLock>>(snapshot);
        }
    }

    private ResourceLock GetLock(Guid id)
        => _locks.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException($"Resource lock '{id}' was not found.");
}

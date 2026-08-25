using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class SqlServerTaskPersistenceStore(IDbContextFactory<WarehouseDbContext> dbContextFactory)
    : ITaskPersistenceStore, IResourceLockStore
{
    private const int MaxTransientAttempts = 3;
    private int _deadlockRetryCount;
    private int _lockContentionCount;

    /// <summary>Number of transient deadlock retries observed by this store.</summary>
    public int DeadlockRetryCount => Volatile.Read(ref _deadlockRetryCount);

    /// <summary>Number of lease/unique-key lock contention observations.</summary>
    public int LockContentionCount => Volatile.Read(ref _lockContentionCount);

    public async Task<TaskCreateResult> CreateTaskAsync(WarehouseTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.Tasks.SingleOrDefaultAsync(x => x.TaskNumber == task.TaskNumber, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.TaskType, task.TaskType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Task number '{task.TaskNumber}' is already used by another task.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new TaskCreateResult(existing, true);
        }

        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskCreateResult(task, false);
    }

    public async Task<WarehouseTask?> GetTaskAsync(string taskNumber, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks.Include(x => x.StateHistory).SingleOrDefaultAsync(x => x.TaskNumber == taskNumber.Trim(), cancellationToken);
    }

    public async Task UpdateWorkflowContextAsync(string taskNumber, string workflowKind, string workflowReference, string snapshotJson, WorkflowRecoveryStatus recoveryStatus = WorkflowRecoveryStatus.Pending, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.TaskNumber == taskNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
        task.SetWorkflowContext(workflowKind, workflowReference, snapshotJson);
        if (recoveryStatus == WorkflowRecoveryStatus.BlockedMissingBusinessState) task.MarkWorkflowRecoveryBlocked();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkWorkflowRecoveryBlockedAsync(string taskNumber, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.TaskNumber == taskNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
        task.MarkWorkflowRecoveryBlocked();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkWorkflowRecoveredAsync(string taskNumber, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.TaskNumber == taskNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
        task.MarkWorkflowRecovered();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDispatchContextAsync(string taskNumber, string contextJson, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.TaskNumber == taskNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
        task.SetDispatchContext(contextJson);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WarehouseTask>> GetTasksByStatesAsync(IReadOnlyCollection<TaskState> states, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(states);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tasks.Include(x => x.StateHistory)
            .Where(x => states.Contains(x.State))
            .ToListAsync(cancellationToken);
    }

    public async Task<WarehouseTask> TransitionTaskAsync(string taskNumber, int expectedVersion, TaskState nextState, string operatorName, string reason, string? errorCode = null, DateTimeOffset? occurredAt = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await db.Tasks.Include(x => x.StateHistory).SingleOrDefaultAsync(x => x.TaskNumber == taskNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Task '{taskNumber}' was not found.");
        if (task.Version != expectedVersion)
        {
            throw new InvalidOperationException($"Task '{task.TaskNumber}' version conflict; expected {expectedVersion}, actual {task.Version}.");
        }

        var originalVersion = task.Version;
        task.TransitionTo(nextState, operatorName, reason, errorCode, occurredAt);
        db.Entry(task).Property(x => x.Version).OriginalValue = originalVersion;
        db.Entry(task.StateHistory[^1]).State = EntityState.Added;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return task;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException($"Task '{task.TaskNumber}' was changed by another worker.", exception);
        }
    }

    public async Task<TaskIdempotencyRegistrationResult> RegisterIdempotencyKeyAsync(TaskIdempotencyKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.TaskIdempotencyKeys.SingleOrDefaultAsync(x => x.Scope == key.Scope && x.Key == key.Key, cancellationToken);
        if (existing is not null)
        {
            existing.EnsureRequestMatches(key.Scope, key.Key, key.RequestHash);
            await transaction.CommitAsync(cancellationToken);
            return new TaskIdempotencyRegistrationResult(existing, true);
        }

        db.TaskIdempotencyKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TaskIdempotencyRegistrationResult(key, false);
    }

    public async Task<TaskIdempotencyKey?> GetIdempotencyKeyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.TaskIdempotencyKeys.SingleOrDefaultAsync(x => x.Scope == scope.Trim() && x.Key == key.Trim(), cancellationToken);
    }

    public async Task<ResourceLock> AcquireResourceLockAsync(string resourceType, string resourceId, string ownerTaskNumber, DateTimeOffset acquiredAt, TimeSpan leaseDuration, Guid? lockToken = null, CancellationToken cancellationToken = default)
    {
        ResourceLock? result = null;
        await ExecuteWithTransientRetryAsync(
            async () => result = await AcquireResourceLockOnceAsync(resourceType, resourceId, ownerTaskNumber, acquiredAt, leaseDuration, lockToken, cancellationToken),
            cancellationToken,
            IsTransientLockFailure,
            ObserveTransientRetry);
        return result!;
    }

    private async Task<ResourceLock> AcquireResourceLockOnceAsync(string resourceType, string resourceId, string ownerTaskNumber, DateTimeOffset acquiredAt, TimeSpan leaseDuration, Guid? lockToken, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.ResourceLocks.SingleOrDefaultAsync(x => x.ResourceType == resourceType.Trim() && x.ResourceId == resourceId.Trim() && x.ReleasedAt == null, cancellationToken);
        if (existing is not null)
        {
            if (existing.IsActive(acquiredAt))
            {
                Interlocked.Increment(ref _lockContentionCount);
                throw new InvalidOperationException($"Resource '{existing.ResourceKey}' is already locked.");
            }

            existing.Release(existing.OwnerTaskNumber, existing.Version, acquiredAt, existing.LockToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var created = new ResourceLock(resourceType, resourceId, ownerTaskNumber, acquiredAt, leaseDuration, lockToken);
        db.ResourceLocks.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<ResourceLock> RenewResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset renewedAt, TimeSpan leaseDuration, Guid lockToken, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var resourceLock = await db.ResourceLocks.SingleOrDefaultAsync(x => x.Id == lockId, cancellationToken)
            ?? throw new KeyNotFoundException($"Resource lock '{lockId}' was not found.");
        var originalVersion = resourceLock.Version;
        resourceLock.Renew(ownerTaskNumber, expectedVersion, renewedAt, leaseDuration, lockToken);
        db.Entry(resourceLock).Property(x => x.Version).OriginalValue = originalVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return resourceLock;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException($"Resource lock '{resourceLock.ResourceKey}' was changed by another worker.", exception);
        }
    }

    public async Task ReleaseResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset releasedAt, Guid lockToken, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var resourceLock = await db.ResourceLocks.SingleOrDefaultAsync(x => x.Id == lockId, cancellationToken)
            ?? throw new KeyNotFoundException($"Resource lock '{lockId}' was not found.");
        var originalVersion = resourceLock.Version;
        resourceLock.Release(ownerTaskNumber, expectedVersion, releasedAt, lockToken);
        db.Entry(resourceLock).Property(x => x.Version).OriginalValue = originalVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException($"Resource lock '{resourceLock.ResourceKey}' was changed by another worker.", exception);
        }
    }

    public async Task<IReadOnlyList<ResourceLock>> GetActiveResourceLocksAsync(DateTimeOffset? at = null, CancellationToken cancellationToken = default)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ResourceLocks.Where(x => x.ReleasedAt == null && x.ExpiresAt > timestamp).ToListAsync(cancellationToken);
    }

    internal static async Task ExecuteWithTransientRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken,
        Func<Exception, bool>? transient = null,
        Action<Exception>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        transient ??= IsTransientLockFailure;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception exception) when (attempt < MaxTransientAttempts && transient(exception))
            {
                onRetry?.Invoke(exception);
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private void ObserveTransientRetry(Exception exception)
    {
        if (FindSqlError(exception, 1205) is not null)
            Interlocked.Increment(ref _deadlockRetryCount);
        else
            Interlocked.Increment(ref _lockContentionCount);
    }

    private static bool IsTransientLockFailure(Exception exception)
        => FindSqlError(exception, 1205, 1222, 2601, 2627) is not null;

    private static SqlError? FindSqlError(Exception exception, params int[] numbers)
    {
        Exception? current = exception;
        while (current is not null)
        {
            if (current is SqlException sql)
                return sql.Errors.Cast<SqlError>().FirstOrDefault(error => numbers.Contains(error.Number));
            current = current.InnerException;
        }

        return null;
    }
}

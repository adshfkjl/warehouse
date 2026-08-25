using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Tasks;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class SqlServerBusinessWorkflowStore(IDbContextFactory<WarehouseDbContext> dbContextFactory) : IBusinessWorkflowStore
{
    public async Task<BusinessWorkflowSnapshot?> GetAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var type = Require(aggregateType, nameof(aggregateType));
        var key = Require(aggregateKey, nameof(aggregateKey));
        var entity = await db.BusinessWorkflows.AsNoTracking().SingleOrDefaultAsync(x => x.AggregateType == type && x.AggregateKey == key, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<BusinessWorkflowSnapshot>> GetByTypeAsync(string aggregateType, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var type = Require(aggregateType, nameof(aggregateType));
        return await db.BusinessWorkflows.AsNoTracking().Where(x => x.AggregateType == type)
            .OrderBy(x => x.AggregateKey).Select(x => new BusinessWorkflowSnapshot(
                x.AggregateType, x.AggregateKey, x.Version, x.Status, x.SnapshotJson, x.UpdatedAt, x.WarehouseTaskNumber)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BusinessWorkflowStateHistory>> GetHistoryAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var type = Require(aggregateType, nameof(aggregateType));
        var key = Require(aggregateKey, nameof(aggregateKey));
        return await db.BusinessWorkflowHistories.AsNoTracking()
            .Where(x => x.AggregateType == type && x.AggregateKey == key).OrderBy(x => x.Version)
            .Select(x => new BusinessWorkflowStateHistory(x.Id, x.AggregateType, x.AggregateKey, x.Version, x.FromStatus, x.ToStatus, x.OccurredAt, x.Reason, x.OperatorId))
            .ToListAsync(cancellationToken);
    }

    public async Task SaveAsync(BusinessWorkflowSnapshot snapshot, int expectedVersion, string? reason = null, string? operatorId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate(snapshot, expectedVersion);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await SaveOnceAsync(snapshot, expectedVersion, reason, operatorId, cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < 3 && IsTransientConcurrency(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken);
            }
        }
    }

    private async Task SaveOnceAsync(BusinessWorkflowSnapshot snapshot, int expectedVersion, string? reason, string? operatorId, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var type = Require(snapshot.AggregateType, nameof(snapshot.AggregateType));
        var key = Require(snapshot.AggregateKey, nameof(snapshot.AggregateKey));
        var existing = await db.BusinessWorkflows.SingleOrDefaultAsync(x => x.AggregateType == type && x.AggregateKey == key, cancellationToken);
        var actual = existing?.Version ?? 0;
        if (actual != expectedVersion)
            throw new BusinessWorkflowConcurrencyException($"Business workflow '{type}:{key}' version conflict; expected {expectedVersion}, actual {actual}.");
        if (snapshot.Version != expectedVersion + 1)
            throw new BusinessWorkflowConcurrencyException($"Business workflow '{type}:{key}' must advance to version {expectedVersion + 1}.");

        var timestamp = snapshot.UpdatedAt.ToUniversalTime();
        if (existing is null)
        {
            db.BusinessWorkflows.Add(new BusinessWorkflowEntity
            {
                Id = Guid.NewGuid(), AggregateType = type, AggregateKey = key, Version = snapshot.Version,
                Status = Require(snapshot.Status, nameof(snapshot.Status)), SnapshotJson = Require(snapshot.SnapshotJson, nameof(snapshot.SnapshotJson)),
                UpdatedAt = timestamp, WarehouseTaskNumber = Normalize(snapshot.WarehouseTaskNumber)
            });
        }
        else
        {
            var previousStatus = existing.Status;
            existing.Version = snapshot.Version;
            existing.Status = Require(snapshot.Status, nameof(snapshot.Status));
            existing.SnapshotJson = Require(snapshot.SnapshotJson, nameof(snapshot.SnapshotJson));
            existing.UpdatedAt = timestamp;
            existing.WarehouseTaskNumber = Normalize(snapshot.WarehouseTaskNumber);
            db.BusinessWorkflowHistories.Add(new BusinessWorkflowStateHistoryEntity
            {
                Id = Guid.NewGuid(), AggregateType = type, AggregateKey = key, Version = snapshot.Version,
                FromStatus = previousStatus, ToStatus = snapshot.Status, OccurredAt = timestamp,
                Reason = Normalize(reason), OperatorId = Normalize(operatorId)
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BusinessWorkflowIdempotencyResult> RegisterIdempotencyAsync(string scope, string key, string requestHash, string? aggregateType = null, string? aggregateKey = null, CancellationToken cancellationToken = default)
    {
        var normalizedScope = Require(scope, nameof(scope));
        var normalizedKey = Require(key, nameof(key));
        var normalizedHash = Require(requestHash, nameof(requestHash));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.BusinessWorkflowIdempotency.SingleOrDefaultAsync(x => x.Scope == normalizedScope && x.Key == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, normalizedHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Idempotency key '{normalizedScope}:{normalizedKey}' was reused with a different request.");
            await transaction.CommitAsync(cancellationToken);
            return new BusinessWorkflowIdempotencyResult(ToIdempotency(existing), true);
        }

        var entry = new BusinessWorkflowIdempotencyEntity
        {
            Id = Guid.NewGuid(), Scope = normalizedScope, Key = normalizedKey, RequestHash = normalizedHash,
            AggregateType = Normalize(aggregateType), AggregateKey = Normalize(aggregateKey), CreatedAt = DateTimeOffset.UtcNow
        };
        db.BusinessWorkflowIdempotency.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BusinessWorkflowIdempotencyResult(ToIdempotency(entry), false);
    }

    public async Task<BusinessWorkflowIdempotency?> GetIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedScope = Require(scope, nameof(scope));
        var normalizedKey = Require(key, nameof(key));
        var entity = await db.BusinessWorkflowIdempotency.AsNoTracking().SingleOrDefaultAsync(x => x.Scope == normalizedScope && x.Key == normalizedKey, cancellationToken);
        return entity is null ? null : ToIdempotency(entity);
    }

    public async Task RemoveIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var normalizedScope = Require(scope, nameof(scope));
        var normalizedKey = Require(key, nameof(key));
        var entity = await db.BusinessWorkflowIdempotency.SingleOrDefaultAsync(x => x.Scope == normalizedScope && x.Key == normalizedKey, cancellationToken);
        if (entity is null) return;
        db.BusinessWorkflowIdempotency.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static BusinessWorkflowSnapshot ToSnapshot(BusinessWorkflowEntity x) => new(x.AggregateType, x.AggregateKey, x.Version, x.Status, x.SnapshotJson, x.UpdatedAt, x.WarehouseTaskNumber);
    private static BusinessWorkflowIdempotency ToIdempotency(BusinessWorkflowIdempotencyEntity x) => new(x.Scope, x.Key, x.RequestHash, x.AggregateType, x.AggregateKey, x.CreatedAt);
    private static void Validate(BusinessWorkflowSnapshot snapshot, int expectedVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        if (snapshot.Version <= 0) throw new ArgumentOutOfRangeException(nameof(snapshot), "Version must be positive.");
        Require(snapshot.AggregateType, nameof(snapshot.AggregateType)); Require(snapshot.AggregateKey, nameof(snapshot.AggregateKey));
        Require(snapshot.Status, nameof(snapshot.Status)); Require(snapshot.SnapshotJson, nameof(snapshot.SnapshotJson));
    }
    private static string Require(string? value, string parameterName) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsTransientConcurrency(Exception exception)
    {
        Exception? current = exception;
        SqlException? sql = null;
        while (current is not null)
        {
            if (current is SqlException direct) { sql = direct; break; }
            current = current.InnerException;
        }
        return sql is not null && sql.Errors.Cast<SqlError>().Any(error => error.Number is 1205 or 2601 or 2627);
    }
}

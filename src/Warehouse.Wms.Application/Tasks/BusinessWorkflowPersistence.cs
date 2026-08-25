namespace Warehouse.Wms.Application.Tasks;

/// <summary>
/// Durable application-owned state for a business workflow. Device execution is
/// deliberately outside this boundary; a snapshot only records the task link
/// and the state needed to resume a workflow after a process restart.
/// </summary>
public sealed record BusinessWorkflowSnapshot(
    string AggregateType,
    string AggregateKey,
    int Version,
    string Status,
    string SnapshotJson,
    DateTimeOffset UpdatedAt,
    string? WarehouseTaskNumber = null);

public sealed record BusinessWorkflowStateHistory(
    Guid Id,
    string AggregateType,
    string AggregateKey,
    int Version,
    string? FromStatus,
    string ToStatus,
    DateTimeOffset OccurredAt,
    string? Reason = null,
    string? OperatorId = null);

public sealed record BusinessWorkflowIdempotency(
    string Scope,
    string Key,
    string RequestHash,
    string? AggregateType,
    string? AggregateKey,
    DateTimeOffset CreatedAt);

public sealed record BusinessWorkflowIdempotencyResult(BusinessWorkflowIdempotency Entry, bool Replayed);

public sealed class BusinessWorkflowConcurrencyException(string message) : InvalidOperationException(message);

public interface IBusinessWorkflowStore
{
    Task<BusinessWorkflowSnapshot?> GetAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BusinessWorkflowSnapshot>> GetByTypeAsync(string aggregateType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BusinessWorkflowStateHistory>> GetHistoryAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default);
    Task SaveAsync(BusinessWorkflowSnapshot snapshot, int expectedVersion, string? reason = null, string? operatorId = null, CancellationToken cancellationToken = default);
    Task<BusinessWorkflowIdempotencyResult> RegisterIdempotencyAsync(string scope, string key, string requestHash, string? aggregateType = null, string? aggregateKey = null, CancellationToken cancellationToken = default);
    Task<BusinessWorkflowIdempotency?> GetIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default);
    Task RemoveIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Explicit in-memory implementation used by unit tests and local development.
/// CreateRestartedInstance simulates a new process over the same durable store.
/// </summary>
public sealed class InMemoryBusinessWorkflowStore : IBusinessWorkflowStore
{
    private sealed class State
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, BusinessWorkflowSnapshot> Snapshots = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<BusinessWorkflowStateHistory>> History = new(StringComparer.Ordinal);
        public readonly Dictionary<string, BusinessWorkflowIdempotency> Idempotency = new(StringComparer.Ordinal);
    }

    private readonly State _state;

    public InMemoryBusinessWorkflowStore() : this(new State()) { }

    private InMemoryBusinessWorkflowStore(State state) => _state = state;

    public InMemoryBusinessWorkflowStore CreateRestartedInstance() => new(_state);

    public Task<BusinessWorkflowSnapshot?> GetAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = BuildAggregateKey(aggregateType, aggregateKey);
        lock (_state.Gate)
        {
            _state.Snapshots.TryGetValue(id, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    public Task<IReadOnlyList<BusinessWorkflowSnapshot>> GetByTypeAsync(string aggregateType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = Require(aggregateType, nameof(aggregateType));
        lock (_state.Gate)
        {
            return Task.FromResult<IReadOnlyList<BusinessWorkflowSnapshot>>(
                _state.Snapshots.Values.Where(x => x.AggregateType == type).ToArray());
        }
    }

    public Task<IReadOnlyList<BusinessWorkflowStateHistory>> GetHistoryAsync(string aggregateType, string aggregateKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = BuildAggregateKey(aggregateType, aggregateKey);
        lock (_state.Gate)
        {
            return Task.FromResult<IReadOnlyList<BusinessWorkflowStateHistory>>(
                _state.History.TryGetValue(id, out var history) ? history.ToArray() : []);
        }
    }

    public Task SaveAsync(BusinessWorkflowSnapshot snapshot, int expectedVersion, string? reason = null, string? operatorId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSnapshot(snapshot, expectedVersion);
        var id = BuildAggregateKey(snapshot.AggregateType, snapshot.AggregateKey);
        lock (_state.Gate)
        {
            _state.Snapshots.TryGetValue(id, out var existing);
            var actual = existing?.Version ?? 0;
            if (actual != expectedVersion)
                throw new BusinessWorkflowConcurrencyException($"Business workflow '{id}' version conflict; expected {expectedVersion}, actual {actual}.");
            if (snapshot.Version != expectedVersion + 1)
                throw new BusinessWorkflowConcurrencyException($"Business workflow '{id}' must advance to version {expectedVersion + 1}.");

            _state.Snapshots[id] = snapshot with { UpdatedAt = snapshot.UpdatedAt.ToUniversalTime() };
            if (existing is not null)
            {
                if (!_state.History.TryGetValue(id, out var history))
                    _state.History[id] = history = [];
                history.Add(new BusinessWorkflowStateHistory(
                    Guid.NewGuid(), snapshot.AggregateType, snapshot.AggregateKey, snapshot.Version,
                    existing.Status, snapshot.Status, snapshot.UpdatedAt.ToUniversalTime(), reason, operatorId));
            }
            return Task.CompletedTask;
        }
    }

    public Task<BusinessWorkflowIdempotencyResult> RegisterIdempotencyAsync(string scope, string key, string requestHash, string? aggregateType = null, string? aggregateKey = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedScope = Require(scope, nameof(scope));
        var normalizedKey = Require(key, nameof(key));
        var normalizedHash = Require(requestHash, nameof(requestHash));
        var id = BuildIdempotencyKey(normalizedScope, normalizedKey);
        lock (_state.Gate)
        {
            if (_state.Idempotency.TryGetValue(id, out var existing))
            {
                if (!string.Equals(existing.RequestHash, normalizedHash, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Idempotency key '{normalizedScope}:{normalizedKey}' was reused with a different request.");
                return Task.FromResult(new BusinessWorkflowIdempotencyResult(existing, true));
            }
            var entry = new BusinessWorkflowIdempotency(normalizedScope, normalizedKey, normalizedHash,
                Normalize(aggregateType), Normalize(aggregateKey), DateTimeOffset.UtcNow);
            _state.Idempotency.Add(id, entry);
            return Task.FromResult(new BusinessWorkflowIdempotencyResult(entry, false));
        }
    }

    public Task<BusinessWorkflowIdempotency?> GetIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = BuildIdempotencyKey(scope, key);
        lock (_state.Gate)
        {
            _state.Idempotency.TryGetValue(id, out var value);
            return Task.FromResult(value);
        }
    }

    public Task RemoveIdempotencyAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = BuildIdempotencyKey(scope, key);
        lock (_state.Gate) _state.Idempotency.Remove(id);
        return Task.CompletedTask;
    }

    private static void ValidateSnapshot(BusinessWorkflowSnapshot snapshot, int expectedVersion)
    {
        Require(snapshot.AggregateType, nameof(snapshot.AggregateType));
        Require(snapshot.AggregateKey, nameof(snapshot.AggregateKey));
        Require(snapshot.Status, nameof(snapshot.Status));
        Require(snapshot.SnapshotJson, nameof(snapshot.SnapshotJson));
        if (snapshot.Version <= 0) throw new ArgumentOutOfRangeException(nameof(snapshot), "Version must be positive.");
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
    }

    internal static string BuildAggregateKey(string aggregateType, string aggregateKey)
        => $"{Require(aggregateType, nameof(aggregateType))}:{Require(aggregateKey, nameof(aggregateKey))}";

    internal static string BuildIdempotencyKey(string scope, string key)
        => $"{Require(scope, nameof(scope))}:{Require(key, nameof(key))}";

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value.Trim();

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

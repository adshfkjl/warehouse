using System.Globalization;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.Application.Inventory;

public sealed class InventoryService
{
    private readonly object _gate = new();
    private readonly Dictionary<BalanceKey, InventoryBalance> _balances = new();
    private readonly List<InventoryTransaction> _transactions = [];
    private readonly Dictionary<string, InventoryTransaction> _idempotency = new(StringComparer.Ordinal);

    public Task<InventoryTransaction> IncreaseAsync(
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal quantity,
        decimal weightKg,
        InventoryOperationContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            context,
            InventoryTransactionType.Increase,
            Fingerprint("increase", materialId, palletId, locationId, batchNumber, quantity, weightKg),
            cancellationToken,
            () =>
            {
                EnsurePositive(quantity, nameof(quantity));
                EnsureNonNegative(weightKg, nameof(weightKg));
                var key = new BalanceKey(materialId, palletId, locationId, Normalize(batchNumber));
                var current = GetOrEmpty(key, InventoryStatus.Available);
                EnsureMutable(current.Status);
                var next = new InventoryBalance(
                    materialId,
                    palletId,
                    locationId,
                    batchNumber,
                    current.Quantity + quantity,
                    current.WeightKg + weightKg,
                    current.Status,
                    current.Version + 1);
                _balances[key] = next;
                return CreateTransaction(context, InventoryTransactionType.Increase, materialId, palletId, locationId, null, null, batchNumber, quantity, weightKg, current.Status, next.Status, Fingerprint("increase", materialId, palletId, locationId, batchNumber, quantity, weightKg));
            });

    public Task<InventoryTransaction> DecreaseAsync(
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal quantity,
        decimal weightKg,
        InventoryOperationContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            context,
            InventoryTransactionType.Decrease,
            Fingerprint("decrease", materialId, palletId, locationId, batchNumber, quantity, weightKg),
            cancellationToken,
            () =>
            {
                EnsurePositive(quantity, nameof(quantity));
                EnsureNonNegative(weightKg, nameof(weightKg));
                var key = new BalanceKey(materialId, palletId, locationId, Normalize(batchNumber));
                var current = RequireBalance(key);
                EnsureMutable(current.Status);
                EnsureEnough(current, quantity, weightKg);
                var next = new InventoryBalance(
                    materialId,
                    palletId,
                    locationId,
                    batchNumber,
                    current.Quantity - quantity,
                    current.WeightKg - weightKg,
                    current.Status,
                    current.Version + 1);
                _balances[key] = next;
                return CreateTransaction(context, InventoryTransactionType.Decrease, materialId, palletId, locationId, null, null, batchNumber, quantity, weightKg, current.Status, next.Status, Fingerprint("decrease", materialId, palletId, locationId, batchNumber, quantity, weightKg));
            });

    public Task<InventoryTransaction> LockAsync(
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal quantity,
        InventoryOperationContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            context,
            InventoryTransactionType.Lock,
            Fingerprint("lock", materialId, palletId, locationId, batchNumber, quantity, 0m),
            cancellationToken,
            () =>
            {
                EnsurePositive(quantity, nameof(quantity));
                var key = new BalanceKey(materialId, palletId, locationId, Normalize(batchNumber));
                var current = RequireBalance(key);
                if (current.Status != InventoryStatus.Available || current.Quantity != quantity)
                {
                    throw new InvalidOperationException("Only the complete available balance can be locked atomically.");
                }

                var next = new InventoryBalance(materialId, palletId, locationId, batchNumber, current.Quantity, current.WeightKg, InventoryStatus.Locked, current.Version + 1);
                _balances[key] = next;
                return CreateTransaction(context, InventoryTransactionType.Lock, materialId, palletId, locationId, null, null, batchNumber, quantity, 0m, current.Status, next.Status, Fingerprint("lock", materialId, palletId, locationId, batchNumber, quantity, 0m));
            });

    public Task<InventoryTransaction> UnlockAsync(
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal quantity,
        InventoryOperationContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            context,
            InventoryTransactionType.Unlock,
            Fingerprint("unlock", materialId, palletId, locationId, batchNumber, quantity, 0m),
            cancellationToken,
            () =>
            {
                EnsurePositive(quantity, nameof(quantity));
                var key = new BalanceKey(materialId, palletId, locationId, Normalize(batchNumber));
                var current = RequireBalance(key);
                if (current.Status != InventoryStatus.Locked || current.Quantity != quantity)
                {
                    throw new InvalidOperationException("Only the complete locked balance can be unlocked atomically.");
                }

                var next = new InventoryBalance(materialId, palletId, locationId, batchNumber, current.Quantity, current.WeightKg, InventoryStatus.Available, current.Version + 1);
                _balances[key] = next;
                return CreateTransaction(context, InventoryTransactionType.Unlock, materialId, palletId, locationId, null, null, batchNumber, quantity, 0m, current.Status, next.Status, Fingerprint("unlock", materialId, palletId, locationId, batchNumber, quantity, 0m));
            });

    public Task<InventoryTransaction> MoveAsync(
        Guid materialId,
        Guid? palletId,
        Guid sourceLocationId,
        Guid destinationLocationId,
        string? batchNumber,
        decimal quantity,
        decimal weightKg,
        InventoryOperationContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            context,
            InventoryTransactionType.Move,
            Fingerprint("move", materialId, palletId, sourceLocationId, destinationLocationId, batchNumber, quantity, weightKg),
            cancellationToken,
            () =>
            {
                if (sourceLocationId == destinationLocationId)
                {
                    throw new InvalidOperationException("Source and destination locations must differ.");
                }

                EnsurePositive(quantity, nameof(quantity));
                EnsureNonNegative(weightKg, nameof(weightKg));
                var sourceKey = new BalanceKey(materialId, palletId, sourceLocationId, Normalize(batchNumber));
                var destinationKey = new BalanceKey(materialId, palletId, destinationLocationId, Normalize(batchNumber));
                var source = RequireBalance(sourceKey);
                EnsureMutable(source.Status);
                EnsureEnough(source, quantity, weightKg);
                var destination = _balances.TryGetValue(destinationKey, out var existing)
                    ? existing
                    : new InventoryBalance(materialId, palletId, destinationLocationId, batchNumber, 0m, 0m, source.Status);
                if (destination.Status != source.Status && destination.Quantity > 0m)
                {
                    throw new InvalidOperationException("Destination inventory status must match the source.");
                }

                _balances[sourceKey] = new InventoryBalance(materialId, palletId, sourceLocationId, batchNumber, source.Quantity - quantity, source.WeightKg - weightKg, source.Status, source.Version + 1);
                _balances[destinationKey] = new InventoryBalance(materialId, palletId, destinationLocationId, batchNumber, destination.Quantity + quantity, destination.WeightKg + weightKg, source.Status, destination.Version + 1);
                return CreateTransaction(context, InventoryTransactionType.Move, materialId, palletId, null, sourceLocationId, destinationLocationId, batchNumber, quantity, weightKg, source.Status, source.Status, Fingerprint("move", materialId, palletId, sourceLocationId, destinationLocationId, batchNumber, quantity, weightKg));
            });

    public Task<InventoryTransaction> AdjustAsync(
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal quantityDelta,
        decimal weightDelta,
        InventoryOperationContext context,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            context,
            InventoryTransactionType.Adjustment,
            Fingerprint("adjustment", materialId, palletId, locationId, batchNumber, quantityDelta, weightDelta),
            cancellationToken,
            () =>
            {
                if (quantityDelta == 0m && weightDelta == 0m)
                {
                    throw new ArgumentException("An adjustment must change quantity or weight.");
                }

                var key = new BalanceKey(materialId, palletId, locationId, Normalize(batchNumber));
                var current = GetOrEmpty(key, InventoryStatus.Available);
                EnsureMutable(current.Status);
                var nextQuantity = current.Quantity + quantityDelta;
                var nextWeight = current.WeightKg + weightDelta;
                if (nextQuantity < 0m || nextWeight < 0m)
                {
                    throw new InvalidOperationException("An adjustment cannot create a negative balance.");
                }

                var next = new InventoryBalance(materialId, palletId, locationId, batchNumber, nextQuantity, nextWeight, current.Status, current.Version + 1);
                _balances[key] = next;
                return CreateTransaction(context, InventoryTransactionType.Adjustment, materialId, palletId, locationId, null, null, batchNumber, quantityDelta, weightDelta, current.Status, next.Status, Fingerprint("adjustment", materialId, palletId, locationId, batchNumber, quantityDelta, weightDelta));
            });

    public InventoryBalance? GetBalance(Guid materialId, Guid? palletId, Guid? locationId, string? batchNumber)
    {
        lock (_gate)
        {
            _balances.TryGetValue(new BalanceKey(materialId, palletId, locationId, Normalize(batchNumber)), out var balance);
            return balance;
        }
    }

    public IReadOnlyList<InventoryBalance> GetBalances()
    {
        lock (_gate)
        {
            return _balances.Values.ToArray();
        }
    }

    public IReadOnlyList<InventoryTransaction> GetTransactions()
    {
        lock (_gate)
        {
            return _transactions.ToArray();
        }
    }

    public IReadOnlyList<InventoryBalance> RebuildBalances()
    {
        lock (_gate)
        {
            var rebuilt = new Dictionary<BalanceKey, InventoryBalance>();
            foreach (var transaction in _transactions)
            {
                ApplyTransaction(rebuilt, transaction);
            }

            return rebuilt.Values.ToArray();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "The cancellation token is kept before the operation delegate to make the internal transaction wrapper readable.")]
    private Task<InventoryTransaction> ExecuteAsync(
        InventoryOperationContext context,
        InventoryTransactionType type,
        string fingerprint,
        CancellationToken cancellationToken,
        Func<InventoryTransaction> operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_idempotency.TryGetValue(context.IdempotencyKey, out var existing))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The idempotency key was already used for a different inventory operation.");
                }

                return Task.FromResult(existing);
            }

            var balances = new Dictionary<BalanceKey, InventoryBalance>(_balances);
            var transactionsCount = _transactions.Count;
            try
            {
                var transaction = operation();
                _transactions.Add(transaction);
                _idempotency.Add(context.IdempotencyKey, transaction);
                return Task.FromResult(transaction);
            }
            catch
            {
                _balances.Clear();
                foreach (var item in balances)
                {
                    _balances.Add(item.Key, item.Value);
                }

                if (_transactions.Count > transactionsCount)
                {
                    _transactions.RemoveRange(transactionsCount, _transactions.Count - transactionsCount);
                }

                throw;
            }
        }
    }

    private static InventoryTransaction CreateTransaction(
        InventoryOperationContext context,
        InventoryTransactionType type,
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        Guid? sourceLocationId,
        Guid? destinationLocationId,
        string? batchNumber,
        decimal quantity,
        decimal weightKg,
        InventoryStatus statusBefore,
        InventoryStatus statusAfter,
        string fingerprint) =>
        new(
            Guid.NewGuid(),
            context,
            type,
            materialId,
            palletId,
            locationId,
            sourceLocationId,
            destinationLocationId,
            batchNumber,
            quantity,
            weightKg,
            statusBefore,
            statusAfter,
            fingerprint,
            DateTimeOffset.UtcNow);

    private InventoryBalance GetOrEmpty(BalanceKey key, InventoryStatus status) =>
        _balances.TryGetValue(key, out var current)
            ? current
            : new InventoryBalance(key.MaterialId, key.PalletId, key.LocationId, key.BatchNumber, 0m, 0m, status);

    private InventoryBalance RequireBalance(BalanceKey key) =>
        _balances.TryGetValue(key, out var balance) && balance.Quantity > 0m
            ? balance
            : throw new InvalidOperationException("The requested inventory balance does not exist.");

    private static void ApplyTransaction(Dictionary<BalanceKey, InventoryBalance> balances, InventoryTransaction transaction)
    {
        var batch = Normalize(transaction.BatchNumber);
        switch (transaction.Type)
        {
            case InventoryTransactionType.Increase:
                ApplyDelta(balances, new BalanceKey(transaction.MaterialId, transaction.PalletId, transaction.LocationId, batch), transaction.Quantity, transaction.WeightKg, transaction.StatusAfter);
                break;
            case InventoryTransactionType.Decrease:
                ApplyDelta(balances, new BalanceKey(transaction.MaterialId, transaction.PalletId, transaction.LocationId, batch), -transaction.Quantity, -transaction.WeightKg, transaction.StatusAfter);
                break;
            case InventoryTransactionType.Lock:
            case InventoryTransactionType.Unlock:
                SetStatus(balances, new BalanceKey(transaction.MaterialId, transaction.PalletId, transaction.LocationId, batch), transaction.StatusAfter);
                break;
            case InventoryTransactionType.Move:
                var sourceKey = new BalanceKey(transaction.MaterialId, transaction.PalletId, transaction.SourceLocationId, batch);
                var destinationKey = new BalanceKey(transaction.MaterialId, transaction.PalletId, transaction.DestinationLocationId, batch);
                ApplyDelta(balances, sourceKey, -transaction.Quantity, -transaction.WeightKg, transaction.StatusAfter);
                ApplyDelta(balances, destinationKey, transaction.Quantity, transaction.WeightKg, transaction.StatusAfter);
                break;
            case InventoryTransactionType.Adjustment:
                ApplyDelta(balances, new BalanceKey(transaction.MaterialId, transaction.PalletId, transaction.LocationId, batch), transaction.Quantity, transaction.WeightKg, transaction.StatusAfter);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transaction), transaction.Type, "Unsupported inventory transaction type.");
        }
    }

    private static void ApplyDelta(Dictionary<BalanceKey, InventoryBalance> balances, BalanceKey key, decimal quantityDelta, decimal weightDelta, InventoryStatus status)
    {
        var current = balances.TryGetValue(key, out var existing)
            ? existing
            : new InventoryBalance(key.MaterialId, key.PalletId, key.LocationId, key.BatchNumber, 0m, 0m, status);
        var quantity = current.Quantity + quantityDelta;
        var weight = current.WeightKg + weightDelta;
        if (quantity < 0m || weight < 0m)
        {
            throw new InvalidOperationException("The inventory ledger produces a negative balance.");
        }

        balances[key] = new InventoryBalance(key.MaterialId, key.PalletId, key.LocationId, key.BatchNumber, quantity, weight, status, current.Version + 1);
    }

    private static void SetStatus(Dictionary<BalanceKey, InventoryBalance> balances, BalanceKey key, InventoryStatus status)
    {
        if (!balances.TryGetValue(key, out var current))
        {
            throw new InvalidOperationException("The requested inventory balance does not exist.");
        }

        balances[key] = new InventoryBalance(key.MaterialId, key.PalletId, key.LocationId, key.BatchNumber, current.Quantity, current.WeightKg, status, current.Version + 1);
    }

    private static void EnsureMutable(InventoryStatus status)
    {
        if (status is InventoryStatus.Frozen or InventoryStatus.Exception)
        {
            throw new InvalidOperationException("Frozen or exception inventory cannot be changed automatically.");
        }
    }

    private static void EnsureEnough(InventoryBalance current, decimal quantity, decimal weightKg)
    {
        if (current.Quantity < quantity || current.WeightKg < weightKg)
        {
            throw new InvalidOperationException("Insufficient inventory quantity or weight.");
        }
    }

    private static void EnsurePositive(decimal value, string name)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be greater than zero.");
        }
    }

    private static void EnsureNonNegative(decimal value, string name)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value cannot be negative.");
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Fingerprint(params object?[] values) => string.Join(
        "|",
        values.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>"));

    private readonly record struct BalanceKey(Guid MaterialId, Guid? PalletId, Guid? LocationId, string? BatchNumber);
}

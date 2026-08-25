using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.Application.Inventory;

/// <summary>
/// Durable inventory state loaded at process start. Transactions are ordered
/// by their occurrence time by the persistence adapter.
/// </summary>
public sealed record InventoryLedgerSnapshot(
    IReadOnlyList<InventoryBalance> Balances,
    IReadOnlyList<InventoryTransaction> Transactions)
{
    public static InventoryLedgerSnapshot Empty { get; } = new([], []);
}

public sealed record InventoryLedgerBalanceChange(
    InventoryBalance Balance,
    int ExpectedVersion);

public sealed record InventoryLedgerAppend(
    InventoryTransaction Transaction,
    IReadOnlyList<InventoryLedgerBalanceChange> BalanceChanges);

public sealed record InventoryLedgerAppendResult(
    InventoryTransaction Transaction,
    bool Replayed);

/// <summary>
/// Persistence boundary for the inventory ledger. Implementations must append
/// the immutable transaction and update all supplied balances atomically.
/// </summary>
public interface IInventoryLedgerStore
{
    Task<InventoryLedgerSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default);

    Task<InventoryLedgerAppendResult> AppendAsync(
        InventoryLedgerAppend append,
        CancellationToken cancellationToken = default);
}

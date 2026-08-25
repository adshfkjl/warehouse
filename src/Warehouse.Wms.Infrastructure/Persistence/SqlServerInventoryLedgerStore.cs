using System.Data;
using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.Infrastructure.Persistence;

/// <summary>
/// SQL Server adapter for the inventory ledger. The transaction only covers
/// balance rows and the immutable ledger row; device calls happen elsewhere.
/// </summary>
public sealed class SqlServerInventoryLedgerStore(IDbContextFactory<WarehouseDbContext> dbContextFactory)
    : IInventoryLedgerStore
{
    public async Task<InventoryLedgerSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var balanceEntities = await db.InventoryBalances
            .AsNoTracking()
            .OrderBy(x => x.MaterialId)
            .ThenBy(x => x.PalletId)
            .ThenBy(x => x.LocationId)
            .ThenBy(x => x.BatchNumber)
            .ToListAsync(cancellationToken);
        var transactionEntities = await db.InventoryTransactions
            .AsNoTracking()
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return new InventoryLedgerSnapshot(balanceEntities.Select(ToBalance).ToArray(), transactionEntities.Select(ToTransaction).ToArray());
    }

    public async Task<InventoryLedgerAppendResult> AppendAsync(
        InventoryLedgerAppend append,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(append);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var existing = await db.InventoryTransactions
            .SingleOrDefaultAsync(x => x.IdempotencyKey == append.Transaction.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, append.Transaction.Fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The idempotency key was already used for a different inventory operation.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new InventoryLedgerAppendResult(ToTransaction(existing), true);
        }

        foreach (var change in append.BalanceChanges)
        {
            await ApplyBalanceChangeAsync(db, change, cancellationToken);
        }

        db.InventoryTransactions.Add(ToEntity(append.Transaction));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new InvalidOperationException("The inventory balance changed concurrently; no ledger row was written.", exception);
        }

        return new InventoryLedgerAppendResult(append.Transaction, false);
    }

    private static async Task ApplyBalanceChangeAsync(
        WarehouseDbContext db,
        InventoryLedgerBalanceChange change,
        CancellationToken cancellationToken)
    {
        var balance = change.Balance;
        var entity = await db.InventoryBalances.SingleOrDefaultAsync(
            x => x.MaterialId == balance.MaterialId
                 && x.PalletId == balance.PalletId
                 && x.LocationId == balance.LocationId
                 && x.BatchNumber == balance.BatchNumber,
            cancellationToken);

        if (entity is null)
        {
            if (change.ExpectedVersion != 0)
            {
                throw new InvalidOperationException("The inventory balance changed concurrently; no ledger row was written.");
            }

            db.InventoryBalances.Add(ToEntity(balance));
            return;
        }

        if (change.ExpectedVersion == 0 || entity.Version != change.ExpectedVersion)
        {
            throw new InvalidOperationException("The inventory balance changed concurrently; no ledger row was written.");
        }

        entity.Quantity = balance.Quantity;
        entity.WeightKg = balance.WeightKg;
        entity.Status = balance.Status;
        entity.Version = balance.Version;
        db.Entry(entity).Property(x => x.Version).OriginalValue = change.ExpectedVersion;
    }

    private static InventoryBalanceEntity ToEntity(InventoryBalance balance) => new()
    {
        Id = Guid.NewGuid(),
        BalanceKey = balance.Key,
        MaterialId = balance.MaterialId,
        PalletId = balance.PalletId,
        LocationId = balance.LocationId,
        BatchNumber = balance.BatchNumber,
        Quantity = balance.Quantity,
        WeightKg = balance.WeightKg,
        Status = balance.Status,
        Version = balance.Version
    };

    private static InventoryTransactionEntity ToEntity(InventoryTransaction transaction) => new()
    {
        Id = transaction.Id,
        IdempotencyKey = transaction.IdempotencyKey,
        Type = transaction.Type,
        MaterialId = transaction.MaterialId,
        PalletId = transaction.PalletId,
        LocationId = transaction.LocationId,
        SourceLocationId = transaction.SourceLocationId,
        DestinationLocationId = transaction.DestinationLocationId,
        BatchNumber = transaction.BatchNumber,
        Quantity = transaction.Quantity,
        WeightKg = transaction.WeightKg,
        StatusBefore = transaction.StatusBefore,
        StatusAfter = transaction.StatusAfter,
        Fingerprint = transaction.Fingerprint,
        SourceDocumentId = transaction.SourceDocumentId,
        TaskNumber = transaction.TaskNumber,
        OperatorId = transaction.OperatorId,
        Reason = transaction.Reason,
        OccurredAt = transaction.OccurredAt
    };

    private static InventoryBalance ToBalance(InventoryBalanceEntity entity) =>
        new(entity.MaterialId, entity.PalletId, entity.LocationId, entity.BatchNumber,
            entity.Quantity, entity.WeightKg, entity.Status, entity.Version);

    private static InventoryTransaction ToTransaction(InventoryTransactionEntity entity) =>
        new(entity.Id,
            new InventoryOperationContext(entity.IdempotencyKey, entity.SourceDocumentId, entity.TaskNumber, entity.OperatorId, entity.Reason),
            entity.Type, entity.MaterialId, entity.PalletId, entity.LocationId,
            entity.SourceLocationId, entity.DestinationLocationId, entity.BatchNumber,
            entity.Quantity, entity.WeightKg, entity.StatusBefore, entity.StatusAfter,
            entity.Fingerprint, entity.OccurredAt);
}

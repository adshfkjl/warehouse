using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.UnitTests.Inventory;

public sealed class InventoryLedgerStoreTests
{
    [Fact]
    public async Task Injected_store_receives_append_and_snapshot_restores_after_restart()
    {
        var store = new RecordingLedgerStore();
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();

        var first = new InventoryService(store);
        var transaction = await first.IncreaseAsync(materialId, palletId, locationId, "B-1", 4m, 40m, Context("receive-1"));

        Assert.Single(store.Appends);
        Assert.Equal(transaction.Id, store.Appends[0].Transaction.Id);

        var restarted = new InventoryService(store);
        var balance = restarted.GetBalance(materialId, palletId, locationId, "B-1");
        Assert.NotNull(balance);
        Assert.Equal(4m, balance!.Quantity);
        Assert.Single(restarted.GetTransactions());
    }

    [Fact]
    public async Task Store_replay_with_same_fingerprint_does_not_duplicate_transaction()
    {
        var store = new RecordingLedgerStore { Replay = true };
        var service = new InventoryService(store);
        var transaction = await service.IncreaseAsync(Guid.NewGuid(), null, null, null, 1m, 1m, Context("receive-replay"));

        Assert.Equal(store.ReplayedTransaction!.Id, transaction.Id);
        Assert.Single(service.GetTransactions());
    }

    [Fact]
    public async Task Store_replay_restores_balance_when_store_has_authoritative_snapshot()
    {
        var materialId = Guid.NewGuid();
        var firstStore = new RecordingLedgerStore();
        var first = new InventoryService(firstStore);
        var transaction = await first.IncreaseAsync(materialId, null, null, null, 2m, 20m, Context("receive-authoritative"));

        var replayStore = new RecordingLedgerStore(firstStore.Snapshot, transaction);
        var restarted = new InventoryService(replayStore);
        var replay = await restarted.IncreaseAsync(materialId, null, null, null, 2m, 20m, Context("receive-authoritative"));

        Assert.Equal(transaction.Id, replay.Id);
        Assert.Equal(2m, restarted.GetBalance(materialId, null, null, null)!.Quantity);
    }

    private static InventoryOperationContext Context(string key) => new(key, operatorId: "operator");

    private sealed class RecordingLedgerStore : IInventoryLedgerStore
    {
        public List<InventoryLedgerAppend> Appends { get; } = [];
        public bool Replay { get; init; }
        public InventoryTransaction? ReplayedTransaction { get; private set; }
        private readonly List<InventoryTransaction> _transactions = [];
        private readonly List<InventoryBalance> _balances = [];

        public InventoryLedgerSnapshot Snapshot => new(_balances.ToArray(), _transactions.ToArray());

        public RecordingLedgerStore()
        {
        }

        public RecordingLedgerStore(InventoryLedgerSnapshot snapshot, InventoryTransaction replayedTransaction)
        {
            _balances.AddRange(snapshot.Balances);
            _transactions.AddRange(snapshot.Transactions);
            Replay = true;
            ReplayedTransaction = replayedTransaction;
        }

        public Task<InventoryLedgerSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new InventoryLedgerSnapshot(_balances.ToArray(), _transactions.ToArray()));

        public Task<InventoryLedgerAppendResult> AppendAsync(InventoryLedgerAppend append, CancellationToken cancellationToken = default)
        {
            Appends.Add(append);
            if (Replay)
            {
                ReplayedTransaction ??= append.Transaction;
                return Task.FromResult(new InventoryLedgerAppendResult(ReplayedTransaction, true));
            }

            _transactions.Add(append.Transaction);
            foreach (var change in append.BalanceChanges)
            {
                _balances.RemoveAll(x => x.Key == change.Balance.Key);
                _balances.Add(change.Balance);
            }

            return Task.FromResult(new InventoryLedgerAppendResult(append.Transaction, false));
        }
    }
}

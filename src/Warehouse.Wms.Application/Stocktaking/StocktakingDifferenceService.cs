using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;
using Warehouse.Wms.Application.Tasks;
using System.Text.Json;

namespace Warehouse.Wms.Application.Stocktaking;

public sealed record StocktakingAdjustmentOperatorView(
    Guid AdjustmentId,
    string AdjustmentNumber,
    decimal? BookQuantity,
    decimal? BookWeightKg,
    decimal ActualQuantity,
    decimal ActualWeightKg,
    StocktakingCountMode CountMode,
    StocktakingAdjustmentState State,
    bool IsFrozen);

/// <summary>
/// Coordinates stocktaking differences. Inventory is changed only through InventoryService;
/// this service owns approval, audit, idempotency and the physical putaway reservation view.
/// </summary>
public sealed class StocktakingDifferenceService
{
    public const string AdjustmentAuthorizationOperation = "Stocktaking.ApplyAdjustment";

    private readonly StocktakingService _stocktaking;
    private readonly InventoryService _inventory;
    private readonly ICurrentUser _currentUser;
    private readonly IRiskAuthorizationService _riskAuthorization;
    private readonly StocktakingFreezeStrategy _freezeStrategy;
    private readonly IBusinessWorkflowStore? _workflowStore;
    private readonly Dictionary<string, int> _workflowVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, StocktakingAdjustment> _adjustments = [];
    private readonly Dictionary<string, StocktakingAdjustment> _byTaskItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PutawayReservation> _reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public StocktakingDifferenceService(
        StocktakingService stocktaking,
        InventoryService inventory,
        ICurrentUser currentUser,
        IRiskAuthorizationService riskAuthorization,
        StocktakingFreezeStrategy freezeStrategy = StocktakingFreezeStrategy.FreezeOnDifference,
        IBusinessWorkflowStore? workflowStore = null)
    {
        _stocktaking = stocktaking ?? throw new ArgumentNullException(nameof(stocktaking));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _riskAuthorization = riskAuthorization ?? throw new ArgumentNullException(nameof(riskAuthorization));
        _freezeStrategy = freezeStrategy;
        _workflowStore = workflowStore;
    }

    public IReadOnlyCollection<StocktakingAdjustment> Adjustments
    {
        get { lock (_gate) return _adjustments.Values.ToArray(); }
    }

    public IReadOnlyCollection<PutawayReservation> PutawayReservations
    {
        get { lock (_gate) return _reservations.Values.ToArray(); }
    }

    public StocktakingAdjustment CreateDifference(
        string taskNumber,
        Guid itemId,
        StocktakingCountMode countMode,
        string differenceReason)
    {
        var task = _stocktaking.Get(taskNumber);
        var item = task.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException($"Stocktaking item '{itemId}' was not found.");
        if (item.ActualQuantity is null || item.ActualWeightKg is null)
            throw new InvalidOperationException("A counted stocktaking item is required before creating a difference.");
        if (item.State != StocktakingItemState.Difference)
            throw new InvalidOperationException("A stocktaking difference can only be created for an item with a counted difference.");

        var key = Key(task.TaskNumber, item.Id);
        lock (_gate)
        {
            if (_byTaskItem.TryGetValue(key, out var existing)) return existing;

            var adjustment = new StocktakingAdjustment(
                task.TaskNumber,
                item.Id,
                item.MaterialId,
                item.PalletId,
                item.LocationCode,
                item.BookQuantity,
                item.BookWeightKg,
                item.ActualQuantity.Value,
                item.ActualWeightKg.Value,
                countMode,
                Require(differenceReason, nameof(differenceReason)),
                _freezeStrategy == StocktakingFreezeStrategy.FreezeOnDifference);
            _adjustments.Add(adjustment.Id, adjustment);
            _byTaskItem.Add(key, adjustment);
            Persist(task.TaskNumber);
            return adjustment;
        }
    }

    public StocktakingAdjustment GetAdjustment(Guid adjustmentId)
    {
        lock (_gate)
        {
            return _adjustments.TryGetValue(adjustmentId, out var adjustment)
                ? adjustment
                : throw new KeyNotFoundException($"Stocktaking adjustment '{adjustmentId}' was not found.");
        }
    }

    public StocktakingAdjustmentOperatorView GetOperatorView(Guid adjustmentId)
    {
        var adjustment = GetAdjustment(adjustmentId);
        decimal? visibleBookQuantity = adjustment.CountMode == StocktakingCountMode.Blind ? null : adjustment.BookQuantity;
        decimal? visibleBookWeight = adjustment.CountMode == StocktakingCountMode.Blind ? null : adjustment.BookWeightKg;
        return new StocktakingAdjustmentOperatorView(
            adjustment.Id,
            adjustment.AdjustmentNumber,
            visibleBookQuantity,
            visibleBookWeight,
            adjustment.FinalQuantity,
            adjustment.FinalWeightKg,
            adjustment.CountMode,
            adjustment.State,
            adjustment.IsFrozen);
    }

    public StocktakingAdjustment RequestRecount(Guid adjustmentId, string reason)
    {
        var adjustment = GetAdjustment(adjustmentId);
        adjustment.RequestRecount(CurrentUserId(), Require(reason, nameof(reason)));
        Persist(adjustment.TaskNumber);
        return adjustment;
    }

    public StocktakingAdjustment RecordRecount(Guid adjustmentId, decimal quantity, decimal weightKg, string reason)
    {
        var adjustment = GetAdjustment(adjustmentId);
        adjustment.RecordRecount(quantity, weightKg, CurrentUserId(), Require(reason, nameof(reason)));
        Persist(adjustment.TaskNumber);
        return adjustment;
    }

    public async Task<StocktakingAdjustment> ApproveAndApplyAsync(
        Guid adjustmentId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var adjustment = GetAdjustment(adjustmentId);
        var normalizedReason = Require(reason, nameof(reason));

        if (adjustment.State == StocktakingAdjustmentState.Applied
            || adjustment.State == StocktakingAdjustmentState.Rejected)
        {
            return adjustment;
        }

        if (adjustment.State == StocktakingAdjustmentState.RecountRequired)
            throw new InvalidOperationException("A recount must be recorded before approval.");

        if (adjustment.State == StocktakingAdjustmentState.PendingReview)
        {
            var authorized = await _riskAuthorization.AuthorizeAsync(
                AdjustmentAuthorizationOperation,
                _currentUser,
                adjustment.TaskNumber,
                normalizedReason,
                cancellationToken);
            if (!authorized)
            {
                adjustment.AddAudit("AuthorizationRejected", CurrentUserId(), normalizedReason);
                throw new UnauthorizedAccessException($"Second authorization was denied for task '{adjustment.TaskNumber}'.");
            }

            adjustment.Approve(CurrentUserId(), normalizedReason);
            if (adjustment.State == StocktakingAdjustmentState.Rejected) return adjustment;
        }

        if (adjustment.State != StocktakingAdjustmentState.Approved)
            throw new InvalidOperationException($"Adjustment '{adjustment.AdjustmentNumber}' is not ready for application.");

        // This is the only inventory mutation in the difference workflow. The idempotency
        // scope is the adjustment id, so retries cannot create a second ledger entry.
        var transaction = await _inventory.AdjustAsync(
            adjustment.MaterialId,
            adjustment.PalletId,
            null,
            null,
            adjustment.QuantityDelta,
            adjustment.WeightDelta,
            new InventoryOperationContext(
                $"stocktaking-adjustment:{adjustment.Id:N}",
                adjustment.AdjustmentNumber,
                adjustment.TaskNumber,
                CurrentUserId(),
                normalizedReason),
            cancellationToken);
            adjustment.MarkApplied(transaction.Id, CurrentUserId(), normalizedReason);
            Persist(adjustment.TaskNumber);
            return adjustment;
    }

    public PutawayReservation ReservePutaway(
        string taskNumber,
        Guid itemId,
        string loadingPointCode,
        string deviceId)
    {
        var task = _stocktaking.Get(taskNumber);
        if (!task.Items.Any(item => item.Id == itemId))
            throw new KeyNotFoundException($"Stocktaking item '{itemId}' was not found.");

        var key = Key(task.TaskNumber, itemId);
        lock (_gate)
        {
            if (_reservations.TryGetValue(key, out var existing)) return existing;
            var reservation = new PutawayReservation(task.TaskNumber, itemId, loadingPointCode, deviceId);
            _reservations.Add(key, reservation);
            Persist(task.TaskNumber);
            return reservation;
        }
    }

    public PutawayReservation MarkPutawaySent(Guid reservationId, string deviceTaskNumber)
    {
        var reservation = FindReservation(reservationId);
        reservation.MarkSent(deviceTaskNumber);
        Persist(reservation.TaskNumber);
        return reservation;
    }

    public PutawayReservation MarkPutawayCompleted(Guid reservationId)
    {
        var reservation = FindReservation(reservationId);
        reservation.MarkCompleted();
        Persist(reservation.TaskNumber);
        return reservation;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null) return;
        var snapshots = await _workflowStore.GetByTypeAsync("StocktakingDifference", cancellationToken);
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DifferenceWorkflowState? state;
            try { state = JsonSerializer.Deserialize<DifferenceWorkflowState>(snapshot.SnapshotJson); }
            catch (JsonException) { continue; }
            if (state is null) continue;
            foreach (var item in state.Adjustments)
            {
                if (_adjustments.ContainsKey(item.Id)) continue;
                var adjustment = new StocktakingAdjustment(item.TaskNumber, item.ItemId, item.MaterialId, item.PalletId, item.LocationCode, item.BookQuantity, item.BookWeightKg, item.InitialQuantity, item.InitialWeightKg, item.CountMode, item.DifferenceReason, item.IsFrozen, item.Id);
                adjustment.RestoreState(item.State, item.FinalQuantity, item.FinalWeightKg, item.AppliedTransactionId);
                _adjustments[item.Id] = adjustment;
                _byTaskItem[Key(item.TaskNumber, item.ItemId)] = adjustment;
            }
            foreach (var item in state.Reservations)
            {
                if (_reservations.ContainsKey(Key(item.TaskNumber, item.ItemId))) continue;
                var reservation = new PutawayReservation(item.TaskNumber, item.ItemId, item.LoadingPointCode, item.DeviceId, item.Id);
                reservation.RestoreState(item.State, item.DeviceTaskNumber);
                _reservations[Key(item.TaskNumber, item.ItemId)] = reservation;
            }
            _workflowVersions[snapshot.AggregateKey] = snapshot.Version;
        }
    }

    private void Persist(string taskNumber)
    {
        if (_workflowStore is null) return;
        var key = taskNumber.Trim();
        var version = _workflowVersions.TryGetValue(key, out var current) ? current : 0;
        StocktakingAdjustment[] adjustments;
        PutawayReservation[] reservations;
        lock (_gate)
        {
            adjustments = _adjustments.Values.Where(x => x.TaskNumber.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            reservations = _reservations.Values.Where(x => x.TaskNumber.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        var state = new DifferenceWorkflowState(
            adjustments.Select(x => new AdjustmentState(x.Id, x.TaskNumber, x.ItemId, x.MaterialId, x.PalletId, x.LocationCode, x.BookQuantity, x.BookWeightKg, x.InitialQuantity, x.InitialWeightKg, x.FinalQuantity, x.FinalWeightKg, x.CountMode, x.DifferenceReason, x.State, x.IsFrozen, x.AppliedTransactionId)).ToArray(),
            reservations.Select(x => new ReservationState(x.Id, x.TaskNumber, x.ItemId, x.LoadingPointCode, x.DeviceId, x.DeviceTaskNumber, x.State)).ToArray());
        var json = JsonSerializer.Serialize(state);
        _workflowStore.SaveAsync(new BusinessWorkflowSnapshot("StocktakingDifference", key, version + 1, "Active", json, DateTimeOffset.UtcNow, key), version, "盘点差异状态保存", "system").GetAwaiter().GetResult();
        _workflowVersions[key] = version + 1;
    }

    private sealed record DifferenceWorkflowState(IReadOnlyList<AdjustmentState> Adjustments, IReadOnlyList<ReservationState> Reservations);
    private sealed record AdjustmentState(Guid Id, string TaskNumber, Guid ItemId, Guid MaterialId, Guid PalletId, string LocationCode, decimal BookQuantity, decimal BookWeightKg, decimal InitialQuantity, decimal InitialWeightKg, decimal FinalQuantity, decimal FinalWeightKg, StocktakingCountMode CountMode, string DifferenceReason, StocktakingAdjustmentState State, bool IsFrozen, Guid? AppliedTransactionId);
    private sealed record ReservationState(Guid Id, string TaskNumber, Guid ItemId, string LoadingPointCode, string DeviceId, string? DeviceTaskNumber, PutawayReservationState State);

    private PutawayReservation FindReservation(Guid reservationId)
    {
        lock (_gate)
        {
            return _reservations.Values.FirstOrDefault(candidate => candidate.Id == reservationId)
                ?? throw new KeyNotFoundException($"Putaway reservation '{reservationId}' was not found.");
        }
    }

    private string CurrentUserId() => Require(_currentUser.UserId, nameof(_currentUser.UserId));

    private static string Key(string taskNumber, Guid itemId) => $"{taskNumber.Trim()}|{itemId:D}";

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;

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
    private readonly Dictionary<Guid, StocktakingAdjustment> _adjustments = [];
    private readonly Dictionary<string, StocktakingAdjustment> _byTaskItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PutawayReservation> _reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public StocktakingDifferenceService(
        StocktakingService stocktaking,
        InventoryService inventory,
        ICurrentUser currentUser,
        IRiskAuthorizationService riskAuthorization,
        StocktakingFreezeStrategy freezeStrategy = StocktakingFreezeStrategy.FreezeOnDifference)
    {
        _stocktaking = stocktaking ?? throw new ArgumentNullException(nameof(stocktaking));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _riskAuthorization = riskAuthorization ?? throw new ArgumentNullException(nameof(riskAuthorization));
        _freezeStrategy = freezeStrategy;
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
        return adjustment;
    }

    public StocktakingAdjustment RecordRecount(Guid adjustmentId, decimal quantity, decimal weightKg, string reason)
    {
        var adjustment = GetAdjustment(adjustmentId);
        adjustment.RecordRecount(quantity, weightKg, CurrentUserId(), Require(reason, nameof(reason)));
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
            return reservation;
        }
    }

    public PutawayReservation MarkPutawaySent(Guid reservationId, string deviceTaskNumber)
    {
        var reservation = FindReservation(reservationId);
        reservation.MarkSent(deviceTaskNumber);
        return reservation;
    }

    public PutawayReservation MarkPutawayCompleted(Guid reservationId)
    {
        var reservation = FindReservation(reservationId);
        reservation.MarkCompleted();
        return reservation;
    }

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

namespace Warehouse.Wms.Domain.Stocktaking;

public enum StocktakingCountMode
{
    Open,
    Blind
}

public enum StocktakingAdjustmentState
{
    PendingReview,
    RecountRequired,
    Approved,
    Rejected,
    Applied
}

public enum StocktakingFreezeStrategy
{
    None,
    FreezeOnDifference
}

public enum PutawayReservationState
{
    Reserved,
    Sent,
    Completed,
    Failed,
    Unknown
}

public sealed record StocktakingAdjustmentAudit(
    string Action,
    string OperatorId,
    string Reason,
    StocktakingAdjustmentState State,
    DateTimeOffset OccurredAt);

public sealed class StocktakingAdjustment
{
    public StocktakingAdjustment(
        string taskNumber,
        Guid itemId,
        Guid materialId,
        Guid palletId,
        string locationCode,
        decimal bookQuantity,
        decimal bookWeightKg,
        decimal actualQuantity,
        decimal actualWeightKg,
        StocktakingCountMode countMode,
        string reason,
        bool frozen,
        Guid? id = null)
    {
        TaskNumber = Require(taskNumber, nameof(taskNumber));
        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("An adjustment id is required.", nameof(id));
        ItemId = itemId;
        MaterialId = materialId;
        PalletId = palletId;
        LocationCode = Require(locationCode, nameof(locationCode));
        if (bookQuantity < 0m || bookWeightKg < 0m || actualQuantity < 0m || actualWeightKg < 0m)
            throw new ArgumentOutOfRangeException(nameof(actualQuantity), "Stocktaking values cannot be negative.");

        BookQuantity = bookQuantity;
        BookWeightKg = bookWeightKg;
        InitialQuantity = actualQuantity;
        InitialWeightKg = actualWeightKg;
        FinalQuantity = actualQuantity;
        FinalWeightKg = actualWeightKg;
        CountMode = countMode;
        DifferenceReason = Require(reason, nameof(reason));
        State = StocktakingAdjustmentState.PendingReview;
        IsFrozen = frozen;
        AddAudit("Created", "system", reason);
    }

    public Guid Id { get; }
    public string AdjustmentNumber => $"ADJ-{Id:N}";
    public string TaskNumber { get; }
    public Guid ItemId { get; }
    public Guid MaterialId { get; }
    public Guid PalletId { get; }
    public string LocationCode { get; }
    public decimal BookQuantity { get; }
    public decimal BookWeightKg { get; }
    public decimal InitialQuantity { get; }
    public decimal InitialWeightKg { get; }
    public decimal FinalQuantity { get; private set; }
    public decimal FinalWeightKg { get; private set; }
    public decimal QuantityDelta => FinalQuantity - BookQuantity;
    public decimal WeightDelta => FinalWeightKg - BookWeightKg;
    public StocktakingCountMode CountMode { get; }
    public string DifferenceReason { get; private set; }
    public StocktakingAdjustmentState State { get; private set; }
    public bool IsFrozen { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public Guid? AppliedTransactionId { get; private set; }
    public IReadOnlyList<StocktakingAdjustmentAudit> AuditTrail => _auditTrail;

    private readonly List<StocktakingAdjustmentAudit> _auditTrail = [];

    public void RequestRecount(string operatorId, string reason)
    {
        EnsureNotTerminal();
        State = StocktakingAdjustmentState.RecountRequired;
        IsFrozen = true;
        AddAudit("RecountRequested", operatorId, reason);
    }

    public void RecordRecount(decimal quantity, decimal weightKg, string operatorId, string reason)
    {
        EnsureState(StocktakingAdjustmentState.RecountRequired);
        EnsureNonNegative(quantity, nameof(quantity));
        EnsureNonNegative(weightKg, nameof(weightKg));
        FinalQuantity = quantity;
        FinalWeightKg = weightKg;
        DifferenceReason = Require(reason, nameof(reason));
        State = QuantityDelta == 0m && WeightDelta == 0m
            ? StocktakingAdjustmentState.Rejected
            : StocktakingAdjustmentState.PendingReview;
        IsFrozen = State == StocktakingAdjustmentState.PendingReview;
        AddAudit("RecountRecorded", operatorId, reason);
    }

    public void Approve(string operatorId, string reason)
    {
        EnsureState(StocktakingAdjustmentState.PendingReview);
        if (QuantityDelta == 0m && WeightDelta == 0m)
        {
            State = StocktakingAdjustmentState.Rejected;
            IsFrozen = false;
            AddAudit("Rejected", operatorId, reason);
            return;
        }

        State = StocktakingAdjustmentState.Approved;
        ApprovedBy = Require(operatorId, nameof(operatorId));
        ApprovedAt = DateTimeOffset.UtcNow;
        AddAudit("Approved", operatorId, reason);
    }

    public void MarkApplied(Guid transactionId, string operatorId, string reason)
    {
        if (transactionId == Guid.Empty) throw new ArgumentException("A transaction id is required.", nameof(transactionId));
        if (State == StocktakingAdjustmentState.Applied)
        {
            if (AppliedTransactionId != transactionId)
                throw new InvalidOperationException($"Adjustment '{AdjustmentNumber}' is already applied by another transaction.");
            return;
        }

        EnsureState(StocktakingAdjustmentState.Approved);
        AppliedTransactionId = transactionId;
        State = StocktakingAdjustmentState.Applied;
        IsFrozen = false;
        AddAudit("Applied", operatorId, reason);
    }

    public void AddAudit(string action, string operatorId, string reason)
        => _auditTrail.Add(new StocktakingAdjustmentAudit(
            Require(action, nameof(action)),
            Require(operatorId, nameof(operatorId)),
            Require(reason, nameof(reason)),
            State,
            DateTimeOffset.UtcNow));

    public void RestoreState(StocktakingAdjustmentState target, decimal finalQuantity, decimal finalWeightKg, Guid? appliedTransactionId)
    {
        if (target == StocktakingAdjustmentState.RecountRequired)
        {
            RequestRecount("recovery", "从盘点差异快照恢复");
            return;
        }
        if (target == StocktakingAdjustmentState.Approved || target == StocktakingAdjustmentState.Applied || target == StocktakingAdjustmentState.Rejected)
        {
            if (finalQuantity != InitialQuantity || finalWeightKg != InitialWeightKg)
            {
                RequestRecount("recovery", "从盘点差异快照恢复");
                RecordRecount(finalQuantity, finalWeightKg, "recovery", "从盘点差异快照恢复");
            }
            if (target == StocktakingAdjustmentState.Rejected)
            {
                if (State == StocktakingAdjustmentState.PendingReview) Approve("recovery", "从盘点差异快照恢复");
                return;
            }
            if (State == StocktakingAdjustmentState.PendingReview) Approve("recovery", "从盘点差异快照恢复");
            if (target == StocktakingAdjustmentState.Applied && appliedTransactionId is Guid transactionId)
                MarkApplied(transactionId, "recovery", "从盘点差异快照恢复");
        }
    }

    private void EnsureNotTerminal()
    {
        if (State is StocktakingAdjustmentState.Applied or StocktakingAdjustmentState.Rejected)
            throw new InvalidOperationException($"Adjustment '{AdjustmentNumber}' is already terminal.");
    }

    private void EnsureState(StocktakingAdjustmentState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Adjustment '{AdjustmentNumber}' must be in '{expected}' state, but is '{State}'.");
    }

    private static void EnsureNonNegative(decimal value, string parameterName)
    {
        if (value < 0m) throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative.");
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

public sealed class PutawayReservation
{
    public PutawayReservation(string taskNumber, Guid itemId, string loadingPointCode, string deviceId, Guid? id = null)
    {
        TaskNumber = string.IsNullOrWhiteSpace(taskNumber) ? throw new ArgumentException("A task number is required.", nameof(taskNumber)) : taskNumber.Trim();
        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("A reservation id is required.", nameof(id));
        ItemId = itemId;
        LoadingPointCode = string.IsNullOrWhiteSpace(loadingPointCode) ? throw new ArgumentException("A loading point is required.", nameof(loadingPointCode)) : loadingPointCode.Trim();
        DeviceId = string.IsNullOrWhiteSpace(deviceId) ? throw new ArgumentException("A device is required.", nameof(deviceId)) : deviceId.Trim();
    }

    public Guid Id { get; }
    public string TaskNumber { get; }
    public Guid ItemId { get; }
    public string LoadingPointCode { get; }
    public string DeviceId { get; }
    public string? DeviceTaskNumber { get; private set; }
    public PutawayReservationState State { get; private set; } = PutawayReservationState.Reserved;

    public void MarkSent(string deviceTaskNumber)
    {
        if (State == PutawayReservationState.Completed) return;
        if (State != PutawayReservationState.Reserved && State != PutawayReservationState.Sent)
            throw new InvalidOperationException($"Reservation '{Id}' cannot be sent from '{State}'.");
        DeviceTaskNumber = string.IsNullOrWhiteSpace(deviceTaskNumber) ? throw new ArgumentException("A device task number is required.", nameof(deviceTaskNumber)) : deviceTaskNumber.Trim();
        State = PutawayReservationState.Sent;
    }

    public void MarkCompleted()
    {
        if (State == PutawayReservationState.Completed) return;
        if (State != PutawayReservationState.Sent)
            throw new InvalidOperationException($"Reservation '{Id}' cannot be completed from '{State}'.");
        State = PutawayReservationState.Completed;
    }

    public void RestoreState(PutawayReservationState state, string? deviceTaskNumber)
    {
        if (state is PutawayReservationState.Sent or PutawayReservationState.Completed)
            MarkSent(deviceTaskNumber ?? $"recovery:{Id:N}");
        if (state == PutawayReservationState.Completed) MarkCompleted();
    }
}

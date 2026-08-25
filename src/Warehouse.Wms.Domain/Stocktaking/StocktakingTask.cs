namespace Warehouse.Wms.Domain.Stocktaking;

public enum StocktakingState
{
    Draft,
    Pending,
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Canceled,
    Exception
}

public enum StocktakingItemState
{
    Pending,
    Counting,
    Counted,
    Difference,
    Error
}

public sealed class StocktakingItem
{
    public StocktakingItem(
        string locationCode,
        Guid materialId,
        Guid palletId,
        decimal bookQuantity,
        decimal bookWeightKg)
    {
        LocationCode = Require(locationCode, nameof(locationCode));
        MaterialId = materialId;
        PalletId = palletId;
        BookQuantity = bookQuantity;
        BookWeightKg = bookWeightKg;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string LocationCode { get; }
    public Guid MaterialId { get; }
    public Guid PalletId { get; }
    public decimal BookQuantity { get; }
    public decimal BookWeightKg { get; }
    public decimal? ActualQuantity { get; private set; }
    public decimal? ActualWeightKg { get; private set; }
    public string? LoadingPointCode { get; private set; }
    public StocktakingItemState State { get; private set; } = StocktakingItemState.Pending;

    public void RecordCount(decimal quantity, decimal weightKg, string? loadingPointCode = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(weightKg);
        ActualQuantity = quantity;
        ActualWeightKg = weightKg;
        LoadingPointCode = string.IsNullOrWhiteSpace(loadingPointCode) ? null : loadingPointCode.Trim();
        State = quantity == BookQuantity && weightKg == BookWeightKg
            ? StocktakingItemState.Counted
            : StocktakingItemState.Difference;
    }

    public void BeginCounting()
    {
        if (State != StocktakingItemState.Pending)
            throw new InvalidOperationException($"Stocktaking item '{Id}' is not pending.");
        State = StocktakingItemState.Counting;
    }

    private static string Require(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

public sealed class StocktakingTask
{
    public StocktakingTask(string taskNumber, IEnumerable<StocktakingItem> items)
    {
        if (string.IsNullOrWhiteSpace(taskNumber)) throw new ArgumentException("A task number is required.", nameof(taskNumber));
        TaskNumber = taskNumber.Trim();
        Items = items?.ToArray() ?? throw new ArgumentNullException(nameof(items));
        if (Items.Count == 0) throw new InvalidOperationException("A stocktaking task must contain at least one item.");
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string TaskNumber { get; }
    public StocktakingState State { get; private set; } = StocktakingState.Draft;
    public IReadOnlyList<StocktakingItem> Items { get; }

    public void TransitionTo(StocktakingState next)
    {
        var allowed = State switch
        {
            StocktakingState.Draft => next == StocktakingState.Pending || next == StocktakingState.Canceled,
            StocktakingState.Pending => next == StocktakingState.Running || next == StocktakingState.Canceled || next == StocktakingState.Exception,
            StocktakingState.Running => next is StocktakingState.Completed or StocktakingState.CompletedWithErrors or StocktakingState.Failed or StocktakingState.Canceled or StocktakingState.Exception,
            StocktakingState.Exception => next is StocktakingState.Pending or StocktakingState.Canceled,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Stocktaking state transition '{State}' -> '{next}' is not allowed.");
        State = next;
    }
}

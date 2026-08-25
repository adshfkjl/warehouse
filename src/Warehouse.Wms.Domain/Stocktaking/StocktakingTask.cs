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
        decimal bookWeightKg,
        Guid? id = null)
    {
        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("A stocktaking item id is required.", nameof(id));
        LocationCode = Require(locationCode, nameof(locationCode));
        MaterialId = materialId;
        PalletId = palletId;
        BookQuantity = bookQuantity;
        BookWeightKg = bookWeightKg;
    }

    public Guid Id { get; }
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

    public void RestoreCount(decimal? quantity, decimal? weightKg, string? loadingPointCode, StocktakingItemState state)
    {
        if (quantity is not null || weightKg is not null)
        {
            if (quantity is null || weightKg is null) throw new ArgumentException("Count quantity and weight must be provided together.");
            RecordCount(quantity.Value, weightKg.Value, loadingPointCode);
        }
        if (state == StocktakingItemState.Counting && State == StocktakingItemState.Pending) BeginCounting();
        else if (state is StocktakingItemState.Pending or StocktakingItemState.Counting or StocktakingItemState.Counted or StocktakingItemState.Difference)
        {
            if (State != state && state is StocktakingItemState.Counted or StocktakingItemState.Difference)
                throw new InvalidOperationException($"Count state '{state}' requires a count snapshot.");
        }
        else if (state == StocktakingItemState.Error) State = StocktakingItemState.Error;
    }

    private static string Require(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

public sealed class StocktakingTask
{
    public StocktakingTask(string taskNumber, IEnumerable<StocktakingItem> items, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(taskNumber)) throw new ArgumentException("A task number is required.", nameof(taskNumber));
        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("A stocktaking task id is required.", nameof(id));
        TaskNumber = taskNumber.Trim();
        Items = items?.ToArray() ?? throw new ArgumentNullException(nameof(items));
        if (Items.Count == 0) throw new InvalidOperationException("A stocktaking task must contain at least one item.");
    }

    public Guid Id { get; }
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

    public void RestoreState(StocktakingState target)
    {
        while (State != target)
        {
            var next = State switch
            {
                StocktakingState.Draft when target == StocktakingState.Canceled => StocktakingState.Canceled,
                StocktakingState.Draft => StocktakingState.Pending,
                StocktakingState.Pending when target == StocktakingState.Canceled => StocktakingState.Canceled,
                StocktakingState.Pending when target == StocktakingState.Exception => StocktakingState.Exception,
                StocktakingState.Pending => StocktakingState.Running,
                StocktakingState.Running when target == StocktakingState.CompletedWithErrors => StocktakingState.CompletedWithErrors,
                StocktakingState.Running when target == StocktakingState.Completed => StocktakingState.Completed,
                StocktakingState.Running when target == StocktakingState.Failed => StocktakingState.Failed,
                StocktakingState.Running when target == StocktakingState.Canceled => StocktakingState.Canceled,
                StocktakingState.Running => StocktakingState.Exception,
                StocktakingState.Exception when target == StocktakingState.Canceled => StocktakingState.Canceled,
                StocktakingState.Exception => StocktakingState.Pending,
                _ => throw new InvalidOperationException($"Stocktaking state '{State}' cannot be restored to '{target}'.")
            };
            TransitionTo(next);
        }
    }
}

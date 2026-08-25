using System.Text.RegularExpressions;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.Application.Stocktaking;

public sealed record StocktakingInventoryItem(
    string LocationCode,
    string ZoneCode,
    string MaterialCode,
    string? BatchNumber,
    string PalletCode,
    Guid MaterialId,
    Guid PalletId,
    decimal Quantity,
    decimal WeightKg,
    InventoryStatus Status);

public sealed record StocktakingRequest(
    string TaskNumber,
    string? ZoneCode = null,
    string? MaterialCode = null,
    string? BatchNumber = null,
    string? PalletCode = null,
    string? LocationRangeStart = null,
    string? LocationRangeEnd = null);

public sealed record StocktakingDeviceTaskResult(
    Guid ItemId,
    WarehouseTask Task,
    DeviceTask DeviceTask,
    string LoadingPointCode);

public sealed class StocktakingService
{
    private static readonly Regex ShelfRangePattern = new("^(?<prefix>[A-Za-z]+)(?<number>\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocationPattern = new("^(?<prefix>[A-Za-z]+)(?<shelf>\\d+)-(?<slot>\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly IReadOnlyList<StocktakingInventoryItem> _inventory;
    private readonly WmsTaskScheduler? _scheduler;
    private readonly Dictionary<string, StocktakingTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, StocktakingDeviceTaskResult> _deviceTasks = [];
    private readonly HashSet<string> _activeLoadingPoints = new(StringComparer.OrdinalIgnoreCase);

    public StocktakingService(IEnumerable<StocktakingInventoryItem> inventory, WmsTaskScheduler? scheduler = null)
    {
        _inventory = inventory?.ToArray() ?? throw new ArgumentNullException(nameof(inventory));
        _scheduler = scheduler;
    }

    public IReadOnlyCollection<StocktakingTask> Tasks
    {
        get { lock (_gate) return _tasks.Values.ToArray(); }
    }

    public StocktakingTask Create(StocktakingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var taskNumber = Require(request.TaskNumber, nameof(request.TaskNumber));
        lock (_gate)
        {
            if (_tasks.ContainsKey(taskNumber)) throw new InvalidOperationException($"Stocktaking task '{taskNumber}' already exists.");
        }

        var selected = _inventory
            .Where(item => item.Status is InventoryStatus.Available or InventoryStatus.Locked)
            .Where(item => Match(item, request))
            .OrderBy(item => item.LocationCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PalletCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("The stocktaking range contains no inventory.");
        var task = new StocktakingTask(taskNumber, selected.Select(item => new StocktakingItem(
            item.LocationCode, item.MaterialId, item.PalletId, item.Quantity, item.WeightKg)));
        lock (_gate) _tasks.Add(taskNumber, task);
        return task;
    }

    public StocktakingTask Start(string taskNumber)
    {
        var task = Get(taskNumber);
        task.TransitionTo(StocktakingState.Pending);
        task.TransitionTo(StocktakingState.Running);
        return task;
    }

    public StocktakingTask RecordCount(string taskNumber, Guid itemId, decimal quantity, decimal weightKg, string? loadingPointCode = null)
    {
        var task = Get(taskNumber);
        if (task.State != StocktakingState.Running)
            throw new InvalidOperationException("Stocktaking task is not running.");
        var item = task.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException($"Stocktaking item '{itemId}' was not found.");
        item.RecordCount(quantity, weightKg, loadingPointCode);
        return task;
    }

    public async Task<StocktakingDeviceTaskResult> QueueDeviceTaskAsync(
        string taskNumber,
        Guid itemId,
        string deviceId,
        string loadingPointCode,
        CancellationToken cancellationToken = default)
    {
        var scheduler = _scheduler ?? throw new InvalidOperationException("A task scheduler is required for device-assisted stocktaking.");
        var task = Get(taskNumber);
        if (task.State != StocktakingState.Running) throw new InvalidOperationException("Stocktaking task is not running.");
        var item = task.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException($"Stocktaking item '{itemId}' was not found.");
        var loadingPoint = Require(loadingPointCode, nameof(loadingPointCode));
        lock (_gate)
        {
            if (_deviceTasks.TryGetValue(itemId, out var existing)) return existing;
            if (!_activeLoadingPoints.Add(loadingPoint))
                throw new InvalidOperationException($"Loading point '{loadingPoint}' is occupied by another stocktaking item.");
        }
        try
        {
            item.BeginCounting();
            var warehouseTask = new WarehouseTask($"{task.TaskNumber}-ITEM-{itemId:N}", "StocktakingOutbound");
            var deviceTask = new DeviceTask(
                $"stocktaking:{task.TaskNumber}:{itemId:D}", warehouseTask.TaskNumber, Require(deviceId, nameof(deviceId)),
                item.LocationCode, null, loadingPoint, "v1");
            var dispatch = new TaskDispatchRequest(warehouseTask, deviceTask, DeviceOperationKind.Outbound, 0, 1);
            await scheduler.EnqueueAsync(dispatch, cancellationToken);
            var result = new StocktakingDeviceTaskResult(itemId, warehouseTask, deviceTask, loadingPoint);
            lock (_gate) _deviceTasks.Add(itemId, result);
            return result;
        }
        catch
        {
            lock (_gate) _activeLoadingPoints.Remove(loadingPoint);
            throw;
        }
    }

    public StocktakingTask Complete(string taskNumber)
    {
        var task = Get(taskNumber);
        if (task.State != StocktakingState.Running)
            throw new InvalidOperationException("Stocktaking task is not running.");
        if (task.Items.Any(item => item.State == StocktakingItemState.Pending || item.State == StocktakingItemState.Counting))
            throw new InvalidOperationException("Every stocktaking item must be counted before completion.");
        task.TransitionTo(task.Items.Any(item => item.State == StocktakingItemState.Difference)
            ? StocktakingState.CompletedWithErrors
            : StocktakingState.Completed);
        return task;
    }

    public StocktakingTask Get(string taskNumber)
    {
        var normalized = Require(taskNumber, nameof(taskNumber));
        lock (_gate)
        {
            return _tasks.TryGetValue(normalized, out var task)
                ? task
                : throw new KeyNotFoundException($"Stocktaking task '{normalized}' was not found.");
        }
    }

    private static bool Match(StocktakingInventoryItem item, StocktakingRequest request)
        => (request.ZoneCode is null || string.Equals(item.ZoneCode, request.ZoneCode.Trim(), StringComparison.OrdinalIgnoreCase))
           && (request.MaterialCode is null || string.Equals(item.MaterialCode, request.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase))
           && (request.BatchNumber is null || string.Equals(item.BatchNumber, request.BatchNumber.Trim(), StringComparison.OrdinalIgnoreCase))
           && (request.PalletCode is null || string.Equals(item.PalletCode, request.PalletCode.Trim(), StringComparison.OrdinalIgnoreCase))
           && InLocationRange(item.LocationCode, request.LocationRangeStart, request.LocationRangeEnd);

    private static bool InLocationRange(string locationCode, string? start, string? end)
    {
        if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end)) return true;
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end)) throw new ArgumentException("Both location range endpoints are required.");
        var normalizedStart = start.Trim();
        var normalizedEnd = end.Trim();
        var shelfStart = ShelfRangePattern.Match(normalizedStart);
        var shelfEnd = ShelfRangePattern.Match(normalizedEnd);
        if (shelfStart.Success && shelfEnd.Success && string.Equals(shelfStart.Groups["prefix"].Value, shelfEnd.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase))
        {
            var parsedLocation = ParseLocation(locationCode);
            if (parsedLocation is null) return false;
            var min = int.Parse(shelfStart.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var max = int.Parse(shelfEnd.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (min > max) (min, max) = (max, min);
            return string.Equals(parsedLocation.Value.Prefix, shelfStart.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase)
                && parsedLocation.Value.Shelf >= min && parsedLocation.Value.Shelf <= max;
        }

        var parsedLocation2 = ParseLocation(locationCode);
        var parsedStart = ParseLocation(normalizedStart);
        var parsedEnd = ParseLocation(normalizedEnd);
        if (parsedLocation2 is null || parsedStart is null || parsedEnd is null) return false;
        var lower = Compare(parsedStart.Value, parsedEnd.Value) <= 0 ? parsedStart.Value : parsedEnd.Value;
        var upper = Compare(parsedStart.Value, parsedEnd.Value) <= 0 ? parsedEnd.Value : parsedStart.Value;
        return Compare(parsedLocation2.Value, lower) >= 0 && Compare(parsedLocation2.Value, upper) <= 0;
    }

    private static (string Prefix, int Number)? ParseShelf(string value)
    {
        var match = ShelfRangePattern.Match(value);
        return match.Success
            ? (match.Groups["prefix"].Value, int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private static (string Prefix, int Shelf, int Slot)? ParseLocation(string value)
    {
        var match = LocationPattern.Match(value);
        return match.Success
            ? (match.Groups["prefix"].Value, int.Parse(match.Groups["shelf"].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private static int Compare((string Prefix, int Shelf, int Slot) left, (string Prefix, int Shelf, int Slot) right)
    {
        var prefix = string.Compare(left.Prefix, right.Prefix, StringComparison.OrdinalIgnoreCase);
        return prefix != 0 ? prefix : left.Shelf != right.Shelf ? left.Shelf.CompareTo(right.Shelf) : left.Slot.CompareTo(right.Slot);
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value.Trim();
}

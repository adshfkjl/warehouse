using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Application.Tasks;

namespace Warehouse.Wms.Application.Outbound;

public sealed record OutboundAllocationRequest(string IdempotencyKey, string OrderNumber, Guid LineId, decimal Quantity, string? BatchNumber = null, Guid? PalletId = null, Guid? LocationId = null);
public sealed record OutboundAllocation(OutboundOrder Order, OutboundLine Line, InventoryBalance Source, decimal Quantity, IReadOnlyList<ResourceLock> ResourceLocks)
{
    public string? IdempotencyKey { get; init; }
}

public sealed class OutboundAllocationService
{
    private readonly object _gate = new();
    private readonly InventoryService _inventory;
    private readonly IResourceLockStore? _resourceLockStore;
    private readonly Dictionary<string, OutboundOrder> _orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboundAllocation> _allocations = new(StringComparer.Ordinal);
    private readonly List<ResourceLock> _locks = [];

    public OutboundAllocationService(InventoryService inventory, IResourceLockStore? resourceLockStore = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _resourceLockStore = resourceLockStore;
    }
    public IReadOnlyCollection<OutboundOrder> Orders { get { lock (_gate) return _orders.Values.ToArray(); } }

    /// <summary>Rehydrates an allocation from a durable task snapshot without reacquiring locks.</summary>
    public OutboundAllocation Restore(
        string orderNumber,
        Guid lineId,
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal requestedQuantity,
        decimal quantity,
        IReadOnlyList<ResourceLock> resourceLocks,
        string? allocationIdempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(resourceLocks);
        lock (_gate)
        {
            var normalized = orderNumber.Trim();
            if (!_orders.TryGetValue(normalized, out var order))
            {
                order = new OutboundOrder(normalized);
                _orders.Add(normalized, order);
            }
            var line = order.Lines.FirstOrDefault(item => item.Id == lineId);
            if (line is null)
            {
                line = new OutboundLine(materialId, requestedQuantity, batchNumber, palletId, locationId);
                order.AddLine(line);
            }
            var source = _inventory.GetBalance(materialId, palletId, locationId, batchNumber)
                ?? throw new InvalidOperationException($"Inventory source for restored outbound task '{normalized}' was not found.");
            var allocation = new OutboundAllocation(order, line, source, quantity, resourceLocks) { IdempotencyKey = allocationIdempotencyKey };
            var key = $"recovered:{normalized}:{line.Id:D}:{quantity:G29}";
            _allocations[key] = allocation;
            if (!string.IsNullOrWhiteSpace(allocationIdempotencyKey))
                _allocations[allocationIdempotencyKey] = allocation;
            while (order.State != OutboundState.Locked)
            {
                if (order.State == OutboundState.Draft) order.TransitionTo(OutboundState.Allocated);
                else if (order.State == OutboundState.Allocated) order.TransitionTo(OutboundState.Locked);
                else break;
            }
            return allocation;
        }
    }

    public OutboundOrder Create(string orderNumber, IEnumerable<OutboundLine>? lines = null)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new ArgumentException("An order number is required.", nameof(orderNumber));
        lock (_gate)
        {
            var key = orderNumber.Trim();
            if (_orders.ContainsKey(key)) throw new InvalidOperationException($"Outbound order '{key}' already exists.");
            var order = new OutboundOrder(key);
            foreach (var line in lines ?? []) order.AddLine(line);
            _orders.Add(key, order);
            return order;
        }
    }

    public OutboundAllocation Allocate(OutboundAllocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("An idempotency key is required.", nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Quantity, 0m);
        lock (_gate)
        {
            if (_allocations.TryGetValue(request.IdempotencyKey, out var existing)) return existing;
            if (!_orders.TryGetValue(request.OrderNumber.Trim(), out var order)) throw new KeyNotFoundException("Outbound order was not found.");
            var line = order.Lines.FirstOrDefault(item => item.Id == request.LineId) ?? throw new KeyNotFoundException("Outbound line was not found.");
            if (request.Quantity > line.RequestedQuantity) throw new InvalidOperationException("Allocation exceeds requested quantity.");
            var source = _inventory.GetBalances()
                .Where(item => item.Status == InventoryStatus.Available && item.MaterialId == line.MaterialId && item.Quantity >= request.Quantity)
                .Where(item => request.BatchNumber is null || string.Equals(item.BatchNumber, request.BatchNumber, StringComparison.OrdinalIgnoreCase))
                .Where(item => request.PalletId is null || item.PalletId == request.PalletId)
                .Where(item => request.LocationId is null || item.LocationId == request.LocationId)
                .OrderBy(item => item.BatchNumber, StringComparer.Ordinal)
                .ThenBy(item => item.LocationId)
                .FirstOrDefault() ?? throw new InvalidOperationException("Insufficient available inventory for allocation.");
            var now = DateTimeOffset.UtcNow;
            var resourceKeys = new[]
            {
                ("Inventory", source.Key),
                ("Pallet", source.PalletId?.ToString("D") ?? source.Key),
                ("Location", source.LocationId?.ToString("D") ?? source.Key)
            };
            ResourceLock[] locks;
            if (_resourceLockStore is not null)
            {
                var acquired = new List<ResourceLock>(resourceKeys.Length);
                try
                {
                    foreach (var resource in resourceKeys)
                    {
                        acquired.Add(_resourceLockStore.AcquireResourceLockAsync(
                            resource.Item1, resource.Item2, order.OrderNumber, now, TimeSpan.FromMinutes(15)).GetAwaiter().GetResult());
                    }

                    locks = acquired.ToArray();
                }
                catch
                {
                    foreach (var resourceLock in acquired.Where(item => item.IsActive()))
                    {
                        _resourceLockStore.ReleaseResourceLockAsync(
                            resourceLock.Id, order.OrderNumber, resourceLock.Version, now, resourceLock.LockToken).GetAwaiter().GetResult();
                    }

                    throw;
                }
            }
            else
            {
                if (resourceKeys.Any(item => _locks.Any(existingLock => existingLock.IsActive(now) && existingLock.ResourceKey == ResourceLock.BuildResourceKey(item.Item1, item.Item2))))
                    throw new InvalidOperationException("Inventory, pallet or location is already locked.");
                locks = resourceKeys.Select(item => new ResourceLock(item.Item1, item.Item2, order.OrderNumber, now, TimeSpan.FromMinutes(15))).ToArray();
                _locks.AddRange(locks);
            }
            order.TransitionTo(OutboundState.Allocated);
            order.TransitionTo(OutboundState.Locked);
            var allocation = new OutboundAllocation(order, line, source, request.Quantity, locks) { IdempotencyKey = request.IdempotencyKey };
            _allocations.Add(request.IdempotencyKey, allocation);
            return allocation;
        }
    }
}

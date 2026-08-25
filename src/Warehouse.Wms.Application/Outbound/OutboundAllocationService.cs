using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Outbound;

public sealed record OutboundAllocationRequest(string IdempotencyKey, string OrderNumber, Guid LineId, decimal Quantity, string? BatchNumber = null, Guid? PalletId = null, Guid? LocationId = null);
public sealed record OutboundAllocation(OutboundOrder Order, OutboundLine Line, InventoryBalance Source, decimal Quantity, IReadOnlyList<ResourceLock> ResourceLocks);

public sealed class OutboundAllocationService
{
    private readonly object _gate = new();
    private readonly InventoryService _inventory;
    private readonly Dictionary<string, OutboundOrder> _orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboundAllocation> _allocations = new(StringComparer.Ordinal);
    private readonly List<ResourceLock> _locks = [];

    public OutboundAllocationService(InventoryService inventory) => _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    public IReadOnlyCollection<OutboundOrder> Orders { get { lock (_gate) return _orders.Values.ToArray(); } }

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
            if (resourceKeys.Any(item => _locks.Any(existingLock => existingLock.IsActive(now) && existingLock.ResourceKey == ResourceLock.BuildResourceKey(item.Item1, item.Item2))))
                throw new InvalidOperationException("Inventory, pallet or location is already locked.");
            var locks = resourceKeys.Select(item => new ResourceLock(item.Item1, item.Item2, order.OrderNumber, now, TimeSpan.FromMinutes(15))).ToArray();
            _locks.AddRange(locks);
            order.TransitionTo(OutboundState.Allocated);
            order.TransitionTo(OutboundState.Locked);
            var allocation = new OutboundAllocation(order, line, source, request.Quantity, locks);
            _allocations.Add(request.IdempotencyKey, allocation);
            return allocation;
        }
    }
}

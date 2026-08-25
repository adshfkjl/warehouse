using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.Application.Inbound;

public sealed record InboundLineRequest(
    Guid MaterialId,
    decimal OrderedQuantity,
    string? BatchNumber = null,
    DateOnly? ExpirationDate = null);

public sealed record InboundReceiptRequest(
    string IdempotencyKey,
    decimal Quantity,
    decimal WeightKg = 0m,
    string? BatchNumber = null,
    DateOnly? ExpirationDate = null,
    string? PalletCode = null,
    Guid? PalletId = null,
    string? OperatorId = null);

/// <summary>
/// A received quantity awaiting putaway. LocationId is intentionally always null
/// in Task 5.1; Task 5.2/5.3 owns allocation and physical inventory submission.
/// </summary>
public sealed record PendingInboundInventory(
    Guid Id,
    Guid InboundOrderId,
    string OrderNumber,
    Guid InboundLineId,
    Guid MaterialId,
    Guid? PalletId,
    string? PalletCode,
    string? BatchNumber,
    DateOnly? ExpirationDate,
    decimal Quantity,
    decimal WeightKg,
    InventoryStatus Status,
    Guid? LocationId,
    DateTimeOffset ReceivedAt,
    string IdempotencyKey);

public sealed record InboundReceiptResult(
    Guid ReceiptId,
    Guid InboundOrderId,
    string OrderNumber,
    Guid InboundLineId,
    decimal Quantity,
    decimal WeightKg,
    PendingInboundInventory Inventory)
{
    public PendingInboundInventory PendingInbound => Inventory;
};

/// <summary>
/// Task 5.1 application contract. The current store is intentionally in-memory:
/// SQL persistence and transactional outbox integration are delivered by the
/// following inbound tasks. It never updates a location-backed inventory balance.
/// </summary>
public sealed class InboundOrderService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InboundOrder> _orders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredReceipt> _receiptsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _palletCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _palletIds = [];
    private readonly List<PendingInboundInventory> _pendingInbound = [];

    public IReadOnlyCollection<InboundOrder> Orders
    {
        get
        {
            lock (_gate)
            {
                return _orders.Values.ToArray();
            }
        }
    }

    public IReadOnlyCollection<PendingInboundInventory> PendingInboundInventory
    {
        get
        {
            lock (_gate)
            {
                return _pendingInbound.ToArray();
            }
        }
    }

    public IReadOnlyCollection<PendingInboundInventory> PendingInbound => PendingInboundInventory;

    public InboundOrder CreateOrder(string orderNumber, IEnumerable<InboundLineRequest>? lines = null)
        => Create(orderNumber, lines);

    public Task<InboundOrder> CreateOrderAsync(
        string orderNumber,
        IEnumerable<InboundLineRequest>? lines = null,
        CancellationToken cancellationToken = default)
        => CreateAsync(orderNumber, lines, cancellationToken);

    public InboundOrder Create(string orderNumber, IEnumerable<InboundLineRequest>? lines = null)
    {
        var normalizedNumber = Require(orderNumber, nameof(orderNumber));
        lock (_gate)
        {
            if (_orders.ContainsKey(normalizedNumber))
            {
                throw new InvalidOperationException($"Inbound order '{normalizedNumber}' already exists.");
            }

            var order = new InboundOrder(normalizedNumber);
            if (lines is not null)
            {
                foreach (var line in lines)
                {
                    ArgumentNullException.ThrowIfNull(line);
                    order.AddLine(new InboundLine(
                        line.MaterialId,
                        line.OrderedQuantity,
                        line.BatchNumber,
                        line.ExpirationDate));
                }
            }

            _orders.Add(normalizedNumber, order);
            return order;
        }
    }

    public Task<InboundOrder> CreateAsync(
        string orderNumber,
        IEnumerable<InboundLineRequest>? lines = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Create(orderNumber, lines));
    }

    public InboundLine AddLine(string orderNumber, InboundLineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            var order = GetOrder(orderNumber);
            return order.AddLine(new InboundLine(
                request.MaterialId,
                request.OrderedQuantity,
                request.BatchNumber,
                request.ExpirationDate));
        }
    }

    public InboundOrder Get(string orderNumber)
    {
        lock (_gate)
        {
            return GetOrder(orderNumber);
        }
    }

    public InboundReceiptResult Receive(
        string orderNumber,
        Guid lineId,
        InboundReceiptRequest request,
        DateTimeOffset? receivedAt = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedOrderNumber = Require(orderNumber, nameof(orderNumber));
        var normalizedKey = Require(request.IdempotencyKey, nameof(request.IdempotencyKey));
        lock (_gate)
        {
            var order = GetOrder(normalizedOrderNumber);
            var fingerprint = Fingerprint(normalizedOrderNumber, lineId, request);
            if (_receiptsByKey.TryGetValue(normalizedKey, out var duplicate))
            {
                if (!string.Equals(duplicate.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Receipt idempotency key '{normalizedKey}' was already used with a different payload.");
                }

                return duplicate.Result;
            }

            order.EnsureCanReceive();
            var line = order.Lines.FirstOrDefault(candidate => candidate.Id == lineId)
                ?? throw new KeyNotFoundException(
                    $"Inbound line '{lineId}' was not found in order '{normalizedOrderNumber}'.");

            ValidateRequest(request);
            if (request.Quantity > line.RemainingQuantity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request), request.Quantity,
                    $"Receipt quantity exceeds remaining quantity {line.RemainingQuantity}.");
            }

            var palletCode = Normalize(request.PalletCode);
            if (palletCode is not null && _palletCodes.ContainsKey(palletCode))
            {
                throw new InvalidOperationException($"Pallet '{palletCode}' is already bound to an inbound receipt.");
            }

            if (request.PalletId is Guid palletId && _palletIds.ContainsKey(palletId))
            {
                throw new InvalidOperationException($"Pallet '{palletId}' is already bound to an inbound receipt.");
            }

            var timestamp = (receivedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
            order.StartReceiving(request.OperatorId ?? "system", timestamp);
            var receipt = line.AddReceipt(
                normalizedKey,
                request.Quantity,
                request.WeightKg,
                request.BatchNumber,
                request.ExpirationDate,
                palletCode,
                request.PalletId,
                request.OperatorId,
                timestamp);

            var inventory = new PendingInboundInventory(
                Guid.NewGuid(),
                order.Id,
                order.OrderNumber,
                line.Id,
                line.MaterialId,
                request.PalletId,
                palletCode,
                Normalize(request.BatchNumber) ?? line.BatchNumber,
                request.ExpirationDate ?? line.ExpirationDate,
                request.Quantity,
                request.WeightKg,
                InventoryStatus.PendingInbound,
                null,
                timestamp,
                normalizedKey);

            order.CompleteReceiving(request.OperatorId ?? "system", timestamp);
            var result = new InboundReceiptResult(
                receipt.Id,
                order.Id,
                order.OrderNumber,
                line.Id,
                receipt.Quantity,
                receipt.WeightKg,
                inventory);
            _receiptsByKey.Add(normalizedKey, new StoredReceipt(fingerprint, result));
            _pendingInbound.Add(inventory);
            if (palletCode is not null)
            {
                _palletCodes.Add(palletCode, normalizedKey);
            }

            if (request.PalletId is Guid boundPalletId)
            {
                _palletIds.Add(boundPalletId, normalizedKey);
            }

            return result;
        }
    }

    public Task<InboundReceiptResult> ReceiveAsync(
        string orderNumber,
        Guid lineId,
        InboundReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Receive(orderNumber, lineId, request));
    }

    public InboundReceiptResult ReceiveLine(
        string orderNumber,
        Guid lineId,
        InboundReceiptRequest request,
        DateTimeOffset? receivedAt = null)
        => Receive(orderNumber, lineId, request, receivedAt);

    public Task<InboundReceiptResult> ReceiveLineAsync(
        string orderNumber,
        Guid lineId,
        InboundReceiptRequest request,
        CancellationToken cancellationToken = default)
        => ReceiveAsync(orderNumber, lineId, request, cancellationToken);

    public InboundReceiptResult Receive(
        string orderNumber,
        InboundReceiptRequest request,
        DateTimeOffset? receivedAt = null)
    {
        lock (_gate)
        {
            var order = GetOrder(orderNumber);
            if (order.Lines.Count != 1)
            {
                throw new InvalidOperationException(
                    "A line id is required when an inbound order has zero or multiple lines.");
            }

            return Receive(order.OrderNumber, order.Lines[0].Id, request, receivedAt);
        }
    }

    public Task<InboundReceiptResult> ReceiveAsync(
        string orderNumber,
        InboundReceiptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Receive(orderNumber, request));
    }

    public void Cancel(string orderNumber, string @operator, string reason)
    {
        lock (_gate)
        {
            GetOrder(orderNumber).Cancel(@operator, reason);
        }
    }

    public Task CancelAsync(
        string orderNumber,
        string @operator,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cancel(orderNumber, @operator, reason);
        return Task.CompletedTask;
    }

    public void MarkException(string orderNumber, string @operator, string reason)
    {
        lock (_gate)
        {
            GetOrder(orderNumber).MarkException(@operator, reason);
        }
    }

    public void MarkPutawayQueued(string orderNumber, string @operator = "system", string reason = "待上架任务已创建")
    {
        lock (_gate)
        {
            var order = GetOrder(orderNumber);
            if (!order.IsFullyReceived)
            {
                throw new InvalidOperationException("Only a fully received inbound order can enter putaway queue.");
            }

            order.TransitionTo(InboundState.PutawayQueued, @operator, reason);
        }
    }

    public void Complete(string orderNumber, string @operator = "system", string reason = "上架完成")
    {
        lock (_gate)
        {
            GetOrder(orderNumber).TransitionTo(InboundState.Completed, @operator, reason);
        }
    }

    private InboundOrder GetOrder(string orderNumber)
    {
        var normalized = Require(orderNumber, nameof(orderNumber));
        return _orders.TryGetValue(normalized, out var order)
            ? order
            : throw new KeyNotFoundException($"Inbound order '{normalized}' was not found.");
    }

    private static void ValidateRequest(InboundReceiptRequest request)
    {
        Require(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Quantity, "Receipt quantity must be greater than zero.");
        }

        if (request.WeightKg < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.WeightKg, "Receipt weight cannot be negative.");
        }

        if (request.PalletId is Guid palletId && palletId == Guid.Empty)
        {
            throw new ArgumentException("Pallet id cannot be empty.", nameof(request));
        }
    }

    private static string Fingerprint(string orderNumber, Guid lineId, InboundReceiptRequest request)
        => string.Join(
            "|",
            orderNumber,
            lineId.ToString("D"),
            request.Quantity.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            request.WeightKg.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            Normalize(request.BatchNumber) ?? "-",
            request.ExpirationDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            Normalize(request.PalletCode) ?? "-",
            request.PalletId?.ToString("D") ?? "-");

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private sealed record StoredReceipt(string Fingerprint, InboundReceiptResult Result);
}

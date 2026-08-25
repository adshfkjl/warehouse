namespace Warehouse.Wms.Domain.Inbound;

public sealed record InboundReceipt(
    Guid Id,
    string IdempotencyKey,
    decimal Quantity,
    decimal WeightKg,
    string? BatchNumber,
    DateOnly? ExpirationDate,
    string? PalletCode,
    Guid? PalletId,
    DateTimeOffset ReceivedAt,
    string? OperatorId = null);

public sealed class InboundLine
{
    private readonly List<InboundReceipt> _receipts = [];

    private InboundLine()
    {
    }

    public InboundLine(
        Guid materialId,
        decimal orderedQuantity,
        string? batchNumber = null,
        DateOnly? expirationDate = null,
        Guid? id = null)
    {
        if (materialId == Guid.Empty)
        {
            throw new ArgumentException("A material is required.", nameof(materialId));
        }

        if (orderedQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderedQuantity), orderedQuantity, "Ordered quantity must be greater than zero.");
        }

        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("An inbound line id is required.", nameof(id));
        MaterialId = materialId;
        OrderedQuantity = orderedQuantity;
        BatchNumber = Normalize(batchNumber);
        ExpirationDate = expirationDate;
    }

    public Guid Id { get; private set; }

    public Guid MaterialId { get; private set; }

    public decimal OrderedQuantity { get; private set; }

    public decimal ExpectedQuantity => OrderedQuantity;

    public decimal ReceivedQuantity { get; private set; }

    public decimal ReceivedWeightKg { get; private set; }

    public decimal TotalReceivedQuantity => ReceivedQuantity;

    public decimal TotalReceivedWeightKg => ReceivedWeightKg;

    /// <summary>
    /// Requested/default batch. Each receipt also retains its own batch value so a
    /// line can be received in more than one batch without losing traceability.
    /// </summary>
    public string? BatchNumber { get; private set; }

    public DateOnly? ExpirationDate { get; private set; }

    public decimal RemainingQuantity => OrderedQuantity - ReceivedQuantity;

    public IReadOnlyList<InboundReceipt> Receipts => _receipts.AsReadOnly();

    public IReadOnlyList<InboundReceipt> ReceiptRecords => Receipts;

    public InboundReceipt AddReceipt(
        string idempotencyKey,
        decimal quantity,
        decimal weightKg,
        string? batchNumber,
        DateOnly? expirationDate,
        string? palletCode,
        Guid? palletId,
        string? operatorId,
        DateTimeOffset receivedAt,
        Guid? receiptId = null)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Receipt quantity must be greater than zero.");
        }

        if (weightKg < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(weightKg), weightKg, "Receipt weight cannot be negative.");
        }

        var normalizedKey = Require(idempotencyKey, nameof(idempotencyKey));
        var receipt = new InboundReceipt(
            receiptId.GetValueOrDefault(Guid.NewGuid()),
            normalizedKey,
            quantity,
            weightKg,
            Normalize(batchNumber),
            expirationDate,
            Normalize(palletCode),
            palletId,
            receivedAt.ToUniversalTime(),
            Normalize(operatorId));

        _receipts.Add(receipt);
        ReceivedQuantity += quantity;
        ReceivedWeightKg += weightKg;
        BatchNumber ??= receipt.BatchNumber;
        ExpirationDate ??= receipt.ExpirationDate;
        return receipt;
    }

    public void RemoveReceipt(Guid receiptId, decimal previousReceivedQuantity, decimal previousReceivedWeightKg, string? previousBatchNumber, DateOnly? previousExpirationDate)
    {
        if (receiptId == Guid.Empty) return;
        var index = _receipts.FindLastIndex(receipt => receipt.Id == receiptId);
        if (index < 0) return;
        _receipts.RemoveAt(index);
        ReceivedQuantity = previousReceivedQuantity;
        ReceivedWeightKg = previousReceivedWeightKg;
        BatchNumber = previousBatchNumber;
        ExpirationDate = previousExpirationDate;
    }

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
}

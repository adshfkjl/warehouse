namespace Warehouse.Wms.Infrastructure.Health;

public sealed record InventoryReportRow(
    string MaterialCode,
    string PalletCode,
    string LocationCode,
    decimal Quantity,
    decimal WeightKg,
    string Status);

public sealed record LocationUtilizationRow(
    string LocationCode,
    int Capacity,
    int OccupiedUnits,
    decimal OccupiedWeightKg);

public sealed record LocationUtilizationReport(
    string LocationCode,
    int Capacity,
    int OccupiedUnits,
    decimal OccupiedWeightKg,
    decimal UtilizationPercent);

public sealed record InboundOutboundReportRow(
    string DocumentNumber,
    string Direction,
    decimal Quantity,
    string Status);

public sealed record TransferReportRow(
    string TaskNumber,
    string PalletCode,
    string SourceLocationCode,
    string DestinationLocationCode,
    string Status);

public sealed record PalletTraceReportRow(
    string PalletCode,
    string FromLocationCode,
    string ToLocationCode,
    string ReferenceNumber,
    string Status);

public sealed record StocktakingDifferenceReportRow(
    string TaskNumber,
    string LocationCode,
    decimal BookQuantity,
    decimal CountedQuantity,
    decimal Difference);

public sealed record DeviceAlarmReportRow(
    string DeviceId,
    string AlarmCode,
    string Status,
    DateTimeOffset OccurredAt);

public interface IReportsReadModel
{
    IReadOnlyCollection<InventoryReportRow> Inventory { get; }
    IReadOnlyCollection<LocationUtilizationRow> LocationUtilization { get; }
    IReadOnlyCollection<InboundOutboundReportRow> InboundOutbound { get; }
    IReadOnlyCollection<TransferReportRow> Transfers { get; }
    IReadOnlyCollection<PalletTraceReportRow> PalletTraces { get; }
    IReadOnlyCollection<StocktakingDifferenceReportRow> StocktakingDifferences { get; }
    IReadOnlyCollection<DeviceAlarmReportRow> DeviceAlarms { get; }
}

/// <summary>
/// Development-safe report snapshot. Production SQL projections are a later
/// persistence task; this contract keeps reporting read-only and explicit.
/// </summary>
public sealed class InMemoryReportsReadModel : IReportsReadModel
{
    public InMemoryReportsReadModel(
        IEnumerable<InventoryReportRow>? inventory = null,
        IEnumerable<LocationUtilizationRow>? locationUtilization = null,
        IEnumerable<InboundOutboundReportRow>? inboundOutbound = null,
        IEnumerable<TransferReportRow>? transfers = null,
        IEnumerable<PalletTraceReportRow>? palletTraces = null,
        IEnumerable<StocktakingDifferenceReportRow>? stocktakingDifferences = null,
        IEnumerable<DeviceAlarmReportRow>? deviceAlarms = null)
    {
        Inventory = (inventory ?? []).ToArray();
        LocationUtilization = (locationUtilization ?? []).ToArray();
        InboundOutbound = (inboundOutbound ?? []).ToArray();
        Transfers = (transfers ?? []).ToArray();
        PalletTraces = (palletTraces ?? []).ToArray();
        StocktakingDifferences = (stocktakingDifferences ?? []).ToArray();
        DeviceAlarms = (deviceAlarms ?? []).ToArray();
    }

    public IReadOnlyCollection<InventoryReportRow> Inventory { get; }
    public IReadOnlyCollection<LocationUtilizationRow> LocationUtilization { get; }
    public IReadOnlyCollection<InboundOutboundReportRow> InboundOutbound { get; }
    public IReadOnlyCollection<TransferReportRow> Transfers { get; }
    public IReadOnlyCollection<PalletTraceReportRow> PalletTraces { get; }
    public IReadOnlyCollection<StocktakingDifferenceReportRow> StocktakingDifferences { get; }
    public IReadOnlyCollection<DeviceAlarmReportRow> DeviceAlarms { get; }
}

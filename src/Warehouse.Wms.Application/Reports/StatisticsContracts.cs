namespace Warehouse.Wms.Application.Reports;

public enum StatisticsPeriod { Hour, Day, Week, Month }

public sealed record StatisticsKpi(
    decimal InventoryQuantity,
    decimal InventoryWeightKg,
    decimal LocationUtilizationPercent,
    decimal InboundQuantity,
    decimal OutboundQuantity,
    decimal TransferQuantity,
    decimal TaskSuccessRatePercent,
    int ExceptionCount,
    int StocktakingDifferenceCount);

public sealed record StatisticsTrendPoint(
    DateTimeOffset PeriodStart,
    decimal InboundQuantity,
    decimal OutboundQuantity,
    decimal TransferQuantity,
    int ExceptionCount);

public sealed record TaskStateCount(string State, int Count);

public sealed record StatisticsSnapshot(
    string BatchId,
    StatisticsPeriod Period,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset GeneratedAt,
    string SourceVersion,
    string Freshness,
    StatisticsKpi Kpi,
    IReadOnlyCollection<StatisticsTrendPoint> Trends,
    IReadOnlyCollection<TaskStateCount> TaskStates,
    string? WarehouseCode = null)
{
    public string IdempotencyKey => BatchId;
}

public sealed record StatisticsScheduleOptions(
    StatisticsPeriod Period = StatisticsPeriod.Day,
    TimeSpan? RunAt = null,
    bool Enabled = true,
    string? WarehouseCode = null)
{
    public TimeSpan EffectiveRunAt => RunAt ?? TimeSpan.FromHours(1);
}

public sealed record StatisticsBatchRequest(
    StatisticsPeriod Period,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string SourceVersion,
    string? WarehouseCode = null,
    StatisticsKpi? Kpi = null,
    IReadOnlyCollection<StatisticsTrendPoint>? Trends = null,
    IReadOnlyCollection<TaskStateCount>? TaskStates = null);

public interface IStatisticsService
{
    StatisticsSnapshot? LatestSuccessful { get; }
    Task<StatisticsSnapshot> GenerateAsync(StatisticsPeriod period, DateTimeOffset periodStart, DateTimeOffset periodEnd, string sourceVersion, bool fail = false, CancellationToken cancellationToken = default);
    Task<StatisticsSnapshot> GenerateAsync(StatisticsBatchRequest request, bool fail = false, CancellationToken cancellationToken = default);
    StatisticsSnapshot GetSummary(StatisticsPeriod? period = null, string? warehouseCode = null);
}

public interface IStatisticsSource
{
    Task<StatisticsBatchRequest?> BuildAsync(StatisticsPeriod period, DateTimeOffset periodStart, DateTimeOffset periodEnd, string sourceVersion, CancellationToken cancellationToken = default);
}

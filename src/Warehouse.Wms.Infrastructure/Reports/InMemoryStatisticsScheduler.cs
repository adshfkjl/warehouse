using Warehouse.Wms.Application.Reports;

namespace Warehouse.Wms.Infrastructure.Reports;

public sealed class InMemoryStatisticsScheduler
{
    private readonly IStatisticsService _statistics;
    private readonly StatisticsScheduleOptions _options;

    public InMemoryStatisticsScheduler(IStatisticsService statistics, StatisticsScheduleOptions? options = null)
    {
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _options = options ?? new StatisticsScheduleOptions();
    }

    public Task<StatisticsSnapshot> RunAsync(DateTimeOffset observedAt, string sourceVersion, string? warehouseCode = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new InvalidOperationException("Statistics schedule is disabled.");
        var utc = observedAt.ToUniversalTime();
        var start = Floor(utc, _options.Period);
        var end = Next(start, _options.Period);
        return _statistics.GenerateAsync(new StatisticsBatchRequest(_options.Period, start, end, sourceVersion, warehouseCode), cancellationToken: cancellationToken);
    }

    private static DateTimeOffset Floor(DateTimeOffset value, StatisticsPeriod period)
    {
        var date = value.Date;
        return period switch
        {
            StatisticsPeriod.Hour => new DateTimeOffset(date.AddHours(value.Hour), TimeSpan.Zero),
            StatisticsPeriod.Day => new DateTimeOffset(date, TimeSpan.Zero),
            StatisticsPeriod.Week => new DateTimeOffset(date.AddDays(-(int)date.DayOfWeek), TimeSpan.Zero),
            StatisticsPeriod.Month => new DateTimeOffset(new DateTime(date.Year, date.Month, 1), TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
    }

    private static DateTimeOffset Next(DateTimeOffset start, StatisticsPeriod period) => period switch
    {
        StatisticsPeriod.Hour => start.AddHours(1),
        StatisticsPeriod.Day => start.AddDays(1),
        StatisticsPeriod.Week => start.AddDays(7),
        StatisticsPeriod.Month => start.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(period))
    };
}

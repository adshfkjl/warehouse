using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Warehouse.Wms.Application.Reports;

namespace Warehouse.Wms.Infrastructure.Reports;

public sealed class UnavailableStatisticsSource : IStatisticsSource
{
    public Task<StatisticsBatchRequest?> BuildAsync(StatisticsPeriod period, DateTimeOffset periodStart, DateTimeOffset periodEnd, string sourceVersion, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<StatisticsBatchRequest?>(null); }
}

public sealed class StatisticsWorker(IStatisticsService statistics, StatisticsScheduleOptions options, string sourceVersion = "wms-v1", IStatisticsSource? source = null)
{
    public async Task<StatisticsSnapshot?> RunOnceAsync(DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!options.Enabled) throw new InvalidOperationException("Statistics schedule is disabled."); var utc=observedAt.ToUniversalTime(); if (utc.TimeOfDay < options.EffectiveRunAt) return null; var completedEnd=Floor(utc,options.Period); var start=Previous(completedEnd, options.Period); var request = source is null ? null : await source.BuildAsync(options.Period,start,completedEnd,sourceVersion,cancellationToken); if (request is null) return null; return await statistics.GenerateAsync(request, cancellationToken:cancellationToken);
    }
    private static DateTimeOffset Floor(DateTimeOffset v,StatisticsPeriod p){var d=v.Date; return p switch{StatisticsPeriod.Hour=>new DateTimeOffset(d.AddHours(v.Hour),TimeSpan.Zero),StatisticsPeriod.Day=>new DateTimeOffset(d,TimeSpan.Zero),StatisticsPeriod.Week=>new DateTimeOffset(d.AddDays(-(int)d.DayOfWeek),TimeSpan.Zero),StatisticsPeriod.Month=>new DateTimeOffset(new DateTime(d.Year,d.Month,1),TimeSpan.Zero),_=>throw new ArgumentOutOfRangeException(nameof(p))};}
    private static DateTimeOffset Next(DateTimeOffset s,StatisticsPeriod p)=>p switch{StatisticsPeriod.Hour=>s.AddHours(1),StatisticsPeriod.Day=>s.AddDays(1),StatisticsPeriod.Week=>s.AddDays(7),StatisticsPeriod.Month=>s.AddMonths(1),_=>throw new ArgumentOutOfRangeException(nameof(p))};
    private static DateTimeOffset Previous(DateTimeOffset s, StatisticsPeriod p) => p switch { StatisticsPeriod.Hour => s.AddHours(-1), StatisticsPeriod.Day => s.AddDays(-1), StatisticsPeriod.Week => s.AddDays(-7), StatisticsPeriod.Month => s.AddMonths(-1), _ => throw new ArgumentOutOfRangeException(nameof(p)) };
}
public sealed class StatisticsWorkerHostedService(StatisticsWorker worker, ILogger<StatisticsWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken){while(!stoppingToken.IsCancellationRequested){try{await worker.RunOnceAsync(DateTimeOffset.UtcNow,stoppingToken);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}catch(Exception ex){logger.LogError(ex,"Statistics worker iteration failed");} await Task.Delay(TimeSpan.FromMinutes(1),stoppingToken);}}
}

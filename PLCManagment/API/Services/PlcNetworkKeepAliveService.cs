using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;

namespace PLCManagement.API.Services
{
    public sealed class PlcNetworkKeepAliveService : BackgroundService
    {
        private const int MaxConcurrentProbes = 2;
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConnectedProbeInterval = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan PlcListRefreshInterval = TimeSpan.FromMinutes(1);

        private readonly IServiceProvider _serviceProvider;
        private readonly IPlcConnectionManager _connectionManager;
        private readonly ILogger<PlcNetworkKeepAliveService> _logger;
        private readonly ConcurrentDictionary<string, DateTime> _lastProbeTimes = new();
        private IReadOnlyList<string> _cachedActivePlcIds = Array.Empty<string>();
        private DateTime _nextPlcListRefresh = DateTime.MinValue;

        public PlcNetworkKeepAliveService(
            IServiceProvider serviceProvider,
            IPlcConnectionManager connectionManager,
            ILogger<PlcNetworkKeepAliveService> logger)
        {
            _serviceProvider = serviceProvider;
            _connectionManager = connectionManager;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PLC network keepalive service started.");

            using var timer = new PeriodicTimer(ProbeInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                await ProbeActivePlcs(stoppingToken);

                try
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ProbeActivePlcs(CancellationToken stoppingToken)
        {
            IReadOnlyList<string> activePlcIds;
            try
            {
                activePlcIds = await GetActivePlcIds(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PLC keepalive failed to load active PLC list.");
                return;
            }

            if (activePlcIds.Count == 0)
            {
                return;
            }

            await Parallel.ForEachAsync(
                activePlcIds,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentProbes,
                    CancellationToken = stoppingToken
                },
                async (plcId, token) =>
                {
                    await ProbePlc(plcId, token);
                });
        }

        private async Task ProbePlc(string plcId, CancellationToken stoppingToken)
        {
            var snapshot = _connectionManager.GetConnectionSnapshot(plcId);
            if (!ShouldProbe(snapshot))
            {
                return;
            }

            try
            {
                var isConnected = snapshot.IsConnected
                    ? await _connectionManager.TestConnection(plcId)
                    : await _connectionManager.ReconnectIfNeeded(plcId);

                _lastProbeTimes[plcId] = DateTime.Now;

                if (isConnected && snapshot.ConsecutiveFailures > 0)
                {
                    _logger.LogInformation("PLC {PlcId} keepalive recovered the connection.", plcId);
                }
            }
            catch (PlcConnectionUnavailableException)
            {
                _lastProbeTimes[plcId] = DateTime.Now;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _lastProbeTimes[plcId] = DateTime.Now;
                _logger.LogDebug(ex, "PLC {PlcId} keepalive probe failed.", plcId);
            }
        }

        private bool ShouldProbe(PlcConnectionSnapshot snapshot)
        {
            if (!snapshot.IsConnected)
            {
                return snapshot.CanAttemptConnection;
            }

            return !_lastProbeTimes.TryGetValue(snapshot.PlcId, out var lastProbeAt)
                || DateTime.Now - lastProbeAt >= ConnectedProbeInterval;
        }

        private async Task<IReadOnlyList<string>> GetActivePlcIds(CancellationToken stoppingToken)
        {
            if (DateTime.Now < _nextPlcListRefresh)
            {
                return _cachedActivePlcIds;
            }

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _cachedActivePlcIds = await context.PlcConfigurations
                .AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.PlcId)
                .Select(p => p.PlcId)
                .ToListAsync(stoppingToken);
            _nextPlcListRefresh = DateTime.Now.Add(PlcListRefreshInterval);
            return _cachedActivePlcIds;
        }
    }
}

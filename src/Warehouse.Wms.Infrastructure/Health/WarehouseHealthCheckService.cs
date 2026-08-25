namespace Warehouse.Wms.Infrastructure.Health;

public sealed record DeviceGatewayProbeResult(bool IsHealthy, string Detail);

public sealed record HealthComponentResult(
    string Component,
    string Status,
    string Detail,
    DateTimeOffset CheckedAt);

public sealed record HealthReportResponse(
    string OverallStatus,
    IReadOnlyCollection<HealthComponentResult> Components);

public sealed class MessagingHealthState
{
    private int _replayCount;

    public int ReplayCount => Volatile.Read(ref _replayCount);

    public void RecordReplay() => Interlocked.Increment(ref _replayCount);

    public void ClearReplay() => Interlocked.Exchange(ref _replayCount, 0);
}

public sealed class WarehouseHealthCheckService
{
    private readonly Func<CancellationToken, Task<bool>> _databaseProbe;
    private readonly Func<CancellationToken, Task<bool>> _workerProbe;
    private readonly Func<CancellationToken, Task<DeviceGatewayProbeResult>> _gatewayProbe;
    private readonly MessagingHealthState _messagingState;

    public WarehouseHealthCheckService(
        Func<CancellationToken, Task<bool>>? databaseProbe = null,
        Func<CancellationToken, Task<bool>>? workerProbe = null,
        Func<CancellationToken, Task<DeviceGatewayProbeResult>>? gatewayProbe = null,
        MessagingHealthState? messagingState = null)
    {
        _databaseProbe = databaseProbe ?? (_ => Task.FromResult(true));
        _workerProbe = workerProbe ?? (_ => Task.FromResult(true));
        _gatewayProbe = gatewayProbe ?? (_ => Task.FromResult(new DeviceGatewayProbeResult(true, "simulated gateway configured")));
        _messagingState = messagingState ?? new MessagingHealthState();
    }

    public async Task<IReadOnlyCollection<HealthComponentResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAt = DateTimeOffset.UtcNow;
        var database = await ProbeAsync(_databaseProbe, cancellationToken);
        var worker = await ProbeAsync(_workerProbe, cancellationToken);
        var gateway = await ProbeGatewayAsync(cancellationToken);
        var messagingStatus = _messagingState.ReplayCount > 0 ? "Degraded" : "Healthy";
        var messagingDetail = _messagingState.ReplayCount > 0
            ? $"replayed messages observed: {_messagingState.ReplayCount}"
            : "outbox/inbox replay guard is clear";

        return [
            Result("api", "Healthy", "API process is responding", checkedAt),
            Result("database", database ? "Healthy" : "Unhealthy", database ? "probe succeeded" : "probe failed", checkedAt),
            Result("worker", worker ? "Healthy" : "Degraded", worker ? "worker heartbeat is current" : "worker heartbeat is stale", checkedAt),
            Result("device-gateway", gateway.IsHealthy ? "Healthy" : "Unhealthy", gateway.Detail, checkedAt),
            Result("outbox-inbox", messagingStatus, messagingDetail, checkedAt)
        ];
    }

    public async Task<HealthReportResponse> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var components = await CheckAsync(cancellationToken);
        var overall = components.Any(item => item.Status == "Unhealthy")
            ? "Unhealthy"
            : components.Any(item => item.Status == "Degraded") ? "Degraded" : "Healthy";
        return new HealthReportResponse(overall, components);
    }

    private static async Task<bool> ProbeAsync(Func<CancellationToken, Task<bool>> probe, CancellationToken cancellationToken)
    {
        try
        {
            return await probe(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<DeviceGatewayProbeResult> ProbeGatewayAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _gatewayProbe(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DeviceGatewayProbeResult(false, $"probe failed: {exception.Message}");
        }
    }

    private static HealthComponentResult Result(string component, string status, string detail, DateTimeOffset checkedAt)
        => new(component, status, detail, checkedAt);
}

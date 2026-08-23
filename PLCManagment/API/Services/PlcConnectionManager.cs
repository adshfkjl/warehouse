using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.Core.Interfaces;
using PLCManagement.Core.Services;

namespace PLCManagement.API.Services
{
    public interface IPlcConnectionManager
    {
        Task<IModbusClient> GetConnection(string plcId);
        Task<bool> TestConnection(string plcId);
        void ReleaseConnection(string plcId, IModbusClient connection);
        Task InitializeConnections();
        Task<bool> ReconnectIfNeeded(string plcId, bool force = false);
        PlcConnectionSnapshot GetConnectionSnapshot(string plcId);
        void Dispose();
    }

    public sealed class PlcConnectionUnavailableException : Exception
    {
        public PlcConnectionUnavailableException(string plcId, TimeSpan retryAfter, string? lastError)
            : base($"PLC {plcId} is offline. Retry after {Math.Ceiling(retryAfter.TotalSeconds)} seconds. Last error: {lastError ?? "unknown"}")
        {
            PlcId = plcId;
            RetryAfter = retryAfter;
            LastError = lastError;
        }

        public string PlcId { get; }
        public TimeSpan RetryAfter { get; }
        public string? LastError { get; }
    }

    public sealed record PlcConnectionSnapshot(
        string PlcId,
        bool IsConnected,
        int ConsecutiveFailures,
        DateTime? LastFailureAt,
        DateTime NextAttemptAt,
        string? LastError)
    {
        public bool CanAttemptConnection => NextAttemptAt <= DateTime.Now;
    }

    public class PlcConnectionManager : IPlcConnectionManager, IDisposable
    {
        private const int PlcTimeoutMilliseconds = 3000;
        private const int ConnectionFailureLogInterval = 10;
        private static readonly TimeSpan MinimumReconnectInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan InitialFailureCooldown = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxFailureCooldown = TimeSpan.FromSeconds(60);

        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PlcConnectionManager> _logger;
        private readonly ConcurrentDictionary<string, IModbusClient> _connections = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _plcLocks = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastReconnectAttempts = new();
        private readonly ConcurrentDictionary<string, int> _connectionFailureCounts = new();
        private readonly ConcurrentDictionary<string, PlcCircuitBreaker> _circuitBreakers = new();
        private bool _disposed;

        private sealed class PlcCircuitBreaker
        {
            public int ConsecutiveFailures { get; set; }
            public DateTime? LastFailureAt { get; set; }
            public DateTime NextAttemptAt { get; set; } = DateTime.MinValue;
            public string? LastError { get; set; }
        }

        public PlcConnectionManager(IServiceProvider serviceProvider, ILogger<PlcConnectionManager> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task<IModbusClient> GetConnection(string plcId)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlcConnectionManager));

            plcId = NormalizePlcId(plcId);

            if (_connections.TryGetValue(plcId, out var existingClient) && existingClient.IsConnected)
            {
                return existingClient;
            }

            if (!CanAttemptConnection(plcId, out var retryAfter, out var lastError))
            {
                throw new PlcConnectionUnavailableException(plcId, retryAfter, lastError);
            }

            var plcLock = _plcLocks.GetOrAdd(plcId, _ => new SemaphoreSlim(1, 1));
            await plcLock.WaitAsync();
            try
            {
                if (_connections.TryGetValue(plcId, out existingClient) && existingClient.IsConnected)
                {
                    return existingClient;
                }

                if (!CanAttemptConnection(plcId, out retryAfter, out lastError))
                {
                    throw new PlcConnectionUnavailableException(plcId, retryAfter, lastError);
                }

                if (existingClient != null)
                {
                    existingClient.Dispose();
                    _connections.TryRemove(plcId, out _);
                }

                var plcConfig = await GetActivePlcConfig(plcId);
                if (plcConfig == null)
                {
                    throw new ArgumentException($"PLC {plcId} 未配置或未激活");
                }

                var client = new ModbusClient(
                    plcConfig.IpAddress,
                    plcConfig.Port,
                    plcConfig.SlaveId,
                    plcConfig.RegisterAddrOffset,
                    _serviceProvider.GetService<ILogger<ModbusClient>>(),
                    suppressConnectionFailureLogging: true);

                client.SetTimeout(PlcTimeoutMilliseconds);
                var result = client.Connect();
                _lastReconnectAttempts[plcId] = DateTime.Now;

                if (!result.IsSuccess)
                {
                    client.Dispose();
                    var failureCount = _connectionFailureCounts.AddOrUpdate(plcId, 1, (_, count) => count + 1);
                    RecordConnectionFailure(plcId, result.Message);
                    await UpdateConnectionStatus(plcId, "Disconnected", result.Message);
                    if (ShouldLogConnectionFailure(failureCount))
                    {
                        LogNetworkException(plcId, plcConfig.IpAddress, plcConfig.Port, "PLC连接", result.Message);
                    }
                    throw new Exception($"连接PLC {plcId} 失败: {result.Message}");
                }

                if (!client.TestConnection())
                {
                    client.Dispose();
                    const string probeFailureMessage = "Modbus probe failed";
                    var failureCount = _connectionFailureCounts.AddOrUpdate(plcId, 1, (_, count) => count + 1);
                    RecordConnectionFailure(plcId, probeFailureMessage);
                    await UpdateConnectionStatus(plcId, "Disconnected", probeFailureMessage);
                    if (ShouldLogConnectionFailure(failureCount))
                    {
                        LogNetworkException(plcId, plcConfig.IpAddress, plcConfig.Port, "PLC Modbus probe", probeFailureMessage);
                    }
                    throw new Exception($"PLC {plcId} {probeFailureMessage}");
                }

                _connections[plcId] = client;
                _connectionFailureCounts[plcId] = 0;
                RecordConnectionSuccess(plcId);
                await UpdateConnectionStatus(plcId, "Connected", null);
                _logger.LogInformation("PLC {PlcId} Modbus communication probe succeeded; long connection established.", plcId);

                return client;
            }
            finally
            {
                plcLock.Release();
            }
        }

        public async Task<bool> TestConnection(string plcId)
        {
            try
            {
                plcId = NormalizePlcId(plcId);
                var client = await GetConnection(plcId);
                if (client.IsConnected && client.TestConnection())
                {
                    await UpdateConnectionStatus(plcId, "Connected", null);
                    return true;
                }

                await ReconnectIfNeeded(plcId);
                return _connections.TryGetValue(plcId, out client) && client.IsConnected;
            }
            catch (Exception ex)
            {
                await UpdateConnectionStatus(plcId, "Disconnected", ex.Message);
                return false;
            }
        }

        public void ReleaseConnection(string plcId, IModbusClient connection)
        {
            // Connections are owned by this singleton manager and intentionally kept open.
        }

        public async Task InitializeConnections()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var activePlcIds = await context.PlcConfigurations
                .Where(p => p.IsActive)
                .Select(p => p.PlcId)
                .ToListAsync();

            _logger.LogInformation("开始初始化 {Count} 个PLC长连接", activePlcIds.Count);

            await Parallel.ForEachAsync(
                activePlcIds,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(activePlcIds.Count, 2) },
                async (plcId, _) =>
                {
                    try
                    {
                        await GetConnection(plcId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "**** PLC网络异常 **** PLC={PlcId}, 操作=初始化PLC连接, 错误={Message}", plcId, ex.Message);
                        _logger.LogWarning("初始化PLC {PlcId} 长连接失败: {Message}", plcId, ex.Message);
                    }
                });
        }

        public async Task<bool> ReconnectIfNeeded(string plcId, bool force = false)
        {
            if (_disposed) return false;

            plcId = NormalizePlcId(plcId);

            if (!force && !CanAttemptConnection(plcId, out _, out _))
            {
                return false;
            }

            if (!force &&
                _lastReconnectAttempts.TryGetValue(plcId, out var lastAttempt) &&
                DateTime.Now - lastAttempt < MinimumReconnectInterval)
            {
                return false;
            }

            var plcLock = _plcLocks.GetOrAdd(plcId, _ => new SemaphoreSlim(1, 1));
            await plcLock.WaitAsync();
            try
            {
                if (!force && _connections.TryGetValue(plcId, out var existingClient) && existingClient.IsConnected)
                {
                    return true;
                }

                if (_connections.TryRemove(plcId, out var oldClient))
                {
                    oldClient.Dispose();
                }

                _lastReconnectAttempts[plcId] = DateTime.Now;

                var plcConfig = await GetActivePlcConfig(plcId);
                if (plcConfig == null)
                {
                    await UpdateConnectionStatus(plcId, "Disconnected", "PLC未配置或未激活");
                    return false;
                }

                var client = new ModbusClient(
                    plcConfig.IpAddress,
                    plcConfig.Port,
                    plcConfig.SlaveId,
                    plcConfig.RegisterAddrOffset,
                    _serviceProvider.GetService<ILogger<ModbusClient>>(),
                    suppressConnectionFailureLogging: true);

                client.SetTimeout(PlcTimeoutMilliseconds);
                var result = client.Connect();

                if (!result.IsSuccess)
                {
                    client.Dispose();
                    var failureCount = _connectionFailureCounts.AddOrUpdate(plcId, 1, (_, count) => count + 1);
                    RecordConnectionFailure(plcId, result.Message);
                    await UpdateConnectionStatus(plcId, "Disconnected", result.Message);
                    if (ShouldLogConnectionFailure(failureCount))
                    {
                        LogNetworkException(plcId, plcConfig.IpAddress, plcConfig.Port, "PLC重连", result.Message);
                    }
                    _logger.LogWarning("PLC {PlcId} 重连失败: {Message}", plcId, result.Message);
                    return false;
                }

                if (!client.TestConnection())
                {
                    client.Dispose();
                    const string probeFailureMessage = "Modbus probe failed";
                    var failureCount = _connectionFailureCounts.AddOrUpdate(plcId, 1, (_, count) => count + 1);
                    RecordConnectionFailure(plcId, probeFailureMessage);
                    await UpdateConnectionStatus(plcId, "Disconnected", probeFailureMessage);
                    if (ShouldLogConnectionFailure(failureCount))
                    {
                        LogNetworkException(plcId, plcConfig.IpAddress, plcConfig.Port, "PLC Modbus probe", probeFailureMessage);
                    }
                    _logger.LogWarning("PLC {PlcId} reconnect rejected: {Message}", plcId, probeFailureMessage);
                    return false;
                }

                _connections[plcId] = client;
                _connectionFailureCounts[plcId] = 0;
                RecordConnectionSuccess(plcId);
                await UpdateConnectionStatus(plcId, "Connected", null);
                _logger.LogInformation("PLC {PlcId} Modbus communication probe succeeded; reconnect completed.", plcId);
                return true;
            }
            finally
            {
                plcLock.Release();
            }
        }

        private async Task<API.Models.PlcConfiguration?> GetActivePlcConfig(string plcId)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await context.PlcConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.PlcId.Trim() == plcId && p.IsActive);
        }

        private static string NormalizePlcId(string plcId)
        {
            if (string.IsNullOrWhiteSpace(plcId))
            {
                throw new ArgumentException("PLC编号不能为空", nameof(plcId));
            }

            return plcId.Trim();
        }

        private async Task UpdateConnectionStatus(string plcId, string status, string? errorMessage)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var plc = await context.PlcConfigurations.FirstOrDefaultAsync(p => p.PlcId == plcId);
                if (plc == null) return;

                plc.BoxWeightA ??= 0;
                plc.BoxWeightB ??= 0;
                plc.LastConnectionStatus = status;
                plc.LastErrorMessage = errorMessage;
                plc.LastTestTime = DateTime.Now;
                await context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "更新PLC {PlcId} 连接状态失败", plcId);
            }
        }

        private void LogNetworkException(string plcId, string ipAddress, int port, string operation, string? message)
        {
            _logger.LogWarning(
                "**** PLC网络异常 **** PLC={PlcId}, IP={IpAddress}, Port={Port}, 操作={Operation}, 错误={Message}",
                plcId,
                ipAddress,
                port,
                operation,
                message);
        }

        private static bool ShouldLogConnectionFailure(int failureCount)
        {
            return failureCount == 1 || failureCount % ConnectionFailureLogInterval == 0;
        }

        public PlcConnectionSnapshot GetConnectionSnapshot(string plcId)
        {
            plcId = NormalizePlcId(plcId);
            var isConnected = _connections.TryGetValue(plcId, out var client) && client.IsConnected;

            if (!_circuitBreakers.TryGetValue(plcId, out var circuit))
            {
                return new PlcConnectionSnapshot(plcId, isConnected, 0, null, DateTime.MinValue, null);
            }

            lock (circuit)
            {
                return new PlcConnectionSnapshot(
                    plcId,
                    isConnected,
                    circuit.ConsecutiveFailures,
                    circuit.LastFailureAt,
                    circuit.NextAttemptAt,
                    circuit.LastError);
            }
        }

        private bool CanAttemptConnection(string plcId, out TimeSpan retryAfter, out string? lastError)
        {
            retryAfter = TimeSpan.Zero;
            lastError = null;

            if (!_circuitBreakers.TryGetValue(plcId, out var circuit))
            {
                return true;
            }

            lock (circuit)
            {
                lastError = circuit.LastError;
                var now = DateTime.Now;
                if (circuit.NextAttemptAt <= now)
                {
                    return true;
                }

                retryAfter = circuit.NextAttemptAt - now;
                return false;
            }
        }

        private void RecordConnectionFailure(string plcId, string? errorMessage)
        {
            var circuit = _circuitBreakers.GetOrAdd(plcId, _ => new PlcCircuitBreaker());
            lock (circuit)
            {
                circuit.ConsecutiveFailures++;
                circuit.LastFailureAt = DateTime.Now;
                circuit.LastError = errorMessage;
                circuit.NextAttemptAt = DateTime.Now.Add(GetFailureCooldown(circuit.ConsecutiveFailures));
            }
        }

        private void RecordConnectionSuccess(string plcId)
        {
            _circuitBreakers.TryRemove(plcId, out _);
        }

        private static TimeSpan GetFailureCooldown(int consecutiveFailures)
        {
            if (consecutiveFailures <= 1)
            {
                return InitialFailureCooldown;
            }

            var multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 4));
            var cooldown = TimeSpan.FromMilliseconds(InitialFailureCooldown.TotalMilliseconds * multiplier);
            return cooldown <= MaxFailureCooldown ? cooldown : MaxFailureCooldown;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            foreach (var connection in _connections.Values)
            {
                connection.Dispose();
            }
            _connections.Clear();

            foreach (var plcLock in _plcLocks.Values)
            {
                plcLock.Dispose();
            }
            _plcLocks.Clear();
        }
    }
}

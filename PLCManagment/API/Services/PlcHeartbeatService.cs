using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCManagement.API.Data;
using System.Collections.Concurrent;

namespace PLCManagement.API.Services
{
    public class PlcHeartbeatService : IHostedService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PlcHeartbeatService> _logger;
        private Timer _timer;
        private bool _disposed = false;
        private readonly ConcurrentDictionary<string, DateTime> _lastHeartbeatTimes = new();
        private readonly ConcurrentDictionary<string, DateTime> _lastConnectionErrors = new();
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _plcListRefreshInterval = TimeSpan.FromMinutes(5);
        private DateTime _lastPlcListRefresh = DateTime.MinValue;
        private List<string> _cachedActivePlcIds = new List<string>();

        public PlcHeartbeatService(IServiceProvider serviceProvider, ILogger<PlcHeartbeatService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PLC心跳服务启动");
            _timer = new Timer(DoWork, null, TimeSpan.FromSeconds(2), _heartbeatInterval);
            return Task.CompletedTask;
        }

        private async void DoWork(object state)
        {
            if (_disposed) return;

            try
            {
                // 获取活跃PLC列表（使用缓存）
                var activePlcIds = await GetActivePlcIdsWithCache();

                if (!activePlcIds.Any())
                {
                    _logger.LogDebug("没有活跃的PLC设备");
                    return;
                }

                _logger.LogDebug($"开始处理 {activePlcIds.Count} 个PLC设备的心跳");

                var tasks = new List<Task>();

                foreach (var plcId in activePlcIds)
                {
                    if (_disposed) break;

                    // 检查是否需要跳过这个PLC（最近连接失败）
                    if (ShouldSkipPlc(plcId))
                    {
                        continue;
                    }

                    // 为每个PLC创建异步任务
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _serviceProvider.CreateScope();
                            var connectionManager = scope.ServiceProvider.GetRequiredService<IPlcConnectionManager>();
                            await ProcessPlcHeartbeat(plcId, connectionManager);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"PLC {plcId} 心跳处理异常");
                            RecordConnectionError(plcId);
                        }
                    });

                    tasks.Add(task);

                    // 添加小延迟避免同时处理所有PLC
                    await Task.Delay(20);
                }

                // 等待所有任务完成
                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "心跳服务执行异常");
            }
        }

        private async Task ProcessPlcHeartbeat(string plcId, IPlcConnectionManager connectionManager)
        {
            const int maxRetries = 2;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        _logger.LogDebug($"第 {attempt} 次重试PLC {plcId} 心跳");
                        await Task.Delay(200); // 重试延迟
                    }

                    var client = await connectionManager.GetConnection(plcId);
                    if (client != null && client.IsConnected)
                    {
                        var result = client.WriteSingleRegister(22027,
                            (ushort)(DateTime.Now.Second % 2 == 0 ? 1 : 0),2);

                        if (result.IsSuccess)
                        {
                            _logger.LogDebug($"心跳写入成功: {plcId}");
                            _lastHeartbeatTimes[plcId] = DateTime.Now;
                            _lastConnectionErrors.TryRemove(plcId, out _);
                            break;
                        }

                        if (attempt == maxRetries)
                        {
                            _logger.LogWarning($"心跳写入失败: {plcId} - {result.Message}");
                            RecordConnectionError(plcId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        _logger.LogError($"PLC {plcId} 心跳失败: {ex.Message}");
                        RecordConnectionError(plcId);
                    }
                }
            }
        }

        private bool ShouldSkipPlc(string plcId)
        {
            // 如果最近30秒内连接失败过，跳过这个PLC
            if (_lastConnectionErrors.TryGetValue(plcId, out var lastErrorTime))
            {
                if (DateTime.Now - lastErrorTime < TimeSpan.FromSeconds(30))
                {
                    _logger.LogDebug($"跳过PLC {plcId}，最近连接失败");
                    return true;
                }
            }
            return false;
        }

        private void RecordConnectionError(string plcId)
        {
            _lastConnectionErrors[plcId] = DateTime.Now;
        }

        private async Task<List<string>> GetActivePlcIdsWithCache()
        {
            // 使用缓存，避免频繁查询数据库
            if (DateTime.Now - _lastPlcListRefresh < _plcListRefreshInterval && _cachedActivePlcIds.Any())
            {
                return _cachedActivePlcIds;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var activePlcs = await context.PlcConfigurations
                    .Where(p => p.IsActive)
                    .Select(p => p.PlcId)
                    .ToListAsync();

                _cachedActivePlcIds = activePlcs;
                _lastPlcListRefresh = DateTime.Now;

                _logger.LogInformation($"刷新活跃PLC列表，共 {activePlcs.Count} 个设备");

                return activePlcs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取活跃PLC列表失败，使用缓存数据");
                return _cachedActivePlcIds; // 返回旧的缓存数据
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("PLC心跳服务停止");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _timer?.Dispose();
            _logger.LogInformation("PLC心跳服务已释放");
        }
    }
}
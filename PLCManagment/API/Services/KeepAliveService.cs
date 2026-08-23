// Services/KeepAliveService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PLCManagement.API.Data;

namespace PLCManagement.API.Services
{
    public class KeepAliveService : IHostedService, IDisposable
    {
        private readonly ILogger<KeepAliveService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private Timer _timer;
        private bool _disposed = false;
        private readonly int _maxParallelConnections = 8; // 最大并行连接数


        public KeepAliveService(ILogger<KeepAliveService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("保活服务启动");
            // 每3分钟执行一次保活操作
            _timer = new Timer(DoKeepAlive, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
            return Task.CompletedTask;

        }

        private async void DoKeepAlive(object state)
        {
            if (_disposed) return;

            try
            {
                using var scope = _serviceProvider.CreateScope();

                // 保持数据库连接活跃
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var count = await context.PlcConfigurations.CountAsync();
                _logger.LogDebug($"保活操作：数据库连接正常，共有 {count} 个PLC配置");

                // 保持PLC连接活跃
                var connectionManager = scope.ServiceProvider.GetRequiredService<IPlcConnectionManager>();
                var activePlcs = await context.PlcConfigurations
                    .Where(p => p.IsActive)
                    .Select(p => p.PlcId)
                    .ToListAsync();

                // 使用并行处理多个PLC连接
                await Parallel.ForEachAsync(
                    activePlcs.Take(_maxParallelConnections), // 限制并行数量，避免负载过高
                    new ParallelOptions { MaxDegreeOfParallelism = _maxParallelConnections },
                    async (plcId, cancellationToken) =>
                    {
                        try
                        {
                            await connectionManager.GetConnection(plcId);
                            _logger.LogDebug($"保活操作：PLC {plcId} 连接正常");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"保活操作：PLC {plcId} 连接失败: {ex.Message}");
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保活操作执行异常");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("保活服务停止");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
        }
    }
}
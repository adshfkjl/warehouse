using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PLCManagement.API.Interfaces;

namespace PLCManagement.API.Services
{
    public class PlcBackgroundService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<PlcBackgroundService> _logger;
        private bool _disposed = false;

        public PlcBackgroundService(
            IServiceProvider services,
            ILogger<PlcBackgroundService> logger)
        {
            _services = services;
            _logger = logger;
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PLC后台服务启动");

            // 初始连接
            await InitializeConnections(stoppingToken);
            while (!stoppingToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    using (var scope = _services.CreateScope())
                    {
                        var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();
                        var allPlcs = await plcService.GetAllPlcConfigurations();

                        foreach (var plc in allPlcs.Where(p => p.IsActive))
                        {
                            if (stoppingToken.IsCancellationRequested || _disposed)
                                break;

                            try
                            {
                                if (plc.LastConnectionStatus == "Connected")
                                {
                                    await plcService.UpdatePlcStatusAsync(plc.PlcId);
                                    await Task.Delay(100, stoppingToken);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, $"处理PLC {plc.PlcId} 时出错");
                            }
                        }
                    }

                    await Task.Delay(1000, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PLC后台服务执行异常");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            _logger.LogInformation("PLC后台服务停止");
        }



        //protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        //{
        //    //_logger.LogInformation("PLC Background Service is starting.");

        //    // 初始连接
        //    await InitializePlcConnections(stoppingToken);

        //    _logger.LogInformation("开启循环服务！"+System.DateTime.Now.ToString());
        //    while (!stoppingToken.IsCancellationRequested)
        //    {
        //        await Task.Delay(1000);
        //        using (var scope = _services.CreateScope())
        //        {
        //            var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();

        //            try
        //            {
        //                // 获取所有PLC配置
        //                var allPlcs = await plcService.GetAllPlcConfigurations();


        //                foreach (var plc in allPlcs)
        //                {
        //                    if (stoppingToken.IsCancellationRequested)
        //                        break;
        //                    _logger.LogInformation("循环检查PLC状态:"+plc.PlcId+"状态"+ plc.LastConnectionStatus);

        //                    try
        //                    {
        //                        if (plc.IsActive)
        //                        {
        //                            // 对于已连接的PLC，每秒更新状态
        //                            if (plc.LastConnectionStatus == "Connected")
        //                            {
        //                                await plcService.UpdatePlcStatusAsync(plc.PlcId);
        //                                await Task.Delay(1000, stoppingToken);
        //                            }
        //                            // 对于断开连接的PLC，每5分钟重试
        //                            else if (!_lastConnectionAttempts.ContainsKey(plc.PlcId) ||
        //                                    (DateTime.Now - _lastConnectionAttempts[plc.PlcId]).TotalMinutes >= 5)
        //                            {
        //                                _logger.LogInformation($"Attempting to reconnect to PLC {plc.PlcId}");
        //                                await plcService.TestPlcConnection(plc.PlcId);
        //                                _lastConnectionAttempts[plc.PlcId] = DateTime.Now;
        //                            }
        //                        }
        //                    }
        //                    catch (Exception ex)
        //                    {
        //                        _logger.LogError(ex, $"Error processing PLC {plc.PlcId}");
        //                    }
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                _logger.LogError(ex, "Error in PLC background service");
        //                await Task.Delay(5000, stoppingToken);
        //            }
        //        }
        //    }

        //    _logger.LogInformation("PLC Background Service is stopping.");
        //}


        private async Task InitializeConnections(CancellationToken stoppingToken)
        {
            using (var scope = _services.CreateScope())
            {
                var connectionManager = scope.ServiceProvider.GetRequiredService<IPlcConnectionManager>();
                try
                {
                    _logger.LogInformation("初始化PLC连接管理器...");
                    await connectionManager.InitializeConnections();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "初始化PLC连接管理器时出错");
                }
            }
        }

        public override void Dispose()
        {
            _disposed = true;
            base.Dispose();
        }

    }
}
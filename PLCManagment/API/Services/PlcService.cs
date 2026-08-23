// PLCManagement.API/Services/PlcService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Models;
using PLCManagement.Core.Interfaces;
using PLCManagement.Core.Models;
using PLCManagement.Core.Services;
using PLCManagement.API.Interfaces;
using Azure;
using PLCManagement.API.Models.Dtos;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text;
using Azure.Core;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Reflection.Metadata;
using Microsoft.Extensions.Configuration;

namespace PLCManagement.API.Services
{
    public class PlcService : IPlcService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PlcService> _logger;
        private readonly IBillOperationService _billOperationService;
        //private readonly ConcurrentDictionary<string, IModbusClient> _plcClients = new();
        private bool _disposed = false;
        private readonly IPlcConnectionManager _connectionManager;
        private DateTime _lastActivePlcsRefresh = DateTime.MinValue;
        private List<string> _cachedActivePlcIds = new List<string>();
        private readonly TimeSpan _activePlcsCacheDuration = TimeSpan.FromMinutes(5);
        private readonly ILocationCheckService  _locationCheckService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IConfiguration _configuration;


        //private readonly ILogService _logService;
        ///private readonly Dictionary<string, IModbusClient> _plcClients = new();


        //private readonly IServiceProvider contextISP;


        // 在构造函数中初始化心跳定时器
        public PlcService(
            IServiceProvider serviceProvider,
            ILogger<PlcService> logger,
            IBillOperationService billOperationService,
            IPlcConnectionManager connectionManager,
            ILocationCheckService locationCheckService,
            IServiceScopeFactory serviceScopeFactory,
            IConfiguration configuration

            )
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _billOperationService = billOperationService;
            _locationCheckService = locationCheckService;
            _connectionManager = connectionManager;
            _serviceScopeFactory = serviceScopeFactory;
            _configuration = configuration;
        }



        /*

        public PlcService(IServiceProvider serviceProvider, ILogService logService, ILogger<PlcService> logger)
        {
            _serviceProvider = serviceProvider;

            _logService = logService;
            _logger = logger;

        }


            public PlcBackgroundService(
            IServiceProvider services,
            ILogger<PlcBackgroundService> logger)
        {
            _services = services;
            _logger = logger;
        }
         
         */
        //public PlcService(ApplicationDbContext context, ILogService logService)
        //{
        //    _context = context;
        //    _logService = logService;
        //}

        //public async Task InitializePlcConnections()
        //{
        //    _logger.LogInformation("初始化PlcService");

        //    ApplicationDbContext _context;
        //    using var scope = _serviceProvider.CreateScope();

        //    _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        //    var activePlcs = await _context.PlcConfigurations
        //        .Where(p => p.IsActive)
        //        .ToListAsync();

        //    foreach (var plc in activePlcs)
        //    {
        //        try
        //        {
        //            var result = await TestPlcConnection(plc.PlcId);
        //            if (!result.IsSuccess)
        //            {
        //                _logger.LogInformation($"初始化连接到 PLC {plc.PlcId} 失败: {result.Message}");

        //                //_logger.LogInformation($"Initial connection to PLC {plc.PlcId} failed: {result.Message}");
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //                 _logger.LogError($"初始化 PLC {plc.PlcId}连接出错: {ex.Message}");
        //           //_logger.LogError($"Error initializing connection to PLC {plc.PlcId}: {ex.Message}");
        //        }
        //    }
        //}


        /*        public async Task InitializePlcConnections()
                {
                    _logger.LogInformation("初始化PlcService");

                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var activePlcs = await context.PlcConfigurations
                        .Where(p => p.IsActive)
                        .ToListAsync();

                    // 创建并行任务列表
                    var tasks = activePlcs.Select(plc => InitializeSinglePlcAsync(plc));

                    // 并行执行所有任务，可以添加并发限制
                    await Task.WhenAll(tasks);
                }

                private async Task InitializeSinglePlcAsync(PlcConfiguration plc)
                {
                    try
                    {
                        var result = await TestPlcConnection(plc.PlcId);
                        if (!result.IsSuccess)
                        {
                            _logger.LogInformation($"初始化连接到 PLC {plc.PlcId} 失败: {result.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"初始化 PLC {plc.PlcId}连接出错: {ex.Message}");
                    }
                }
        */

        public async Task InitializePlcConnections()
        {
            _logger.LogInformation("开始快速初始化PLC连接");

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var activePlcs = await context.PlcConfigurations
                .Where(p => p.IsActive)
                .ToListAsync();

            // 设置最大并发数
            var maxConcurrent = Math.Min(Environment.ProcessorCount * 2, 5); // 最多5个并发
            var semaphore = new SemaphoreSlim(maxConcurrent);

            // 创建快速连接任务
            var quickTasks = activePlcs.Select(async plc =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await QuickInitializePlcAsync(plc.PlcId);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            // 不等待所有连接完成，只等待一个合理的时间
            var initializationTask = Task.WhenAll(quickTasks);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10)); // 最多等待10秒

            var completedTask = await Task.WhenAny(initializationTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogInformation("PLC初始化超时（10秒），剩余连接将在后台继续");

                // 继续在后台运行剩余任务
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await initializationTask;
                        _logger.LogInformation("后台PLC连接完成");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"后台PLC连接异常: {ex.Message}");
                    }
                });
            }
            else
            {
                // 所有任务在超时前完成
                try
                {
                    await initializationTask;
                    _logger.LogInformation($"所有PLC连接初始化完成，共{activePlcs.Count}个设备");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"PLC连接初始化过程中出错: {ex.Message}");
                }
            }
        }

        private async Task QuickInitializePlcAsync(string plcId)
        {
            try
            {
                // 快速测试连接（2秒超时）
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                var connectionTask = TestPlcConnection(plcId);

                if (await Task.WhenAny(connectionTask, Task.Delay(Timeout.Infinite, cts.Token)) == connectionTask)
                {
                    var result = await connectionTask;
                    if (result.IsSuccess)
                    {
                        _logger.LogDebug($"PLC {plcId} 连接成功");
                    }
                    else
                    {
                        _logger.LogInformation($"PLC {plcId} 连接失败: {result.Message}");
                    }
                }
                else
                {
                    _logger.LogInformation($"PLC {plcId} 连接超时（2秒）");

                    // 记录超时状态
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var plc = await context.PlcConfigurations
                        .FirstOrDefaultAsync(p => p.PlcId == plcId);

                    if (plc != null)
                    {
                        plc.LastTestTime = DateTime.Now;
                        plc.LastConnectionStatus = "Timeout";
                        plc.LastErrorMessage = "连接测试超时（2秒）";
                        await context.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is TimeoutException)
            {
                _logger.LogInformation($"PLC {plcId} 连接测试超时或取消");
            }
            catch (Exception ex)
            {
                _logger.LogError($"初始化 PLC {plcId} 连接出错: {ex.Message}");
            }
        }



        public async Task<PlcConfiguration> GetPlcConfiguration(string plcId)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await _context.PlcConfigurations
                .FirstOrDefaultAsync(p => p.PlcId == plcId);
        }

        public async Task<List<PlcConfiguration>> GetAllPlcConfigurations()
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await _context.PlcConfigurations.ToListAsync();
        }

        private async Task<List<string>> GetActivePlcIdsWithCache()
        {
            // 使用缓存，避免频繁查询数据库
            if (DateTime.Now - _lastActivePlcsRefresh < _activePlcsCacheDuration && _cachedActivePlcIds.Any())
            {
                return _cachedActivePlcIds;
            }

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var activePlcs = await context.PlcConfigurations
                .Where(p => p.IsActive)
                .Select(p => p.PlcId)
                .ToListAsync();

            _cachedActivePlcIds = activePlcs;
            _lastActivePlcsRefresh = DateTime.Now;

            _logger.LogDebug($"刷新活跃PLC列表，共 {activePlcs.Count} 个设备");

            return activePlcs;
        }


        public async Task<PlcConfiguration> AddPlcConfiguration(PlcConfiguration plc)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            plc.UpdatedAt = DateTime.Now;
            _context.PlcConfigurations.Add(plc);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Added new PLC configuration: {plc.PlcId}");

            //await _logService.LogInformation($"Added new PLC configuration: {plc.PlcId}");

            // Test connection after adding
            await TestPlcConnection(plc.PlcId);

            return plc;
        }

        public async Task UpdatePlcConfiguration(string plcId, PlcConfiguration plc)
        {
            var existingPlc = await GetPlcConfiguration(plcId);
            if (existingPlc == null)
            {
                throw new ArgumentException($"PLC with ID {plcId} not found");
            }

            existingPlc.IpAddress = plc.IpAddress;
            existingPlc.Port = plc.Port;
            existingPlc.SlaveId = plc.SlaveId;
            existingPlc.Description = plc.Description;
            existingPlc.IsActive = plc.IsActive;
            existingPlc.UpdatedAt = DateTime.Now;


            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await _context.SaveChangesAsync();
            _logger.LogInformation($"Updated PLC configuration: {plcId}");

            //await _logService.LogInformation($"Updated PLC configuration: {plcId}");

            // Test connection after update
            await TestPlcConnection(plcId);
        }

        public async Task DeletePlcConfiguration(string plcId)
        {
            var plc = await GetPlcConfiguration(plcId);
            if (plc == null)
            {
                throw new ArgumentException($"PLC with ID {plcId} not found");
            }

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            _context.PlcConfigurations.Remove(plc);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Deleted PLC configuration: {plcId}");

            //await _logService.LogInformation($"Deleted PLC configuration: {plcId}");
        }


        /*  不能保存状态的方法
        public async Task<ModbusResponse> TestPlcConnection(string plcId)
        {
            var plc = await GetPlcConfiguration(plcId);
            if (plc == null)
            {
                return new ModbusResponse
                {
                    IsSuccess = false,
                    Message = $"PLC with ID {plcId} not found"
                };
            }

            if (!_plcClients.ContainsKey(plcId))
            {
                _plcClients[plcId] = new ModbusClient(plc.IpAddress, plc.Port, plc.SlaveId);
            }

            var response = _plcClients[plcId].Connect();

            plc.LastTestTime = DateTime.Now;
            plc.LastConnectionStatus = response.IsSuccess ? "Connected" : "Disconnected";
            plc.LastErrorMessage = response.IsSuccess ? null : response.Message;


            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await _context.SaveChangesAsync();

            _logger.LogInformation($"连接到PLC {plcId}: {(response.IsSuccess ? "成功" : "失败")}");

            //await _logService.LogInformation(
            //    $"Tested connection to PLC {plcId}: {(response.IsSuccess ? "Success" : "Failed")}");

            return response;
        }*/

        //public async Task<ModbusResponse> TestPlcConnection(string plcId)
        //{
        //    using var scope = _serviceProvider.CreateScope();
        //    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        //    var plc = await context.PlcConfigurations
        //        .FirstOrDefaultAsync(p => p.PlcId == plcId);

        //    if (plc == null)
        //    {
        //        return new ModbusResponse
        //        {
        //            IsSuccess = false,
        //            Message = $"PLC with ID {plcId} not found"
        //        };
        //    }

        //    if (!_plcClients.ContainsKey(plcId))
        //    {
        //        _plcClients[plcId] = new ModbusClient(plc.IpAddress, plc.Port, plc.SlaveId);
        //    }

        //    var response = _plcClients[plcId].Connect();

        //    plc.LastTestTime = DateTime.Now;
        //    plc.LastConnectionStatus = response.IsSuccess ? "在线" : "离线";
        //    plc.LastErrorMessage = response.IsSuccess ? null : response.Message;

        //    await context.SaveChangesAsync(); // ✅ 使用同一个 context 保存

        //    _logger.LogInformation($"连接到PLC {plcId}: {(response.IsSuccess ? "成功" : "失败")}");

        //    return response;
        //}

        //private async Task<IModbusClient> GetOrCreateConnection(string plcId)
        //{
        //    if (_plcClients.TryGetValue(plcId, out var client) && client.IsConnected)
        //    {
        //        return client;
        //    }

        //    // 清理无效连接
        //    if (client != null)
        //    {
        //        client.Dispose();
        //        _plcClients.TryRemove(plcId, out _);
        //    }

        //    // 重新创建连接
        //    using var scope = _serviceProvider.CreateScope();
        //    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        //    var plcConfig = await context.PlcConfigurations
        //        .FirstOrDefaultAsync(p => p.PlcId == plcId);

        //    if (plcConfig == null) throw new ArgumentException($"PLC {plcId} 未找到");

        //    var newClient = new ModbusClient(plcConfig.IpAddress, plcConfig.Port, plcConfig.SlaveId);
        //    var result = newClient.Connect();

        //    if (result.IsSuccess)
        //    {
        //        _plcClients[plcId] = newClient;
        //        return newClient;
        //    }

        //    throw new Exception($"连接PLC {plcId} 失败: {result.Message}");
        //}



        public async Task<ModbusResponse> TestPlcConnection(string plcId)
        {
            try
            {
                var client = await _connectionManager.GetConnection(plcId);
                var response = new ModbusResponse { IsSuccess = client.IsConnected };

                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var plc = await context.PlcConfigurations
                    .FirstOrDefaultAsync(p => p.PlcId == plcId);

                if (plc != null)
                {
                    EnsurePlcWeights(plc);
                    plc.LastTestTime = DateTime.Now;
                    plc.LastConnectionStatus = response.IsSuccess ? "Connected" : "Disconnected";
                    plc.LastErrorMessage = response.IsSuccess ? null : "连接测试失败";
                    await context.SaveChangesAsync();
                }

                return response;
            }
            catch (Exception ex)
            {
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }



        private async Task<bool> IsPlcConnected(string plcId)
        {
            IModbusClient client = await _connectionManager.GetConnection(plcId);
            return client.IsConnected;

        }

        private async Task EnsurePlcConnected(string plcId)
        {
            if (!await IsPlcConnected(plcId))
            {
                await TestPlcConnection(plcId);
            }
        }

        public async Task<ModbusResponse> WriteToPlc(string plcId, ushort startAddress, string value, int dataType)
        {
            
            try
            {
                var client = await _connectionManager.GetConnection(plcId);
                ModbusResponse response;

                switch (dataType)
                {
                    case 1: // 16-bit integer
                        if (!ushort.TryParse(value, out var ushortValue))
                        {
                            return new ModbusResponse
                            {
                                IsSuccess = false,
                                Message = "Invalid 16-bit integer value"
                            };
                        }
                        response = client.WriteSingleRegister(startAddress, ushortValue,2);
                        break;

                    case 2: // 32-bit integer
                        if (!int.TryParse(value, out var intValue))
                        {
                            return new ModbusResponse
                            {
                                IsSuccess = false,
                                Message = "Invalid 32-bit integer value"
                            };
                        }
                        var registers = new ushort[2];
                        registers[0] = (ushort)(intValue & 0xFFFF); // Low word
                        registers[1] = (ushort)(intValue >> 16);    // High word
                        response = client.WriteMultipleRegisters(startAddress, registers);
                        break;

                    default:
                        return new ModbusResponse
                        {
                            IsSuccess = false,
                            Message = "Invalid data type"
                        };
                }

                _logger.LogInformation($"Write to PLC {plcId} at address {startAddress}: {(response.IsSuccess ? "Success" : "Failed")}");

                //await _logService.LogInformation(
                //    $"Write to PLC {plcId} at address {startAddress}: {(response.IsSuccess ? "Success" : "Failed")}");

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error writing to PLC {plcId}: {ex.Message}");
                //_logger.LogError($"Error writing to PLC {plcId}: {ex.Message}");
                return new ModbusResponse
                {
                    IsSuccess = false,
                    Message = ex.Message
                };
            }
        }

        public async Task<ModbusResponse<ushort[]>> ReadFromPlc(string plcId, ushort startAddress, ushort numberOfPoints)
        {
            try
            {
                var client = await _connectionManager.GetConnection(plcId);
                return client.ReadHoldingRegisters(startAddress, numberOfPoints);
            }
            catch (Exception ex)
            {
                return new ModbusResponse<ushort[]>
                {
                    IsSuccess = false,
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// 出库操作
        /// </summaryIsLoadingPointEmpty
        /// <param name="plcId"></param>
        /// <param name="outShelf"></param>
        /// <param name="selectedOutPosition"></param>
        /// <param name="loadingPoint"></param>
        /// <returns></returns>
        public async Task<ModbusResponse> OutboundOperation(string plcId, int outShelf, int selectedOutPosition, int loadingPoint)
        {
            try
            {
                // 检查装载点是否为空
                var isloadingPointEmpty = await _locationCheckService.IsLoadingPointEmpty(plcId, loadingPoint);
                if (!isloadingPointEmpty)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "装载点当前有物品，不能执行出库" };
                }

                // 检查储位是否有货
                var storageStatus = await _locationCheckService.GetStorageLocationStatus(plcId, outShelf, selectedOutPosition);
                if (storageStatus != 1) // 1表示有货
                {
                    return new ModbusResponse { IsSuccess = false, Message = "指定储位没有货物，不能执行出库" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("出库操作失败:" + ex.Message);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var logEntry = new LocationOperationLog
            {
                CreateTime = DateTime.Now,
                PLCID = plcId,
                OutboundShelf = outShelf,
                OutboundPosition = selectedOutPosition,
                LoadingPoint = loadingPoint,
                OperationType = 1, // 1: Outbound
                OperationResult = 4 // Initially set to 4 (START)
            };

            try
            {

                var existingPlc = await UpdatePlcStatusAsync(plcId);

                if (existingPlc == null)
                {
                    throw new ArgumentException($"PLC with ID {plcId} not found");
                }
                await this.ReadPlcStatusAsync(plcId);

                if (existingPlc.OperationResult != 0)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "设备当前任务未完成，不能执行新任务。" };
                }

                if (existingPlc.ForksStat == true)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "当前货叉有货，不能执行任务。" };
                }
                if (((double)(existingPlc.BoxWeightA.GetValueOrDefault(50)) >= 0.4 && loadingPoint == 0) || ((double)(existingPlc.BoxWeightB.GetValueOrDefault(50)) >= 0.4 && loadingPoint == 1))
                { 
                    return new ModbusResponse { IsSuccess = false, Message = "出货点当前有物品。" };
                }

                existingPlc.OutboundPosition = selectedOutPosition;
                existingPlc.LoadingPoint = loadingPoint;
                existingPlc.OperationType = 1;
                existingPlc.OperationResult = 0;
                existingPlc.OutboundShelf = outShelf;
                //existingPlc.LoadingPoint = loadingPoint;
                existingPlc.IsOutboundCompleted = false;

                existingPlc.TaskCreateTime = DateTime.Now;
                existingPlc.IsActive = true;
                existingPlc.UpdatedAt = DateTime.Now;
                existingPlc.TaskStartTime = DateTime.Now;

                await _context.SaveChangesAsync();

                // Get PLC connection
                var client = await _connectionManager.GetConnection(plcId);


                //向PLC发送指令
                // Set operation parameters
                await WriteRegister(client, 22000, 1);
                //await WriteRegister(client, 22001, (ushort)outShelf);
                await WriteRegister(client, 22002, (ushort)outShelf);
                await WriteRegister(client, 22003, (ushort)selectedOutPosition);
                await WriteRegister(client, 22001, (ushort)loadingPoint);

                // Trigger operation
                await WriteRegister(client, 22009, 1);
                await Task.Delay(2500);
                await WriteRegister(client, 22009, 0);
                //向PLC发送指令结果

                // Update operation result
                //if (loadingPoint == 0)
                //    logEntry.WeightBegin = existingPlc.BoxWeightA;
                //else
                //    logEntry.WeightBegin = existingPlc.BoxWeightB;

                logEntry.OperationResult = 4; // Success
                logEntry.EndTime = DateTime.Now;

                await _context.SaveChangesAsync();
                await SaveOperationLog(logEntry);

                // Update shelf status
                //await UpdateShelfStatus(plcId, outShelf, selectedOutPosition, 0); // 0: No goods
                return new ModbusResponse { IsSuccess = true, Message = "出库操作成功" };

                //return new ModbusResponse { IsSuccess = true, Message = "出库操作成功"+loadingPoint.ToString() };
            }
            catch (Exception ex)
            {
                _logger.LogError("出库操作失败:" + ex.Message);
                logEntry.EndTime = DateTime.Now;
                await SaveOperationLog(logEntry);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }

        // 出库操作
        public async Task<ModbusResponse> OutboundOperation(string plcId, int outShelf,
            int selectedOutPosition, int loadingPoint, string billId,
            string billNo, int itm = 0)
        {

            try
            {
                // 检查装载点是否为空
                var isloadingPointEmpty = await _locationCheckService.IsLoadingPointEmpty(plcId, loadingPoint);
                if (!isloadingPointEmpty)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "装载点当前有物品，不能执行出库" };
                }

                // 检查储位是否有货
                var storageStatus = await _locationCheckService.GetStorageLocationStatus(plcId, outShelf, selectedOutPosition);
                if (storageStatus != 1) // 1表示有货
                {
                    return new ModbusResponse { IsSuccess = false, Message = "指定储位没有货物，不能执行出库" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("出库操作失败:" + ex.Message);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var logEntry = new LocationOperationLog
            {
                CreateTime = DateTime.Now,
                PLCID = plcId,
                OutboundShelf = outShelf,
                OutboundPosition = selectedOutPosition,
                LoadingPoint = loadingPoint,
                OperationType = 1, // 1: Outbound
                OperationResult = 4 // Initially set to 0 (failure)
            };


            try
            {
                var existingPlc = await UpdatePlcStatusAsync(plcId);

                if (existingPlc == null)
                {
                    throw new ArgumentException($"PLC with ID {plcId} not found");
                }
                await this.ReadPlcStatusAsync(plcId);

                if (existingPlc.OperationResult != 0)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "设备当前任务未完成，不能执行新任务。" };
                }

                if (existingPlc.ForksStat == true)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "当前货叉有货，不能执行任务。" };
                }

                if ((double)(existingPlc.BoxWeightA.GetValueOrDefault(50)) >= 0.4 && loadingPoint == 0 || (double)(existingPlc.BoxWeightB.GetValueOrDefault(50)) >= 0.4 && loadingPoint == 1)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "出货点当前有物品。" };
                }

                existingPlc.OutboundPosition = selectedOutPosition;
                existingPlc.LoadingPoint = loadingPoint;
                existingPlc.OperationType = 1;
                existingPlc.OperationResult = 4;
                existingPlc.OutboundShelf = outShelf;
                //existingPlc.LoadingPoint = loadingPoint;
                existingPlc.IsOutboundCompleted = false;

                existingPlc.TaskCreateTime = DateTime.Now;
                existingPlc.IsActive = true;
                existingPlc.UpdatedAt = DateTime.Now;
                existingPlc.TaskStartTime = DateTime.Now;

                await _context.SaveChangesAsync();

                // Get PLC connection
                var client = await _connectionManager.GetConnection(plcId);


                //向PLC发送指令
                // Set operation parameters
                await WriteRegister(client, 22000, 1);
                //await WriteRegister(client, 22001, (ushort)outShelf);
                await WriteRegister(client, 22002, (ushort)outShelf);
                await WriteRegister(client, 22003, (ushort)selectedOutPosition);
                await WriteRegister(client, 22001, (ushort)loadingPoint);

                // Trigger operation
                await WriteRegister(client, 22009, 1);
                await Task.Delay(2500);
                await WriteRegister(client, 22009, 0);
                //向PLC发送指令结果


                // Update operation result
                logEntry.OperationResult = 4; // Success
                logEntry.EndTime = DateTime.Now;
                //await SaveOperationLog(logEntry);
                await _context.SaveChangesAsync();

                await SaveOperationLog(logEntry);
                
                //await UpdateShelfStatus(plcId, outShelf, selectedOutPosition, 0); // 0: No goods

                // 更新业务明细
                if (!string.IsNullOrEmpty(billId) && !string.IsNullOrEmpty(billNo) && itm > 0)
                {
                    await _billOperationService.UpdateBillOperation(
                        billId, billNo, itm, 1, 1, loadingPoint, plcId);
                }

                return new ModbusResponse { IsSuccess = true, Message = "出库操作成功" };
            }
            catch (Exception ex)
            {
                // 记录失败状态
                if (!string.IsNullOrEmpty(billId) && !string.IsNullOrEmpty(billNo) && itm > 0)
                {
                    await _billOperationService.UpdateBillOperation(
                        billId, billNo, itm, 1, 0, loadingPoint, plcId);
                }
                _logger.LogError("出库操作失败:" + ex.Message);
                logEntry.EndTime = DateTime.Now;
                await SaveOperationLog(logEntry);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }

        public async Task<PlcStatusDto> ReadPlcStatusAsync(string plcId)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Get PLC configuration from database
            var plcConfig = await GetPlcConfiguration(plcId);
            if (plcConfig == null)
            {
                throw new ArgumentException($"PLC with ID {plcId} not found");
            }

            var client = await _connectionManager.GetConnection(plcId);



            ushort startAddress = 23000;
            ushort numberOfPoints = 50; // Enough to cover up to 3015

            var response = client.ReadHoldingRegisters(startAddress, numberOfPoints);
            if (!response.IsSuccess || response.Data == null || response.Data.Length < numberOfPoints)
            {
                _logger.LogError($"读取PLC {plcId} 寄存器失败或数据不完整");
                throw new Exception($"读取PLC {plcId} 寄存器失败: {response.Message}");
            }

            ushort[] registers = response.Data;
            //// 确保数组长度足够
            //if (registers.Length < 44)
            //{
            //    _logger.LogError($"PLC {plcId} 返回数据长度不足: {registers.Length}/44");
            //    throw new Exception($"PLC数据不完整");
            //}

            ushort[] posx = new ushort[2];
            Array.Copy(registers, 30, posx, 0, 2);
            ushort[] posy = new ushort[2];
            Array.Copy(registers, 32, posy, 0, 2);
            ushort[] posz = new ushort[2];
            Array.Copy(registers, 34, posz, 0, 2);
            //ushort[] wetA = new ushort[2];
            //Array.Copy(registers, 42, wetA, 0, 2);
            //ushort[] wetB = new ushort[2];
            //Array.Copy(registers, 44, wetB, 0, 2);

            // Convert registers to values
            var status = new PlcStatusDto
            {
                Online = registers[0] == 1,
                Working = registers[1],
                TaskStatus =registers[2],
                HasGoods = registers[3] == 1,
                ShelfHasGoods = registers[4],
                ErrorCode = Convert.ToInt32((registers[20] << 16) | registers[19]),
                ErrorMSG = AlarmDecoder.DecodeAlarmToString((registers[20] << 16) | registers[19]),
                XPosition = FloatToRegisters.UInt16ArrayToFloat(posx),
                YPosition = FloatToRegisters.UInt16ArrayToFloat(posy),
                ZPosition = FloatToRegisters.UInt16ArrayToFloat(posz),
                WeightA = (float)(Convert.ToInt32((short)registers[42])/100.00),  // FloatToRegisters.UInt16ArrayToFloat(wetA),
                WeightB = (float)(Convert.ToInt32((short)registers[44])/100.00),  //FloatToRegisters.UInt16ArrayToFloat(wetB),


                MainSpeed = (int)plcConfig.MainSpeed.GetValueOrDefault(5000),
                AuxSpeed = (int)plcConfig.AuxSpeed.GetValueOrDefault(1500),
                ForkLength = (decimal)plcConfig.ForkLength.GetValueOrDefault(0.38M)
            };

            plcConfig.OperationResult = Convert.ToInt32(registers[2]);


            plcConfig.PosX = 0;// (decimal?)status.XPosition;
            plcConfig.PosY = 0;//  (decimal?)status.YPosition;
            plcConfig.PosZ = 0;//  (decimal?)status.ZPosition;

            plcConfig.ForksStat = (registers[3]==1);
            plcConfig.ServoStat = (registers[6]==1);

            plcConfig.BoxWeightA = (decimal?)(Convert.ToInt32((short)registers[42]) / 100.00);  // FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[40], registers[41]);   
            plcConfig.BoxWeightB = (decimal?)(Convert.ToInt32((short)registers[44]) / 100.00); //FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[42], registers[43]);
            EnsurePlcStatusDecimals(plcConfig);

            plcConfig.IsInboundCompleted = (registers[20]==1);
            plcConfig.IsOutboundCompleted = (registers[21]==1);
            plcConfig.IsRelocationCompleted = (registers[22]==1);
            plcConfig.IsEmergencyStop = (registers[27]==1);

            plcConfig.IsResetCompleted = (registers[30]==1);
            plcConfig.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();
            return status;
        }


        public async Task<PlcConfiguration> UpdatePlcStatusAsync(string plcId)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // 获取PLC配置
            //var plcConfig = await GetPlcConfiguration(plcId);
            var plcConfig = await _context.PlcConfigurations.FirstOrDefaultAsync(p => p.PlcId == plcId);

            if (plcConfig == null)
            {
                throw new ArgumentException($"PLC with ID {plcId} not found");
            }

            var client = await _connectionManager.GetConnection(plcId);


            ushort startAddress = 23000;
            ushort numberOfPoints = 45;

            try
            {
                var response = client.ReadHoldingRegisters(startAddress, numberOfPoints);

                // 添加数据验证
                if (!response.IsSuccess || response.Data == null || response.Data.Length < numberOfPoints)
                {
                    _logger.LogWarning(
                        "读取PLC状态失败或数据不完整: PLC={PlcId}, Success={Success}, DataLength={DataLength}, Message={Message}",
                        plcId,
                        response.IsSuccess,
                        response.Data?.Length ?? 0,
                        response.Message);
                    plcConfig.LastConnectionStatus = "Disconnected";
                    plcConfig.LastErrorMessage = response.IsSuccess ? "数据不完整" : response.Message;
                    plcConfig.LastTestTime = DateTime.Now;
                    await _context.SaveChangesAsync();
                    return plcConfig;
                }

                ushort[] registers = response.Data;
                var previousOperationResult = plcConfig.OperationResult;
                var previousForksStat = plcConfig.ForksStat;
                var previousServoStat = plcConfig.ServoStat;

                // 添加数组长度检查
                if (registers.Length >= 16) // 确保有足够的数据读取位置信息
                {
                    ushort[] posx = new ushort[2];
                    Array.Copy(registers, 30, posx, 0, 2);

                    ushort[] posy = new ushort[2];
                    Array.Copy(registers, 32, posy, 0, 2);

                    ushort[] posz = new ushort[2];
                    Array.Copy(registers, 34, posz, 0, 2);

                    plcConfig.PosX = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(
                        posx[1], posx[0]);
                    plcConfig.PosY = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(
                        posy[1], posy[0]);
                    plcConfig.PosZ = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(
                        posz[1], posz[0]);
                }

                // 更新其他状态字段（添加类似的安全检查）
                plcConfig.OperationResult = registers.Length > 2 ? Convert.ToInt32(registers[2]) : 0;
                plcConfig.ForksStat = registers.Length > 3 ? registers[3] == 1 : false;
                plcConfig.ServoStat = registers.Length > 6 ? registers[6] == 1 : false;

                if (registers.Length > 42)
                {
                    plcConfig.BoxWeightA = Convert.ToInt32((short)registers[42]) / 100.00M;
                }
                if (registers.Length > 44)
                {
                    plcConfig.BoxWeightB = Convert.ToInt32((short)registers[44]) / 100.00M;
                }
                EnsurePlcStatusDecimals(plcConfig);

                // 更新其他状态标志
                plcConfig.IsInboundCompleted = registers.Length > 20 ? registers[20] == 1 : false;
                plcConfig.IsOutboundCompleted = registers.Length > 21 ? registers[21] == 1 : false;
                plcConfig.IsRelocationCompleted = registers.Length > 22 ? registers[22] == 1 : false;
                plcConfig.IsEmergencyStop = registers.Length > 27 ? registers[1] == 4 : false;
                plcConfig.IsResetCompleted = registers.Length > 30 ? registers[30] == 1 : false;

                plcConfig.LastConnectionStatus = "Connected";
                plcConfig.LastErrorMessage = null;
                plcConfig.LastTestTime = DateTime.Now;
                plcConfig.UpdatedAt = DateTime.Now;
                await SyncLoadingPointWeightsAsync(_context, plcConfig);

                if (previousOperationResult != plcConfig.OperationResult ||
                    previousForksStat != plcConfig.ForksStat ||
                    previousServoStat != plcConfig.ServoStat)
                {
                    _logger.LogInformation(
                        "PLC状态变化: PLC={PlcId}, OperationResult {PreviousOperationResult}->{OperationResult}, ForksStat {PreviousForksStat}->{ForksStat}, ServoStat {PreviousServoStat}->{ServoStat}, WeightA={WeightA}, WeightB={WeightB}",
                        plcId,
                        previousOperationResult,
                        plcConfig.OperationResult,
                        previousForksStat,
                        plcConfig.ForksStat,
                        previousServoStat,
                        plcConfig.ServoStat,
                        plcConfig.BoxWeightA,
                        plcConfig.BoxWeightB);
                }

                await _context.SaveChangesAsync();

                return plcConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新PLC状态时出错: PLC={PlcId}", plcId);
                EnsurePlcStatusDecimals(plcConfig);
                plcConfig.LastConnectionStatus = "Disconnected";
                plcConfig.LastErrorMessage = ex.Message;
                plcConfig.LastTestTime = DateTime.Now;
                await _context.SaveChangesAsync();
                return plcConfig;
            }
        }

        //public async Task<PlcConfiguration> UpdatePlcStatusAsync(string plcId)
        //{
        //    // Get PLC configuration from database
        //    var plcConfig = await GetPlcConfiguration(plcId);
        //    if (plcConfig == null)
        //    {
        //        throw new ArgumentException($"PLC with ID {plcId} not found");
        //    }

        //    if (!_plcClients.ContainsKey(plcId))
        //    {
        //        await TestPlcConnection(plcId);
        //    }


        //    ushort startAddress = 3000;
        //    ushort numberOfPoints = 44; // Enough to cover up to 3015

        //    var response = _plcClients[plcId].ReadHoldingRegisters(startAddress, numberOfPoints);
        //    ushort[] registers = response.Data;

        //    ushort[] posx = new ushort[2];
        //    Array.Copy(registers, 10, posx, 0, 2);
        //    ushort[] posy = new ushort[2];
        //    Array.Copy(registers, 12, posy, 0, 2);
        //    ushort[] posz = new ushort[2];
        //    Array.Copy(registers, 14, posz, 0, 2);


        //    plcConfig.OperationResult = Convert.ToInt32(registers[2]);


        //    plcConfig.PosX = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[11], registers[10]); 
        //    plcConfig.PosY = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[13], registers[12]);
        //    plcConfig.PosZ = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[15], registers[14]); 

        //    plcConfig.IsActive = registers[1] == 1;
        //    plcConfig.ForksStat = (registers[3] == 1);
        //    plcConfig.ServoStat = (registers[6] == 1);


        //    plcConfig.BoxWeightA = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[40], registers[41]);
        //    plcConfig.BoxWeightB = FloatToRegisters.ConvertTwoRegistersToFloatDecimal(registers[42], registers[43]);
        //    if (plcConfig.BoxWeightA > 10000)
        //        plcConfig.BoxWeightA = 10000;
        //    if (plcConfig.BoxWeightA < -10)
        //        plcConfig.BoxWeightA = -10000;
        //    if (plcConfig.BoxWeightB > 10000)
        //        plcConfig.BoxWeightB = 10000;
        //    if (plcConfig.BoxWeightB < -10)
        //        plcConfig.BoxWeightB = -10000;

        //    plcConfig.IsInboundCompleted = (registers[20] == 1);
        //    plcConfig.IsOutboundCompleted = (registers[21] == 1);
        //    plcConfig.IsRelocationCompleted = (registers[22] == 1);
        //    plcConfig.IsEmergencyStop = (registers[27] == 1);

        //    plcConfig.IsResetCompleted = (registers[30] == 1);
        //    plcConfig.UpdatedAt = DateTime.Now;

        //    await _context.SaveChangesAsync();
        //    return plcConfig;
        //}

        public async Task<ModbusResponse> SysSettingOperation(string plcId, int maxSpeed, int auxSpeed, decimal forkLength)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var client = await _connectionManager.GetConnection(plcId);

            var plc = await GetPlcConfiguration(plcId);
            if (plc == null)
            {
                return new ModbusResponse
                {
                    IsSuccess = false,
                    Message = $"PLC with ID {plcId} not found"
                };
            }

            try
            {
                // 获取PLC连接

                if (maxSpeed>10000 || maxSpeed<0)
                {
                    return new ModbusResponse
                    {
                        IsSuccess = false,
                        Message = "YZ轴速度要在1~10000间"
                    };
                }

                var registers = new ushort[2];
                registers[0] = (ushort)(maxSpeed & 0xFFFF); // Low word
                registers[1] = (ushort)(maxSpeed >> 16);    // High word
                ModbusResponse response = client.WriteMultipleRegisters(22030, registers);

                var auxregisters = new ushort[2];
                //设置X轴速度
                auxregisters[0] = (ushort)(auxSpeed & 0xFFFF); // Low word
                auxregisters[1] = (ushort)(auxSpeed >> 16);    // High word
                response = client.WriteMultipleRegisters(2032, auxregisters);

                float floatValue = (float)forkLength;


                // 设置货叉伸出长度
                registers = FloatToRegisters.FloatToUInt16Array(floatValue, false);
                response = client.WriteMultipleRegisters(2034, registers);
                response = client.WriteMultipleRegisters(2036, registers);

                plc.MainSpeed = maxSpeed;
                plc.AuxSpeed = auxSpeed;
                plc.ForkLength = (decimal)forkLength;

                await _context.SaveChangesAsync();

                return new ModbusResponse { IsSuccess = true, Message = "设置参数成功" };
            }
            catch (Exception ex)
            {
                _logger.LogError("设置参数失败:" + ex.Message);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// 入库操作
        /// </summary>
        /// <param name="plcId"></param>
        /// <param name="inShelf"></param>
        /// <param name="selectedInPosition"></param>
        /// <param name="loadingPoint"></param>
        /// <returns></returns>
        public async Task<ModbusResponse> InboundOperation(string plcId, int inShelf, int selectedInPosition, int loadingPoint)
        {
            try
            {
                // 1. 检查储位是否为空（用于入库）
                var storageStatus = await _locationCheckService.GetStorageLocationStatus(plcId, inShelf, selectedInPosition);
                if (storageStatus != 0) // 0表示空
                {
                    string statusMessage = storageStatus switch
                    {
                        1 => "有货",
                        2 => "停用",
                        -1 => "不存在",
                        _ => "未知状态"
                    };
                    _logger.LogWarning($"入库操作失败: 储位 {plcId}-{inShelf}-{selectedInPosition} 状态为 {statusMessage}");
                    return new ModbusResponse { IsSuccess = false, Message = $"指定储位{statusMessage}，不能执行入库" };
                }

                // 2. 检查装载点是否有货（需要从装载点取货入库）
                var isloadingPointEmpty = await _locationCheckService.IsLoadingPointEmpty(plcId, loadingPoint);
                if (isloadingPointEmpty)
                {
                    _logger.LogWarning($"入库操作失败: 装载点 {plcId}-{loadingPoint} 为空");
                    return new ModbusResponse { IsSuccess = false, Message = "装载点没有物品，不能执行入库" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("入库操作失败:" + ex.Message);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }


            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Create operation log entry
            var logEntry = new LocationOperationLog
            {
                CreateTime = DateTime.Now,
                PLCID = plcId,
                InboundShelf = inShelf,
                InboundPosition = selectedInPosition,
                LoadingPoint = loadingPoint,
                OperationType = 0, // 0: Inbound
                OperationResult = 1 // Initially set to 0 (failure)
            };

            try
            {
                var existingPlc = await UpdatePlcStatusAsync(plcId);
                if (existingPlc == null)
                {
                    throw new ArgumentException($"PLC with ID {plcId} not found");
                }
                await this.ReadPlcStatusAsync(plcId);

                if(existingPlc.OperationResult!=0)
                {
                    return await RegisterBackgroundInboundTask(
                        plcId,
                        inShelf,
                        selectedInPosition,
                        loadingPoint,
                        existingPlc.OperationResult.GetValueOrDefault());
                }

                if( existingPlc.ForksStat== true)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "当前货叉有货，不能执行任务。" };
                }

                /* 根据重量判断有没有物品
                if ((double)(existingPlc.BoxWeightA.GetValueOrDefault(50)) <= 0.4 && loadingPoint == 0 || (double)(existingPlc.BoxWeightB.GetValueOrDefault(50)) <= 0.4 && loadingPoint == 1)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "出货点当前没有物品。" };
                }
                */

                existingPlc.InboundPosition = selectedInPosition;
                existingPlc.LoadingPoint = loadingPoint;
                existingPlc.OperationType = 0;
                existingPlc.OperationResult = 1;
                existingPlc.InboundShelf = inShelf;
                existingPlc.LoadingPoint = loadingPoint;
                existingPlc.IsInboundCompleted = false;
                existingPlc.TaskCreateTime = DateTime.Now;
                existingPlc.IsActive = true;
                existingPlc.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();

                //if (!this.IsEmpty(plcId))
                //{
                //    await SaveOperationLog(logEntry);
                //    return new ModbusResponse { IsSuccess = false, Message = "货叉有货，不能操作。" };
                //}

                // Get PLC connection
                var client = await _connectionManager.GetConnection(plcId);
                ;

                // Set operation parameters
                await WriteRegister(client, 22000, 0);
                await WriteRegister(client, 22002, (ushort)inShelf);
                await WriteRegister(client, 22003, (ushort)selectedInPosition);
                await WriteRegister(client, 22001, (ushort)loadingPoint);

                // Trigger operation
                await WriteRegister(client, 22008, 1);
                await Task.Delay(1500);
                await WriteRegister(client, 22008, 0);

                // Update operation result
                if (loadingPoint == 0)
                    logEntry.WeightBegin = existingPlc.BoxWeightA;
                else
                    logEntry.WeightBegin = existingPlc.BoxWeightB;

                logEntry.OperationResult = 1; // Success
                logEntry.EndTime = DateTime.Now;

                //await _context.SaveChangesAsync();
                await SaveOperationLog(logEntry);

                // Update shelf status
                //await UpdateShelfStatus(plcId, inShelf, selectedInPosition, 1); // 1: Has goods

                return new ModbusResponse { IsSuccess = true, Message = "入库操作成功" };
            }
            catch (Exception ex)
            {
                _logger.LogError("入库操作失败:" + ex.Message);
                logEntry.EndTime = DateTime.Now;
                await SaveOperationLog(logEntry);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }

        // 在类级别添加一个字典来管理后台任务
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _backgroundTasks = new();


        // 添加一个方法来取消指定任务（如果需要的话）
        public bool CancelBackgroundTask(string taskKey)
        {
            if (_backgroundTasks.TryRemove(taskKey, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                    _logger.LogInformation($"已取消后台任务: {taskKey}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"取消后台任务 {taskKey} 时出错: {ex.Message}");
                }
            }
            return false;
        }

        public bool CancelBackgroundTask(string plcId, int inShelf, int selectedInPosition)
        {
            var taskKey = $"{plcId}-{inShelf}-{selectedInPosition}";
            if (_backgroundTasks.TryRemove(taskKey, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _logger.LogInformation($"已取消后台任务: {taskKey}");
                return true;
            }
            return false;
        }

        public async Task<bool> CheckBackgroundTaskStatus(string taskKey)
        {
            if (_backgroundTasks.ContainsKey(taskKey))
            {
                var cts = _backgroundTasks[taskKey];
                if (cts != null && !cts.IsCancellationRequested)
                {
                    // 可以添加更多的状态检查逻辑
                    return true;
                }
            }
            return false;
        }

        public List<string> GetActiveBackgroundTasks()
        {
            return _backgroundTasks.Keys.ToList();
        }


        private async Task MonitorAndExecuteInboundWithServicesAsync(
            string plcId,
            int inShelf,
            int selectedInPosition,
            int loadingPoint,
            string billID,
            string billNo,
            int itm,
            double qty,
            string rem,
            long logEntryId,
            ILogger logger,
            IPlcConnectionManager connectionManager,
            ILocationCheckService locationCheckService,
            IBillOperationService billOperationService,
            ApplicationDbContext dbContext,
            CancellationToken cancellationToken)
        {
            var taskKey = $"{plcId}-{inShelf}-{selectedInPosition}";

            logger.LogInformation($"[{taskKey}] 开始监控PLC {plcId} 状态，等待空闲执行入库任务");

            int maxAttempts = 750; // 最多尝试5分钟（750 * 0.4秒）
            int attemptCount = 0;
            bool operationExecuted = false;

            while (attemptCount < maxAttempts &&
                   !cancellationToken.IsCancellationRequested &&
                   !operationExecuted)
            {
                try
                {
                    attemptCount++;

                    // 添加状态显示
                    logger.LogInformation($"[{taskKey}] 第{attemptCount}/{maxAttempts}次检测 - 等待PLC空闲...");

                    // 延迟0.4秒，避免漏掉PLC短暂状态
                    await Task.Delay(400, cancellationToken);

                    // 检查是否已取消
                    if (cancellationToken.IsCancellationRequested)
                    {
                        logger.LogInformation($"[{taskKey}] 任务被取消");
                        break;
                    }

                    // 通过ID重新加载日志记录
                    var logEntry = await dbContext.LocationOperationLogs
                        .FirstOrDefaultAsync(l => l.Id == logEntryId);

                    if (logEntry == null)
                    {
                        logger.LogError($"[{taskKey}] 未找到日志记录 ID: {logEntryId}");
                        break;
                    }

                    // 检查任务是否已经被执行
                    if (logEntry.OperationResult == 3)
                    {
                        logger.LogInformation($"[{taskKey}] 入库任务已执行成功，停止监控");
                        operationExecuted = true;
                        break;
                    }

                    // 获取PLC配置
                    var plc = await dbContext.PlcConfigurations
                        .FirstOrDefaultAsync(p => p.PlcId == plcId);

                    if (plc == null)
                    {
                        logger.LogError($"[{taskKey}] PLC {plcId} 配置不存在");
                        logEntry.OperationResult = -1;
                        logEntry.EndTime = DateTime.Now;
                        await dbContext.SaveChangesAsync();
                        break;
                    }

                    // 读取PLC状态
                    try
                    {
                        var client = await connectionManager.GetConnection(plcId);
                        if (client != null && client.IsConnected)
                        {
                            var statusResponse = client.ReadHoldingRegisters(23000, 10);

                            if (statusResponse.IsSuccess && statusResponse.Data != null && statusResponse.Data.Length >= 10)
                            {
                                plc.OperationResult = statusResponse.Data[2];
                                plc.ForksStat = statusResponse.Data[3] == 1;
                                plc.LastTestTime = DateTime.Now;
                                plc.UpdatedAt = DateTime.Now;

                                await dbContext.SaveChangesAsync();

                                logger.LogDebug($"[{taskKey}] PLC状态更新: OperationResult={plc.OperationResult}, ForksStat={plc.ForksStat}");
                            }
                            else
                            {
                                logger.LogWarning($"[{taskKey}] 读取PLC状态数据无效");
                            }
                        }
                        else
                        {
                            logger.LogWarning($"[{taskKey}] PLC连接不可用");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[{taskKey}] 读取PLC状态失败: {ex.Message}");
                    }

                    // 检查PLC是否空闲
                    if (plc.OperationResult == 0)
                    {
                        logger.LogInformation($"[{taskKey}] PLC {plcId} 在第{attemptCount}次检测时变为空闲，开始执行入库指令");

                        // 再次检查条件
                        if (plc.ForksStat == true)
                        {
                            logger.LogWarning($"[{taskKey}] PLC {plcId} 货叉有货，不能执行任务，等待...");
                            continue;
                        }

                        // 重新检查储位状态
                        try
                        {
                            var storageStatus = await locationCheckService.GetStorageLocationStatus(plcId, inShelf, selectedInPosition);
                            if (storageStatus != 0)
                            {
                                string statusMessage = storageStatus switch
                                {
                                    1 => "有货",
                                    2 => "停用",
                                    -1 => "不存在",
                                    _ => "未知状态"
                                };

                                logger.LogWarning($"[{taskKey}] 储位状态已改变 ({statusMessage})，无法执行入库");
                                logEntry.OperationResult = -1;
                                logEntry.EndTime = DateTime.Now;
                                await dbContext.SaveChangesAsync();
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"[{taskKey}] 检查储位状态失败: {ex.Message}");
                        }

                        // 执行入库操作
                        try
                        {
                            logger.LogInformation($"[{taskKey}] 开始执行入库操作...");

                            // 更新PLC配置
                            plc.InboundPosition = selectedInPosition;
                            plc.LoadingPoint = loadingPoint;
                            plc.OperationType = 0;
                            plc.OperationResult = 1;
                            plc.InboundShelf = inShelf;
                            plc.IsInboundCompleted = false;
                            plc.TaskCreateTime = DateTime.Now;
                            plc.IsActive = true;
                            plc.UpdatedAt = DateTime.Now;

                            await dbContext.SaveChangesAsync();

                            logger.LogInformation($"[{taskKey}] 开始发送入库指令到PLC");

                            // 获取PLC连接并发送指令
                            var client = await connectionManager.GetConnection(plcId);

                            if (client == null || !client.IsConnected)
                            {
                                logger.LogError($"[{taskKey}] PLC连接失败，无法发送指令");
                                throw new Exception("PLC连接失败");
                            }

                            logger.LogInformation($"[{taskKey}] 发送入库指令: 22000=0, 22002={inShelf}, 22003={selectedInPosition}, 22001={loadingPoint}");

                            // 设置操作参数
                            await WriteRegister(client, 22000, 0);
                            await WriteRegister(client, 22002, (ushort)inShelf);
                            await WriteRegister(client, 22003, (ushort)selectedInPosition);
                            await WriteRegister(client, 22001, (ushort)loadingPoint);

                            // 触发操作
                            logger.LogInformation($"[{taskKey}] 触发入库操作: 22008=1");
                            await WriteRegister(client, 22008, 1);

                            // 短暂延迟确保PLC接收到信号
                            await Task.Delay(100, cancellationToken);

                            // 发送脉冲信号（置1后置0）
                            await Task.Delay(2400, cancellationToken);
                            await WriteRegister(client, 22008, 0);

                            logger.LogInformation($"[{taskKey}] 入库操作已触发完成");

                            // 更新操作结果
                            logEntry.OperationResult = 1; // Success
                            logEntry.EndTime = DateTime.Now;
                            await dbContext.SaveChangesAsync();

                            // 更新PLC的操作ID
                            plc.OperationID = logEntryId;
                            await dbContext.SaveChangesAsync();

                            // 更新业务明细
                            if (!string.IsNullOrEmpty(billID) && !string.IsNullOrEmpty(billNo) && itm > 0)
                            {
                                await billOperationService.UpdateBillOperation(
                                    billID, billNo, itm, 0, 1, loadingPoint, plcId);
                            }

                            logger.LogInformation($"[{taskKey}] PLC {plcId} 入库任务执行成功 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            operationExecuted = true;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"[{taskKey}] 发送入库指令失败: {ex.Message}\n{ex.StackTrace}");

                            // 更新操作结果为失败
                            logEntry.OperationResult = -1;
                            logEntry.EndTime = DateTime.Now;
                            await dbContext.SaveChangesAsync();

                            // 更新业务明细（失败状态）
                            if (!string.IsNullOrEmpty(billID) && !string.IsNullOrEmpty(billNo) && itm > 0)
                            {
                                await billOperationService.UpdateBillOperation(
                                    billID, billNo, itm, 0, 0, loadingPoint, plcId);
                            }

                            // 这里可以选择继续监控或直接退出
                            // break; // 如果失败直接退出监控
                        }
                    }
                    else
                    {
                        logger.LogDebug($"[{taskKey}] PLC {plcId} 状态码={plc.OperationResult}，继续等待");
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation($"[{taskKey}] PLC {plcId} 入库监控任务被取消");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{taskKey}] 监控PLC {plcId} 状态时出错: {ex.Message}\n{ex.StackTrace}");
                    // 继续尝试，不中断循环
                }
            }

            if (!operationExecuted)
            {
                if (attemptCount >= maxAttempts)
                {
                    logger.LogWarning($"[{taskKey}] PLC {plcId} 在{maxAttempts}次检测后仍未空闲或执行失败，取消入库任务");

                    // 更新业务明细（失败状态）
                    if (!string.IsNullOrEmpty(billID) && !string.IsNullOrEmpty(billNo) && itm > 0)
                    {
                        await billOperationService.UpdateBillOperation(
                            billID, billNo, itm, 0, 0, loadingPoint, plcId);
                    }
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation($"[{taskKey}] PLC {plcId} 入库监控任务被主动取消");
                }
            }
        }


        // 后台监控和执行的私有方法
        private async Task MonitorAndExecuteInboundAsync(string plcId, int inShelf, int selectedInPosition,
            int loadingPoint, string billID, string billNo, int itm, double qty, string rem,
            long logEntryId, CancellationToken cancellationToken)
        {
            var taskKey = $"{plcId}-{inShelf}-{selectedInPosition}";

            _logger.LogInformation($"[{taskKey}] 开始监控PLC {plcId} 状态，等待空闲执行入库任务 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            int maxAttempts = 150;
            int attemptCount = 0;

            while (attemptCount < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    attemptCount++;

                    _logger.LogDebug($"[{taskKey}] 第{attemptCount}次检测PLC状态... - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                    // 延迟0.4秒，避免漏掉PLC短暂状态
                    await Task.Delay(400, cancellationToken);

                    _logger.LogDebug($"[{taskKey}] 开始读取PLC状态...");


                    // 关键修改：使用 IServiceScopeFactory 创建新的作用域
                    using var scope = _serviceScopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // 通过ID重新加载日志记录
                    var logEntry = await context.LocationOperationLogs
                        .FirstOrDefaultAsync(l => l.Id == logEntryId);

                    if (logEntry == null)
                    {
                        _logger.LogError($"[{taskKey}] 未找到日志记录 ID: {logEntryId}");
                        break;
                    }

                    // 更新PLC状态
                    var existingPlc = await UpdatePlcStatusAsync(plcId);
                    if (existingPlc == null)
                    {
                        _logger.LogError($"[{taskKey}] PLC {plcId} 配置不存在");
                        logEntry.EndTime = DateTime.Now;
                        await context.SaveChangesAsync();
                        break;
                    }

                    await this.ReadPlcStatusAsync(plcId);

                    _logger.LogDebug($"[{taskKey}] 第{attemptCount}次检测结果 - OperationResult: {existingPlc.OperationResult}, ForksStat: {existingPlc.ForksStat}");

                    // 检查PLC是否空闲
                    if (existingPlc.OperationResult == 0)
                    {
                        _logger.LogInformation($"[{taskKey}] PLC {plcId} 在第{attemptCount}次检测时变为空闲，开始执行入库指令 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                        // 再次检查条件
                        if (existingPlc.ForksStat == true)
                        {
                            _logger.LogWarning($"[{taskKey}] PLC {plcId} 货叉有货，不能执行任务");
                            logEntry.EndTime = DateTime.Now;
                            await context.SaveChangesAsync();
                            break;
                        }

                        // 重新获取PLC配置（使用新的DbContext）
                        var plcConfig = await context.PlcConfigurations
                            .FirstOrDefaultAsync(p => p.PlcId == plcId);

                        if (plcConfig == null)
                        {
                            _logger.LogError($"[{taskKey}] PLC {plcId} 配置不存在");
                            logEntry.EndTime = DateTime.Now;
                            await context.SaveChangesAsync();
                            break;
                        }

                        // 更新PLC配置
                        plcConfig.InboundPosition = selectedInPosition;
                        plcConfig.LoadingPoint = loadingPoint;
                        plcConfig.OperationType = 0;
                        plcConfig.OperationResult = 1;
                        plcConfig.InboundShelf = inShelf;
                        plcConfig.IsInboundCompleted = false;
                        plcConfig.TaskCreateTime = DateTime.Now;
                        plcConfig.IsActive = true;
                        plcConfig.UpdatedAt = DateTime.Now;

                        await context.SaveChangesAsync();

                        _logger.LogInformation($"[{taskKey}] 开始发送入库指令到PLC");

                        try
                        {
                            // 获取PLC连接并发送指令
                            var connectionManager = scope.ServiceProvider.GetRequiredService<IPlcConnectionManager>();
                            var client = await connectionManager.GetConnection(plcId);

                            _logger.LogInformation($"[{taskKey}] 发送入库指令: 22000=0, 22002={inShelf}, 22003={selectedInPosition}, 22001={loadingPoint}");

                            // 设置操作参数
                            await WriteRegister(client, 22000, 0);
                            await WriteRegister(client, 22002, (ushort)inShelf);
                            await WriteRegister(client, 22003, (ushort)selectedInPosition);
                            await WriteRegister(client, 22001, (ushort)loadingPoint);

                            // 触发操作
                            _logger.LogInformation($"[{taskKey}] 触发入库操作: 22008=1");
                            await WriteRegister(client, 22008, 1);
                            await Task.Delay(2500, cancellationToken);
                            await WriteRegister(client, 22008, 0);
                            _logger.LogInformation($"[{taskKey}] 入库操作已触发完成");

                            // 更新操作结果
                            logEntry.OperationResult = 1; // Success
                            logEntry.EndTime = DateTime.Now;

                            await context.SaveChangesAsync();

                            _logger.LogInformation($"[{taskKey}] PLC {plcId} 入库任务执行成功 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[{taskKey}] 发送入库指令失败: {ex.Message}");
                            logEntry.EndTime = DateTime.Now;
                            await context.SaveChangesAsync();
                            throw;
                        }
                    }
                    else
                    {
                        _logger.LogDebug($"[{taskKey}] PLC {plcId} 第{attemptCount}次检测: 状态码={existingPlc.OperationResult}，继续等待");
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation($"[{taskKey}] PLC {plcId} 入库监控任务被取消 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{taskKey}] 监控PLC {plcId} 状态时出错: {ex.Message}\n{ex.StackTrace}");
                    // 继续尝试，不中断循环
                }
            }

            if (attemptCount >= maxAttempts)
            {
                _logger.LogWarning($"[{taskKey}] PLC {plcId} 在150次检测后仍未空闲，取消入库任务 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                // 记录失败日志
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var logEntry = await context.LocationOperationLogs
                    .FirstOrDefaultAsync(l => l.Id == logEntryId);

                if (logEntry != null)
                {
                    logEntry.OperationResult = -1;
                    logEntry.EndTime = DateTime.Now;
                    await context.SaveChangesAsync();
                }
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation($"[{taskKey}] PLC {plcId} 入库监控任务被主动取消 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
        }

        //private async Task MonitorAndExecuteInboundAsync(string plcId, int inShelf, int selectedInPosition,
        //    int loadingPoint, string billID, string billNo, int itm, double qty, string rem,
        //    LocationOperationLog logEntry, CancellationToken cancellationToken)
        //{
        //    _logger.LogInformation($"开始监控PLC {plcId} 状态，等待空闲执行入库任务");

        //    int maxAttempts = 60;
        //    int attemptCount = 0;

        //    while (attemptCount < maxAttempts && !cancellationToken.IsCancellationRequested)
        //    {
        //        try
        //        {
        //            attemptCount++;

        //            // 延迟1秒
        //            await Task.Delay(1000, cancellationToken);

        //            // 更新PLC状态
        //            var existingPlc = await UpdatePlcStatusAsync(plcId);
        //            if (existingPlc == null)
        //            {
        //                _logger.LogError($"PLC {plcId} 配置不存在");
        //                break;
        //            }

        //            await this.ReadPlcStatusAsync(plcId);

        //            // 检查PLC是否空闲
        //            if (existingPlc.OperationResult == 0)
        //            {
        //                _logger.LogInformation($"PLC {plcId} 在第{attemptCount}次检测时变为空闲，开始执行入库指令");

        //                // 再次检查条件
        //                if (existingPlc.ForksStat == true)
        //                {
        //                    _logger.LogWarning($"PLC {plcId} 货叉有货，不能执行任务");
        //                    break;
        //                }

        //                if ((double)(existingPlc.BoxWeightA.GetValueOrDefault(50)) <= 1 && loadingPoint == 0 ||
        //                    (double)(existingPlc.BoxWeightB.GetValueOrDefault(50)) <= 1 && loadingPoint == 1)
        //                {
        //                    _logger.LogWarning($"PLC {plcId} 装载点没有物品");
        //                    break;
        //                }

        //                // 执行入库操作
        //                using var scope = _serviceProvider.CreateScope();
        //                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        //                // 更新PLC配置
        //                existingPlc.InboundPosition = selectedInPosition;
        //                existingPlc.LoadingPoint = loadingPoint;
        //                existingPlc.OperationType = 0;
        //                existingPlc.OperationResult = 0;
        //                existingPlc.InboundShelf = inShelf;
        //                existingPlc.IsInboundCompleted = false;
        //                existingPlc.TaskCreateTime = DateTime.Now;
        //                existingPlc.IsActive = true;
        //                existingPlc.UpdatedAt = DateTime.Now;

        //                await context.SaveChangesAsync();

        //                // 获取PLC连接并发送指令
        //                using var client = await _connectionManager.GetConnection(plcId);

        //                // 设置操作参数
        //                await WriteRegister(client, 22000, 0);
        //                await WriteRegister(client, 22002, (ushort)inShelf);
        //                await WriteRegister(client, 22003, (ushort)selectedInPosition);
        //                await WriteRegister(client, 22001, (ushort)loadingPoint);

        //                // 触发操作
        //                await WriteRegister(client, 22008, 1);
        //                await Task.Delay(2500, cancellationToken);
        //                await WriteRegister(client, 22008, 0);

        //                // 更新操作结果
        //                logEntry.OperationResult = 1; // Success
        //                logEntry.EndTime = DateTime.Now;

        //                await SaveOperationLog(logEntry);

        //                _logger.LogInformation($"PLC {plcId} 入库任务执行成功");
        //                return;
        //            }
        //            else
        //            {
        //                _logger.LogDebug($"PLC {plcId} 第{attemptCount}次检测: 状态码={existingPlc.OperationResult}，继续等待");
        //            }
        //        }
        //        catch (OperationCanceledException)
        //        {
        //            _logger.LogInformation($"PLC {plcId} 入库监控任务被取消");
        //            break;
        //        }
        //        catch (Exception ex)
        //        {
        //            _logger.LogError($"监控PLC {plcId} 状态时出错: {ex.Message}");
        //            // 继续尝试，不中断循环
        //        }
        //    }

        //    if (attemptCount >= maxAttempts)
        //    {
        //        _logger.LogWarning($"PLC {plcId} 在60次检测后仍未空闲，取消入库任务");

        //        // 记录失败日志
        //        logEntry.OperationResult = 0;
        //        logEntry.EndTime = DateTime.Now;
        //        await SaveOperationLog(logEntry);
        //    }
        //    else if (cancellationToken.IsCancellationRequested)
        //    {
        //        _logger.LogInformation($"PLC {plcId} 入库监控任务被主动取消");
        //    }
        //}
        //private readonly IDocumentUpLoadService _upLoadService;

        public async Task<ModbusResponse> InboundOperation(string plcId, int inShelf,
            int selectedInPosition, int loadingPoint, string billID,
            string billNo, int itm, double qty, string rem)
        {
            _logger.LogInformation($"开始入库操作: PLC={plcId}, 货架={inShelf}, 位置={selectedInPosition}, 装载点={loadingPoint}");

            try
            {
                // 1. 检查储位是否为空（用于入库）
                var storageStatus = await _locationCheckService.GetStorageLocationStatus(plcId, inShelf, selectedInPosition);
                if (storageStatus != 0) // 0表示空
                {
                    string statusMessage = storageStatus switch
                    {
                        1 => "有货",
                        2 => "停用",
                        -1 => "不存在",
                        _ => "未知状态"
                    };
                    _logger.LogWarning($"入库操作失败: 储位 {plcId}-{inShelf}-{selectedInPosition} 状态为 {statusMessage}");
                    return new ModbusResponse { IsSuccess = false, Message = $"指定储位{statusMessage}，不能执行入库" };
                }

                // 2. 检查装载点是否有货（需要从装载点取货入库）
                var isloadingPointEmpty = await _locationCheckService.IsLoadingPointEmpty(plcId, loadingPoint);
                if (isloadingPointEmpty)
                {
                    _logger.LogWarning($"入库操作失败: 装载点 {plcId}-{loadingPoint} 为空");
                    return new ModbusResponse { IsSuccess = false, Message = "装载点没有物品，不能执行入库" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("入库操作失败:" + ex.Message);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();
            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Create operation log entry
            var logEntry = new LocationOperationLog
            {
                CreateTime = DateTime.Now,
                PLCID = plcId,
                InboundShelf = inShelf,
                InboundPosition = selectedInPosition,
                LoadingPoint = loadingPoint,
                OperationType = 0, // 0: Inbound
                OperationResult = 1 // Initially set to 0 (failure)
            };

            try
            {
                _logger.LogDebug("即将访问PLC连接状态");
                var existingPlc = await UpdatePlcStatusAsync(plcId);
                if (existingPlc == null)
                {
                    throw new ArgumentException($"PLC with ID {plcId} not found");
                }

                await this.ReadPlcStatusAsync(plcId);
                _logger.LogInformation($"PLC {plcId} 当前状态 - OperationResult: {existingPlc.OperationResult}, ForksStat: {existingPlc.ForksStat}");

                // 如果PLC当前任务未完成，保存后台上架任务
                if (existingPlc.OperationResult != 0)
                {
                    return await RegisterBackgroundInboundTask(
                        plcId,
                        inShelf,
                        selectedInPosition,
                        loadingPoint,
                        existingPlc.OperationResult.GetValueOrDefault());
                }

                // 如果货叉有货，不能执行任务
                if (existingPlc.ForksStat == true)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "当前货叉有货，不能执行任务。" };
                }

                // 根据重量判断有没有物品
                if ((double)(existingPlc.BoxWeightA.GetValueOrDefault(50)) <= 1 && loadingPoint == 0 ||
                    (double)(existingPlc.BoxWeightB.GetValueOrDefault(50)) <= 1 && loadingPoint == 1)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "装载点当前没有物品。" };
                }

                // 更新PLC配置
                existingPlc.InboundPosition = selectedInPosition;
                existingPlc.LoadingPoint = loadingPoint;
                existingPlc.OperationType = 0;
                existingPlc.OperationResult = 1;
                existingPlc.InboundShelf = inShelf;
                existingPlc.IsInboundCompleted = false;
                existingPlc.TaskCreateTime = DateTime.Now;
                existingPlc.IsActive = true;
                existingPlc.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();

                // 获取PLC连接并发送指令
                var client = await _connectionManager.GetConnection(plcId);

                // 设置操作参数
                await WriteRegister(client, 22000, 0);
                await WriteRegister(client, 22002, (ushort)inShelf);
                await WriteRegister(client, 22003, (ushort)selectedInPosition);
                await WriteRegister(client, 22001, (ushort)loadingPoint);

                // 触发操作
                await WriteRegister(client, 22008, 1);
                await Task.Delay(2500);
                await WriteRegister(client, 22008, 0);

                // 更新操作结果
                logEntry.OperationResult = 1; // Success
                await SaveOperationLog(logEntry);

                return new ModbusResponse { IsSuccess = true, Message = "入库操作成功" };
            }
            catch (Exception ex)
            {
                _logger.LogError("入库操作失败:" + ex.Message);
                logEntry.EndTime = DateTime.Now;
                await SaveOperationLog(logEntry);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }


        private async Task MonitorAndExecuteInboundWithServices(
            string plcId,
            int inShelf,
            int selectedInPosition,
            int loadingPoint,
            string billID,
            string billNo,
            int itm,
            double qty,
            string rem,
            long logEntryId,
            ILogger logger,
            IPlcConnectionManager connectionManager,
            ILocationCheckService locationCheckService,
            IBillOperationService billOperationService,
            CancellationToken cancellationToken)
        {
            var taskKey = $"{plcId}-{inShelf}-{selectedInPosition}";

            logger.LogInformation($"[{taskKey}] 开始监控PLC {plcId} 状态，等待空闲执行入库任务");

            int maxAttempts = 750; // 最多尝试5分钟（750 * 0.4秒）
            int attemptCount = 0;
            bool operationExecuted = false;

            while (attemptCount < maxAttempts &&
                   !cancellationToken.IsCancellationRequested &&
                   !operationExecuted)
            {
                try
                {
                    attemptCount++;
                    logger.LogDebug($"[{taskKey}] 第{attemptCount}次检测PLC状态...");

                    // 延迟1秒
                    await Task.Delay(400, cancellationToken);

                    // 使用新的作用域
                    using var scope = _serviceScopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // 通过ID重新加载日志记录
                    var logEntry = await context.LocationOperationLogs
                        .FirstOrDefaultAsync(l => l.Id == logEntryId);

                    if (logEntry == null)
                    {
                        logger.LogError($"[{taskKey}] 未找到日志记录 ID: {logEntryId}");
                        break;
                    }

                    // 检查任务是否已经被执行
                    if (logEntry.OperationResult == 1)
                    {
                        logger.LogInformation($"[{taskKey}] 入库任务已执行成功");
                        operationExecuted = true;
                        break;
                    }

                    // 更新PLC状态
                    var plc = await context.PlcConfigurations
                        .FirstOrDefaultAsync(p => p.PlcId == plcId);

                    if (plc == null)
                    {
                        logger.LogError($"[{taskKey}] PLC {plcId} 配置不存在");
                        logEntry.OperationResult = -1;
                        logEntry.EndTime = DateTime.Now;
                        await context.SaveChangesAsync();
                        break;
                    }

                    // 读取PLC状态
                    try
                    {
                        var client = await connectionManager.GetConnection(plcId);
                        var statusResponse = client.ReadHoldingRegisters(23000, 10);

                        if (statusResponse.IsSuccess && statusResponse.Data != null)
                        {
                            plc.OperationResult = statusResponse.Data[2];
                            plc.ForksStat = statusResponse.Data[3] == 1;
                            plc.LastTestTime = DateTime.Now;

                            await context.SaveChangesAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[{taskKey}] 读取PLC状态失败: {ex.Message}");
                    }

                    logger.LogDebug($"[{taskKey}] 检测结果 - OperationResult: {plc.OperationResult}, ForksStat: {plc.ForksStat}");

                    // 检查PLC是否空闲
                    if (plc.OperationResult == 0)
                    {
                        logger.LogInformation($"[{taskKey}] PLC {plcId} 在第{attemptCount}次检测时变为空闲，开始执行入库指令");

                        // 再次检查条件
                        if (plc.ForksStat == true)
                        {
                            logger.LogWarning($"[{taskKey}] PLC {plcId} 货叉有货，不能执行任务");
                            continue;
                        }

                        // 重新检查储位状态
                        try
                        {
                            var storageStatus = await locationCheckService.GetStorageLocationStatus(plcId, inShelf, selectedInPosition);
                            if (storageStatus != 0)
                            {
                                logger.LogWarning($"[{taskKey}] 储位状态已改变，无法执行入库");
                                logEntry.OperationResult =-1;
                                logEntry.EndTime = DateTime.Now;
                                await context.SaveChangesAsync();
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"[{taskKey}] 检查储位状态失败: {ex.Message}");
                        }

                        // 执行入库操作
                        try
                        {
                            // 更新PLC配置
                            plc.InboundPosition = selectedInPosition;
                            plc.LoadingPoint = loadingPoint;
                            plc.OperationType = 0;
                            plc.OperationResult = -1;
                            plc.InboundShelf = inShelf;
                            plc.IsInboundCompleted = false;
                            plc.TaskCreateTime = DateTime.Now;
                            plc.IsActive = true;
                            plc.UpdatedAt = DateTime.Now;

                            await context.SaveChangesAsync();

                            logger.LogInformation($"[{taskKey}] 开始发送入库指令到PLC");

                            // 获取PLC连接并发送指令
                            var client = await connectionManager.GetConnection(plcId);

                            logger.LogInformation($"[{taskKey}] 发送入库指令: 22000=0, 22002={inShelf}, 22003={selectedInPosition}, 22001={loadingPoint}");

                            // 设置操作参数
                            await WriteRegister(client, 22000, 0);
                            await WriteRegister(client, 22002, (ushort)inShelf);
                            await WriteRegister(client, 22003, (ushort)selectedInPosition);
                            await WriteRegister(client, 22001, (ushort)loadingPoint);

                            // 触发操作
                            logger.LogInformation($"[{taskKey}] 触发入库操作: 22008=1");
                            await WriteRegister(client, 22008, 1);
                            await Task.Delay(2500, cancellationToken);
                            await WriteRegister(client, 22008, 0);
                            logger.LogInformation($"[{taskKey}] 入库操作已触发完成");

                            // 更新操作结果
                            logEntry.OperationResult = 1; // Success
                            logEntry.EndTime = DateTime.Now;
                            await context.SaveChangesAsync();

                            // 更新业务明细
                            if (!string.IsNullOrEmpty(billID) && !string.IsNullOrEmpty(billNo) && itm > 0)
                            {
                                await billOperationService.UpdateBillOperation(
                                    billID, billNo, itm, 0, 1, loadingPoint, plcId);
                            }

                            logger.LogInformation($"[{taskKey}] PLC {plcId} 入库任务执行成功");
                            operationExecuted = true;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"[{taskKey}] 发送入库指令失败: {ex.Message}");

                            // 更新业务明细（失败状态）
                            if (!string.IsNullOrEmpty(billID) && !string.IsNullOrEmpty(billNo) && itm > 0)
                            {
                                await billOperationService.UpdateBillOperation(
                                    billID, billNo, itm, 0, 0, loadingPoint, plcId);
                            }

                            logEntry.OperationResult = -1;
                            logEntry.EndTime = DateTime.Now;
                            await context.SaveChangesAsync();
                        }
                    }
                    else
                    {
                        logger.LogDebug($"[{taskKey}] PLC {plcId} 第{attemptCount}次检测: 状态码={plc.OperationResult}，继续等待");
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation($"[{taskKey}] PLC {plcId} 入库监控任务被取消");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError($"[{taskKey}] 监控PLC {plcId} 状态时出错: {ex.Message}");
                    // 继续尝试，不中断循环
                }
            }

            if (!operationExecuted)
            {
                if (attemptCount >= maxAttempts)
                {
                    logger.LogWarning($"[{taskKey}] PLC {plcId} 在{maxAttempts}次检测后仍未空闲，取消入库任务");

                    // 记录失败日志
                    using var scope = _serviceScopeFactory.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var logEntry = await context.LocationOperationLogs
                        .FirstOrDefaultAsync(l => l.Id == logEntryId);

                    if (logEntry != null)
                    {
                        logEntry.OperationResult = -1;
                        logEntry.EndTime = DateTime.Now;
                        await context.SaveChangesAsync();
                    }

                    // 更新业务明细（失败状态）
                    if (!string.IsNullOrEmpty(billID) && !string.IsNullOrEmpty(billNo) && itm > 0)
                    {
                        await billOperationService.UpdateBillOperation(
                            billID, billNo, itm, 0, 0, loadingPoint, plcId);
                    }
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogInformation($"[{taskKey}] PLC {plcId} 入库监控任务被主动取消");
                }
            }
        }


        private bool IsEmpty(string plcId)
        {
            IModbusClient client = _connectionManager.GetConnection(plcId).GetAwaiter().GetResult();

            var response = client.ReadHoldingRegisters(23003, 1);
            ushort[] bEmpty = response.Data;

            if (bEmpty[0] == 0)
            {
                return true;
            }
            else
            {
                return false; 
            }
        }

        /// <summary>
        /// /移库操作
        /// </summary>
        /// <param name="plcId"></param>
        /// <param name="outShelf"></param>
        /// <param name="selectedOutPosition"></param>
        /// <param name="inShelf"></param>
        /// <param name="selectedInPosition"></param>
        /// <returns></returns>
        public async Task<ModbusResponse> TransferOperation(string plcId, int outShelf, int selectedOutPosition, int inShelf, int selectedInPosition)
        {
            try
            {
                // 1. 检查入库储位是否为空 
                var storageStatus = await _locationCheckService.GetStorageLocationStatus(plcId, inShelf, selectedInPosition);
                if (storageStatus != 0) // 0表示空
                {
                    string statusMessage = storageStatus switch
                    {
                        1 => "有货",
                        2 => "停用",
                        -1 => "不存在",
                        _ => "未知状态"
                    };
                    _logger.LogWarning($"移库操作失败: 入库储位 {plcId}-{inShelf}-{selectedInPosition} 状态为 {statusMessage}");
                    return new ModbusResponse { IsSuccess = false, Message = $"指定储位{statusMessage}，不能执行入库" };
                }

                // 1. 检查出库储位是否为空 
                storageStatus = await _locationCheckService.GetStorageLocationStatus(plcId, inShelf, selectedOutPosition);
                if (storageStatus == 0) // 0表示空
                {
                    string statusMessage = storageStatus switch
                    {
                        1 => "有货",
                        2 => "停用",
                        -1 => "不存在",
                        _ => "未知状态"
                    };
                    _logger.LogWarning($"移库操作失败: 储位 {plcId}-{inShelf}-{selectedInPosition} 状态为 {statusMessage}");
                    return new ModbusResponse { IsSuccess = false, Message = $"指定储位{statusMessage}，不能执行出库" };
                }

            }
            catch (Exception ex)
            {
                _logger.LogError("入库操作失败:" + ex.Message);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Create operation log entry
            var logEntry = new LocationOperationLog
            {
                CreateTime = DateTime.Now,
                PLCID = plcId,
                OutboundShelf = outShelf,
                OutboundPosition = selectedOutPosition,
                InboundShelf = inShelf,
                InboundPosition = selectedInPosition,
                OperationType = 2, // 2: Transfer
                OperationResult = 4 // Initially set to 0 (failure)
            };

            try
            {

                //if (!this.IsEmpty(plcId))
                //{
                //    await SaveOperationLog(logEntry);
                //    return new ModbusResponse { IsSuccess = false, Message = "货叉有货，不能操作。" };
                //}

                var existingPlc = await UpdatePlcStatusAsync(plcId);

                if (existingPlc == null)
                {
                    throw new ArgumentException($"PLC with ID {plcId} not found");
                }
                await this.ReadPlcStatusAsync(plcId);

                if (existingPlc.OperationResult != 0)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "设备当前任务未完成，不能执行新任务。" };
                }

                if (existingPlc.ForksStat == true)
                {
                    return new ModbusResponse { IsSuccess = false, Message = "当前货叉有货，不能执行任务。" };
                }

                existingPlc.InboundPosition = selectedInPosition;
                //existingPlc.LoadingPoint = loadingPoint;
                existingPlc.OperationType = 2;
                existingPlc.OperationResult = 0;
                existingPlc.InboundShelf = inShelf;
                //existingPlc.LoadingPoint = loadingPoint;
                existingPlc.IsInboundCompleted = false;
                existingPlc.IsRelocationCompleted = false;
                existingPlc.OutboundPosition = selectedOutPosition;
                existingPlc.OutboundShelf = outShelf;
                existingPlc.TaskCreateTime = DateTime.Now;
                existingPlc.IsActive = true;
                existingPlc.UpdatedAt = DateTime.Now;

                await _context.SaveChangesAsync();

                // Get PLC connection
                var client = await _connectionManager.GetConnection(plcId);


                // Set operation parameters
                await WriteRegister(client, 22000, 2);
                await WriteRegister(client, 22004, (ushort)outShelf);
                await WriteRegister(client, 22005, (ushort)inShelf);
                await WriteRegister(client, 22006, (ushort)selectedOutPosition);
                await WriteRegister(client, 22007, (ushort)selectedInPosition);

                // Trigger operation
                await WriteRegister(client, 22010, 1);
                await Task.Delay(2500);
                await WriteRegister(client, 22010, 0);

                // Update operation result
                logEntry.OperationResult = 4; // Success
                logEntry.EndTime = DateTime.Now;
                //await _context.SaveChangesAsync();
                await SaveOperationLog(logEntry);

                // Update shelf statuses
                //await UpdateShelfStatus(plcId, outShelf, selectedOutPosition, 0); // 0: No goods (outbound)
                //await UpdateShelfStatus(plcId, inShelf, selectedInPosition, 1); // 1: Has goods (inbound)

                return new ModbusResponse { IsSuccess = true, Message = "移库操作成功" };
            }
            catch (Exception ex)
            {
                _logger.LogError("移库操作失败:" + ex.Message);
                logEntry.EndTime = DateTime.Now;
                await SaveOperationLog(logEntry);
                return new ModbusResponse { IsSuccess = false, Message = ex.Message };
            }
        }

        /// <summary>
        /// Saves operation log to database
        /// </summary>
        private async Task<ModbusResponse> RegisterBackgroundInboundTask(
            string plcId,
            int shelf,
            int position,
            int loadingPoint,
            int currentOperationResult)
        {
            try
            {
                _logger.LogInformation(
                    "PLC {PlcId} 当前任务未完成(OperationResult={OperationResult})，登记后台上架任务: Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                    plcId,
                    currentOperationResult,
                    shelf,
                    position,
                    loadingPoint);

                var connectionString = _configuration.GetConnectionString("StoreHouseConnection");
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return new ModbusResponse
                    {
                        IsSuccess = false,
                        Message = "数据库连接字符串 StoreHouseConnection 未配置"
                    };
                }

                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand("StoreHouse.dbo.[SDL_PreUPLoadTray]", connection)
                {
                    CommandType = CommandType.StoredProcedure,
                    CommandTimeout = 10
                };

                command.Parameters.Add(new SqlParameter("@PlcID", SqlDbType.VarChar, 10) { Value = plcId });
                command.Parameters.Add(new SqlParameter("@Shelf", SqlDbType.Int) { Value = shelf });
                command.Parameters.Add(new SqlParameter("@Position", SqlDbType.Int) { Value = position });
                command.Parameters.Add(new SqlParameter("@LoadingPoint", SqlDbType.Int) { Value = loadingPoint });

                var resultFlagParam = new SqlParameter("@ResultFlag", SqlDbType.Int)
                {
                    Direction = ParameterDirection.Output
                };
                command.Parameters.Add(resultFlagParam);

                await command.ExecuteNonQueryAsync();

                var resultFlag = resultFlagParam.Value != DBNull.Value
                    ? Convert.ToInt32(resultFlagParam.Value)
                    : 0;

                var message = GetPreUploadTrayMessage(resultFlag);
                if (resultFlag > 0)
                {
                    _logger.LogInformation(
                        "后台上架任务登记成功: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}, ResultFlag={ResultFlag}",
                        plcId,
                        shelf,
                        position,
                        loadingPoint,
                        resultFlag);

                    return new ModbusResponse
                    {
                        IsSuccess = true,
                        Message = $"PLC当前繁忙，已登记后台上架任务。{message}"
                    };
                }

                _logger.LogWarning(
                    "后台上架任务登记失败: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}, ResultFlag={ResultFlag}, Message={Message}",
                    plcId,
                    shelf,
                    position,
                    loadingPoint,
                    resultFlag,
                    message);

                return new ModbusResponse
                {
                    IsSuccess = false,
                    Message = $"PLC当前繁忙，后台上架任务登记失败。{message}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "登记后台上架任务异常: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                    plcId,
                    shelf,
                    position,
                    loadingPoint);

                return new ModbusResponse
                {
                    IsSuccess = false,
                    Message = $"PLC当前繁忙，登记后台上架任务异常：{ex.Message}"
                };
            }
        }

        private static string GetPreUploadTrayMessage(int resultFlag)
        {
            return resultFlag switch
            {
                > 0 => "登记成功",
                -1 => "要入库的货位当前已有货",
                -2 => "入库货位信息有误",
                -3 => "当前装载点箱码与入库位置箱码不同",
                0 => "没有更新任何记录",
                _ => $"未知返回码：{resultFlag}"
            };
        }

        private async Task SaveOperationLog(LocationOperationLog logEntry)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                _context.LocationOperationLogs.Add(logEntry);
                await _context.SaveChangesAsync();

                // 更新 PLC 的 OperationID
                var plc = await _context.PlcConfigurations.FirstOrDefaultAsync(p => p.PlcId == logEntry.PLCID);
                if (plc != null)
                {
                    plc.OperationID = logEntry.Id;
                    await _context.SaveChangesAsync();
                }

            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to save operation log: {ex.Message}");
            }
        }

        private static void EnsurePlcWeights(PlcConfiguration plcConfig)
        {
            plcConfig.BoxWeightA ??= 0;
            plcConfig.BoxWeightB ??= 0;
        }

        private static void EnsurePlcStatusDecimals(PlcConfiguration plcConfig)
        {
            EnsurePlcWeights(plcConfig);
            plcConfig.BoxWeightA = EnsureDbDecimalRange(plcConfig.BoxWeightA, 99999999.99M);
            plcConfig.BoxWeightB = EnsureDbDecimalRange(plcConfig.BoxWeightB, 99999999.99M);
            plcConfig.PosX = EnsureDbDecimalRange(plcConfig.PosX, 9999999.999M);
            plcConfig.PosY = EnsureDbDecimalRange(plcConfig.PosY, 9999999.999M);
            plcConfig.PosZ = EnsureDbDecimalRange(plcConfig.PosZ, 9999999.999M);
        }

        private static decimal? EnsureDbDecimalRange(decimal? value, decimal maxAbsValue)
        {
            if (!value.HasValue)
            {
                return value;
            }

            return Math.Abs(value.Value) <= maxAbsValue ? value : 0;
        }

        private static Task SyncLoadingPointWeightsAsync(ApplicationDbContext context, PlcConfiguration plcConfig)
        {
            return context.Database.ExecuteSqlRawAsync(@"
                UPDATE LoadingPoint_Status
                   SET [Weight] = {1}
                 WHERE PLCID = {0}
                   AND PLCLocationCode = 0;

                UPDATE LoadingPoint_Status
                   SET [Weight] = {2}
                 WHERE PLCID = {0}
                   AND PLCLocationCode = 1;",
                plcConfig.PlcId,
                plcConfig.BoxWeightA ?? 0,
                plcConfig.BoxWeightB ?? 0);
        }

        /// <summary>
        /// Updates shelf status in LocationManagement table
        /// </summary>
        private async Task UpdateShelfStatus(string plcId, int shelf, int position, int status)
        {

            ApplicationDbContext _context;
            using var scope = _serviceProvider.CreateScope();

            _context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            try
            {
                var location = await _context.LocationManagements
                    .FirstOrDefaultAsync(l => l.PLCID == plcId &&
                                            l.Shelf == shelf &&
                                            l.Position == position);

                if (location != null)
                {
                    location.ShelfStatus = status;
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to update shelf status: {ex.Message}");
            }
        }


        private async Task WriteRegister(IModbusClient client, ushort address, ushort value)
        {
            
            var result = client.WriteSingleRegister(address, value, 2);
            if (!result.IsSuccess)
            {
                throw new Exception($"写入寄存器{address}失败: {result.Message}");
            }
            await Task.CompletedTask;
        }



        //private async void HeartbeatCallback(object state)
        //{
        //    if (_disposed) return;

        //    try
        //    {
        //        // 获取活跃PLC列表（使用缓存，避免频繁查询数据库）
        //        var activePlcIds = await GetActivePlcIdsWithCache();

        //        foreach (var plcId in activePlcIds)
        //        {
        //            try
        //            {
        //                // 只处理真正需要心跳的PLC
        //                var client = await _connectionManager.GetConnection(plcId);
        //                if (client != null && client.IsConnected)
        //                {
        //                    var result = client.WriteSingleRegister(22027,
        //                        (ushort)(DateTime.Now.Second % 2 == 0 ? 1 : 0));

        //                    if (result.IsSuccess)
        //                    {
        //                        _logger.LogDebug($"心跳写入成功: {plcId}");
        //                    }
        //                    else
        //                    {
        //                        _logger.LogWarning($"心跳写入失败: {plcId} - {result.Message}");
        //                    }
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                // 记录错误但不中断其他PLC的心跳
        //                _logger.LogError(ex, $"PLC {plcId} 心跳处理异常");

        //                // 对于连接失败的PLC，暂时从缓存中移除，避免持续尝试
        //                if (ex.Message.Contains("连接尝试失败") || ex.Message.Contains("没有正确答复"))
        //                {
        //                    _logger.LogWarning($"PLC {plcId} 连接持续失败，暂时跳过");
        //                    _cachedActivePlcIds.Remove(plcId);
        //                }
        //            }

        //            await Task.Delay(50); // 添加延迟避免同时处理
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "心跳定时器执行异常"+ex.Message);
        //    }
        //}


        //private async Task TryReconnectIfNeeded(string plcId)
        //{
        //    if (_disposed) return;

        //    // 检查是否需要重连（至少间隔30秒）
        //    if (_lastReconnectAttempts.TryGetValue(plcId, out var lastAttempt) &&
        //        (DateTime.Now - lastAttempt).TotalSeconds < 30)
        //    {
        //        return;
        //    }

        //    _lastReconnectAttempts[plcId] = DateTime.Now;

        //    try
        //    {
        //        using (var scope = _serviceProvider.CreateScope())
        //        {
        //            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        //            var plcConfig = await context.PlcConfigurations
        //                .FirstOrDefaultAsync(p => p.PlcId == plcId);

        //            if (plcConfig == null || !plcConfig.IsActive)
        //            {
        //                return;
        //            }

        //            _logger.LogInformation($"尝试重新连接PLC: {plcId}");

        //            // 清理旧连接
        //            if (_plcClients.TryRemove(plcId, out var oldClient))
        //            {
        //                oldClient?.Dispose();
        //            }

        //            // 创建新连接
        //            var newClient = new ModbusClient(plcConfig.IpAddress, plcConfig.Port, plcConfig.SlaveId);
        //            var result = newClient.Connect();

        //            if (result.IsSuccess)
        //            {
        //                _plcClients[plcId] = newClient;
        //                _lastHeartbeatTimes[plcId] = DateTime.Now;
        //                _logger.LogInformation($"重新连接PLC成功: {plcId}");
        //            }
        //            else
        //            {
        //                newClient.Dispose();
        //                _logger.LogWarning($"重新连接PLC失败: {plcId} - {result.Message}");
        //            }
        //        }
        //    }
        //    catch (ObjectDisposedException)
        //    {
        //        // 服务已释放，忽略
        //        _disposed = true;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"重新连接PLC {plcId} 失败");
        //    }
        //}

        //private async Task TryReconnectIfNeeded(string plcId, bool forceReconnect = false)
        //{
        //    if (_disposed) return;

        //    // 检查是否需要重连
        //    if (!forceReconnect && _lastReconnectAttempts.TryGetValue(plcId, out var lastAttempt) &&
        //        (DateTime.Now - lastAttempt).TotalSeconds < 30)
        //    {
        //        return;
        //    }

        //    _lastReconnectAttempts[plcId] = DateTime.Now;

        //    try
        //    {
        //        using (var scope = _serviceProvider.CreateScope())
        //        {
        //            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        //            var plcConfig = await context.PlcConfigurations
        //                .FirstOrDefaultAsync(p => p.PlcId == plcId);

        //            if (plcConfig == null || !plcConfig.IsActive)
        //            {
        //                _logger.LogInformation($"PLC {plcId} 未配置或未激活，跳过重连");
        //                return;
        //            }

        //            _logger.LogInformation($"尝试重新连接PLC: {plcId}");

        //            // 清理旧连接
        //            if (_plcClients.TryRemove(plcId, out var oldClient))
        //            {
        //                try
        //                {
        //                    oldClient?.Dispose();
        //                }
        //                catch (Exception ex)
        //                {
        //                    _logger.LogWarning(ex, $"清理PLC {plcId} 旧连接时出错");
        //                }
        //            }

        //            // 创建新连接
        //            var newClient = new ModbusClient(plcConfig.IpAddress, plcConfig.Port, plcConfig.SlaveId);
        //            var result = newClient.Connect();

        //            if (result.IsSuccess)
        //            {
        //                // 测试新连接是否真正有效
        //                bool isReallyConnected = newClient.TestConnection();

        //                if (isReallyConnected)
        //                {
        //                    _plcClients[plcId] = newClient;
        //                    _lastHeartbeatTimes[plcId] = DateTime.Now;
        //                    _logger.LogInformation($"重新连接PLC成功: {plcId}");

        //                    // 更新数据库状态
        //                    plcConfig.LastConnectionStatus = "Connected";
        //                    plcConfig.LastErrorMessage = null;
        //                    plcConfig.LastTestTime = DateTime.Now;
        //                    await context.SaveChangesAsync();
        //                }
        //                else
        //                {
        //                    newClient.Dispose();
        //                    _logger.LogWarning($"重新连接PLC {plcId} 测试失败");

        //                    // 更新数据库状态
        //                    plcConfig.LastConnectionStatus = "Disconnected";
        //                    plcConfig.LastErrorMessage = "连接测试失败";
        //                    plcConfig.LastTestTime = DateTime.Now;
        //                    await context.SaveChangesAsync();
        //                }
        //            }
        //            else
        //            {
        //                newClient.Dispose();
        //                _logger.LogWarning($"重新连接PLC失败: {plcId} - {result.Message}");

        //                // 更新数据库状态
        //                plcConfig.LastConnectionStatus = "Disconnected";
        //                plcConfig.LastErrorMessage = result.Message;
        //                plcConfig.LastTestTime = DateTime.Now;
        //                await context.SaveChangesAsync();
        //            }
        //        }
        //    }
        //    catch (ObjectDisposedException)
        //    {
        //        _disposed = true;
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, $"重新连接PLC {plcId} 失败");
        //    }
        //}


        private async Task TryReconnectPlc(string plcId)
        {
            try
            {
                _logger.LogInformation($"尝试重新连接PLC: {plcId}");
                await TestPlcConnection(plcId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"重新连接PLC {plcId} 失败");
            }
        }

        // 在 Dispose 方法中释放定时器

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            // 取消所有后台任务
            foreach (var taskKey in _backgroundTasks.Keys.ToList())
            {
                if (_backgroundTasks.TryRemove(taskKey, out var cts))
                {
                    try
                    {
                        cts?.Cancel();
                        cts?.Dispose();
                        _logger.LogInformation($"已取消后台任务: {taskKey}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"取消后台任务 {taskKey} 时出错: {ex.Message}");
                    }
                }
            }

            _logger.LogInformation("PlcService 已释放");
        }

    }


    public class FloatToRegisters
    {
        /// <summary>
        /// 将 float 拆分为两个 ushort（适用于16位寄存器）
        /// </summary>
        /// <param name="value">浮点数</param>
        /// <param name="isBigEndian">是否使用大端序（默认小端序）</param>
        public static ushort[] FloatToUInt16Array(float value, bool isBigEndian = false)
        {
            // 1. 获取 float 的 4字节 byte[]
            byte[] bytes = BitConverter.GetBytes(value);

            // 2. 处理字节序（硬件通常为大端序）
            if (BitConverter.IsLittleEndian && isBigEndian)
            {
                Array.Reverse(bytes);
            }

            // 3. 转换为两个 ushort
            ushort[] registers = new ushort[2];
            registers[0] = BitConverter.ToUInt16(bytes, 0); // 低16位
            registers[1] = BitConverter.ToUInt16(bytes, 2); // 高16位

            return registers;
        }

        /// <summary>
        /// 将两个 ushort 合并为 float
        /// </summary>
        public static float UInt16ArrayToFloat(ushort[] registers, bool isBigEndian = false)
        {
            byte[] bytes = new byte[4];
            Buffer.BlockCopy(registers, 0, bytes, 0, 4);

            // 处理字节序
            if (BitConverter.IsLittleEndian && isBigEndian)
            {
                Array.Reverse(bytes);
            }

            return BitConverter.ToSingle(bytes, 0);
        }

        public static decimal ConvertTwoRegistersToFloatDecimal(ushort highRegister, ushort lowRegister)
        {
            // 将两个16位寄存器组合成一个32位浮点数
            byte[] bytes = new byte[4];
            BitConverter.GetBytes(highRegister).CopyTo(bytes, 0);
            BitConverter.GetBytes(lowRegister).CopyTo(bytes, 2);

            float floatValue = BitConverter.ToSingle(bytes, 0);

            if (float.IsNaN(floatValue) || float.IsInfinity(floatValue) || Math.Abs(floatValue) > 9999999.999f)
            {
                return 0;
            }

            // 转换为decimal类型
            return (decimal)floatValue;
        }

        //private async Task<int> SaveOperationLogAndUpdatePlc(ApplicationDbContext context, LocationOperationLog logEntry, string plcId)
        //{
        //    // 保存操作日志
        //    context.LocationOperationLogs.Add(logEntry);
        //    await context.SaveChangesAsync(); // 获取 ID

        //    // 更新 PLC 的 OperationID
        //    var plc = await context.PlcConfigurations.FirstOrDefaultAsync(p => p.PlcId == plcId);
        //    if (plc != null)
        //    {
        //        plc.OperationID = logEntry.Id;
        //        await context.SaveChangesAsync();
        //    }

        //    return logEntry.Id;
        //}

    }

    public class AlarmDecoder
    {
        // 报警位定义
        private static readonly Dictionary<int, string> AlarmDefinitions = new Dictionary<int, string>
        {
            {1,"入库时原货架尚无货"},
            {2,"入库时未能取到货"},
            {3,"入库时目标货架已有货"},
            {4,"入库时未能成功放货"},
            {5,"出库时原货架尚无货"},
            {6,"出库时未能取到货"},
            {7,"出库时目标货架已有货"},
            {8,"出库时未能成功放货"},
            {9,"盘库时原货架尚无货"},
            {10,"盘库时未能取到货"},
            {11,"盘库时目标货架已有货"},
            {12,"盘库时未能成功放货"},
            {13,"已装载货物"},
            {14,"手动中禁止自动"},
            {15,"自动中禁止手动"},
            {16,"需要复位"},
            {17,"碰到前限位"},
            {18,"碰到后限位"},
            {19,"碰到上限位"},
            {20,"碰到下限位"},
            {21,"碰到A限位"},
            {22,"碰到B限位"},
            {23,"货位超范围"},
            {24,"未启动"},
            {25,"箱1过重"},
            {26,"箱2过重"},
            {27,"上位机通讯超时"}
        };

        public static string DecodeAlarmToString(int alarmCode)
        {
            StringBuilder alarmString = new StringBuilder();

            // 检查每一位是否被设置
            for (int i = 1; i <= 32; i++)
            {
                if ((alarmCode & (1 << (i - 1))) != 0)
                {
                    if (AlarmDefinitions.TryGetValue(i, out string alarmDescription))
                    {
                        if (alarmString.Length > 0)
                        {
                            alarmString.Append("：");
                        }
                        alarmString.Append(alarmDescription);
                    }
                }
            }

            return alarmString.Length > 0 ? alarmString.ToString() : "无报警";
        }



    }

}

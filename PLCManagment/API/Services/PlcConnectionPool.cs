// PLCManagement.API/Services/PlcConnectionPool.cs
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;
using PLCManagement.Core.Interfaces;
using PLCManagement.Core.Services;
using System.Collections.Concurrent;

namespace PLCManagement.API.Services
{



    public interface IPlcConnectionPool
    {
        IModbusClient GetConnection(string plcId);
        void ReturnConnection(string plcId, IModbusClient connection);
        Task InitializeAsync();
    }

    public class PlcConnectionPool : IPlcConnectionPool, IDisposable
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<IModbusClient>> _connectionPool;
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<PlcConnectionPool> _logger;
        private readonly IServiceProvider _serviceProvider;
        private const int MaxPoolSize = 15; // 每个PLC的最大连接数

        public PlcConnectionPool(
            ApplicationDbContext dbContext,
            ILogger<PlcConnectionPool> logger,
            IServiceProvider serviceProvider)
        {
            _connectionPool = new ConcurrentDictionary<string, ConcurrentBag<IModbusClient>>();
            _dbContext = dbContext;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public async Task InitializeAsync()
        {
            var activePlcs = await _dbContext.PlcConfigurations
                .Where(p => p.IsActive)
                .ToListAsync();

            foreach (var plc in activePlcs)
            {
                var plcId = NormalizePlcId(plc.PlcId);
                _connectionPool.TryAdd(plcId, new ConcurrentBag<IModbusClient>());
                _logger.LogInformation("初始化PLC连接池: {PlcId}", plcId);
            }
        }

        public IModbusClient GetConnection(string plcId)
        {
            plcId = NormalizePlcId(plcId);
            if (!_connectionPool.TryGetValue(plcId, out var connections))
            {
                var plcConfigForPool = GetPlcConfig(plcId);
                if (plcConfigForPool == null)
                {
                    throw new InvalidOperationException($"PLC {plcId} 配置不存在");
                }

                if (!plcConfigForPool.IsActive)
                {
                    throw new InvalidOperationException($"PLC {plcId} 已配置但未激活");
                }

                connections = _connectionPool.GetOrAdd(plcId, _ => new ConcurrentBag<IModbusClient>());
                _logger.LogInformation("运行时加入PLC连接池: {PlcId}", plcId);
            }

            if (connections.TryTake(out var connection))
            {
                _logger.LogDebug("从连接池获取现有连接: {PlcId}", plcId);
                return connection;
            }

            _logger.LogInformation("创建新PLC连接: {PlcId}", plcId);
            var plcConfig = GetPlcConfig(plcId);
            if (plcConfig == null)
            {
                throw new InvalidOperationException($"PLC {plcId} 配置不存在");
            }
            if (!plcConfig.IsActive)
            {
                throw new InvalidOperationException($"PLC {plcId} 已配置但未激活");
            }

            var newConnection = new ModbusClient(
                plcConfig.IpAddress,
                plcConfig.Port,
                plcConfig.SlaveId,
                plcConfig.RegisterAddrOffset,
                _serviceProvider.GetRequiredService<ILogger<ModbusClient>>(),
                suppressConnectionFailureLogging: true);

            var connectResult = newConnection.Connect();
            if (!connectResult.IsSuccess)
            {
                throw new Exception($"连接PLC {plcId} 失败: {connectResult.Message}");
            }

            return newConnection;
        }

        private PlcConfiguration? GetPlcConfig(string plcId)
        {
            return _dbContext.PlcConfigurations
                .AsNoTracking()
                .FirstOrDefault(p => p.PlcId.Trim() == plcId);
        }

        private static string NormalizePlcId(string plcId)
        {
            if (string.IsNullOrWhiteSpace(plcId))
            {
                throw new InvalidOperationException("PLC编号不能为空");
            }

            return plcId.Trim();
        }

        public void ReturnConnection(string plcId, IModbusClient connection)
        {
            plcId = NormalizePlcId(plcId);
            if (!_connectionPool.TryGetValue(plcId, out var connections))
            {
                connection.Dispose();
                return;
            }

            if (connections.Count < MaxPoolSize && connection.IsConnected)
            {
                _logger.LogDebug("归还连接至连接池: {PlcId}", plcId);
                connections.Add(connection);
            }
            else
            {
                _logger.LogDebug("关闭超额或无效连接: {PlcId}", plcId);
                connection.Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var pool in _connectionPool.Values)
            {
                while (pool.TryTake(out var connection))
                {
                    connection.Dispose();
                }
            }
            _connectionPool.Clear();
        }
    }
}

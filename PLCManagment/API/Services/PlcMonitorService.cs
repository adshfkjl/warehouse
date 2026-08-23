using PLCManagement.API.Interfaces;

namespace PLCManagement.API.Services
{
    // PlcMonitorService.cs
    public class PlcMonitorService : IPlcMonitorService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<PlcMonitorService> _logger;

        public PlcMonitorService(IServiceScopeFactory serviceScopeFactory, ILogger<PlcMonitorService> logger)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        public async Task MonitorAndExecuteInboundAsync(string plcId, int inShelf, int selectedInPosition,
            int loadingPoint, string billID, string billNo, int itm, double qty, string rem,
            long logEntryId, CancellationToken cancellationToken)
        {
            // 在这里实现监控逻辑，每次循环都创建新的作用域
            while (!cancellationToken.IsCancellationRequested)
            {
                using var scope = _serviceScopeFactory.CreateScope();
                // 获取需要的服务并执行逻辑
            }
        }
    }
}

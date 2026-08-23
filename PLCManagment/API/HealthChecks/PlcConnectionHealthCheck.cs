// PLCManagement.API/HealthChecks/PlcConnectionHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PLCManagement.API.Services;

namespace PLCManagement.API.HealthChecks
{
    public class PlcConnectionHealthCheck : IHealthCheck
    {
        private readonly IPlcConnectionPool _connectionPool;

        public PlcConnectionHealthCheck(IPlcConnectionPool connectionPool)
        {
            _connectionPool = connectionPool;
        }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // 这里可以添加更详细的状态检查
                return Task.FromResult(HealthCheckResult.Healthy("PLC连接池运行正常"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("PLC连接池异常", ex));
            }
        }
    }
}
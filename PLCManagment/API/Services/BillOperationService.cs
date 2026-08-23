// Services/BillOperationService.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;

namespace PLCManagement.API.Services
{
    public interface IBillOperationService
    {
        Task UpdateBillOperation(string billId, string billNo, int itm,
            int operationType, int operationResult, int loadingPoint, string plcId);
    }

    public class BillOperationService : IBillOperationService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<BillOperationService> _logger;

        public BillOperationService(IConfiguration configuration, ILogger<BillOperationService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task UpdateBillOperation(string billId, string billNo, int itm,
            int operationType, int operationResult, int loadingPoint, string plcId)
        {
            if (string.IsNullOrEmpty(billId) || string.IsNullOrEmpty(billNo) || itm <= 0)
            {
                _logger.LogWarning("更新业务明细失败: 缺少必要的参数 BillID、BillNO 或 ITM");
                return;
            }

            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand("UPDATE_BillDetail_Operation", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.Parameters.AddWithValue("@BillID", billId);
                    command.Parameters.AddWithValue("@BillNO", billNo);
                    command.Parameters.AddWithValue("@ITM", itm);
                    command.Parameters.AddWithValue("@OperationType", operationType);
                    command.Parameters.AddWithValue("@OperationResult", operationResult);
                    command.Parameters.AddWithValue("@LoadingPoint", loadingPoint);
                    command.Parameters.AddWithValue("@PLCID", plcId);
                    command.Parameters.AddWithValue("@OperationTime", DateTime.Now);

                    try
                    {
                        int affectedRows = await command.ExecuteNonQueryAsync();
                        if (affectedRows > 0)
                        {
                            _logger.LogInformation($"更新业务明细成功: {billId}-{billNo}-{itm}");
                        }
                        else
                        {
                            _logger.LogWarning($"未找到对应的业务明细记录: {billId}-{billNo}-{itm}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"更新业务明细失败: {billId}-{billNo}-{itm}");
                    }
                }
            }
        }
    }
}

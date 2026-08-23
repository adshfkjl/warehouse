// Services/LocationCheckService.cs
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using Microsoft.Data.SqlClient;
using System.Data;

namespace PLCManagement.API.Services
{
    public interface ILocationCheckService
    {
        Task<bool> IsLoadingPointEmpty(string plcId, int loadingPoint);
        Task<bool> IsStorageLocationAvailable(string plcId, int shelf, int position);
        Task<int> GetStorageLocationStatus(string plcId, int shelf, int position);
        Task<bool> UpdateLoadingPointPallet(string plcId, int loadingPoint, string palletNumber);
    }

    public class LocationCheckService : ILocationCheckService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<LocationCheckService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public LocationCheckService(
            IConfiguration configuration,
            ILogger<LocationCheckService> logger,
            IServiceProvider serviceProvider)
        {
            _configuration = configuration;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// 检查装载点是否为空（CurrentPalletNumber 为空）
        /// </summary>
        /// <param name="plcId">PLC编号</param>
        /// <param name="loadingPoint">装载点代码（对应PLCLocationCode）</param>
        /// <returns>true表示空，false表示有货</returns>
        public async Task<bool> IsLoadingPointEmpty(string plcId, int loadingPoint)
        {
            if (string.IsNullOrEmpty(plcId))
                throw new ArgumentException("PLC编号不能为空");

            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                // 使用参数化查询防止SQL注入
                const string sql = @"SELECT CASE WHEN (ISNULL(CurrentPalletNumber,'') = '') and (case when PLCLocationCode=0 then plc.BoxWeightA else plc.BoxWeightB end) <1.5 THEN 1 ELSE 0 END 
                                        FROM LoadingPoint_Status lp inner join PlcConfigurations plc on lp.PlcId=plc.PlcId
                                    WHERE lp.PlcId = @PlcId AND PLCLocationCode =@LoadingPoint ";
//SELECT CASE WHEN (CurrentPalletNumber IS NULL OR CurrentPalletNumber = '') and Weight<2.0 THEN 1 ELSE 0 END 
//                    FROM LoadingPoint_Status 
//                    WHERE PlcId = @PlcId AND PLCLocationCode = @LoadingPoint";

                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@PlcId", plcId);
                    command.Parameters.AddWithValue("@LoadingPoint", loadingPoint);

                    try
                    {
                        var result = await command.ExecuteScalarAsync();
                        return result != null && Convert.ToInt32(result) == 1;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"检查装载点状态失败: PLC={plcId}, LoadingPoint={loadingPoint}");
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// 检查储位是否可用（状态为0-空）
        /// </summary>
        /// <param name="plcId">PLC编号</param>
        /// <param name="shelf">货架号</param>
        /// <param name="position">位置号</param>
        /// <returns>true表示可用，false表示不可用</returns>
        public async Task<bool> IsStorageLocationAvailable(string plcId, int shelf, int position)
        {
            var status = await GetStorageLocationStatus(plcId, shelf, position);
            return status == 0; // 0表示空，可用
        }

        /// <summary>
        /// 获取储位状态
        /// </summary>
        /// <param name="plcId">PLC编号</param>
        /// <param name="shelf">货架号</param>
        /// <param name="position">位置号</param>
        /// <returns>0-空, 1-有货, 2-停用, -1-不存在</returns>
        public async Task<int> GetStorageLocationStatus(string plcId, int shelf, int position)
        {
            if (string.IsNullOrEmpty(plcId))
                throw new ArgumentException("PLC编号不能为空");

            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                const string sql = @"
                    SELECT ShelfStatus 
                    FROM LocationManagements 
                    WHERE PLCID = @PlcId AND Shelf = @Shelf AND Position = @Position";

                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@PlcId", plcId);
                    command.Parameters.AddWithValue("@Shelf", shelf);
                    command.Parameters.AddWithValue("@Position", position);

                    try
                    {
                        var result = await command.ExecuteScalarAsync();
                        return result != null ? Convert.ToInt32(result) : -1; // -1表示记录不存在
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"获取储位状态失败: PLC={plcId}, Shelf={shelf}, Position={position}");
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// 批量检查多个储位状态
        /// </summary>
        public async Task<Dictionary<(string, int, int), int>> GetMultipleStorageLocationStatus(
            List<(string PlcId, int Shelf, int Position)> locations)
        {
            var results = new Dictionary<(string, int, int), int>();

            foreach (var location in locations)
            {
                var status = await GetStorageLocationStatus(location.PlcId, location.Shelf, location.Position);
                results[(location.PlcId, location.Shelf, location.Position)] = status;
            }

            return results;
        }

        /// <summary>
        /// 更新储位状态
        /// </summary>
        public async Task<bool> UpdateStorageLocationStatus(string plcId, int shelf, int position, int newStatus)
        {
            if (string.IsNullOrEmpty(plcId))
                throw new ArgumentException("PLC编号不能为空");

            if (newStatus < 0 || newStatus > 2)
                throw new ArgumentException("状态值必须在0-2之间");

            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                const string sql = @"
                    UPDATE LocationManagements 
                    SET ShelfStatus = @NewStatus 
                    WHERE PLCID = @PlcId AND Shelf = @Shelf AND Position = @Position";

                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@PlcId", plcId);
                    command.Parameters.AddWithValue("@Shelf", shelf);
                    command.Parameters.AddWithValue("@Position", position);
                    command.Parameters.AddWithValue("@NewStatus", newStatus);

                    try
                    {
                        int affectedRows = await command.ExecuteNonQueryAsync();
                        return affectedRows > 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"更新储位状态失败: PLC={plcId}, Shelf={shelf}, Position={position}, Status={newStatus}");
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// 更新装载点托盘编号
        /// </summary>
        public async Task<bool> UpdateLoadingPointPallet(string plcId, int loadingPoint, string palletNumber)
        {
            if (string.IsNullOrEmpty(plcId))
                throw new ArgumentException("PLC编号不能为空");

            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                const string sql = @"
                        UPDATE LoadingPoint_Status 
                        SET CurrentPalletNumber = @PalletNumber 
                        WHERE PlcId = @PlcId AND PLCLocationCode = @LoadingPoint";

                using (var command = new SqlCommand(sql, connection))
                {
                    command.Parameters.AddWithValue("@PlcId", plcId);
                    command.Parameters.AddWithValue("@LoadingPoint", loadingPoint);
                    command.Parameters.AddWithValue("@PalletNumber", string.IsNullOrEmpty(palletNumber) ? DBNull.Value : palletNumber);

                    try
                    {
                        int affectedRows = await command.ExecuteNonQueryAsync();
                        return affectedRows > 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"更新装载点托盘失败: PLC={plcId}, LoadingPoint={loadingPoint}, Pallet={palletNumber}");
                        throw;
                    }
                }
            }
        }
    }
}

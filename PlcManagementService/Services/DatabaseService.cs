using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using PlcManagementService.Models;
using PLCService.Configuration;
using PLCService.Models;

namespace PLCService.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService()
        {
            _connectionString = AppSettings.ConnectionString;
        }

        public IEnumerable<PlcConfiguration> GetActivePlcs()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<PlcConfiguration>(
                    "SELECT * FROM PlcConfigurations WHERE IsActive = 1",
                    commandTimeout: AppSettings.DbCommandTimeout);
            }
        }

        ///根据PlcID获取当前可下架的业务信息,返回0条或者1条记录。
        public IEnumerable<AutoRunBill> GetAutoRunBill(string PlcID)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<AutoRunBill>(
                    "exec SDL_GetNextTask @PlcID", new { PlcID },
                    commandTimeout: AppSettings.DbCommandTimeout);
            }
        }

        public IEnumerable<LoadingPointStatus> GetLoadingPointStatuses(string plcId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                return connection.Query<LoadingPointStatus>(@"
                    SELECT PLCID,
                           PLCLocationCode,
                           CurrentPalletNumber,
                           [Weight]
                    FROM LoadingPoint_Status
                    WHERE PLCID = @PlcId
                      AND PLCLocationCode IN (0, 1)",
                    new { PlcId = plcId },
                    commandTimeout: AppSettings.DbCommandTimeout);
            }
        }

        /// <summary>
        /// 获取单个PLC的配置信息（异步版本）
        /// </summary>
        public async Task<PlcConfiguration> GetPlcConfigurationAsync(string plcId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var query = "SELECT * FROM PlcConfigurations WHERE PlcId = @PlcId";
                return await connection.QueryFirstOrDefaultAsync<PlcConfiguration>(query, new { PlcId = plcId },
                    commandTimeout: AppSettings.DbCommandTimeout);
            }
        }

        //// 可以保留同步版本以供其他用途
        //public PlcConfiguration GetPlcConfiguration(string plcId)
        //{
        //    return GetPlcConfigurationAsync(plcId).GetAwaiter().GetResult();
        //}

        /// <summary>
        /// 更新PLC的OperationID
        /// </summary>
        public void UpdatePlcOperationId(string PlcID, long? operationId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var query = @"UPDATE PlcConfigurations 
                         SET OperationID = @OperationID,
                             UpdatedAt = GETDATE()
                         WHERE PlcID = @PlcID";

                connection.Execute(query, new
                {
                    PlcID = PlcID,
                    OperationID = operationId
                }, commandTimeout: AppSettings.DbCommandTimeout);
                LogService.Error($"保存操作日志ID到PLC {PlcID}: {operationId}");

            }
        }

        public int UpdateLoadingPointPallet(string plcId, int loadingPoint, string palletNumber)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var query = @"
                    UPDATE LoadingPoint_Status
                       SET CurrentPalletNumber = @PalletNumber
                     WHERE PLCID = @PlcId
                       AND PLCLocationCode = @LoadingPoint";

                return connection.Execute(query, new
                {
                    PlcId = plcId,
                    LoadingPoint = loadingPoint,
                    PalletNumber = string.IsNullOrWhiteSpace(palletNumber) ? null : palletNumber
                }, commandTimeout: AppSettings.DbCommandTimeout);
            }
        }

        /// <summary>
        /// 插入操作日志并返回生成的ID
        /// </summary>
        public long InsertLocationOperationLog(LocationOperationLog log)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var query = @"
                INSERT INTO LocationOperationLogs 
                (CreateTime, StartTime, PLCID, OutboundShelf, OutboundPosition, 
                 LoadingPoint, OperationType, OperationResult)
                OUTPUT INSERTED.ID
                VALUES 
                (GETDATE(), GETDATE(), @PLCID, @OutboundShelf, @OutboundPosition,
                 @LoadingPoint, @OperationType, @OperationResult)";

                return connection.ExecuteScalar<long>(query, log, commandTimeout: AppSettings.DbCommandTimeout);
            }
        }

        /// <summary>
        /// 更新操作日志的结束时间和结果
        /// </summary>
        //public void UpdateLocationOperationLog(long id, DateTime endTime, int operationResult)
        //{
        //    using (var connection = new SqlConnection(_connectionString))
        //    {
        //        var query = @"UPDATE LocationOperationLogs 
        //                 SET EndTime = @EndTime,
        //                     OperationResult = @OperationResult
        //                 WHERE ID = @ID";

        //        connection.Execute(query, new
        //        {
        //            ID = id,
        //            EndTime = endTime,
        //            OperationResult = operationResult
        //        });
        //    }
        //}

        public void UpdatePlcStatus(PlcConfiguration plc)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var query = @" exec [SDL_UPD_PlcConfigurations] @ID,
                                  @LastConnectionStatus,
                                     @LastErrorMessage,
                                     @LastTestTime,
                                     @MainSpeed,
                                     @AuxSpeed,
                                     @IsResetCompleted,
                                     @IsInboundCompleted,
                                     @IsOutboundCompleted,
                                     @IsRelocationCompleted,
                                     @IsEmergencyStop,
                                     @BoxWeightA,
                                     @BoxWeightB,
                                     @OperationResult,
                                     @ForksStat,
                                     @PosX,
                                     @PosY,
                                     @PosZ";

                    connection.Execute(query, plc, transaction, commandTimeout: AppSettings.DbCommandTimeout);

                    var loadingPointStatusQuery = @"
                    UPDATE LoadingPoint_Status
                       SET [Weight] = @BoxWeightA
                     WHERE PLCID = @PlcId
                       AND PLCLocationCode = 0;

                    UPDATE LoadingPoint_Status
                       SET [Weight] = @BoxWeightB
                     WHERE PLCID = @PlcId
                       AND PLCLocationCode = 1;";

                    connection.Execute(loadingPointStatusQuery, new
                    {
                        plc.PlcId,
                        plc.BoxWeightA,
                        plc.BoxWeightB
                    }, transaction, commandTimeout: AppSettings.DbCommandTimeout);

                    transaction.Commit();
                }
            }
        }

        public void UpdateConnectionStatusOnly(PlcConfiguration plc)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                if (plc.OperationID != 0)
                {
                    var query = @"UPDATE PlcConfigurations 
                             SET LastConnectionStatus = @LastConnectionStatus,
                                 LastErrorMessage = @LastErrorMessage,
                                 LastTestTime = @LastTestTime,
                                 UpdatedAt = GETDATE(),
                                 OperationID = @OperationID
                             WHERE Id = @Id OR (ISNULL(@Id, 0) = 0 AND PlcId = @PlcId)";

                    connection.Execute(query, new
                    {
                        plc.LastConnectionStatus,
                        plc.LastErrorMessage,
                        plc.LastTestTime,
                        plc.Id,
                        plc.OperationID,
                        plc.PlcId
                    }, commandTimeout: AppSettings.DbCommandTimeout);
                }
                else
                {
                    var query = @"UPDATE PlcConfigurations 
                             SET LastConnectionStatus = @LastConnectionStatus,
                                 LastErrorMessage = @LastErrorMessage,
                                 LastTestTime = @LastTestTime,
                                 UpdatedAt = GETDATE()
                             WHERE Id = @Id OR (ISNULL(@Id, 0) = 0 AND PlcId = @PlcId)";

                    connection.Execute(query, new
                    {
                        plc.LastConnectionStatus,
                        plc.LastErrorMessage,
                        plc.LastTestTime,
                        plc.Id,
                        plc.PlcId
                    }, commandTimeout: AppSettings.DbCommandTimeout);

                }
            }
        }


        /// <summary>
        /// 获取所有PLC的AutoRun状态
        /// </summary>
        public Dictionary<string, bool> GetPlcAutoRunStatus()
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var query = "SELECT PlcId, AutoRun FROM PlcConfigurations WHERE IsActive = 1";
                var results = connection.Query<(string PlcId, bool AutoRun)>(query,
                    commandTimeout: AppSettings.DbCommandTimeout);

                return results.ToDictionary(x => x.PlcId, x => x.AutoRun);
            }
        }

        /// <summary>
        /// 获取单个PLC的配置信息（包括AutoRun）
        /// </summary>
        public PlcConfiguration GetPlcConfiguration(string plcId)
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var query = "SELECT * FROM PlcConfigurations WHERE PlcId = @PlcId";
                return connection.QueryFirstOrDefault<PlcConfiguration>(query, new { PlcId = plcId },
                    commandTimeout: AppSettings.DbCommandTimeout);
            }
        }
    }


}

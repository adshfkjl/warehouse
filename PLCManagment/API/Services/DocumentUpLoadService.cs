using Microsoft.Data.SqlClient;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;
using System.Data;

namespace PLCManagement.API.Services
{
    public class DocumentUpLoadService : IDocumentUpLoadService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DocumentUpLoadService> _logger;
        private readonly IPlcService _plcService;

        public DocumentUpLoadService(
            IConfiguration configuration,
            ILogger<DocumentUpLoadService> logger,
            IPlcService plcService)
        {
            _configuration = configuration;
            _logger = logger;
            _plcService = plcService;
        }

        public async Task<DocumentOperationResult> ProcessDocumentUpLoadAsync(string documentNo, string storageLocation)
        {
            var result = new DocumentOperationResult
            {
                PLCID = "" // 显式初始化 required 属性
            };
            
            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            try
            {
                _logger.LogInformation("开始处理单据上架：单据编号={DocumentNo}, 储位信息={StorageLocation}",
                    documentNo, storageLocation);

                // 调用存储过程
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand("StoreHouse.dbo.[WMS_上架处理]", connection)
                {
                    CommandType = CommandType.StoredProcedure
                };

                // 添加存储过程参数
                command.Parameters.Add(new SqlParameter("@DocumentNo", SqlDbType.VarChar, 50) { Value = documentNo });
                command.Parameters.Add(new SqlParameter("@StorageLocation", SqlDbType.VarChar, 50) { Value = storageLocation });

                // 添加输出参数
                var resultFlagParam = new SqlParameter("@ResultFlag", SqlDbType.Int) { Direction = ParameterDirection.Output };
                var errorMsgParam = new SqlParameter("@ErrorMessage", SqlDbType.VarChar, 500) { Direction = ParameterDirection.Output };
                var plcIdParam = new SqlParameter("@PLCID", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var shelfParam = new SqlParameter("@Shelf", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var locationParam = new SqlParameter("@StorageLocationOut", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var loadPointParam = new SqlParameter("@LoadPoint", SqlDbType.Int) { Direction = ParameterDirection.Output }; // 新增装载点参数
                var docTypeParam = new SqlParameter("@DocumentType", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var itemSeqParam = new SqlParameter("@ItemSequence", SqlDbType.Int) { Direction = ParameterDirection.Output }; // 修正为Int类型

                command.Parameters.AddRange(new[]
                {
                    resultFlagParam, errorMsgParam, plcIdParam, shelfParam, locationParam,
                    docTypeParam, itemSeqParam
                });

                await command.ExecuteNonQueryAsync();

                // 获取输出参数值
                result.ResultFlag = resultFlagParam.Value != DBNull.Value ? Convert.ToInt32(resultFlagParam.Value) : 0;
                result.ErrorMessage = errorMsgParam.Value?.ToString();
                result.PLCID = plcIdParam.Value != DBNull.Value ? plcIdParam.Value.ToString() ?? "" : "";
                result.Shelf = shelfParam.Value != DBNull.Value ? Convert.ToInt32(shelfParam.Value) : 0;
                result.StorageLocation = locationParam.Value != DBNull.Value ? Convert.ToInt32(locationParam.Value) : 0;
                result.LoadPoint = loadPointParam.Value != DBNull.Value ? Convert.ToInt32(loadPointParam.Value) : 0;
                result.DocumentNo = documentNo;
                result.DocumentType = docTypeParam.Value?.ToString();
                result.ItemSequence = itemSeqParam.Value != DBNull.Value ? Convert.ToInt32(itemSeqParam.Value) : 0;

                _logger.LogInformation("上架存储过程执行完成，结果标志={ResultFlag}", result.ResultFlag);

                // 根据结果标志处理
                if (result.ResultFlag == 1)
                {
                    _logger.LogInformation("开始调用PLC入库操作");

                    // 调用PLC入库操作
                    var plcResult = await _plcService.InboundOperation(
                        result.PLCID,
                        result.Shelf,
                        result.StorageLocation,
                        result.LoadPoint,
                        result.DocumentNo,
                        result.DocumentType,
                        result.ItemSequence,
                        result.qty,result.rem);

                    if (result.ResultFlag==0)
                    {
                        _logger.LogWarning("PLC入库操作失败");
                        result.ResultFlag = 0;
                        result.ErrorMessage = "PLC入库操作执行失败";
                    }
                    else
                    {
                        _logger.LogInformation("PLC入库操作成功完成");
                    }
                }
                else
                {
                    _logger.LogWarning("单据上架业务分析失败：{ErrorMessage}", result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "单据上架处理异常：{Message}", ex.Message);
                result.ResultFlag = 0;
                result.ErrorMessage = $"系统异常：{ex.Message}";
            }

            return result;
        }

    }
}
using Microsoft.Data.SqlClient;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;
using System.Data;

namespace PLCManagement.API.Services
{
    public class DocumentDownLoadService : IDocumentDownLoadService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DocumentDownLoadService> _logger;
        private readonly IPlcService _plcService;

        public DocumentDownLoadService(
            IConfiguration configuration,
            ILogger<DocumentDownLoadService> logger,
            IPlcService plcService)
        {
            _configuration = configuration;
            _logger = logger;
            _plcService = plcService;
        }

        public async Task<DocumentOperationResult> ProcessDocumentDownLoadAsync(string documentNo, String loadingPoint)
        {
            var result = new DocumentOperationResult
            {
                PLCID = "" // 显式初始化 required 属性
            };

            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            try
            {
                _logger.LogInformation("开始处理单据下架：单据编号={DocumentNo}, 装载点={LoadingPoint}",
                    documentNo, loadingPoint);

                // 调用存储过程
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand("StoreHouse.dbo.[WMS_下架处理]", connection)
                {
                    CommandType = CommandType.StoredProcedure
                };

                // 添加存储过程参数
                command.Parameters.Add(new SqlParameter("@DocumentNo", SqlDbType.VarChar, 50) { Value = documentNo });
                command.Parameters.Add(new SqlParameter("@LoadingPoint", SqlDbType.VarChar, 50) { Value = loadingPoint });

                // 添加输出参数
                var resultFlagParam = new SqlParameter("@ResultFlag", SqlDbType.Int) { Direction = ParameterDirection.Output };
                var errorMsgParam = new SqlParameter("@ErrorMessage", SqlDbType.VarChar, 500) { Direction = ParameterDirection.Output };
                var plcIdParam = new SqlParameter("@PLCID", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var shelfParam = new SqlParameter("@Shelf", SqlDbType.Int, 50) { Direction = ParameterDirection.Output };
                var locationParam = new SqlParameter("@StorageLocation", SqlDbType.Int, 50) { Direction = ParameterDirection.Output };
                var docTypeParam = new SqlParameter("@DocumentType", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var itemSeqParam = new SqlParameter("@ItemSequence", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };

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
                result.Shelf = Convert.ToInt32(shelfParam.Value);
                result.StorageLocation = Convert.ToInt32(locationParam.Value);
                result.DocumentNo = documentNo;
                result.DocumentType = docTypeParam.Value?.ToString();
                result.ItemSequence = Convert.ToInt32(itemSeqParam.Value);

                _logger.LogInformation("下架存储过程执行完成，结果标志={ResultFlag}", result.ResultFlag);

                // 根据结果标志处理
                if (result.ResultFlag == 1)
                {
                    _logger.LogInformation("开始调用PLC出库操作");

                    // 调用PLC出库操作
                    var plcResult = await _plcService.OutboundOperation(
                        result.PLCID,
                        result.Shelf,
                        result.StorageLocation,
                        result.LoadPoint,
                        result.DocumentNo,
                        result.DocumentType,
                        result.ItemSequence);

                    if (!plcResult.IsSuccess)
                    {
                        _logger.LogWarning("PLC出库操作失败");
                        result.ResultFlag = 0;
                        result.ErrorMessage = "PLC出库操作执行失败";
                    }
                    else
                    {
                        _logger.LogInformation("PLC出库操作成功完成");
                    }
                }
                else
                {
                    _logger.LogWarning("单据下架业务分析失败：{ErrorMessage}", result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "单据下架处理异常：{Message}", ex.Message);
                result.ResultFlag = 0;
                result.ErrorMessage = $"系统异常：{ex.Message}";
            }

            return result;
        }
    }
}
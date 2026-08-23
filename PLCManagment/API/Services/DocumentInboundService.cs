// Services/DocumentInboundService.cs
using Microsoft.Data.SqlClient;
using PLCManagement.API.Interfaces;
using System.Data;

namespace PLCManagement.API.Interfaces
{
    public class DocumentInboundService : IDocumentInboundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DocumentInboundService> _logger;
        private readonly IPlcService _plcService;

        public DocumentInboundService(
            IConfiguration configuration,
            ILogger<DocumentInboundService> logger,
            IPlcService plcService)
        {
            _configuration = configuration;
            _logger = logger;
            _plcService = plcService;
        }

        public async Task<InboundResult> ProcessDocumentInboundAsync(string documentNo, string storageLocation)
        {
            var result = new InboundResult();
            var connectionString = _configuration.GetConnectionString("DB_SDL1");

            try
            {
                _logger.LogInformation("开始处理单据入库：单据编号={DocumentNo}, 储位信息={StorageLocation}",
                    documentNo, storageLocation);

                // 调用存储过程（根据实际存储过程名调整）
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand("DB_SDL1.dbo.[您的入库存储过程名]", connection)
                {
                    CommandType = CommandType.StoredProcedure
                };

                // 添加存储过程参数（根据实际存储过程参数调整）
                command.Parameters.Add(new SqlParameter("@DocumentNo", SqlDbType.VarChar, 50) { Value = documentNo });
                command.Parameters.Add(new SqlParameter("@StorageLocation", SqlDbType.VarChar, 50) { Value = storageLocation });

                // 添加输出参数（根据实际存储过程调整）
                var resultFlagParam = new SqlParameter("@ResultFlag", SqlDbType.Int) { Direction = ParameterDirection.Output };
                var errorMsgParam = new SqlParameter("@ErrorMessage", SqlDbType.VarChar, 500) { Direction = ParameterDirection.Output };
                var plcIdParam = new SqlParameter("@PLCID", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var shelfParam = new SqlParameter("@Shelf", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var locationParam = new SqlParameter("@StorageLocationOut", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var docTypeParam = new SqlParameter("@DocumentType", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var itemSeqParam = new SqlParameter("@ItemSequence", SqlDbType.VarChar, 50) { Direction = ParameterDirection.Output };
                var inOutFlagParam = new SqlParameter("@InOutFlag", SqlDbType.VarChar, 10) { Direction = ParameterDirection.Output };

                command.Parameters.AddRange(new[]
                {
                resultFlagParam, errorMsgParam, plcIdParam, shelfParam, locationParam,
                docTypeParam, itemSeqParam, inOutFlagParam
            });

                await command.ExecuteNonQueryAsync();

                // 获取输出参数值
                result.ResultFlag = resultFlagParam.Value != DBNull.Value ? Convert.ToInt32(resultFlagParam.Value) : 0;
                result.ErrorMessage = errorMsgParam.Value?.ToString();
                result.PLCID = plcIdParam.Value?.ToString();
                result.Shelf = shelfParam.Value?.ToString();
                result.StorageLocation = locationParam.Value?.ToString();
                result.DocumentNo = documentNo;
                result.DocumentType = docTypeParam.Value?.ToString();
                result.ItemSequence = itemSeqParam.Value?.ToString();
                result.InOutFlag = inOutFlagParam.Value?.ToString();

                _logger.LogInformation("存储过程执行完成，结果标志={ResultFlag}, 出入库标志={InOutFlag}",
                    result.ResultFlag, result.InOutFlag);

                // 根据结果标志处理
                if (result.ResultFlag == 1)
                {
                    _logger.LogInformation("单据入库业务分析成功");

                    // 根据出入库标志调用相应的PLC操作
                    if (result.InOutFlag == "I") // 入库操作
                    {
                        _logger.LogInformation("开始调用PLC入库操作");

                        var inboundResult = await _plcService.InboundOperation(
                            result.PLCID,
                            result.Shelf,
                            result.StorageLocation,
                            result.DocumentNo,
                            result.DocumentType,
                            result.ItemSequence);

                        if (!inboundResult)
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
                    else if (result.InOutFlag == "O") // 出库操作
                    {
                        _logger.LogInformation("开始调用PLC出库操作");

                        var outboundResult = await _plcService.OutboundOperation(
                            result.PLCID,
                            result.Shelf,
                            result.StorageLocation,
                            result.DocumentNo,
                            result.DocumentType,
                            result.ItemSequence);

                        if (!outboundResult)
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
                        _logger.LogWarning("未知的出入库标志：{InOutFlag}，跳过PLC调用", result.InOutFlag);
                    }
                }
                else
                {
                    _logger.LogWarning("单据入库业务分析失败：{ErrorMessage}", result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "单据入库处理异常：{Message}", ex.Message);
                result.ResultFlag = 0;
                result.ErrorMessage = $"系统异常：{ex.Message}";
            }

            return result;
        }
    }

}
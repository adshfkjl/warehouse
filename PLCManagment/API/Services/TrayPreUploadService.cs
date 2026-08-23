using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Dtos;
using System.Data;
using System.Text;

namespace PLCManagement.API.Services
{
    public class TrayPreUploadService : ITrayPreUploadService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<TrayPreUploadService> _logger;

        public TrayPreUploadService(
            IConfiguration configuration,
            ILogger<TrayPreUploadService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<TrayPreUploadResponse> RegisterPreUploadTrayAsync(TrayPreUploadRequest request)
        {
            var response = new TrayPreUploadResponse();
            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            try
            {
                _logger.LogInformation("开始登记预上架货框：单据编号={DocumentNo}, PLC={PLCID}, 装载点={LoadingPoint}",
                    request.DocumentNo, request.PLCID, request.LoadingPoint);

                // 调用存储过程 SDL_UPLoadTray
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                using var command = new SqlCommand("StoreHouse.dbo.[SDL_UPLoadTray]", connection)
                {
                    CommandType = CommandType.StoredProcedure
                };

                // 添加存储过程参数
                command.Parameters.Add(new SqlParameter("@Bil_NO", SqlDbType.VarChar, 50) { Value = request.DocumentNo });
                command.Parameters.Add(new SqlParameter("@PLCID", SqlDbType.VarChar, 50) { Value = request.PLCID });
                command.Parameters.Add(new SqlParameter("@LoadingPoint", SqlDbType.Int) { Value = request.LoadingPoint });

                // 添加输出参数
                var resultFlagParam = new SqlParameter("@ResultFlag", SqlDbType.Int) { Direction = ParameterDirection.Output };

                command.Parameters.AddRange(new[]
                {
                    resultFlagParam
                });

                await command.ExecuteNonQueryAsync();

                // 获取输出参数值
                response.ResultFlag = resultFlagParam.Value != DBNull.Value ? Convert.ToInt32(resultFlagParam.Value) : 0;
                response.Success = response.ResultFlag == 1;

                if (response.Success)
                {
                    response.Message = "预上架货框登记成功";
                    _logger.LogInformation("预上架货框登记成功：单据编号={DocumentNo}, PLC={PLCID}, 装载点={LoadPoint}",
                        request.DocumentNo, request.PLCID, request.LoadingPoint);
                }
                else
                {
                    response.Message = $"预上架货框登记失败：{response.ErrorMessage}";
                    _logger.LogWarning("预上架货框登记失败：单据编号={DocumentNo}, PLC={PLCID}, 装载点={LoadPoint}",
                        request.DocumentNo, request.PLCID, request.LoadingPoint);
                }
            }
            catch (SqlException sqlEx)
            {
                _logger.LogError(sqlEx, "数据库异常登记预上架货框：单据编号={DocumentNo}, PLC={PLCID}",
                    request.DocumentNo, request.PLCID);
                response.Success = false;
                response.Message = $"数据库异常：{sqlEx.Message}";
                response.ErrorMessage = sqlEx.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "登记预上架货框异常：单据编号={DocumentNo}, PLC={PLCID}",
                    request.DocumentNo, request.PLCID);
                response.Success = false;
                response.Message = $"系统异常：{ex.Message}";
                response.ErrorMessage = ex.Message;
            }

            return response;
        }

        public async Task<List<TrayPreUploadRecordDto>> GetPreUploadRecordsAsync(string documentNo = null, string plcId = null)
        {
            var records = new List<TrayPreUploadRecordDto>();
            var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

            try
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                // 构建查询SQL
                var sql = new StringBuilder(@"
                    SELECT TOP 100 
                        Id, DocumentNo, PLCID, LoadingPoint, PalletCode, 
                        Remark, CreateTime, Status
                    FROM PreUploadTray_Log
                    WHERE 1=1
                ");

                // 添加筛选条件
                var parameters = new List<SqlParameter>();

                if (!string.IsNullOrEmpty(documentNo))
                {
                    sql.Append(" AND DocumentNo LIKE @DocumentNo");
                    parameters.Add(new SqlParameter("@DocumentNo", SqlDbType.VarChar, 50) { Value = $"%{documentNo}%" });
                }

                if (!string.IsNullOrEmpty(plcId))
                {
                    sql.Append(" AND PLCID = @PLCID");
                    parameters.Add(new SqlParameter("@PLCID", SqlDbType.VarChar, 50) { Value = plcId });
                }

                sql.Append(" ORDER BY CreateTime DESC");

                using var command = new SqlCommand(sql.ToString(), connection);
                command.Parameters.AddRange(parameters.ToArray());

                using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var record = new TrayPreUploadRecordDto
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        DocumentNo = reader["DocumentNo"].ToString() ?? string.Empty,
                        PLCID = reader["PLCID"].ToString() ?? string.Empty,
                        LoadingPoint = Convert.ToInt32(reader["LoadingPoint"]),
                        PalletCode = reader["PalletCode"].ToString() ?? string.Empty,
                        Remark = reader["Remark"].ToString() ?? string.Empty,
                        CreateTime = Convert.ToDateTime(reader["CreateTime"]),
                        Status = reader["Status"].ToString() ?? string.Empty
                    };
                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询预上架记录异常：DocumentNo={DocumentNo}, PLCID={PLCID}",
                    documentNo, plcId);
            }

            return records;
        }
    }
}
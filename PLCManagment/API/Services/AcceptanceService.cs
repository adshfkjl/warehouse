// Services/AcceptanceService.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Services
{
    public interface IAcceptanceService
    {
        Task<List<AcceptanceInfoDto>> GetAcceptanceByMoNo(string moNo);
        Task<SaveAcceptanceResponseDto> SaveAcceptanceInfo(SaveAcceptanceRequestDto request);
        Task<List<DefectReasonDto>> GetDefectReasons(string? parentCode = null);
        Task<bool> CreateDefectReason(CreateDefectReasonDto request);
        Task<bool> UpdateDefectReason(string spcNo, UpdateDefectReasonDto request);
        Task<bool> DeleteDefectReason(string spcNo);
    }

    public class AcceptanceService : IAcceptanceService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AcceptanceService> _logger;
        private const int AcceptanceCommandTimeoutSeconds = 30;

        public AcceptanceService(IConfiguration configuration, ILogger<AcceptanceService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        private string GetSdlConnectionString()
        {
            // 从配置中获取DB_SDL1连接字符串，或者基于现有连接字符串修改
            var baseConnectionString = _configuration.GetConnectionString("StoreHouseConnection");
            var builder = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = "DB_SDL1" // 切换到DB_SDL1数据库
            };
            return builder.ConnectionString;
        }

        public async Task<List<AcceptanceInfoDto>> GetAcceptanceByMoNo(string moNo)
        {
            moNo = NormalizeRequiredDocumentNo(moNo, nameof(moNo));
            var connectionString = GetSdlConnectionString();
            var results = new List<AcceptanceInfoDto>();

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand("[dbo].[WX_GetTYByMO_NO]", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandTimeout = AcceptanceCommandTimeoutSeconds;
                    AddVarCharParameter(command, "@MO_NO", moNo, 50);

                    try
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                results.Add(ReadAcceptanceInfo(reader));
                            }
                        }

                        _logger.LogInformation($"成功获取制令单 {moNo} 的验收单信息，共 {results.Count} 条记录");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"获取制令单 {moNo} 的验收单信息失败");
                        throw;
                    }
                }
            }

            return results;
        }

        private AcceptanceInfoDto ReadAcceptanceInfo(SqlDataReader reader)
        {
            return new AcceptanceInfoDto
            {
                TY_ID = SafeGetString(reader, "TY_ID"),
                TY_NO = SafeGetString(reader, "TY_NO"),
                ITM = SafeGetInt(reader, "ITM"),
                PRD_NO = SafeGetString(reader, "PRD_NO"),
                NAME = SafeGetString(reader, "NAME"),
                PRD_NAME = SafeGetString(reader, "PRD_NAME"),
                SPC = SafeGetString(reader, "SPC"),
                UT = SafeGetString(reader, "UT"),
                QTY_CHK = SafeGetDecimal(reader, "QTY_CHK"),
                QTY_OK = SafeGetDecimal(reader, "QTY_OK"),
                BIL_NO = SafeGetString(reader, "BIL_NO"),
                TI_NO = SafeGetString(reader, "TI_NO"),
                SPC_NO = SafeGetString(reader, "SPC_NO"),
                PRC_ID = SafeGetString(reader, "PRC_ID"),
                QTY_LOST = SafeGetDecimal(reader, "QTY_LOST"),
                QTY_OK_RTN = SafeGetNullableDecimal(reader, "QTY_OK_RTN"),
                CLS_ID_OK = SafeGetString(reader, "CLS_ID_OK"),
                CLS_ID_LOST = SafeGetString(reader, "CLS_ID_LOST"),
                CHK_KND = SafeGetInt(reader, "CHK_KND"),
                STAT = SafeGetInt(reader, "STAT")
            };
        }

        private static string NormalizeRequiredDocumentNo(string? value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("单据号不能为空", parameterName);
            }

            return value.Trim();
        }

        private static void AddVarCharParameter(SqlCommand command, string name, string? value, int size)
        {
            var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            command.Parameters.Add(name, SqlDbType.VarChar, size).Value =
                normalizedValue is null ? DBNull.Value : normalizedValue;
        }

        private static void AddDecimalParameter(SqlCommand command, string name, decimal value)
        {
            var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 18;
            parameter.Scale = 6;
            parameter.Value = value;
        }

        private static int GetOrdinalOrMissing(SqlDataReader reader, string columnName)
        {
            try
            {
                return reader.GetOrdinal(columnName);
            }
            catch (IndexOutOfRangeException)
            {
                return -1;
            }
        }

        private static bool IsNullLike(object? value)
        {
            return value is null || value == DBNull.Value || value is INullable { IsNull: true };
        }

        private object? SafeGetValue(SqlDataReader reader, string columnName)
        {
            var ordinal = GetOrdinalOrMissing(reader, columnName);
            if (ordinal < 0)
            {
                _logger.LogWarning("验收单存储过程返回结果缺少列 {ColumnName}，已使用默认值", columnName);
                return null;
            }

            return reader.GetValue(ordinal);
        }

        // 添加安全的读取辅助方法
        private string SafeGetString(SqlDataReader reader, string columnName)
        {
            var value = SafeGetValue(reader, columnName);
            if (IsNullLike(value))
            {
                return "";
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return text is null ? "" : text.Trim();
        }

        private int SafeGetInt(SqlDataReader reader, string columnName)
        {
            var value = SafeGetValue(reader, columnName);
            if (IsNullLike(value) || value is string text && string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                _logger.LogWarning(ex, "验收单列 {ColumnName} 的值 {Value} 无法转换为整数，已使用默认值 0", columnName, value);
                return 0;
            }
        }

        private decimal SafeGetDecimal(SqlDataReader reader, string columnName)
        {
            return SafeGetNullableDecimal(reader, columnName) ?? 0;
        }

        private decimal? SafeGetNullableDecimal(SqlDataReader reader, string columnName)
        {
            var value = SafeGetValue(reader, columnName);
            if (IsNullLike(value) || value is string text && string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                _logger.LogWarning(ex, "验收单列 {ColumnName} 的值 {Value} 无法转换为数字，已使用默认值", columnName, value);
                return null;
            }
        }

        public async Task<SaveAcceptanceResponseDto> SaveAcceptanceInfo(SaveAcceptanceRequestDto request)
        {
            request.MO_NO = NormalizeRequiredDocumentNo(request.MO_NO, nameof(request.MO_NO));
            request.TY_ID = NormalizeRequiredDocumentNo(request.TY_ID, nameof(request.TY_ID));
            request.TY_NO = NormalizeRequiredDocumentNo(request.TY_NO, nameof(request.TY_NO));
            var connectionString = GetSdlConnectionString();
            var response = new SaveAcceptanceResponseDto
            {
                Success = false,
                Data = new List<AcceptanceInfoDto>()
            };

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand("[dbo].[WX_SaveTYInfo]", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandTimeout = AcceptanceCommandTimeoutSeconds;

                    // 输入参数
                    AddVarCharParameter(command, "@TY_ID", request.TY_ID, 20);
                    AddVarCharParameter(command, "@TY_NO", request.TY_NO, 50);
                    command.Parameters.Add("@ITM", SqlDbType.Int).Value = request.ITM;
                    AddVarCharParameter(command, "@MO_NO", request.MO_NO, 50);
                    AddDecimalParameter(command, "@QTY_OK", request.QTY_OK);
                    AddDecimalParameter(command, "@QTY_LOST", request.QTY_LOST);
                    AddVarCharParameter(command, "@SPC_NO", request.SPC_NO, 50);
                    AddVarCharParameter(command, "@PRC_ID", request.PRC_ID, 50);
                    AddVarCharParameter(command, "@REM", request.REM, 500);

                    // 输出参数
                    var resultParam = new SqlParameter("@RES", SqlDbType.Int)
                    {
                        Direction = ParameterDirection.Output
                    };
                    command.Parameters.Add(resultParam);

                    try
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            // 读取返回的数据
                            while (await reader.ReadAsync())
                            {
                                response.Data.Add(ReadAcceptanceInfo(reader));
                            }
                        }

                        // 获取输出参数值
                        var resultCode = resultParam.Value != DBNull.Value ? Convert.ToInt32(resultParam.Value) : 0;

                        response.Success = true;
                        response.ResultCode = resultCode;
                        response.Message = resultCode == 0 ? "保存成功" : "单据已经转单，无法修改";

                        _logger.LogInformation($"保存验收单信息成功: TY_NO={request.TY_NO}, ResultCode={resultCode}");
                    }
                    catch (Exception ex)
                    {
                        response.Message = $"保存失败: {ex.Message}";
                        _logger.LogError(ex, $"保存验收单信息失败: TY_NO={request.TY_NO}");
                    }
                }
            }

            return response;
        }


        public async Task<List<DefectReasonDto>> GetDefectReasons(string? parentCode = null)
        {
            var connectionString = GetSdlConnectionString();
            var results = new List<DefectReasonDto>();

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                string sql = "SELECT SPC_NO, NAME, SPC_NO_UP FROM SPC_LST";
                if (!string.IsNullOrEmpty(parentCode))
                {
                    sql += " WHERE SPC_NO_UP = @ParentCode";
                }
                sql += " ORDER BY SPC_NO";

                using (var command = new SqlCommand(sql, connection))
                {
                    if (!string.IsNullOrEmpty(parentCode))
                    {
                        AddVarCharParameter(command, "@ParentCode", parentCode, 50);
                    }

                    try
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var item = new DefectReasonDto
                                {
                                    SPC_NO = SafeGetString(reader, "SPC_NO"),
                                    NAME = SafeGetString(reader, "NAME"),
                                    SPC_NO_UP = SafeGetString(reader, "SPC_NO_UP")
                                };
                                results.Add(item);
                            }
                        }

                        _logger.LogInformation($"成功获取不合格原因列表，共 {results.Count} 条记录");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "获取不合格原因列表失败");
                        throw;
                    }
                }
            }

            return results;
        }

        public async Task<bool> CreateDefectReason(CreateDefectReasonDto request)
        {
            var connectionString = GetSdlConnectionString();

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                const string sql = @"
INSERT INTO SPC_LST (SPC_NO, NAME, SPC_NO_UP)
VALUES (@SPC_NO, @NAME, @SPC_NO_UP)";

                using (var command = new SqlCommand(sql, connection))
                {
                    AddVarCharParameter(command, "@SPC_NO", request.SPC_NO, 50);
                    AddVarCharParameter(command, "@NAME", request.NAME, 200);
                    AddVarCharParameter(command, "@SPC_NO_UP", request.SPC_NO_UP, 50);

                    try
                    {
                        int affectedRows = await command.ExecuteNonQueryAsync();
                        _logger.LogInformation($"创建不合格原因成功: {request.SPC_NO}-{request.NAME}");
                        return affectedRows > 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"创建不合格原因失败: {request.SPC_NO}-{request.NAME}");
                        throw;
                    }
                }
            }
        }

        public async Task<bool> UpdateDefectReason(string spcNo, UpdateDefectReasonDto request)
        {
            var connectionString = GetSdlConnectionString();

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                const string sql = @"
UPDATE SPC_LST 
SET NAME = @NAME, SPC_NO_UP = @SPC_NO_UP
WHERE SPC_NO = @SPC_NO";

                using (var command = new SqlCommand(sql, connection))
                {
                    AddVarCharParameter(command, "@SPC_NO", spcNo, 50);
                    AddVarCharParameter(command, "@NAME", request.NAME, 200);
                    AddVarCharParameter(command, "@SPC_NO_UP", request.SPC_NO_UP, 50);

                    try
                    {
                        int affectedRows = await command.ExecuteNonQueryAsync();
                        _logger.LogInformation($"更新不合格原因成功: {spcNo}");
                        return affectedRows > 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"更新不合格原因失败: {spcNo}");
                        throw;
                    }
                }
            }
        }

        public async Task<bool> DeleteDefectReason(string spcNo)
        {
            var connectionString = GetSdlConnectionString();

            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();

                const string sql = "DELETE FROM SPC_LST WHERE SPC_NO = @SPC_NO";

                using (var command = new SqlCommand(sql, connection))
                {
                    AddVarCharParameter(command, "@SPC_NO", spcNo, 50);

                    try
                    {
                        int affectedRows = await command.ExecuteNonQueryAsync();
                        _logger.LogInformation($"删除不合格原因成功: {spcNo}");
                        return affectedRows > 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"删除不合格原因失败: {spcNo}");
                        throw;
                    }
                }
            }
        }
    }
}





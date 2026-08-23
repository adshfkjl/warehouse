// Services/IInventoryService.cs
using Microsoft.Data.SqlClient;
using System.Data;

public interface IInventoryService
{
    Task<List<InventoryInfo>> QueryInventoryAsync(InventoryQuery query);
}

// Services/InventoryService.cs
public class InventoryService : IInventoryService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(IConfiguration configuration, ILogger<InventoryService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<List<InventoryInfo>> QueryInventoryAsync(InventoryQuery query)
    {
        var results = new List<InventoryInfo>();
        var connectionString = _configuration.GetConnectionString("DB_SDL1");

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        using var command = new SqlCommand("WX微信查询库存", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        // 添加存储过程参数
        command.Parameters.Add(new SqlParameter("@PRD_NO", SqlDbType.VarChar, 100) { Value = query.PRD_NO ?? string.Empty });
        command.Parameters.Add(new SqlParameter("@PRD_NAME", SqlDbType.VarChar, 200) { Value = query.PRD_NAME ?? string.Empty });
        command.Parameters.Add(new SqlParameter("@PRD_SPC", SqlDbType.VarChar, 200) { Value = query.PRD_SPC ?? string.Empty });
        command.Parameters.Add(new SqlParameter("@WH", SqlDbType.VarChar, 50) { Value = query.WH ?? string.Empty });
        command.Parameters.Add(new SqlParameter("@POS", SqlDbType.VarChar, 50) { Value = query.POS ?? string.Empty });

        try
        {
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var inventoryInfo = new InventoryInfo
                {
                    WH = reader["WH"]?.ToString() ?? string.Empty,
                    PRD_NO = reader["PRD_NO"]?.ToString() ?? string.Empty,
                    NAME = reader["NAME"]?.ToString() ?? string.Empty,
                    PRD_MARK = reader["PRD_MARK"]?.ToString() ?? string.Empty,
                    SPC = reader["SPC"]?.ToString() ?? string.Empty,
                    UT = reader["UT"]?.ToString() ?? string.Empty,
                    REM = reader["REM"]?.ToString() ?? string.Empty,
                    LoadPoint = GetInt32OrDefault(reader, "LOADPOINT")
                };

                // 处理QTY字段（double类型）
                if (reader["QTY"] != DBNull.Value && double.TryParse(reader["QTY"].ToString(), out double qty))
                {
                    inventoryInfo.QTY = qty;
                }

                results.Add(inventoryInfo);
            }

            _logger.LogInformation("库存查询成功，返回 {Count} 条记录", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "库存查询失败：{Message}", ex.Message);
            throw;
        }

        return results;
    }

    private static int GetInt32OrDefault(IDataRecord reader, string columnName, int defaultValue = 0)
    {
        if (!HasColumn(reader, columnName))
        {
            return defaultValue;
        }

        var value = reader[columnName];
        if (value == null || value == DBNull.Value)
        {
            return defaultValue;
        }

        return Convert.ToInt32(value);
    }

    private static bool HasColumn(IDataRecord reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

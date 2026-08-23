// Controllers/InventoryController.cs
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventoryService;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(IInventoryService inventoryService, ILogger<InventoryController> logger)
    {
        _inventoryService = inventoryService;
        _logger = logger;
    }

    /// <summary>
    /// 查询库存信息
    /// </summary>
    /// <param name="query">查询条件</param>
    /// <returns>库存信息列表</returns>
    [HttpPost("query")]
    [ProducesResponseType(typeof(ApiResponse<List<InventoryInfo>>), 200)]
    [ProducesResponseType(typeof(ApiResponse<string>), 400)]
    [ProducesResponseType(typeof(ApiResponse<string>), 500)]
    public async Task<IActionResult> QueryInventory([FromBody] InventoryQuery query)
    {
        try
        {
            if (query == null)
            {
                return BadRequest(new ApiResponse<string>
                {
                    Success = false,
                    Message = "查询参数不能为空",
                    Data = null
                });
            }

            var results = await _inventoryService.QueryInventoryAsync(query);

            return Ok(new ApiResponse<List<InventoryInfo>>
            {
                Success = true,
                Message = "查询成功",
                Data = results
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "库存查询API异常：{Message}", ex.Message);
            return StatusCode(500, new ApiResponse<string>
            {
                Success = false,
                Message = "查询失败",
                Data = ex.Message
            });
        }
    }

    /// <summary>
    /// 通过URL参数查询库存信息（GET方式）
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<InventoryInfo>>), 200)]
    public async Task<IActionResult> GetInventory(
        [FromQuery] string? prdNo = null,
        [FromQuery] string? prdName = null,
        [FromQuery] string? prdSpc = null,
        [FromQuery] string? wh = null,
        [FromQuery] string? pos = null)
    {
        var query = new InventoryQuery
        {
            PRD_NO = prdNo,
            PRD_NAME = prdName,
            PRD_SPC = prdSpc,
            WH = wh,
            POS = pos
        };

        return await QueryInventory(query);
    }
}

// 统一的API响应模型
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
}
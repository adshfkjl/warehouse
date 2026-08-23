using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/inventory-check")]
    public class InventoryCheckController : ControllerBase
    {
        private readonly IInventoryCheckService _inventoryCheckService;
        private readonly ILogger<InventoryCheckController> _logger;

        public InventoryCheckController(
            IInventoryCheckService inventoryCheckService,
            ILogger<InventoryCheckController> logger)
        {
            _inventoryCheckService = inventoryCheckService;
            _logger = logger;
        }

        [HttpPost("outbound-range")]
        public async Task<IActionResult> CreateOutboundRangeTask([FromBody] InventoryCheckOutboundRangeRequest request)
        {
            try
            {
                var result = await _inventoryCheckService.CreateOutboundRangeTaskAsync(request);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create inventory check outbound task.");
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "Failed to create inventory check outbound task.",
                    Data = ex.Message
                });
            }
        }

        [HttpGet("tasks/{taskId:long}")]
        public async Task<IActionResult> GetTask(long taskId)
        {
            var result = await _inventoryCheckService.GetTaskAsync(taskId);
            if (result == null)
            {
                return NotFound(new ApiResponse<string>
                {
                    Success = false,
                    Message = $"Inventory check task {taskId} does not exist.",
                    Data = null
                });
            }

            return Ok(result);
        }

        [HttpPost("items/{itemId:long}/schedule-inbound")]
        public async Task<IActionResult> ScheduleInboundItem(long itemId)
        {
            try
            {
                var result = await _inventoryCheckService.ScheduleInboundItemAsync(itemId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to schedule inventory check inbound for item {ItemId}.", itemId);
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "Failed to schedule inventory check inbound.",
                    Data = ex.Message
                });
            }
        }

        [HttpPost("tasks/{taskId:long}/cancel")]
        public async Task<IActionResult> CancelTask(long taskId)
        {
            try
            {
                var result = await _inventoryCheckService.CancelTaskAsync(taskId);
                return Ok(result);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new ApiResponse<string> { Success = false, Message = ex.Message, Data = null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel inventory check task {TaskId}.", taskId);
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "Failed to cancel inventory check task.",
                    Data = ex.Message
                });
            }
        }
    }
}

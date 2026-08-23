// Controllers/LocationStatusController.cs
using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Services;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LocationStatusController : ControllerBase
    {
        private readonly ILocationCheckService _locationCheckService;
        private readonly ILogger<LocationStatusController> _logger;

        public LocationStatusController(
            ILocationCheckService locationCheckService,
            ILogger<LocationStatusController> logger)
        {
            _locationCheckService = locationCheckService;
            _logger = logger;
        }

        /// <summary>
        /// 检查装载点是否为空
        /// </summary>
        [HttpPost("check-loading-point")]
        public async Task<ActionResult<LoadingPointStatusDto>> CheckLoadingPoint([FromBody] LocationCheckRequestDto request)
        {
            try
            {
                var isEmpty = await _locationCheckService.IsLoadingPointEmpty(request.PlcId, request.LoadingPoint);

                return Ok(new LoadingPointStatusDto
                {
                    PlcId = request.PlcId,
                    LoadingPoint = request.LoadingPoint,
                    IsEmpty = isEmpty
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"检查装载点状态失败: {request.PlcId}-{request.LoadingPoint}");
                return BadRequest($"检查装载点状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查储位状态
        /// </summary>
        [HttpPost("check-storage-location")]
        public async Task<ActionResult<StorageLocationStatusDto>> CheckStorageLocation([FromBody] LocationCheckRequestDto request)
        {
            try
            {
                var status = await _locationCheckService.GetStorageLocationStatus(request.PlcId, request.Shelf, request.Position);

                var statusDescription = status switch
                {
                    0 => "空",
                    1 => "有货",
                    2 => "停用",
                    -1 => "不存在",
                    _ => "未知状态"
                };

                return Ok(new StorageLocationStatusDto
                {
                    PlcId = request.PlcId,
                    Shelf = request.Shelf,
                    Position = request.Position,
                    Status = status,
                    StatusDescription = statusDescription
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"检查储位状态失败: {request.PlcId}-{request.Shelf}-{request.Position}");
                return BadRequest($"检查储位状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查储位是否可用（为空）
        /// </summary>
        [HttpPost("check-storage-available")]
        public async Task<ActionResult<bool>> CheckStorageAvailable([FromBody] LocationCheckRequestDto request)
        {
            try
            {
                var isAvailable = await _locationCheckService.IsStorageLocationAvailable(request.PlcId, request.Shelf, request.Position);
                return Ok(isAvailable);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"检查储位可用性失败: {request.PlcId}-{request.Shelf}-{request.Position}");
                return BadRequest($"检查储位可用性失败: {ex.Message}");
            }
        }
    }
}
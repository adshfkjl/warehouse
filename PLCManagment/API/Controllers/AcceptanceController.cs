// Controllers/AcceptanceController.cs
using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Services;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AcceptanceController : ControllerBase
    {
        private readonly IAcceptanceService _acceptanceService;
        private readonly ILogger<AcceptanceController> _logger;

        public AcceptanceController(
            IAcceptanceService acceptanceService,
            ILogger<AcceptanceController> logger)
        {
            _acceptanceService = acceptanceService;
            _logger = logger;
        }

        /// <summary>
        /// 根据制令单号获取验收单信息
        /// </summary>
        [HttpPost("get-by-mo")]
        public async Task<ActionResult<List<AcceptanceInfoDto>>> GetAcceptanceByMoNo([FromBody] GetAcceptanceByMoRequestDto request)
        {
            try
            {
                var results = await _acceptanceService.GetAcceptanceByMoNo(request.MO_NO);
                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"获取验收单信息失败: MO_NO={request.MO_NO}");
                return BadRequest($"获取验收单信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存验收单信息
        /// </summary>
        [HttpPost("save")]
        public async Task<ActionResult<SaveAcceptanceResponseDto>> SaveAcceptanceInfo([FromBody] SaveAcceptanceRequestDto request)
        {
            try
            {
                // 验证数据
                if (request.QTY_LOST > 0)
                {
                    if (string.IsNullOrEmpty(request.SPC_NO))
                    {
                        return BadRequest("当有不合格数量时，必须填写不合格原因代号");
                    }
                    if (string.IsNullOrEmpty(request.PRC_ID))
                    {
                        return BadRequest("当有不合格数量时，必须填写处理方式");
                    }
                }

                var response = await _acceptanceService.SaveAcceptanceInfo(request);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"保存验收单信息失败: TY_NO={request.TY_NO}");
                return BadRequest($"保存验收单信息失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取不合格原因列表
        /// </summary>
        [HttpGet("defect-reasons")]
        public async Task<ActionResult<List<DefectReasonDto>>> GetDefectReasons([FromQuery] string parentCode = null)
        {
            try
            {
                var results = await _acceptanceService.GetDefectReasons(parentCode);
                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取不合格原因列表失败");
                return BadRequest($"获取不合格原因列表失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建不合格原因
        /// </summary>
        [HttpPost("defect-reasons")]
        public async Task<ActionResult> CreateDefectReason([FromBody] CreateDefectReasonDto request)
        {
            try
            {
                var success = await _acceptanceService.CreateDefectReason(request);
                return success ? Ok("创建成功") : BadRequest("创建失败");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"创建不合格原因失败: {request.SPC_NO}");
                return BadRequest($"创建不合格原因失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新不合格原因
        /// </summary>
        [HttpPut("defect-reasons/{spcNo}")]
        public async Task<ActionResult> UpdateDefectReason(string spcNo, [FromBody] UpdateDefectReasonDto request)
        {
            try
            {
                var success = await _acceptanceService.UpdateDefectReason(spcNo, request);
                return success ? Ok("更新成功") : BadRequest("更新失败");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"更新不合格原因失败: {spcNo}");
                return BadRequest($"更新不合格原因失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 删除不合格原因
        /// </summary>
        [HttpDelete("defect-reasons/{spcNo}")]
        public async Task<ActionResult> DeleteDefectReason(string spcNo)
        {
            try
            {
                var success = await _acceptanceService.DeleteDefectReason(spcNo);
                return success ? Ok("删除成功") : BadRequest("删除失败");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"删除不合格原因失败: {spcNo}");
                return BadRequest($"删除不合格原因失败: {ex.Message}");
            }
        }
    }
}
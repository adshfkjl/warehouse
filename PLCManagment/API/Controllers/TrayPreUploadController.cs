using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Dtos;
using PLCManagement.API.Models;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TrayPreUploadController : ControllerBase
    {
        private readonly ITrayPreUploadService _trayPreUploadService;
        private readonly ILogger<TrayPreUploadController> _logger;

        // 使用主构造函数简化代码
        public TrayPreUploadController(
            ITrayPreUploadService trayPreUploadService,
            ILogger<TrayPreUploadController> logger)
        {
            _trayPreUploadService = trayPreUploadService;
            _logger = logger;
        }

        /// <summary>
        /// 登记预上架货框
        /// </summary>
        /// <param name="request">预上架请求</param>
        /// <returns>处理结果</returns>
        [HttpPost("register")]
        [ProducesResponseType(typeof(ApiResponse<TrayPreUploadResponse>), 200)]
        [ProducesResponseType(typeof(ApiResponse<string>), 400)]
        [ProducesResponseType(typeof(ApiResponse<string>), 500)]
        public async Task<IActionResult> RegisterPreUploadTray([FromBody] TrayPreUploadRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new ApiResponse<string>
                    {
                        Success = false,
                        Message = "请求参数不能为空",
                        Data = null
                    });
                }

                // 验证必填字段
                if (string.IsNullOrWhiteSpace(request.DocumentNo))
                {
                    return BadRequest(new ApiResponse<string>
                    {
                        Success = false,
                        Message = "单据编号不能为空",
                        Data = "DocumentNo is required"
                    });
                }

                if (string.IsNullOrWhiteSpace(request.PLCID))
                {
                    return BadRequest(new ApiResponse<string>
                    {
                        Success = false,
                        Message = "PLC编号不能为空",
                        Data = "PLCID is required"
                    });
                }

                _logger.LogInformation("接收到预上架货框登记请求：单据编号={DocumentNo}, PLC={PLCID}, 装载点={LoadingPoint}",
                    request.DocumentNo, request.PLCID, request.LoadingPoint);

                var result = await _trayPreUploadService.RegisterPreUploadTrayAsync(request);

                if (result.Success)
                {
                    return Ok(new ApiResponse<TrayPreUploadResponse>
                    {
                        Success = true,
                        Message = "预上架货框登记成功",
                        Data = result
                    });
                }
                else
                {
                    return BadRequest(new ApiResponse<TrayPreUploadResponse>
                    {
                        Success = false,
                        Message = result.Message,
                        Data = result
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预上架货框登记API异常：{Message}", ex.Message);
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "系统处理异常",
                    Data = ex.Message
                });
            }
        }

        /// <summary>
        /// 查询预上架记录
        /// </summary>
        /// <param name="documentNo">单据编号</param>
        /// <param name="plcId">PLC编号</param>
        /// <returns>预上架记录列表</returns>
        [HttpGet("records")]
        [ProducesResponseType(typeof(ApiResponse<List<TrayPreUploadRecordDto>>), 200)]
        [ProducesResponseType(typeof(ApiResponse<string>), 500)]
        public async Task<IActionResult> GetPreUploadRecords(
            [FromQuery] string documentNo = null,
            [FromQuery] string plcId = null)
        {
            try
            {
                _logger.LogInformation("查询预上架记录：DocumentNo={DocumentNo}, PLCID={PLCID}",
                    documentNo, plcId);

                var records = await _trayPreUploadService.GetPreUploadRecordsAsync(documentNo, plcId);

                return Ok(new ApiResponse<List<TrayPreUploadRecordDto>>
                {
                    Success = true,
                    Message = "查询成功",
                    Data = records
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询预上架记录API异常：{Message}", ex.Message);
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "查询异常",
                    Data = ex.Message
                });
            }
        }

        /// <summary>
        /// 预上架货框登记（GET方式，适用于简单调用）
        /// </summary>
        [HttpGet("register")]
        public async Task<IActionResult> RegisterPreUploadTrayGet(
            [FromQuery] string documentNo,
            [FromQuery] string plcId,
            [FromQuery] int loadingPoint = 0,
            [FromQuery] string palletCode = null,
            [FromQuery] string remark = null)
        {
            var request = new TrayPreUploadRequest
            {
                DocumentNo = documentNo,
                PLCID = plcId,
                LoadingPoint = loadingPoint,
                PalletCode = palletCode,
                Remark = remark
            };

            // 调用POST方法
            return await RegisterPreUploadTray(request);
        }
    }
}
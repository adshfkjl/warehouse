using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models;
using PLCManagement.API.Services;
using static System.Net.Mime.MediaTypeNames;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentOperationController : ControllerBase
    {
        private readonly IDocumentUpLoadService _upLoadService;
        private readonly IDocumentDownLoadService _downLoadService;
        private readonly ILogger<DocumentOperationController> _logger;

        public DocumentOperationController(
            IDocumentUpLoadService unloadService,
            IDocumentDownLoadService downLoadService,
            ILogger<DocumentOperationController> logger)
        {
            _upLoadService = unloadService;
            _downLoadService = downLoadService;
            _logger = logger;
        }

        /// <summary>
        /// 单据下架接口
        /// </summary>
        /// <param name="request">下架请求参数</param>
        /// <returns>下架处理结果</returns>
        [HttpPost("download")]
        [ProducesResponseType(typeof(ApiResponse<DocumentOperationResult>), 200)]
        [ProducesResponseType(typeof(ApiResponse<string>), 400)]
        [ProducesResponseType(typeof(ApiResponse<string>), 500)]
        public async Task<IActionResult> DownLoadDocument([FromBody] DocumentDownLoadRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.DocumentNo))
                {
                    return BadRequest(new ApiResponse<string>
                    {
                        Success = false,
                        Message = "请求参数无效",
                        Data = "单据编号不能为空"
                    });
                }

                _logger.LogInformation("接收到单据下架请求：单据编号={DocumentNo}", request.DocumentNo);

                var result = await _downLoadService.ProcessDocumentDownLoadAsync(
                    request.DocumentNo, request.LoadingPoint);

                return Ok(new ApiResponse<DocumentOperationResult>
                {
                    Success = result.ResultFlag == 1,
                    Message = result.ResultFlag == 1 ? "单据下架处理成功" : "单据下架处理失败",
                    Data = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "单据下架API异常：{Message}", ex.Message);
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "系统处理异常",
                    Data = ex.Message
                });
            }
        }

        /// <summary>
        /// 单据上架接口
        /// </summary>
        /// <param name="request">上架请求参数</param>
        /// <returns>上架处理结果</returns>
        [HttpPost("upload")]
        [ProducesResponseType(typeof(ApiResponse<DocumentOperationResult>), 200)]
        [ProducesResponseType(typeof(ApiResponse<string>), 400)]
        [ProducesResponseType(typeof(ApiResponse<string>), 500)]
        public async Task<IActionResult> UpLoadDocument([FromBody] DocumentUpLoadRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.DocumentNo))
                {
                    return BadRequest(new ApiResponse<string>
                    {
                        Success = false,
                        Message = "请求参数无效",
                        Data = "单据编号不能为空"
                    });
                }

                _logger.LogInformation("接收到单据上架请求：单据编号={DocumentNo}", request.DocumentNo);

                var result = await _upLoadService.ProcessDocumentUpLoadAsync(
                    request.DocumentNo, request.StorageLocation);

                return Ok(new ApiResponse<DocumentOperationResult>
                {
                    Success = result.ResultFlag == 1,
                    Message = result.ResultFlag == 1 ? "单据上架处理成功" : "单据上架处理失败",
                    Data = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "单据上架API异常：{Message}", ex.Message);
                return StatusCode(500, new ApiResponse<string>
                {
                    Success = false,
                    Message = "系统处理异常",
                    Data = ex.Message
                });
            }
        }

        // GET方式调用接口
        [HttpGet("download")]
        public async Task<IActionResult> DownLoadDocumentGet(
            [FromQuery] string documentNo,
            [FromQuery] string loadingPoint = "")
        {
            var request = new DocumentDownLoadRequest
            {
                DocumentNo = documentNo,
                LoadingPoint = loadingPoint
            };
            return await DownLoadDocument(request);
        }

        [HttpGet("upload")]
        public async Task<IActionResult> UpLoadDocumentGet(
            [FromQuery] string documentNo,
            [FromQuery] string storageLocation = "")
        {
            var request = new DocumentUpLoadRequest
            {
                DocumentNo = documentNo,
                StorageLocation = storageLocation
            };
            return await UpLoadDocument(request);
        }
    }
}
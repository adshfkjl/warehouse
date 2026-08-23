// PLCManagement.API/Controllers/PlcOperationsController.cs
using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Services;
using System.Threading.Tasks;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Dtos;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PLCManagement.API.Data;
using PLCManagement.API.Models;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/plc-operations")]
    public class PlcOperationsController : ControllerBase
    {
        private readonly IPlcService _plcService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<PlcOperationsController> _logger;

        public PlcOperationsController(
            IPlcService plcService,
            IServiceProvider serviceProvider,
            ILogger<PlcOperationsController> logger)
        {
            _plcService = plcService;
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        [HttpPost("write")]
        public async Task<ActionResult> WriteToPlc([FromBody] string command)
        {
            // Expected format: a#b#c#d, PLC#寄存器地址#内容#类型#

            var parts = command.Split('#');
            if (parts.Length != 4)
            {
                return BadRequest("Invalid command format. Expected: PlcId#寄存器地址#写入值#值类型");
            }

            if (!ushort.TryParse(parts[1], out var address))
            {
                return BadRequest("Invalid address format");
            }

            if (!int.TryParse(parts[3], out var dataType) || (dataType != 1 && dataType != 2))
            {
                return BadRequest("Invalid data type. Use 1 for 16-bit or 2 for 32-bit");
            }

            var result = await _plcService.WriteToPlc(parts[0], address, parts[2], dataType);

            //if (result.IsSuccess && (address == 22011 || address == 22013))
            //{
            //    await Task.Delay(2500);

            //    result = await _plcService.WriteToPlc(parts[0], address, "0", dataType);
            //}
            if (result.IsSuccess && (address == 22020))
            {
                await Task.Delay(1000);

                result = await _plcService.WriteToPlc(parts[0], address, "0", dataType);
            }

            string prompt = PromptByAddress(address);

            if (result.IsSuccess)
            {
                return Ok(new
                {
                    OriginalCommand = prompt,
                    Status = "成功",
                    Message = result.Message
                });
            }

            return BadRequest(new
            {
                OriginalCommand = prompt,
                Status = "失败",
                Message = result.Message
            });
        }

        [HttpPost("outbound")]
        [Consumes("application/json")]
        public async Task<IActionResult> Outbound([FromBody] OutboundRequestDto request)
        {
            _logger.LogInformation(
                "收到出库请求: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                request.PlcId,
                request.Shelf,
                request.Position,
                request.LoadingPoint);

            var outboundLocation = await GetLocationByPosition(request.PlcId, request.Shelf, request.Position);
            var palletNumber = outboundLocation?.Tray ?? string.Empty;

            var result = await _plcService.OutboundOperation(
                    request.PlcId,
                    request.Shelf,
                    request.Position,
                    request.LoadingPoint);

            LogOperationResult(
                "出库",
                request.PlcId,
                request.Shelf,
                request.Position,
                request.LoadingPoint,
                result.IsSuccess,
                result.Message);

            if (result.IsSuccess)
            {
                StartOutboundPalletSyncMonitor(
                    request.PlcId,
                    request.Shelf,
                    request.Position,
                    request.LoadingPoint,
                    palletNumber);
            }

            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }

        [HttpPost("inbound")]
        [Consumes("application/json")]
        public async Task<IActionResult> Inbound([FromBody] InboundRequestDto request)
        {
            _logger.LogInformation(
                "收到入库请求: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                request.PlcId,
                request.Shelf,
                request.Position,
                request.LoadingPoint);

            var result = await _plcService.InboundOperation(
                request.PlcId,
                request.Shelf,
                request.Position,
                request.LoadingPoint);

            LogOperationResult(
                "入库",
                request.PlcId,
                request.Shelf,
                request.Position,
                request.LoadingPoint,
                result.IsSuccess,
                result.Message);

            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }

        [HttpPost("outbound-by-tray")]
        [Consumes("application/json")]
        public async Task<IActionResult> OutboundByTray([FromBody] TrayOperationRequestDto request)
        {
            try
            {
                // 根据托盘编号获取位置信息
                var location = await GetLocationByPalletCode(request.PalletCode);
                if (location == null)
                {
                    _logger.LogWarning(
                        "出库按托盘定位失败: PalletCode={PalletCode}, LoadingPoint={LoadingPoint}",
                        request.PalletCode,
                        request.LoadingPoint);
                    return NotFound(new { message = $"未找到托盘编号 {request.PalletCode} 对应的位置信息" });
                }

                _logger.LogInformation(
                    "出库按托盘定位成功: PalletCode={PalletCode}, PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint);

                // 调用原有的出库操作
                var result = await _plcService.OutboundOperation(
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint);

                LogOperationResult(
                    "按托盘出库",
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint,
                    result.IsSuccess,
                    result.Message);

                if (result.IsSuccess)
                {
                    StartOutboundPalletSyncMonitor(
                        location.PLCID,
                        location.Shelf,
                        location.Position,
                        request.LoadingPoint,
                        request.PalletCode);
                }

                return result.IsSuccess ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "按托盘出库异常: PalletCode={PalletCode}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    request.LoadingPoint);
                return StatusCode(500, new { message = $"出库操作失败: {ex.Message}" });
            }
        }

        [HttpPost("outbound-by-bill-tray")]
        [Consumes("application/json")]
        public async Task<IActionResult> OutboundByBillTray([FromBody] TrayOperationRequestDto request)
        {
            try
            {
                // 根据托盘编号获取位置信息
                var location = await GetLocationByPalletCode(request.PalletCode);
                if (location == null)
                {
                    _logger.LogWarning(
                        "单据出库按托盘定位失败: PalletCode={PalletCode}, BillID={BillID}, BillNO={BillNO}, ITM={ITM}, LoadingPoint={LoadingPoint}",
                        request.PalletCode,
                        request.BillID,
                        request.BillNO,
                        request.ITM,
                        request.LoadingPoint);
                    return NotFound(new { message = $"未找到托盘编号 {request.PalletCode} 对应的位置信息" });
                }

                _logger.LogInformation(
                    "单据出库按托盘定位成功: PalletCode={PalletCode}, BillID={BillID}, BillNO={BillNO}, ITM={ITM}, PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    request.BillID,
                    request.BillNO,
                    request.ITM,
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint);

                // 调用原有的出库操作，传入业务参数
                var result = await _plcService.OutboundOperation(
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint,
                    request.BillID ?? string.Empty,
                    request.BillNO ?? string.Empty,
                    request.ITM);

                LogOperationResult(
                    "单据按托盘出库",
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint,
                    result.IsSuccess,
                    result.Message);

                if (result.IsSuccess)
                {
                    StartOutboundPalletSyncMonitor(
                        location.PLCID,
                        location.Shelf,
                        location.Position,
                        request.LoadingPoint,
                        request.PalletCode);
                }

                return result.IsSuccess ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "单据按托盘出库异常: PalletCode={PalletCode}, BillID={BillID}, BillNO={BillNO}, ITM={ITM}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    request.BillID,
                    request.BillNO,
                    request.ITM,
                    request.LoadingPoint);
                return StatusCode(500, new { message = $"出库操作失败: {ex.Message}" });
            }
        }
        [HttpPost("inbound-by-tray")]
        [Consumes("application/json")]
        public async Task<IActionResult> InboundByTray([FromBody] TrayOperationRequestDto request)
        {
            try
            {
                // 根据托盘编号获取位置信息
                var location = await GetLocationByPalletCode(request.PalletCode);
                if (location == null)
                {
                    _logger.LogWarning(
                        "入库按托盘定位失败: PalletCode={PalletCode}, LoadingPoint={LoadingPoint}",
                        request.PalletCode,
                        request.LoadingPoint);
                    return NotFound(new { message = $"未找到托盘编号 {request.PalletCode} 对应的位置信息" });
                }

                _logger.LogInformation(
                    "入库按托盘定位成功: PalletCode={PalletCode}, PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint);

                // 调用入库操作
                var result = await _plcService.InboundOperation(
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint);

                LogOperationResult(
                    "按托盘入库",
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint,
                    result.IsSuccess,
                    result.Message);

                return result.IsSuccess ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "按托盘入库异常: PalletCode={PalletCode}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    request.LoadingPoint);
                return StatusCode(500, new { message = $"入库操作失败: {ex.Message}" });
            }
        }
        [HttpPost("inbound-by-bill-tray")]
        [Consumes("application/json")]
        public async Task<IActionResult> InboundByBillTray([FromBody] TrayOperationRequestDto request)
        {
            try
            {
                // 根据托盘编号获取位置信息
                var location = await GetLocationByPalletCode(request.PalletCode);
                if (location == null)
                {
                    _logger.LogWarning(
                        "单据入库按托盘定位失败: PalletCode={PalletCode}, BillID={BillID}, BillNO={BillNO}, ITM={ITM}, LoadingPoint={LoadingPoint}",
                        request.PalletCode,
                        request.BillID,
                        request.BillNO,
                        request.ITM,
                        request.LoadingPoint);
                    return NotFound(new { message = $"未找到托盘编号 {request.PalletCode} 对应的位置信息" });
                }

                _logger.LogInformation(
                    "单据入库按托盘定位成功: PalletCode={PalletCode}, BillID={BillID}, BillNO={BillNO}, ITM={ITM}, PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    request.BillID,
                    request.BillNO,
                    request.ITM,
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint);

                // 调用原有的入库操作，传入业务参数
                var result = await _plcService.InboundOperation(
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint,
                    request.BillID ?? string.Empty,
                    request.BillNO ?? string.Empty,
                    request.ITM,
                    request.QTY,
                    request.REM ?? string.Empty
                    );

                LogOperationResult(
                    "单据按托盘入库",
                    location.PLCID,
                    location.Shelf,
                    location.Position,
                    request.LoadingPoint,
                    result.IsSuccess,
                    result.Message);

                return result.IsSuccess ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "单据按托盘入库异常: PalletCode={PalletCode}, BillID={BillID}, BillNO={BillNO}, ITM={ITM}, LoadingPoint={LoadingPoint}",
                    request.PalletCode,
                    request.BillID,
                    request.BillNO,
                    request.ITM,
                    request.LoadingPoint);
                return StatusCode(500, new { message = $"出库操作失败: {ex.Message}" });
            }
        }


        [HttpPost("transfer-by-tray")]
        [Consumes("application/json")]
        public async Task<IActionResult> TransferByTray([FromBody] TrayTransferRequestDto request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.TargetPalletCode))
                {
                    return BadRequest(new { message = "移库操作需要提供目标托盘编号" });
                }

                // 获取源位置信息
                var sourceLocation = await GetLocationByPalletCode(request.PalletCode);
                if (sourceLocation == null)
                {
                    return NotFound(new { message = $"未找到源托盘编号 {request.PalletCode} 对应的位置信息" });
                }

                // 获取目标位置信息
                var targetLocation = await GetLocationByPalletCode(request.TargetPalletCode);
                if (targetLocation == null)
                {
                    return NotFound(new { message = $"未找到目标托盘编号 {request.TargetPalletCode} 对应的位置信息" });
                }

                // 检查是否在同一PLC
                if (sourceLocation.PLCID != targetLocation.PLCID)
                {
                    return BadRequest(new { message = "源和目标托盘必须在同一PLC设备中" });
                }

                // 调用原有的移库操作
                var result = await _plcService.TransferOperation(
                    sourceLocation.PLCID,
                    sourceLocation.Shelf,
                    sourceLocation.Position,
                    targetLocation.Shelf,
                    targetLocation.Position);

                return result.IsSuccess ? Ok(result) : BadRequest(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"移库操作失败: {ex.Message}" });
            }
        }

        // 辅助方法：根据托盘编号获取位置信息
        private async Task<LocationManagementDto?> GetLocationByPalletCode(string palletCode)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var location = await context.LocationManagements
                .Where(l => l.Tray == palletCode)
                .Select(l => new LocationManagementDto
                {
                    PLCID = l.PLCID,
                    Shelf = l.Shelf,
                    Position = l.Position,
                    Tray = l.Tray,
                    ShelfStatus = l.ShelfStatus
                })
                .FirstOrDefaultAsync();

            return location;
        }

        private async Task<LocationManagementDto?> GetLocationByPosition(string plcId, int shelf, int position)
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            return await context.LocationManagements
                .Where(l => l.PLCID == plcId && l.Shelf == shelf && l.Position == position)
                .Select(l => new LocationManagementDto
                {
                    PLCID = l.PLCID,
                    Shelf = l.Shelf,
                    Position = l.Position,
                    Tray = l.Tray,
                    ShelfStatus = l.ShelfStatus
                })
                .FirstOrDefaultAsync();
        }

        private void LogOperationResult(
            string operationName,
            string plcId,
            int shelf,
            int position,
            int loadingPoint,
            bool isSuccess,
            string message)
        {
            if (isSuccess)
            {
                _logger.LogInformation(
                    "{OperationName}请求完成: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}, Result=成功, Message={Message}",
                    operationName,
                    plcId,
                    shelf,
                    position,
                    loadingPoint,
                    message);
                return;
            }

            _logger.LogWarning(
                "{OperationName}请求失败: PLC={PlcId}, Shelf={Shelf}, Position={Position}, LoadingPoint={LoadingPoint}, Result=失败, Message={Message}",
                operationName,
                plcId,
                shelf,
                position,
                loadingPoint,
                message);
        }

        private void StartOutboundPalletSyncMonitor(string plcId, int shelf, int position, int loadingPoint, string palletNumber)
        {
            _ = Task.Run(async () =>
            {
                const int pollingIntervalMilliseconds = 400;
                const int maxAttempts = 750;
                var taskKey = $"{plcId}-{shelf}-{position}-{loadingPoint}";

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var plcService = scope.ServiceProvider.GetRequiredService<IPlcService>();
                    var locationCheckService = scope.ServiceProvider.GetRequiredService<ILocationCheckService>();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<PlcOperationsController>>();

                    logger.LogInformation(
                        "[{TaskKey}] 出库托盘同步监控启动，等待 PLC OperationResult 为 6 或 0",
                        taskKey);

                    if (string.IsNullOrWhiteSpace(palletNumber))
                    {
                        logger.LogWarning(
                            "[{TaskKey}] 出库托盘同步监控未获得出库前托盘号，无法同步到装载点",
                            taskKey);
                        return;
                    }

                    for (var attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        await Task.Delay(pollingIntervalMilliseconds);

                        var plcStatus = await plcService.UpdatePlcStatusAsync(plcId);
                        if (plcStatus.OperationResult != 6 && plcStatus.OperationResult != 0)
                        {
                            continue;
                        }

                        var updated = await locationCheckService.UpdateLoadingPointPallet(
                            plcId,
                            loadingPoint,
                            palletNumber);

                        if (updated)
                        {
                            var plc = await context.PlcConfigurations
                                .FirstOrDefaultAsync(p => p.PlcId == plcId);

                            if (plc != null)
                            {
                                plc.TaskEndTime = DateTime.Now;
                                await context.SaveChangesAsync();
                            }

                            logger.LogInformation(
                                "[{TaskKey}] 出库完成，同步托盘号到装载点成功: Tray={Tray}",
                                taskKey,
                                palletNumber);
                        }
                        else
                        {
                            logger.LogWarning(
                                "[{TaskKey}] 出库完成，但 LoadingPoint_Status 未更新任何记录: Tray={Tray}",
                                taskKey,
                                palletNumber);
                        }

                        return;
                    }

                    using (var timeoutScope = _serviceProvider.CreateScope())
                    {
                        var timeoutLogger = timeoutScope.ServiceProvider.GetRequiredService<ILogger<PlcOperationsController>>();
                        timeoutLogger.LogWarning(
                            "[{TaskKey}] 出库托盘同步监控超时，未检测到 OperationResult 为 6 或 0",
                            taskKey);
                    }
                }
                catch (Exception ex)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var logger = scope.ServiceProvider.GetRequiredService<ILogger<PlcOperationsController>>();
                    logger.LogError(ex, "[{TaskKey}] 出库托盘同步监控异常", taskKey);
                }
            });
        }



        [HttpPost("readplcstatus")]
        [Consumes("application/json")]
        public async Task<ActionResult<PlcStatusDto>> GetPlcStatus([FromBody] string plcId)
        {
            try
            {
                var status = await _plcService.ReadPlcStatusAsync(plcId);
                return Ok(status);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("transfer")]
        [Consumes("application/json")]
        public async Task<IActionResult> Transfer([FromBody] TransferRequestDto request)
        {
            var result = await _plcService.TransferOperation(
                request.PlcId,
                request.OutShelf,
                request.OutPosition,
                request.InShelf,
                request.InPosition);

            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }


        [HttpPost("setpara")]
        [Consumes("application/json")]
        public async Task<IActionResult> SetParameter([FromBody] PlcParameterDto request)
        {
            var result = await _plcService.SysSettingOperation(
                request.PlcId,
                request.MainSpeed,
                request.AuxSpeed,
                request.ForkLength);

            return result.IsSuccess ? Ok(result) : BadRequest(result);
        }


        [HttpGet("{plcId}/read")]
        public async Task<ActionResult> ReadFromPlc(string plcId, [FromQuery] ushort address, [FromQuery] ushort count = 1)
        {
            var result = await _plcService.ReadFromPlc(plcId, address, count);

            if (result.IsSuccess)
            {
                return Ok(new
                {
                    PlcId = plcId,
                    Address = address,
                    Values = result.Data,
                    Status = "Success",
                    Message = result.Message
                });
            }

            return BadRequest(new
            {
                PlcId = plcId,
                Address = address,
                Status = "Failed",
                Message = result.Message
            });
        }

        private static string PromptByAddress(int address)
        {
            var addressPromptMap = new Dictionary<int, string>
            {
                { 22000, "工作方式" },
                { 22001, "选择货架" },
                { 22002, "选择AB库" },
                { 22003, "选择储位" },
                { 22008, "启动入库" },
                { 22009, "启动出库" },
                { 22010, "启动移库" },
                { 22011, "系统启停" },
                { 22012, "系统复位" },
                { 22013, "急停/恢复" },
                { 22014, "暂停/恢复" },
                { 22015, "手动/自动切换" },
                { 22016, "报警故障解除后上位机重试" },
                { 22017, "X轴回原点" },
                { 22018, "Y轴回原点" },
                { 22019, "Z轴回原点" },
                { 22020, "伺服故障清除" },
                { 22030, "设置X轴速度" },
                { 22032, "设置Y轴速度" },
                { 22034, "设置Z轴速度" },
                { 22036, "Z轴右伸出长度" },
                { 23000, "设置通信状态" },
            };

            if (addressPromptMap.TryGetValue(address, out string prompt))
            {
                return prompt;
            }
            else
            {
                return "未知操作";
            }
        }
    }


}

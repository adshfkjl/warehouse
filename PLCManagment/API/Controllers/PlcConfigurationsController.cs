// PLCManagement.API/Controllers/PlcConfigurationsController.cs
using Microsoft.AspNetCore.Mvc;
using PLCManagement.API.Models;
using PLCManagement.API.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PLCManagement.API.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using PLCManagement.API.Models.Dtos;
using PLCManagement.API.Data;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlcConfigurationsController(IPlcService plcService, ILogger<PlcConfigurationsController> logService,IConfiguration configuration, ApplicationDbContext dbContext) : ControllerBase
    {
        private readonly IPlcService _plcService = plcService;
        //private readonly ILogService _logger;
        private readonly IConfiguration _configuration = configuration;
        private readonly ApplicationDbContext _dbContext = dbContext;

        private readonly ILogger<PlcConfigurationsController> _logger = logService;

        [HttpGet]
        public async Task<ActionResult<List<PlcConfiguration>>> GetAll()
        {
            return await _plcService.GetAllPlcConfigurations();
        }

        [HttpGet("current-plc-id")]
        public async Task<IActionResult> GetCurrentPlcId()
        {
            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            if (remoteIp == null)
            {
                return BadRequest(new { message = "无法获取客户端IP地址" });
            }

            var clientIp = remoteIp.IsIPv4MappedToIPv6
                ? remoteIp.MapToIPv4().ToString()
                : remoteIp.ToString();

            try
            {
                var plcId = await _dbContext.PlcConfigurations
                    .AsNoTracking()
                    .Where(p => p.IpAddressPC == clientIp)
                    .Select(p => p.PlcId)
                    .FirstOrDefaultAsync();

                if (plcId == null)
                {
                    return Ok(new { clientIp, plcId = "-1" });
                }

                return Ok(new { clientIp, plcId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据客户端IP查询PLC ID失败，ClientIp={ClientIp}", clientIp);
                return StatusCode(500, "根据客户端IP查询PLC ID失败");
            }
        }

        [HttpGet("{plcId}")]
        public async Task<ActionResult<PlcConfiguration>> Get(string plcId)
        {
            var plc = await _plcService.GetPlcConfiguration(plcId);
            if (plc == null)
            {
                return NotFound();
            }
            return plc;
        }

        [HttpPost]
        public async Task<ActionResult<PlcConfiguration>> Create(PlcConfiguration plc)
        {
            try
            {
                var createdPlc = await _plcService.AddPlcConfiguration(plc);
                return CreatedAtAction(nameof(Get), new { plcId = createdPlc.PlcId }, createdPlc);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPut("{plcId}")]
        public async Task<IActionResult> Update(string plcId, PlcConfiguration plc)
        {
            if (plcId != plc.PlcId)
            {
                return BadRequest("PLC ID mismatch");
            }

            try
            {
                await _plcService.UpdatePlcConfiguration(plcId, plc);
                return NoContent();
            }
            catch (ArgumentException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("{plcId}")]
        public async Task<IActionResult> Delete(string plcId)
        {
            try
            {
                await _plcService.DeletePlcConfiguration(plcId);
                return NoContent();
            }
            catch (ArgumentException ex)
            {
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("{plcId}/test-connection")]
        public async Task<ActionResult> TestConnection(string plcId)
        {
            var result = await _plcService.TestPlcConnection(plcId);
            if (result.IsSuccess)
            {
                return Ok(result);
            }
            return BadRequest(result);
        }

        [HttpPost("importbill")]
        public async Task<IActionResult> ImportBill([FromBody] ImportBillDto request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }


            try
            {
                var connectionString = _configuration.GetConnectionString("StoreHouseConnection");

                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new ArgumentNullException(nameof(connectionString),
                        "Database connection string is not configured.");
                }

                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    // Create command for the stored procedure
                    using (var command = new SqlCommand("importBill", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@Bil_ID", request.Bil_ID);
                        command.Parameters.AddWithValue("@Bil_NO", request.Bil_NO);

                        // Execute the stored procedure and read the results
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            var results = new List<BillDetailResponse>();

                            while (await reader.ReadAsync())
                            {
                                var item = new BillDetailResponse
                                {
                                    BillID = reader["BillID"].ToString(),
                                    BillNO = reader["BillNO"].ToString(),
                                    ITM = Convert.ToInt32(reader["ITM"]),
                                    MaterialNo = reader["MaterialNo"].ToString(),
                                    MaterialName = reader["MaterialName"].ToString(),
                                    Spc = reader["Spc"].ToString(),
                                    WareHouseNo = reader["WareHouseNo"].ToString(),
                                    BarNo = reader["BarNo"].ToString(),
                                    Price = Convert.ToDecimal(reader["Price"]),
                                    Qty = Convert.ToDecimal(reader["Qty"]),
                                    Total = Convert.ToDecimal(reader["Total"]),
                                    Remark = reader["Remark"].ToString(),
                                    PalletCode = reader["PalletCode"] != DBNull.Value ? reader["PalletCode"].ToString() : null,
                                    Quantity = reader["Quantity"] != DBNull.Value ? Convert.ToDecimal(reader["Quantity"]) : (decimal?)null,
                                    Flag = reader["Flag"].ToString(),
                                    ShelfStatus = Convert.ToInt32(reader["ShelfStatus"].ToString()),
                                    SortID = Convert.ToInt64(reader["SortID"])
                                };
                                results.Add(item);
                            }

                            return Ok(results);
                        }
                    }
                }
            }
            catch (SqlException sqlEx)
            {
                _logger.LogError(sqlEx, "Database error importing bill {Bil_ID}-{Bil_NO}", request.Bil_ID, request.Bil_NO);
                return StatusCode(500, "A database error occurred.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error importing bill {Bil_ID}-{Bil_NO}", request.Bil_ID, request.Bil_NO);
                return StatusCode(500, "An unexpected error occurred.");
            }
        }
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using PLCManagement.API.Models.Dtos;
using PLCManagement.API.Interfaces;

namespace PLCManagement.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(IAuthService authService, ILogger<AuthController> logger)
        {
            _authService = authService;
            _logger = logger;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] PLCManagement.API.Models.Dtos.LoginRequest request)
        {
            try
            {
                var result = await _authService.Authenticate(request.Username, request.Password, request.DeviceId);

                if (!result.Success)
                    return Unauthorized(new { message = result.Message });

                return Ok(new LoginResponse
                {
                    Token = result.Token,
                    RefreshToken = result.RefreshToken,
                    UserId = result.UserId,
                    FullName = result.FullName,
                    Roles = result.Roles,
                    Permissions = result.Permissions,
                    ExpiresIn = result.ExpiresIn
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "登录过程中发生错误");
                return StatusCode(500, new { message = "内部服务器错误" });
            }
        }

        [HttpPost("refresh-token")]
        [Authorize]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
        {
            var result = await _authService.RefreshToken(request.Token, request.RefreshToken);

            if (!result.Success)
                return Unauthorized(new { message = result.Message });

            return Ok(new
            {
                Token = result.Token,
                RefreshToken = result.RefreshToken,
                ExpiresIn = result.ExpiresIn
            });
        }

        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            var userId = int.Parse(User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value);
            await _authService.RevokeToken(userId);
            return Ok(new { message = "登出成功" });
        }
    }
}
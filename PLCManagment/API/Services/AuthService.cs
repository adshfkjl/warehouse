using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System;
using PLCManagement.API.Models.Entities;
using PLCManagement.API.Data;
using PLCManagement.API.Models.Dtos;
using PLCManagement.API.Interfaces;
using PLCManagement.API.Models.Configurations;
using Microsoft.EntityFrameworkCore;

namespace PLCManagement.API.Services
{
    public class AuthService : IAuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly JwtSettings _jwtSettings;
        private readonly ILogger<AuthService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AuthService(
            ApplicationDbContext context,
            IOptions<JwtSettings> jwtSettings,
            ILogger<AuthService> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _context = context;
            _jwtSettings = jwtSettings.Value;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<AuthResult> Authenticate(string username, string password, string deviceId)
        {
            var user = await _context.Users
                .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
                .ThenInclude(r => r.RolePermissions)
                .ThenInclude(rp => rp.Permission)
                .FirstOrDefaultAsync(u => u.Username == username && u.IsActive);

            if (user == null)
                return new AuthResult { Success = false, Message = "用户名或密码错误" };

            if (!VerifyPassword(password, user.PasswordHash, user.Salt))
                return new AuthResult { Success = false, Message = "用户名或密码错误" };

            // 更新最后登录时间
            user.LastLoginTime = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // 生成JWT令牌
            var token = GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken(user.Id, deviceId);

            // 保存刷新令牌
            _context.RefreshTokens.Add(refreshToken);
            await _context.SaveChangesAsync();

            // 获取用户权限
            var permissions = user.UserRoles
                .SelectMany(ur => ur.Role.RolePermissions)
                .Select(rp => rp.Permission.Code)
                .Distinct()
                .ToList();

            return new AuthResult
            {
                Success = true,
                Token = token,
                RefreshToken = refreshToken.Token,
                UserId = user.Id,
                FullName = user.FullName,
                Roles = user.UserRoles.Select(ur => ur.Role.Name).ToList(),
                Permissions = permissions,
                ExpiresIn = _jwtSettings.ExpireMinutes * 60
            };
        }

        private string GenerateJwtToken(User user)
        {
            // 在您的认证服务中
            var claims = new List<Claim>
            {
                new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new System.Security.Claims.Claim(ClaimTypes.Name, user.Username),
                new System.Security.Claims.Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            // 添加角色声明
            foreach (var role in user.UserRoles.Select(ur => ur.Role.Name))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.Secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expires = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpireMinutes);

            var token = new JwtSecurityToken(
                issuer: _jwtSettings.Issuer,
                audience: _jwtSettings.Audience,
                claims: claims,
                expires: expires,
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private RefreshToken GenerateRefreshToken(int userId, string deviceId)
        {
            return new RefreshToken
            {
                UserId = userId,
                Token = Guid.NewGuid().ToString(),
                Expires = DateTime.UtcNow.AddDays(_jwtSettings.RefreshExpireDays),
                Created = DateTime.UtcNow,
                CreatedByIp = _httpContextAccessor.HttpContext?.Connection?.RemoteIpAddress?.ToString()??"N/A",
                DeviceId = deviceId
            };
        }

        private bool VerifyPassword(string password, string storedHash, string salt)
        {
            // 实现密码验证逻辑
            var hash = HashPassword(password, salt);
            return hash == storedHash;
        }

        private string HashPassword(string password, string salt)
        {
            // 实现密码哈希逻辑
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(salt));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(hash);
        }

        public Task<AuthResult> RefreshToken(string token, string refreshToken)
        {
            throw new NotImplementedException();
        }

        public Task<bool> RevokeToken(int userId)
        {
            throw new NotImplementedException();
        }
    }
}
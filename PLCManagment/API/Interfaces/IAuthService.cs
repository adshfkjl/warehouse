// 文件路径：Interfaces/IAuthService.cs
using PLCManagement.API.Models;
using System.Threading.Tasks;
using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Interfaces
{
    public interface IAuthService
    {
        Task<AuthResult> Authenticate(string username, string password, string deviceId);
        Task<AuthResult> RefreshToken(string token, string refreshToken);
        Task<bool> RevokeToken(int userId);
    }
}
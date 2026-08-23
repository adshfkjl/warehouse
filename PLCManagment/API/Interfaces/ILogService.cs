// PLCManagement.API/Interfaces/ILogService.cs
using System.Threading.Tasks;

namespace PLCManagement.API.Interfaces
{
    public interface ILogService
    {
        Task LogInformation(string message);
        Task LogWarning(string message);
        Task LogError(string message);
        Task LogDebug(string message);
    }
}
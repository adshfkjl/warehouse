using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Interfaces
{
    public interface IInventoryCheckService
    {
        Task<InventoryCheckTaskResponse> CreateOutboundRangeTaskAsync(InventoryCheckOutboundRangeRequest request);
        Task<InventoryCheckTaskResponse?> GetTaskAsync(long taskId);
        Task<InventoryCheckTaskResponse> ScheduleInboundItemAsync(long itemId);
        Task<InventoryCheckTaskResponse> CancelTaskAsync(long taskId);
    }
}

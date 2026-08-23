namespace PLCManagement.API.Interfaces
{
    public interface IPlcMonitorService
    {
        Task MonitorAndExecuteInboundAsync(string plcId, int inShelf, int selectedInPosition,
            int loadingPoint, string billID, string billNo, int itm, double qty, string rem,
            long logEntryId, CancellationToken cancellationToken);
    }
}
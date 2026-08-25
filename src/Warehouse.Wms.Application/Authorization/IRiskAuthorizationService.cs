namespace Warehouse.Wms.Application.Authorization;

/// <summary>
/// Performs the second authorization check required by high-risk warehouse actions.
/// </summary>
public interface IRiskAuthorizationService
{
    Task<bool> AuthorizeAsync(
        string operation,
        ICurrentUser user,
        string taskNumber,
        string reason,
        CancellationToken cancellationToken = default);
}

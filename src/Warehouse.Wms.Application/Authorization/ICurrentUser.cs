namespace Warehouse.Wms.Application.Authorization;

/// <summary>
/// The authenticated identity used for auditable high-risk operations.
/// </summary>
public interface ICurrentUser
{
    string UserId { get; }
}

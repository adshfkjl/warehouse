using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Warehouse.Wms.Application.Identity;

namespace Warehouse.Wms.Api.Identity;

public sealed class HttpCurrentUserAccessor(IHttpContextAccessor httpContextAccessor) : IWarehouseScopedCurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    public string UserId
        => _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name)
            ?? throw new UnauthorizedAccessException("An authenticated user is required.");

    public IReadOnlySet<string> WarehouseIds
        => _httpContextAccessor.HttpContext?.User.FindAll("warehouse")
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

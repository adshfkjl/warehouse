using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Identity;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize(Roles = "Admin")]
public sealed class RolesController(IIdentityService identity) : ControllerBase
{
    private readonly IIdentityService _identity = identity ?? throw new ArgumentNullException(nameof(identity));

    [HttpGet]
    public ActionResult<IReadOnlyCollection<RoleDefinition>> List() => Ok(_identity.Roles);

    [HttpPost]
    public async Task<IActionResult> Create(RoleRequest request, CancellationToken cancellationToken)
    {
        await _identity.CreateRoleAsync(request.Name, cancellationToken);
        return Accepted(new { request.Name });
    }

    [HttpPost("{roleName}/permissions")]
    public async Task<IActionResult> GrantPermission(string roleName, PermissionRequest request, CancellationToken cancellationToken)
    {
        await _identity.GrantPermissionAsync(roleName, request.Permission, cancellationToken);
        return NoContent();
    }

    [HttpPost("{roleName}/users/{userId}")]
    public async Task<IActionResult> AssignRole(string roleName, string userId, CancellationToken cancellationToken)
    {
        await _identity.AssignRoleAsync(userId, roleName, cancellationToken);
        return NoContent();
    }
}

public sealed record RoleRequest(string Name);
public sealed record PermissionRequest(string Permission);

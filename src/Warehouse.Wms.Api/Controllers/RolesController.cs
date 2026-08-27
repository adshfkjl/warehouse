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
    public async Task<ActionResult<IReadOnlyCollection<RoleDefinition>>> List(CancellationToken cancellationToken)
        => Ok(await _identity.GetRolesAsync(cancellationToken));

    [HttpPost]
    public async Task<IActionResult> Create(RoleRequest request, CancellationToken cancellationToken)
    {
        await _identity.CreateRoleAsync(request.Name, ActorUserId(), cancellationToken);
        return Accepted(new { request.Name });
    }

    [HttpPost("{roleName}/permissions")]
    public async Task<IActionResult> GrantPermission(string roleName, PermissionRequest request, CancellationToken cancellationToken)
    {
        await _identity.GrantPermissionAsync(roleName, request.Permission, ActorUserId(), cancellationToken);
        return NoContent();
    }

    [HttpPost("{roleName}/users/{userId}")]
    public async Task<IActionResult> AssignRole(string roleName, string userId, CancellationToken cancellationToken)
    {
        await _identity.AssignRoleAsync(userId, roleName, ActorUserId(), cancellationToken);
        return NoContent();
    }

    private string ActorUserId()
        => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("Authenticated user identifier is required.");
}

public sealed record RoleRequest(string Name);
public sealed record PermissionRequest(string Permission);

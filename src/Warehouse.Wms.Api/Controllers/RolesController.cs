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
    public IActionResult Create(RoleRequest request)
    {
        _identity.CreateRole(request.Name);
        return Accepted(new { request.Name });
    }

    [HttpPost("{roleName}/permissions")]
    public IActionResult GrantPermission(string roleName, PermissionRequest request)
    {
        _identity.GrantPermission(roleName, request.Permission);
        return NoContent();
    }

    [HttpPost("{roleName}/users/{userId}")]
    public IActionResult AssignRole(string roleName, string userId)
    {
        _identity.AssignRole(userId, roleName);
        return NoContent();
    }
}

public sealed record RoleRequest(string Name);
public sealed record PermissionRequest(string Permission);

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Identity;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/users")]
public sealed class UsersController(IIdentityService identity) : ControllerBase
{
    private readonly IIdentityService _identity = identity ?? throw new ArgumentNullException(nameof(identity));

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<TokenPair>> Login(LoginRequest request, CancellationToken cancellationToken)
        => Ok(await _identity.LoginAsync(request, cancellationToken));

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<TokenPair>> Refresh(RefreshRequest request, CancellationToken cancellationToken)
        => Ok(await _identity.RefreshAsync(request.RefreshToken, cancellationToken));

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken cancellationToken)
    {
        await _identity.LogoutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public IActionResult Create(CreateUserRequest request)
    {
        _identity.CreateUser(request);
        return Accepted(new { request.UserId });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{userId}/disable")]
    public IActionResult Disable(string userId, DisableUserRequest request)
    {
        _identity.DisableUser(userId, request.Reason);
        return NoContent();
    }

    [Authorize]
    [HttpPost("{userId}/password")]
    public IActionResult ChangePassword(string userId, ChangePasswordRequest request)
    {
        var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.Equals(currentUserId, userId, StringComparison.OrdinalIgnoreCase) && !User.IsInRole("Admin"))
            return Forbid();

        _identity.ChangePassword(userId, request.CurrentPassword, request.NewPassword);
        return NoContent();
    }
}

public sealed record RefreshRequest(string RefreshToken);
public sealed record DisableUserRequest(string Reason);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

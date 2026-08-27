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
    public async Task<ActionResult<AccessTokenResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var tokens = await _identity.LoginAsync(request, cancellationToken);
        SetRefreshCookie(tokens);
        Response.Headers.CacheControl = "no-store";
        return Ok(new AccessTokenResponse(tokens.UserId, tokens.AccessToken, tokens.AccessTokenExpiresAt));
    }

    [AllowAnonymous]
    [HttpPost("session/refresh")]
    public async Task<ActionResult<AccessTokenResponse>> Refresh(CancellationToken cancellationToken)
    {
        EnsureSameOrigin();
        var refreshToken = Request.Cookies[RefreshCookieName] ?? throw new UnauthorizedAccessException("Invalid refresh token.");
        var tokens = await _identity.RefreshAsync(refreshToken, cancellationToken);
        SetRefreshCookie(tokens);
        Response.Headers.CacheControl = "no-store";
        return Ok(new AccessTokenResponse(tokens.UserId, tokens.AccessToken, tokens.AccessTokenExpiresAt));
    }

    [AllowAnonymous]
    [HttpPost("session/logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        EnsureSameOrigin();
        DeleteRefreshCookie();
        var refreshToken = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            try { await _identity.LogoutAsync(refreshToken, cancellationToken); }
            catch (UnauthorizedAccessException) { }
        }
        Response.Headers.CacheControl = "no-store";
        return NoContent();
    }

    [Authorize(Roles = "Admin")]
    [HttpPost]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        await _identity.CreateUserAsync(request, ActorUserId(), cancellationToken);
        return Accepted(new { request.UserId });
    }

    [Authorize(Roles = "Admin")]
    [HttpPost("{userId}/disable")]
    public async Task<IActionResult> Disable(string userId, DisableUserRequest request, CancellationToken cancellationToken)
    {
        await _identity.DisableUserAsync(userId, request.Reason, ActorUserId(), cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpPost("{userId}/password")]
    public async Task<IActionResult> ChangePassword(string userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.Equals(currentUserId, userId, StringComparison.OrdinalIgnoreCase) && !User.IsInRole("Admin"))
            return Forbid();

        await _identity.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, ActorUserId(), cancellationToken);
        return NoContent();
    }

    private const string RefreshCookieName = "wms_refresh";

    private void SetRefreshCookie(TokenPair tokens)
        => Response.Cookies.Append(RefreshCookieName, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/users/session",
            Expires = tokens.RefreshTokenExpiresAt
        });

    private void DeleteRefreshCookie()
        => Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/users/session"
        });

    private void EnsureSameOrigin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return;
        var expected = $"{Request.Scheme}://{Request.Host}";
        if (!string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Cross-origin credential request is not allowed.");
    }

    private string ActorUserId()
        => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("Authenticated user identifier is required.");
}

public sealed record DisableUserRequest(string Reason);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

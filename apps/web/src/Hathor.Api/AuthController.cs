using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Auth;
using Hathor.Application.Dtos;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class AuthController(IMediator mediator) : ControllerBase
{
    [HttpPost("register")]
    [EnableRateLimiting("Auth")]
    public async Task<ActionResult<AuthTokensDto>> Register(
        [FromBody] RegisterRequest body, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new RegisterCommand(
                body.Username, body.Password, body.RememberMe, DeviceLabel()), ct));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("login")]
    [EnableRateLimiting("Auth")]
    public async Task<ActionResult<AuthTokensDto>> Login(
        [FromBody] LoginRequest body, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new LoginCommand(
                body.Username, body.Password, body.RememberMe, DeviceLabel()), ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Invalid credentials" });
        }
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthTokensDto>> Refresh(
        [FromBody] RefreshRequest body, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new RefreshCommand(body.RefreshToken), ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { message = "Invalid refresh token" });
        }
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest? body, CancellationToken ct)
    {
        await mediator.Send(new LogoutCommand(body?.RefreshToken), ct);
        return Ok(new { message = "Logged out" });
    }

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        await mediator.Send(new LogoutAllCommand(CurrentUserId()), ct);
        return Ok(new { message = "Logged out everywhere" });
    }

    // Public registration gate for single-account mode (unauthenticated).
    [HttpGet("status")]
    [EnableRateLimiting("Auth")]
    public async Task<ActionResult<AuthStatusDto>> Status(
        [FromServices] Domain.Repositories.IUserRepository users, CancellationToken ct) =>
        Ok(new AuthStatusDto(!await users.AnyAsync(ct)));

    // Active login sessions for multi-login management.
    [HttpGet("sessions")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<SessionDto>>> Sessions(CancellationToken ct) =>
        Ok(await mediator.Send(new ListSessionsQuery(CurrentUserId(), CurrentSessionId()), ct));

    [HttpDelete("sessions/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct) =>
        await mediator.Send(new RevokeSessionCommand(CurrentUserId(), id), ct)
            ? NoContent()
            : NotFound();

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        userId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        username = User.Identity?.Name,
    });

    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("Auth")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest body, CancellationToken ct)
    {
        try
        {
            await mediator.Send(new ChangePasswordCommand(
                CurrentUserId(), body.CurrentPassword ?? "", body.NewPassword ?? ""), ct);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private string? DeviceLabel()
    {
        var ua = Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(ua) ? null : ua;
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Guid CurrentSessionId() =>
        Guid.TryParse(User.FindFirst("sid")?.Value, out var sid) ? sid : Guid.Empty;
}

public sealed record RegisterRequest(string Username, string Password, bool RememberMe = false);
public sealed record LoginRequest(string Username, string Password, bool RememberMe = false);
public sealed record RefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record AuthStatusDto(bool RegistrationOpen);

[ApiController]
[Route("api/v1/[controller]")]
// Key management needs full control: otherwise a scoped-down PAT could
// mint itself a full-access replacement (privilege escalation).
[Authorize(Policy = ScopeAuthorization.PlayerControl)]
public sealed class ApiKeysController(
    IApiKeyRepository keys,
    Application.Ports.IApiKeyService apiKeys) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ApiKeyDto>>> List(CancellationToken ct)
    {
        var userId = CurrentUserId();
        var list = await keys.ListForUserAsync(userId, ct);
        return Ok(list.Select(k => new ApiKeyDto(k.Id, k.Name, k.Prefix, k.Scopes, k.CreatedAtUtc)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CreatedApiKeyDto>> Create(
        [FromBody] CreateApiKeyRequest body, CancellationToken ct)
    {
        var userId = CurrentUserId();
        var allowed = new[] { Scopes.PlayerRead, Scopes.PlayerControl, Scopes.LibraryRead, Scopes.DownloadsWrite, Scopes.PlaylistsWrite, Scopes.LibraryWrite };
        var scopes = (body.Scopes ?? Array.Empty<string>())
            .Where(s => allowed.Contains(s))
            .Distinct()
            .ToArray();
        if (scopes.Length == 0)
            return BadRequest(new { message = "At least one valid scope is required." });

        var (token, prefix, hash) = apiKeys.Create();
        var key = new Domain.Entities.ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = body.Name.Trim(),
            Prefix = prefix,
            TokenHash = hash,
            Scopes = string.Join(' ', scopes),
            CreatedAtUtc = DateTime.UtcNow,
        };
        await keys.AddAsync(key, ct);
        await keys.SaveChangesAsync(ct);
        // The raw token is shown exactly once (like GitHub PATs).
        return CreatedAtAction(nameof(List), new CreatedApiKeyDto(
            key.Id, key.Name, key.Prefix, key.Scopes, key.CreatedAtUtc, token));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await keys.RevokeAsync(id, CurrentUserId(), ct);
        return NoContent();
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record CreateApiKeyRequest(string Name, string[]? Scopes);
public sealed record CreatedApiKeyDto(
    Guid Id, string Name, string Prefix, string Scopes, DateTime CreatedAtUtc, string Token);

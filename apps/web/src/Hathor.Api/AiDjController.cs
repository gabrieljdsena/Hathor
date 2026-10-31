using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.AiDj;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/ai-dj")]
public sealed class AiDjController(
    AiDjService service,
    AiDjPrefetcher prefetcher,
    TokenValidator tokens) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("start")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<AiDjStateDto>> Start(
        [FromBody] AiDjStartRequest? body, CancellationToken ct)
    {
        try
        {
            var state = await service.StartAsync(UserId, body ?? new AiDjStartRequest(null, null, null, null, null, null, null), ct);
            prefetcher.Queue(UserId);
            return Ok(state);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("state")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<AiDjStateDto>> State(CancellationToken ct)
    {
        var state = await service.GetAsync(UserId, ct);
        if (state.Status == "active") prefetcher.Queue(UserId);
        return Ok(state);
    }

    [HttpPost("stop")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<AiDjStateDto>> Stop(CancellationToken ct) =>
        Ok(await service.StopAsync(UserId, ct));

    [HttpPost("feedback")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<IActionResult> Feedback(
        [FromBody] AiDjFeedbackRequest? body, CancellationToken ct)
    {
        if (body is null) return BadRequest(new { message = "Feedback body is required." });
        await service.AddFeedbackAsync(UserId, body, ct);
        return NoContent();
    }

    [HttpPost("transcribe")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<object>> Transcribe(
        [FromBody] AiDjTranscribeRequest? body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Audio))
            return BadRequest(new { message = "Audio is required." });
        var text = await service.TranscribeAsync(body.Audio, body.Language, ct);
        return text is null
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Voice recognition unavailable." })
            : Ok(new { text });
    }

    [HttpGet("settings")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<AiDjSettingsDto>> GetSettings(CancellationToken ct) =>
        Ok(await service.GetSettingsAsync(UserId, ct));

    [HttpPut("settings")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<AiDjSettingsDto>> PutSettings(
        [FromBody] AiDjStartRequest? body, CancellationToken ct) =>
        Ok(await service.UpdateSettingsAsync(
            UserId, body ?? new AiDjStartRequest(null, null, null, null, null, null, null), ct));

    [HttpGet("clips/{id:guid}/audio")]
    [AllowAnonymous]
    public async Task<IActionResult> Clip(Guid id, [FromQuery] string? token, CancellationToken ct)
    {
        var userId = AuthenticatedUserId();
        if (userId is null && !string.IsNullOrWhiteSpace(token))
        {
            var principal = await tokens.ValidateAsync(token);
            if (principal is not null &&
                TokenValidator.SatisfiesLibraryRead(principal) &&
                Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var tokenUser))
                userId = tokenUser;
        }
        if (userId is null) return Unauthorized();
        var clip = await service.GetClipAsync(userId.Value, id, ct);
        if (clip is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(clip.Value.Path, clip.Value.Mime, enableRangeProcessing: true);
    }

    private Guid? AuthenticatedUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || User.Identity?.IsAuthenticated != true) return null;
        if (!ScopeAuthorization.SatisfiesLibraryRead(User)) return null;
        return Guid.TryParse(id, out var userId) ? userId : null;
    }
}

public sealed record AiDjTranscribeRequest(string Audio, string? Language);

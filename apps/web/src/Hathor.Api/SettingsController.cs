using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Library;
using Hathor.Application.Ports;
using Hathor.Application.Settings;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class SettingsController(
    IMediator mediator,
    ILibraryStorage storage,
    TokenValidator tokens) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<UserSettingsDto>> Get(CancellationToken ct) =>
        Ok(await mediator.Send(new GetSettingsQuery(CurrentUserId()), ct));

    [HttpPut]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<UserSettingsDto>> Put(
        [FromBody] UpdateSettingsRequest body, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new UpdateSettingsCommand(
                CurrentUserId(), body.Volume, body.LimitDownloads,
                body.CrossfadeEnabled, body.CrossfadeSeconds,
                body.LastRoute, body.Browser), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("background")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    [RequestSizeLimit(10_000_000)]
    public async Task<ActionResult<BackgroundDto>> UploadBackground(
        IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "An image file is required." });
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        try
        {
            var filename = await mediator.Send(new SetBackgroundCommand(
                CurrentUserId(), ms.ToArray(), file.ContentType), ct);
            return Ok(new BackgroundDto(filename));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("background")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> RemoveBackground(CancellationToken ct)
    {
        await mediator.Send(new RemoveBackgroundCommand(CurrentUserId()), ct);
        return NoContent();
    }

    // Served to CSS url() which cannot set headers — same header-or-?token=
    // auth as media streaming (songs/podcasts), validated identically.
    // Mutable single URL (re-upload keeps the name) so it must never be
    // cached — otherwise the new background needs a hard refresh to show.
    [HttpGet("background/file")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> BackgroundFile([FromQuery] string? token, CancellationToken ct)
    {
        var userId = AuthenticatedUserId();
        if (userId is null && !string.IsNullOrEmpty(token))
        {
            var principal = await tokens.ValidateAsync(token);
            if (principal is not null &&
                TokenValidator.HasScope(principal, ScopeAuthorization.LibraryRead) &&
                Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var tokenUser))
                userId = tokenUser;
        }
        if (userId is null) return Unauthorized();
        var settings = await mediator.Send(new GetSettingsQuery(userId.Value), ct);
        var file = settings.BackgroundPath is not null
            ? settings.BackgroundPath
            : storage.FindBackground(userId.Value);
        if (file is null) return NotFound();
        var path = storage.BackgroundPath(userId.Value, file);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, MimeFor(file));
    }

    [HttpPost("/api/v1/songs/scan")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<ScanResultDto>> ScanSongs(CancellationToken ct)
    {
        var result = await mediator.Send(new ScanSongsCommand(CurrentUserId()), ct);
        return Ok(new ScanResultDto(result.Added, result.Updated));
    }

    // Loudness backfill: analyze up to `limit` unmeasured files (resumable).
    [HttpPost("/api/v1/songs/loudness/backfill")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<LoudnessBackfillDto>> BackfillLoudness(
        [FromQuery] int limit = 20, CancellationToken ct = default) =>
        Ok(await mediator.Send(new BackfillLoudnessCommand(CurrentUserId(), limit), ct));

    private static string MimeFor(string file) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Header-authenticated user with the library scope, or null (falls back to ?token=).
    private Guid? AuthenticatedUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || !User.Identity?.IsAuthenticated == true) return null;
        if (!User.FindAll("scope").Any(c => c.Value == ScopeAuthorization.LibraryRead)) return null;
        return Guid.TryParse(id, out var userId) ? userId : null;
    }
}

public sealed record UpdateSettingsRequest(
    double? Volume,
    int? LimitDownloads,
    bool? CrossfadeEnabled,
    double? CrossfadeSeconds,
    string? LastRoute,
    string? Browser);
public sealed record BackgroundDto(string Filename);

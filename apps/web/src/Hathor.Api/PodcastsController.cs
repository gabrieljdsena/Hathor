using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Podcasts;
using Hathor.Application.Ports;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class PodcastsController(
    IMediator mediator,
    ILibraryStorage storage,
    TokenValidator tokens) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> List(CancellationToken ct) =>
        Ok(await mediator.Send(new ListPodcastsQuery(CurrentUserId()), ct));

    [HttpGet("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<SongDto>> Details(string file, CancellationToken ct)
    {
        var episode = await mediator.Send(new GetPodcastDetailsQuery(CurrentUserId(), file), ct);
        return episode is null ? NotFound() : Ok(episode);
    }

    [HttpPatch("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<SongDto>> Patch(
        string file, [FromBody] UpdatePodcastRequest body, CancellationToken ct)
    {
        try
        {
            var updated = await mediator.Send(new UpdatePodcastMetadataCommand(
                CurrentUserId(), file, body.Title, body.Artist, body.CoverArt), ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    [HttpDelete("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> Delete(string file, CancellationToken ct)
    {
        try
        {
            return await mediator.Send(new DeletePodcastCommand(CurrentUserId(), file), ct)
                ? NoContent()
                : NotFound();
        }
        catch (IOException)
        {
            return Conflict(new { message = "File is in use and could not be deleted." });
        }
    }

    // Byte-range streaming with the same header-or-?token= auth as songs.
    [HttpGet("{file}/stream")]
    [AllowAnonymous]
    public async Task<IActionResult> Stream(string file, [FromQuery] string? token)
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
        if (!storage.PodcastExists(userId.Value, file)) return NotFound();
        return PhysicalFile(storage.PodcastPath(userId.Value, file), "audio/mpeg", enableRangeProcessing: true);
    }

    [HttpPost("scan")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<ScanResultDto>> Scan(CancellationToken ct)
    {
        var result = await mediator.Send(
            new Application.Library.ScanPodcastsCommand(CurrentUserId()), ct);
        return Ok(new ScanResultDto(result.Added, result.Updated));
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Guid? AuthenticatedUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || !User.Identity?.IsAuthenticated == true) return null;
        if (!User.FindAll("scope").Any(c => c.Value == ScopeAuthorization.LibraryRead)) return null;
        return Guid.TryParse(id, out var userId) ? userId : null;
    }
}

public sealed record UpdatePodcastRequest(string? Title, string? Artist, string? CoverArt);
public sealed record ScanResultDto(int Added, int Updated);

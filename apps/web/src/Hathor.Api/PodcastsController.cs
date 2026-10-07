using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Podcasts;
using Hathor.Application.PodcastTimestamps;
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

    // 202 when the episode is playing: stashed, applies on track change.
    [HttpPatch("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<UpdatePodcastMetadataResult>> Patch(
        string file, [FromBody] UpdatePodcastRequest body, CancellationToken ct)
    {
        try
        {
            var updated = await mediator.Send(new UpdatePodcastMetadataCommand(
                CurrentUserId(), file, body.Title, body.Artist, body.CoverArt), ct);
            if (updated is null) return NotFound();
            return updated.Pending ? Accepted(updated) : Ok(updated);
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
                TokenValidator.SatisfiesLibraryRead(principal) &&
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

    // Move an episode to the songs library: file moves on disk, rows migrate
    // tables (same cleanup as delete), queue entries stay valid.
    [HttpPost("{file}/move-to-songs")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<SongDto>> MoveToSongs(string file, CancellationToken ct)
    {
        var result = await mediator.Send(new Application.Library.MovePodcastToSongCommand(CurrentUserId(), file), ct);
        return result switch
        {
            Application.Library.LibraryMoveResult.Moved m => Ok(m.Song),
            Application.Library.LibraryMoveResult.Conflict c => Conflict(new { message = c.Message }),
            _ => NotFound(),
        };
    }

    // Episode chapter marks ("timestamps"): start offset + name, optional
    // end offset. Times are media offsets in seconds.
    [HttpGet("{file}/timestamps")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<PodcastTimestampDto>>> ListTimestamps(
        string file, CancellationToken ct)
    {
        var rows = await mediator.Send(new ListPodcastTimestampsQuery(CurrentUserId(), file), ct);
        return rows is null ? NotFound() : Ok(rows);
    }

    [HttpPost("{file}/timestamps")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<PodcastTimestampDto>> CreateTimestamp(
        string file, [FromBody] PodcastTimestampRequest body, CancellationToken ct)
    {
        var result = await mediator.Send(new CreatePodcastTimestampCommand(
            CurrentUserId(), file, body.Name ?? "",
            body.StartSecs ?? -1, body.EndSecs), ct);
        return MapWriteResult(result, created: true);
    }

    [HttpPut("{file}/timestamps/{id:long}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<PodcastTimestampDto>> UpdateTimestamp(
        string file, long id, [FromBody] PodcastTimestampRequest body, CancellationToken ct)
    {
        var result = await mediator.Send(new UpdatePodcastTimestampCommand(
            CurrentUserId(), file, id, body.Name ?? "",
            body.StartSecs ?? -1, body.EndSecs), ct);
        return MapWriteResult(result, created: false);
    }

    [HttpDelete("{file}/timestamps/{id:long}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> DeleteTimestamp(string file, long id, CancellationToken ct) =>
        await mediator.Send(new DeletePodcastTimestampCommand(CurrentUserId(), file, id), ct)
            ? NoContent()
            : NotFound();

    private ActionResult<PodcastTimestampDto> MapWriteResult(
        PodcastTimestampWriteResult result, bool created)
    {
        return result switch
        {
            PodcastTimestampWriteResult.Created c => created
                ? CreatedAtAction(
                    nameof(ListTimestamps), new { file = c.Timestamp.PodcastFile }, c.Timestamp)
                : Ok(c.Timestamp),
            PodcastTimestampWriteResult.Updated u => Ok(u.Timestamp),
            PodcastTimestampWriteResult.EpisodeNotFound => NotFound(),
            PodcastTimestampWriteResult.TimestampNotFound => NotFound(),
            PodcastTimestampWriteResult.Invalid i => BadRequest(new { message = i.Message }),
            _ => BadRequest(new { message = "Invalid timestamp." }),
        };
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Guid? AuthenticatedUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || !User.Identity?.IsAuthenticated == true) return null;
        if (!ScopeAuthorization.SatisfiesLibraryRead(User)) return null;
        return Guid.TryParse(id, out var userId) ? userId : null;
    }
}

public sealed record UpdatePodcastRequest(string? Title, string? Artist, string? CoverArt);
public sealed record PodcastTimestampRequest(string? Name, double? StartSecs, double? EndSecs);
public sealed record ScanResultDto(int Added, int Updated);

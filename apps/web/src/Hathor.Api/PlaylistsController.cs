using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Playlists;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class PlaylistsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<IReadOnlyList<PlaylistDto>>> List(CancellationToken ct) =>
        Ok(await mediator.Send(new ListPlaylistsQuery(CurrentUserId()), ct));

    [HttpGet("with-counts")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<IReadOnlyList<PlaylistWithCountDto>>> ListWithCounts(CancellationToken ct) =>
        Ok(await mediator.Send(new ListPlaylistsWithCountsQuery(CurrentUserId()), ct));

    [HttpGet("{id:long}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<PlaylistDto>> Get(long id, CancellationToken ct)
    {
        var playlist = await mediator.Send(new GetPlaylistQuery(CurrentUserId(), id), ct);
        return playlist is null ? NotFound() : Ok(playlist);
    }

    [HttpPost]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<ActionResult<PlaylistDto>> Create(
        [FromBody] CreatePlaylistRequest body, CancellationToken ct)
    {
        try
        {
            var created = await mediator.Send(
                new CreatePlaylistCommand(CurrentUserId(), body.Title, body.Description, body.Cover), ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<ActionResult<PlaylistDto>> Update(
        long id, [FromBody] UpdatePlaylistRequest body, CancellationToken ct)
    {
        var updated = await mediator.Send(
            new UpdatePlaylistCommand(CurrentUserId(), id, body.Title, body.Description, body.Thumbnail), ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    [HttpDelete("{id:long}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) =>
        await mediator.Send(new DeletePlaylistCommand(CurrentUserId(), id), ct)
            ? NoContent()
            : NotFound();

    [HttpGet("{id:long}/songs")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> Songs(long id, CancellationToken ct) =>
        Ok(await mediator.Send(new GetPlaylistSongsQuery(CurrentUserId(), id), ct));

    [HttpPost("{id:long}/songs")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> AddSong(
        long id, [FromBody] PlaylistSongRequest body, CancellationToken ct) =>
        await mediator.Send(new AddSongToPlaylistCommand(CurrentUserId(), id, body.File), ct)
            ? Ok()
            : NotFound();

    [HttpDelete("{id:long}/songs/{file}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> RemoveSong(long id, string file, CancellationToken ct) =>
        await mediator.Send(new RemoveSongFromPlaylistCommand(CurrentUserId(), id, file), ct)
            ? NoContent()
            : NotFound();

    [HttpPost("{id:long}/cover")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<ActionResult<PlaylistDto>> SetCover(
        long id, [FromBody] PlaylistCoverRequest body, CancellationToken ct)
    {
        var updated = await mediator.Send(
            new UpdatePlaylistCommand(CurrentUserId(), id, null, null, body.Cover), ct);
        return updated is null ? NotFound() : Ok(updated);
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record CreatePlaylistRequest(string Title, string? Description, string? Cover);
public sealed record UpdatePlaylistRequest(string? Title, string? Description, string? Thumbnail);
public sealed record PlaylistSongRequest(string File);
public sealed record PlaylistCoverRequest(string? Cover);

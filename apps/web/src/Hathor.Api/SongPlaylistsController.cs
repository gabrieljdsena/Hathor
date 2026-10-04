using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Playlists;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

// Song↔playlist membership: per-song lookup + bulk replace (desktop
// get_song_playlists / update_song_playlists, used by the Add-to-Playlist modal).
[ApiController]
[Route("api/v1/songs/{file}/playlists")]
public sealed class SongPlaylistsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<IReadOnlyList<long>>> Get(string file, CancellationToken ct) =>
        Ok(await mediator.Send(new GetSongPlaylistsQuery(CurrentUserId(), file), ct));

    [HttpPut]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> Set(
        string file, [FromBody] SetSongPlaylistsRequest body, CancellationToken ct)
    {
        await mediator.Send(new SetSongPlaylistsCommand(
            CurrentUserId(), file, body.Title ?? file, body.PlaylistIds ?? []), ct);
        return Ok();
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record SetSongPlaylistsRequest(string? Title, IReadOnlyList<long>? PlaylistIds);

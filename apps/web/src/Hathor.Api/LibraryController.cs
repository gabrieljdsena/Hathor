using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Enrichment;
using Hathor.Application.Library;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

// Artist/album browsing (desktop artist.html / album.html).
[ApiController]
[Route("api/v1")]
[Authorize(Policy = ScopeAuthorization.LibraryRead)]
public sealed class LibraryController(IMediator mediator) : ControllerBase
{
    [HttpGet("artists")]
    public async Task<ActionResult<IReadOnlyList<string>>> Artists(CancellationToken ct) =>
        Ok(await mediator.Send(new ListArtistsQuery(CurrentUserId()), ct));

    [HttpGet("artists/{name}/songs")]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> ArtistSongs(string name, CancellationToken ct) =>
        Ok(await mediator.Send(new ArtistSongsQuery(CurrentUserId(), name), ct));

    // Artist portrait for the detail header (iTunes 600x600, exact-match
    // preference). 404 → client shows the music-note fallback.
    [HttpGet("artists/{name}/image")]
    public async Task<ActionResult<ArtworkDto>> ArtistImage(string name, CancellationToken ct)
    {
        var art = await mediator.Send(new ArtistImageQuery(name), ct);
        return art is null ? NotFound() : Ok(art);
    }

    [HttpGet("albums")]
    public async Task<ActionResult<IReadOnlyList<string>>> Albums(CancellationToken ct) =>
        Ok(await mediator.Send(new ListAlbumsQuery(CurrentUserId()), ct));

    [HttpGet("albums/{title}/songs")]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> AlbumSongs(string title, CancellationToken ct) =>
        Ok(await mediator.Send(new AlbumSongsQuery(CurrentUserId(), title), ct));

    // Album cover for the detail header (artist disambiguates same-name
    // albums). 404 → client shows the music-note fallback.
    [HttpGet("albums/{title}/image")]
    public async Task<ActionResult<ArtworkDto>> AlbumImage(
        string title, [FromQuery] string? artist, CancellationToken ct)
    {
        var art = await mediator.Send(new AlbumImageQuery(title, artist), ct);
        return art is null ? NotFound() : Ok(art);
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

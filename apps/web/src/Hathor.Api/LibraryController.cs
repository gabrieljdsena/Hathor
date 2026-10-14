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
    public async Task<ActionResult<IReadOnlyList<SongDto>>> ArtistSongs(
        string name, [FromQuery] string? search, [FromQuery] string? sort, [FromQuery] string? dir,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(
                new ArtistSongsQuery(CurrentUserId(), name, search, sort, dir, page, pageSize), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("artists/{name}/songs/count")]
    public async Task<ActionResult<int>> ArtistSongsCount(
        string name, [FromQuery] string? search, CancellationToken ct) =>
        Ok(await mediator.Send(new ArtistSongCountQuery(CurrentUserId(), name, search), ct));

    [HttpGet("artists/{name}/files")]
    public async Task<ActionResult<IReadOnlyList<string>>> ArtistFiles(
        string name, [FromQuery] string? search,
        [FromQuery] string? sort, [FromQuery] string? dir, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new ArtistFilesQuery(CurrentUserId(), name, search, sort, dir), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

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
    public async Task<ActionResult<IReadOnlyList<SongDto>>> AlbumSongs(
        string title, [FromQuery] string? search, [FromQuery] string? sort, [FromQuery] string? dir,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(
                new AlbumSongsQuery(CurrentUserId(), title, search, sort, dir, page, pageSize), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("albums/{title}/songs/count")]
    public async Task<ActionResult<int>> AlbumSongsCount(
        string title, [FromQuery] string? search, CancellationToken ct) =>
        Ok(await mediator.Send(new AlbumSongCountQuery(CurrentUserId(), title, search), ct));

    [HttpGet("albums/{title}/files")]
    public async Task<ActionResult<IReadOnlyList<string>>> AlbumFiles(
        string title, [FromQuery] string? search,
        [FromQuery] string? sort, [FromQuery] string? dir, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new AlbumFilesQuery(CurrentUserId(), title, search, sort, dir), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

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

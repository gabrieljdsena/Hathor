using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Metadata;
using Hathor.Application.Ports;
using Hathor.Application.Songs;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class SongsController(
    IMediator mediator,
    ILibraryStorage storage,
    TokenValidator tokens) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> List(
        [FromQuery] string? search, [FromQuery] string? sort, [FromQuery] string? dir,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        try
        {
            return Ok(await mediator.Send(
                new ListSongsQuery(CurrentUserId(), search, sort, dir, page, pageSize), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("count")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<int>> Count([FromQuery] string? search, CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetSongCountQuery(CurrentUserId(), search), ct));

    [HttpGet("recently-played")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> RecentlyPlayed(
        [FromQuery] int limit = 15, CancellationToken ct = default) =>
        Ok(await mediator.Send(new Application.History.GetRecentlyPlayedQuery(CurrentUserId(), limit), ct));

    [HttpGet("recently-downloaded")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<SongDto>>> RecentlyDownloaded(
        [FromQuery] int limit = 15, CancellationToken ct = default) =>
        Ok(await mediator.Send(new Application.History.GetRecentlyDownloadedQuery(CurrentUserId(), limit), ct));

    [HttpGet("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<SongDto>> Get(
        string file, [FromQuery] bool includeCover = false, CancellationToken ct = default)
    {
        var song = await mediator.Send(new GetSongQuery(CurrentUserId(), file, includeCover), ct);
        return song is null ? NotFound() : Ok(song);
    }

    // Metadata edit (desktop update_song_metadata): null = keep existing,
    // CoverArt = data: URL | http(s) URL | "REMOVE" | null. Returns resumeSec
    // when the edited file is currently playing (client seeks back after reload).
    // 202 when the file is playing: the payload is stashed and applies on
    // track change (gapless playback), with Pending = true.
    [HttpPatch("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<UpdateSongMetadataResult>> Patch(
        string file, [FromBody] UpdateSongMetadataRequest body, CancellationToken ct)
    {
        try
        {
            // TEMPORARY payload shape diagnostics (no cover bytes logged).
            Serilog.Log.Information(
                "PATCH {File}: title={TitleLen} artist={ArtistLen} album={AlbumLen} year={YearLen} genre={GenreLen} cover={CoverKind}",
                file, body.Title?.Length ?? -1, body.Artist?.Length ?? -1,
                body.Album?.Length ?? -1, body.Year?.Length ?? -1, body.Genre?.Length ?? -1,
                body.CoverArt is null ? "null"
                    : body.CoverArt == "REMOVE" ? "REMOVE"
                    : body.CoverArt.StartsWith("data:") ? $"data:{body.CoverArt.Length}"
                    : "url");
            var result = await mediator.Send(new UpdateSongMetadataCommand(
                CurrentUserId(), file, body.Title, body.Artist, body.Album,
                body.Year, body.Genre, body.CoverArt), ct);
            if (result is null) return NotFound();
            return result.Pending ? Accepted(result) : Ok(result);
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
    }

    // Delete file + Song_Playlist/Lyrics/Music_History/Songs rows + tombstones
    // + queue eviction (desktop delete_song).
    [HttpDelete("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> Delete(string file, CancellationToken ct)
    {
        try
        {
            return await mediator.Send(new DeleteSongCommand(CurrentUserId(), file), ct)
                ? NoContent()
                : NotFound();
        }
        catch (IOException)
        {
            return Conflict(new { message = "File is in use and could not be deleted." });
        }
    }

    // Move a song to the podcasts library: file moves on disk, rows migrate
    // tables (same cleanup as delete), queue entries stay valid.
    [HttpPost("{file}/move-to-podcasts")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<SongDto>> MoveToPodcasts(string file, CancellationToken ct)
    {
        var result = await mediator.Send(new Application.Library.MoveSongToPodcastCommand(CurrentUserId(), file), ct);
        return result switch
        {
            Application.Library.LibraryMoveResult.Moved m => Ok(m.Song),
            Application.Library.LibraryMoveResult.Conflict c => Conflict(new { message = c.Message }),
            _ => NotFound(),
        };
    }

    // Byte-range streaming for web <audio> and external players.
    // Auth: Authorization header (Bearer <jwt|hth_...>) or ?token= query
    // (media elements cannot set headers — validated with identical rules).
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
        if (!storage.SongExists(userId.Value, file)) return NotFound();
        return PhysicalFile(storage.SongPath(userId.Value, file), "audio/mpeg", enableRangeProcessing: true);
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    // Header-authenticated user with library-read access (mirrors the
    // LibraryRead policy), or null (falls back to ?token=).
    private Guid? AuthenticatedUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null || !User.Identity?.IsAuthenticated == true) return null;
        if (!ScopeAuthorization.SatisfiesLibraryRead(User)) return null;
        return Guid.TryParse(id, out var userId) ? userId : null;
    }
}

public sealed record UpdateSongMetadataRequest(
    string? Title, string? Artist, string? Album, string? Year, string? Genre, string? CoverArt);

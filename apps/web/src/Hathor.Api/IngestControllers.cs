using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Application.Enrichment;
using Hathor.Application.Lyrics;
using Hathor.Domain.Services;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/youtube")]
[Authorize(Policy = ScopeAuthorization.LibraryRead)]
public sealed class YoutubeController(
    IDownloadEngine engine,
    IHttpClientFactory httpFactory,
    ILogger<YoutubeController> log) : ControllerBase
{
    // Flat search, no download (desktop search_yt).
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<VideoHitDto>>> Search(
        [FromQuery] string q, [FromQuery] int limit = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { message = "Query is required." });
        try
        {
            return Ok(await engine.SearchAsync(q, Math.Clamp(limit, 1, 25), ct));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"Search failed: {ex.Message}" });
        }
    }

    // Preview fallback for embedding-disabled videos (error 153 / "Watch on
    // YouTube"): probes YouTube oEmbed, then resolves a direct audio stream
    // URL for an <audio> element. embeddable=false → play audioUrl;
    // audioUrl=null → neither works (show a watch link instead). Stream URLs
    // expire and are loosely IP-bound: clients must use them immediately.
    // resolveAudio forces the (slower) manifest lookup even for embeddable
    // videos — the player uses it when the iframe itself reports an error
    // after an optimistic oEmbed pass.
    [HttpGet("preview")]
    public async Task<ActionResult<PreviewDto>> Preview(
        [FromQuery] string id, [FromQuery] bool resolveAudio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(c =>
                char.IsLetterOrDigit(c) || c is '-' or '_'))
            return BadRequest(new { message = "A valid video id is required." });
        var pageUrl = $"https://www.youtube.com/watch?v={id}";
        var embeddable = await IsEmbeddableAsync(pageUrl, ct);
        var audioUrl = !embeddable || resolveAudio
            ? await engine.GetPreviewUrlAsync(pageUrl, ct)
            : null;
        return Ok(new PreviewDto(id, embeddable, audioUrl));
    }

    private async Task<bool> IsEmbeddableAsync(string pageUrl, CancellationToken ct)
    {
        // oEmbed answers 200 for embeddable videos, 401/404 for blocked ones.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var http = httpFactory.CreateClient("metadata");
            using var res = await http.GetAsync(
                "https://www.youtube.com/oembed?url=" + Uri.EscapeDataString(pageUrl),
                timeout.Token);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "oEmbed probe failed, assuming embeddable");
            return true;
        }
    }
}

public sealed record PreviewDto(string Id, bool Embeddable, string? AudioUrl);

[ApiController]
[Route("api/v1/metadata")]
[Authorize(Policy = ScopeAuthorization.LibraryRead)]
public sealed class MetadataController(IMediator mediator) : ControllerBase
{
    [HttpGet("itunes")]
    public async Task<ActionResult<ITunesHitDto>> Single(
        [FromQuery] string title, [FromQuery] string? artist, CancellationToken ct) =>
        Ok(await mediator.Send(new ITunesSearchQuery(Guid.Empty, title, artist), ct));

    [HttpGet("itunes/options")]
    public async Task<ActionResult<IReadOnlyList<ITunesHitDto>>> Options(
        [FromQuery] string title, [FromQuery] string? artist, [FromQuery] int limit = 5,
        CancellationToken ct = default) =>
        Ok(await mediator.Send(new ITunesOptionsQuery(Guid.Empty, title, artist, limit), ct));

    [HttpGet("trending")]
    [ResponseCache(Duration = 3600)]
    public async Task<ActionResult<IReadOnlyList<string>>> Trending(
        [FromQuery] int limit = 4, CancellationToken ct = default) =>
        Ok(await mediator.Send(new TrendingQuery(limit), ct));
}

[ApiController]
[Route("api/v1/lyrics")]
[Authorize(Policy = ScopeAuthorization.LibraryRead)]
public sealed class LyricsQueryController(IMediator mediator) : ControllerBase
{
    // lrclib lookup without touching the cache (desktop get_lyrics fetch path
    // minus the current-song coupling — callers pass explicit context).
    [HttpGet]
    public async Task<ActionResult<LyricsDto>> Get(
        [FromQuery] string track, [FromQuery] string artist,
        [FromQuery] string? album, [FromQuery] int? duration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track))
            return BadRequest(new { message = "Track is required." });
        var (cleanTrack, cleanArtist) = LyricsCleaning.CleanTrackArtist(track, artist);
        LyricsDto? dto = LyricsCleaning.ArtistUsable(cleanArtist)
            ? await mediator.Send(new RawExactQuery(cleanTrack, cleanArtist, duration), ct)
            : await mediator.Send(new RawTrackOnlyQuery(cleanTrack, duration), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost("search")]
    public async Task<ActionResult<IReadOnlyList<LyricsHitDto>>> Search(
        [FromBody] LyricsSearchRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Track))
            return BadRequest(new { message = "Track is required." });
        return Ok(await mediator.Send(new SearchLyricsQuery(
            Guid.Empty, body.Track, body.Artist ?? "", body.Album, body.Duration), ct));
    }

    // Kana → romaji (MeCab backend when Python is available, kana table
    // fallback; timestamps preserved for LRC).
    [HttpPost("romanize")]
    public async Task<ActionResult<RomanizeResponse>> Romanize(
        [FromBody] RomanizeRequest body, CancellationToken ct) =>
        Ok(new RomanizeResponse(await mediator.Send(
            new RomanizeTextQuery(body.Text ?? "", body.IsLrc), ct)));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record LyricsSearchRequest(string? Track, string? Artist, string? Album, int? Duration);
public sealed record RomanizeRequest(string? Text, bool IsLrc);
public sealed record RomanizeResponse(string Text);

[ApiController]
[Route("api/v1/songs/{file}/lyrics")]
[Authorize(Policy = ScopeAuthorization.LibraryRead)]
public sealed class SongLyricsController(IMediator mediator) : ControllerBase
{
    // Cached lyrics for one file, fetching + caching on miss.
    // refresh=true skips the cache and re-fetches (wrong cached lyrics).
    // chapterId selects per-chapter podcast lyrics (track = chapter name,
    // artist empty); unknown ids fall back to the file flow.
    [HttpGet]
    public async Task<ActionResult<LyricsDto>> Get(
        string file, [FromQuery] string track, [FromQuery] string artist,
        [FromQuery] string? album, [FromQuery] int? duration,
        [FromQuery] bool refresh = false, [FromQuery] long? chapterId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track))
            return BadRequest(new { message = "Track is required." });
        var dto = await mediator.Send(new GetLyricsQuery(
            CurrentUserId(), file, track, artist ?? "", album, duration, refresh, chapterId), ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    // Manual save (candidate pick from search results). chapterId stores
    // under the chapter key so the chapter view reads back the pick.
    [HttpPut]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> Save(
        string file, [FromBody] SaveLyricsRequest body,
        [FromQuery] long? chapterId, CancellationToken ct) =>
        await mediator.Send(new SaveLyricsCommand(
            CurrentUserId(), file, body.Synced, body.Plain, chapterId), ct)
            ? NoContent()
            : BadRequest(new { message = "Empty lyrics cannot be saved." });

    // Manual removal (wrong lyrics): drops the cached row so the next read
    // re-fetches instead of serving the stale entry.
    [HttpDelete]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> Delete(
        string file, [FromQuery] long? chapterId, CancellationToken ct) =>
        await mediator.Send(new DeleteLyricsCommand(CurrentUserId(), file, chapterId), ct)
            ? NoContent()
            : NotFound();

    // Highlight timing correction, milliseconds (-20000..20000, clamped).
    // Zero removes the row when it holds no lyrics (sparse storage).
    [HttpGet("offset")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<LyricsOffsetDto>> GetOffset(string file, CancellationToken ct) =>
        Ok(new LyricsOffsetDto(await mediator.Send(
            new GetLyricsOffsetQuery(CurrentUserId(), file), ct)));

    [HttpPut("offset")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<LyricsOffsetDto>> SetOffset(
        string file, [FromBody] SetLyricsOffsetRequest body, CancellationToken ct) =>
        Ok(new LyricsOffsetDto(await mediator.Send(new SetLyricsOffsetCommand(
            CurrentUserId(), file, body.OffsetMs ?? 0), ct)));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record SaveLyricsRequest(string? Synced, string? Plain);
public sealed record SetLyricsOffsetRequest(int? OffsetMs);
public sealed record LyricsOffsetDto(int OffsetMs);

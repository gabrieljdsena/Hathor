using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Ports;
using Hathor.Application.Sync;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

// Snapshot exchange with desktop/mobile clients (desktop sync pull/push,
// adapted): export everything (history incremental via sinceId), import and
// merge anything present, pull/push per library against the desktop TiDB
// remote. There is no separate /sync/push — pushes from snapshot clients
// are POST /sync/import; remote pushes are the pull-/push- endpoints below.
//
// Device sync (replaces the remote DB): GET /sync/delta?cursor= for
// incremental pulls, GET/PUT /sync/files/{file} for raw MP3 bytes, and
// POST /sync/import (whose result names server-missing files to upload).
[ApiController]
[Route("api/v1/[controller]")]
public sealed class SyncController(IMediator mediator, ILibraryStorage storage) : ControllerBase
{
    [HttpGet("export")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<SyncSnapshot>> Export(
        [FromQuery] long sinceId = 0, CancellationToken ct = default) =>
        Ok(await mediator.Send(new ExportSnapshotQuery(CurrentUserId(), sinceId), ct));

    [HttpPost("import")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<ImportResult>> Import(
        [FromBody] SyncSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot is null)
            return BadRequest(new { message = "A snapshot body is required." });
        return Ok(await mediator.Send(new ImportSnapshotCommand(CurrentUserId(), snapshot), ct));
    }

    // Incremental pull: changed rows since the opaque cursor (first call:
    // omit it for everything), history by id, tombstones by time.
    [HttpGet("delta")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<SyncDelta>> Delta(
        [FromQuery] string cursor = "", CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetDeltaQuery(CurrentUserId(), cursor), ct));

    // Raw MP3 bytes for pull (byte-range streaming, like {file}/stream).
    // ?library=songs (default) or podcasts — the client's delta section
    // tells it which side each file belongs to.
    [HttpGet("files/{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public ActionResult GetFile(string file, [FromQuery] string library = "songs")
    {
        var isPodcast = library.Equals("podcasts", StringComparison.OrdinalIgnoreCase);
        var exists = isPodcast
            ? storage.PodcastExists(CurrentUserId(), file)
            : storage.SongExists(CurrentUserId(), file);
        if (!exists) return NotFound();
        var path = isPodcast
            ? storage.PodcastPath(CurrentUserId(), file)
            : storage.SongPath(CurrentUserId(), file);
        return PhysicalFile(path, "audio/mpeg", enableRangeProcessing: true);
    }

    // Raw MP3 bytes for push: stores the file in the shared library folder
    // and upserts the catalog row. Bare "*.mp3" filenames only.
    [HttpPut("files/{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    [RequestSizeLimit(100_000_000)]
    public async Task<ActionResult> PutFile(
        string file, [FromQuery] string library = "songs", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file
            || !file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Only bare *.mp3 filenames are accepted." });
        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, ct);
        if (body.Length == 0)
            return BadRequest(new { message = "Empty file body." });
        var isPodcast = library.Equals("podcasts", StringComparison.OrdinalIgnoreCase);
        await mediator.Send(new SaveSyncFileCommand(CurrentUserId(), file, isPodcast, body.ToArray()), ct);
        return Ok(new { file });
    }

    // Per-library pull from the desktop TiDB remote (desktop
    // sync_remote_to_local_and_download, split in two): merge remote rows
    // into this user's tables and queue downloads for missing files.
    [HttpPost("pull-songs")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<RemotePullResult>> PullSongs(CancellationToken ct) =>
        Ok(await mediator.Send(new PullSongsCommand(CurrentUserId()), ct));

    [HttpPost("pull-podcasts")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<RemotePullResult>> PullPodcasts(CancellationToken ct) =>
        Ok(await mediator.Send(new PullPodcastsCommand(CurrentUserId()), ct));

    // Per-library push to the desktop TiDB remote (desktop DatabaseSync
    // push, split in two): upsert this user's rows into the shared remote
    // tables and propagate deletion tombstones.
    [HttpPost("push-songs")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<RemotePushResult>> PushSongs(CancellationToken ct) =>
        Ok(await mediator.Send(new PushSongsCommand(CurrentUserId()), ct));

    [HttpPost("push-podcasts")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<RemotePushResult>> PushPodcasts(CancellationToken ct) =>
        Ok(await mediator.Send(new PushPodcastsCommand(CurrentUserId()), ct));

    // Resume across devices: snapshot this user's playback spot (upserted
    // fire-and-forget on pause — never blocks playback), or read back the
    // latest foreign spot as a resume affordance (nothing auto-plays).
    [HttpPost("playback")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<bool>> PushPlayback(CancellationToken ct) =>
        Ok(await mediator.Send(new PushPlaybackStateCommand(CurrentUserId()), ct));

    [HttpGet("playback")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<PlaybackSpotDto?>> LatestPlayback(CancellationToken ct) =>
        Ok(await mediator.Send(new GetLatestPlaybackQuery(CurrentUserId()), ct));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

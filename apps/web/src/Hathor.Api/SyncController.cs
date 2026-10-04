using System.Security.Claims;
using Hathor.Api.Auth;
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
[ApiController]
[Route("api/v1/[controller]")]
public sealed class SyncController(IMediator mediator) : ControllerBase
{
    [HttpGet("export")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<SyncSnapshot>> Export(
        [FromQuery] long sinceId = 0, CancellationToken ct = default) =>
        Ok(await mediator.Send(new ExportSnapshotQuery(CurrentUserId(), sinceId), ct));

    [HttpPost("import")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<SyncSummary>> Import(
        [FromBody] SyncSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot is null)
            return BadRequest(new { message = "A snapshot body is required." });
        return Ok(await mediator.Send(new ImportSnapshotCommand(CurrentUserId(), snapshot), ct));
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

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

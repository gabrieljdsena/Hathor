using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Player;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Hathor.Api.Controllers;

// Headless song controls: identical surface for hathor-web and external
// clients (curl, mobile, Stream Deck, Home Assistant). Backend is the source
// of truth; mutations broadcast SignalR PlaybackStateChanged + QueueUpdated.
[ApiController]
[Route("api/v1/[controller]")]
public sealed class PlayerController(IMediator mediator, IMemoryCache idempotency) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("state")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<PlayerStateDto>> State(CancellationToken ct) =>
        Ok(await mediator.Send(new GetPlayerStateQuery(UserId), ct));

    [HttpGet("now-playing")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<NowPlayingDto>> NowPlaying(CancellationToken ct)
    {
        var state = await mediator.Send(new GetPlayerStateQuery(UserId), ct);
        return Ok(new NowPlayingDto(
            state.CurrentSong?.Title, state.CurrentSong?.Artist,
            state.CurrentSong?.CoverArt, state.PositionSec, state.CurrentSong?.Duration));
    }

    [HttpPost("play")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Play(
        [FromBody] PlayRequest? body, CancellationToken ct) =>
        Ok(await mediator.Send(new PlayCommand(
            UserId, body?.File, body?.IsPodcast, body?.InstanceId, body?.Opening ?? false), ct));

    [HttpPost("toggle")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Toggle(CancellationToken ct) =>
        Ok(await mediator.Send(new ToggleCommand(UserId), ct));

    [HttpPost("pause")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Pause(CancellationToken ct) =>
        Ok(await mediator.Send(new PauseCommand(UserId), ct));

    [HttpPost("next")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Next(
        [FromBody] NextRequest? body, CancellationToken ct)
    {
        var key = IdempotencyKey();
        if (key is not null && idempotency.TryGetValue(key, out PlayerStateDto? cached) && cached is not null)
            return Ok(cached);
        var dto = await mediator.Send(new NextCommand(UserId, body?.Auto ?? false, key, body?.ExpectedFile), ct);
        if (key is not null) idempotency.Set(key, dto, TimeSpan.FromMinutes(10));
        return Ok(dto);
    }

    [HttpPost("prev")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Prev(CancellationToken ct)
    {
        var key = IdempotencyKey();
        if (key is not null && idempotency.TryGetValue(key, out PlayerStateDto? cached) && cached is not null)
            return Ok(cached);
        var dto = await mediator.Send(new PrevCommand(UserId, key), ct);
        if (key is not null) idempotency.Set(key, dto, TimeSpan.FromMinutes(10));
        return Ok(dto);
    }

    [HttpPost("seek")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Seek(
        [FromBody] SeekRequest body, CancellationToken ct)
    {
        var key = IdempotencyKey();
        if (key is not null && idempotency.TryGetValue(key, out PlayerStateDto? cached) && cached is not null)
            return Ok(cached);
        var dto = await mediator.Send(new SeekCommand(UserId, body.Seconds, key), ct);
        if (key is not null) idempotency.Set(key, dto, TimeSpan.FromMinutes(10));
        return Ok(dto);
    }

    [HttpPost("seek-by")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> SeekBy(
        [FromBody] SeekByRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new SeekByCommand(UserId, body.DeltaSeconds), ct));

    [HttpPost("volume")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Volume(
        [FromBody] VolumeRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new VolumeCommand(UserId, body.Volume), ct));

    [HttpPost("mute")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Mute(CancellationToken ct) =>
        Ok(await mediator.Send(new MuteCommand(UserId), ct));

    [HttpPost("unmute")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Unmute(CancellationToken ct) =>
        Ok(await mediator.Send(new UnmuteCommand(UserId), ct));

    [HttpPost("shuffle")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Shuffle(CancellationToken ct) =>
        Ok(await mediator.Send(new ShuffleCommand(UserId), ct));

    [HttpPost("repeat")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> Repeat(CancellationToken ct) =>
        Ok(await mediator.Send(new RepeatCommand(UserId), ct));

    [HttpGet("queue")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<PlayerStateDto>> GetQueue(CancellationToken ct) =>
        Ok(await mediator.Send(new GetPlayerStateQuery(UserId), ct));

    [HttpGet("queue/page")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<QueuePageDto>> GetQueuePage(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetQueuePageQuery(UserId, page, pageSize), ct));

    [HttpPut("queue")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> ReplaceQueue(
        [FromBody] ReplaceQueueRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new ReplaceQueueCommand(UserId, body.CurrentFile,
            body.Files, body.PlaylistId,
            body.Source is null ? null : new QueueSourceDto(body.Source.Type, body.Source.Id)), ct));

    [HttpPost("queue/add")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> AddToQueue(
        [FromBody] QueueFileRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new AddToQueueCommand(UserId, body.File), ct));

    [HttpPost("queue/play-next")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> PlayNext(
        [FromBody] QueueFileRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new PlayNextCommand(UserId, body.File), ct));

    [HttpDelete("queue/clear")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> ClearQueue(CancellationToken ct) =>
        Ok(await mediator.Send(new ClearQueueCommand(UserId), ct));

    // Restart resume: rebuild an empty queue from the persisted queue_source.
    // {rebuilt:false} means stale source — caller falls back to the general list.
    [HttpPost("queue/rebuild")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<RebuildQueueResponse>> RebuildQueue(CancellationToken ct)
    {
        var result = await mediator.Send(new RebuildQueueCommand(UserId), ct);
        return Ok(new RebuildQueueResponse(result.Rebuilt, result.State));
    }

    [HttpDelete("queue/{index:int}")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> RemoveFromQueue(int index, CancellationToken ct) =>
        Ok(await mediator.Send(new RemoveFromQueueCommand(UserId, index), ct));

    [HttpPut("queue/reorder")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> ReorderQueue(
        [FromBody] ReorderQueueRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new ReorderQueueCommand(UserId, body.OldIndex, body.NewIndex), ct));

    [HttpPost("queue/jump")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerStateDto>> JumpQueue(
        [FromBody] JumpQueueRequest body, CancellationToken ct) =>
        Ok(await mediator.Send(new JumpQueueCommand(UserId, body.Index), ct));

    // Crossfade prefs (desktop get/set_playback_settings). Gapless handoff
    // is always on; the client renders the overlap from these prefs.
    [HttpGet("settings")]
    [Authorize(Policy = ScopeAuthorization.PlayerRead)]
    public async Task<ActionResult<PlayerSettingsDto>> GetSettings(CancellationToken ct) =>
        Ok(await mediator.Send(new Application.Settings.GetPlayerSettingsQuery(UserId), ct));

    [HttpPut("settings")]
    [Authorize(Policy = ScopeAuthorization.PlayerControl)]
    public async Task<ActionResult<PlayerSettingsDto>> SetSettings(
        [FromBody] SetPlayerSettingsRequest body, CancellationToken ct)
    {
        try
        {
            return Ok(await mediator.Send(new Application.Settings.SetPlayerSettingsCommand(
                UserId, body.CrossfadeEnabled, body.CrossfadeSeconds), ct));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private string? IdempotencyKey() =>
        Request.Headers.TryGetValue("Idempotency-Key", out var v) &&
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)
            ? $"{uid}:{v}"
            : null;
}

public sealed record PlayRequest(string? File, bool? IsPodcast, string? InstanceId, bool Opening = false);
public sealed record NextRequest(bool Auto = false, string? ExpectedFile = null);
public sealed record SeekRequest(double Seconds);
public sealed record SeekByRequest(double DeltaSeconds);
public sealed record VolumeRequest(double Volume);
public sealed record ReplaceQueueRequest(
    string? CurrentFile, IReadOnlyList<string> Files, long? PlaylistId, QueueSourceRequest? Source);
public sealed record QueueSourceRequest(string Type, string? Id);
public sealed record QueueFileRequest(string File);
public sealed record ReorderQueueRequest(int OldIndex, int NewIndex);
public sealed record JumpQueueRequest(int Index);
public sealed record RebuildQueueResponse(bool Rebuilt, PlayerStateDto State);
public sealed record SetPlayerSettingsRequest(bool CrossfadeEnabled, double CrossfadeSeconds);

[ApiController]
[Route("api")]
public sealed class ApiInfoController(IConfiguration config) : ControllerBase
{
    // Public metadata only (name/version/capabilities) — no user data.
    // Explicit opt-out of the global fallback authorization policy.
    [HttpGet("info")]
    [AllowAnonymous]
    public ActionResult<ApiInfoDto> Info() => Ok(new ApiInfoDto(
        "Hathor",
        config.GetValue("ApiInfo:Version", "1.0.0"),
        DateTime.UtcNow,
        ["crossfade", "lyrics", "romanize", "api-keys", "headless-controls"]));
}

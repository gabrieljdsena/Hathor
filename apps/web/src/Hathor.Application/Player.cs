using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Application.Sync;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using Hathor.Application.Metadata;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Hathor.Application.Player;

// Queries/commands backing §4 Playback endpoints. Backend is the source of
// truth; every mutation broadcasts state so web UI and external remotes converge.

public sealed record GetPlayerStateQuery(Guid UserId) : IRequest<PlayerStateDto>;

public sealed class GetPlayerStateHandler(
    IPlaybackStateRepository playback,
    ISongReadModel songs) : IRequestHandler<GetPlayerStateQuery, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(GetPlayerStateQuery q, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(q.UserId, ct);
        return await PlayerHelpers.ToDtoAsync(state, songs, q.UserId, ct);
    }
}

// POST /player/play {file?, instanceId?, opening?} — empty body toggles like play_button(null).
public sealed record PlayCommand(Guid UserId, string? File, bool? IsPodcast, string? InstanceId, bool Opening = false)
    : IRequest<PlayerStateDto>;

public sealed class PlayHandler(
    IPlaybackStateRepository playback,
    ISongReadModel songs,
    PendingMetadataApplier pendingEdits,
    IServiceScopeFactory scopes,
    IPlaybackHub hub) : IRequestHandler<PlayCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(PlayCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var now = DateTime.UtcNow;
        var oldFile = state.CurrentFile;

        if (cmd.File is null)
        {
            // Toggle. Null when nothing ever loaded (desktop returns null → UI plays first row).
            if (state.CurrentFile is null) return await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
            if (state.IsPlaying) state.Pause(now);
            else state.Resume(now);
        }
        else if (cmd.Opening)
        {
            // Startup preload: load paused (desktop opening=True pauses immediately).
            state.CurrentFile = cmd.File;
            state.CurrentIsPodcast = cmd.IsPodcast ?? false;
            state.FirstPlay = true;
            state.IsPlaying = false;
            state.PausePositionSec = 0;
            state.PositionOffsetSec = 0;
        }
        else
        {
            var sameFile = string.Equals(state.CurrentFile, cmd.File, StringComparison.OrdinalIgnoreCase);
            var replayInstance = sameFile && cmd.InstanceId is not null; // history _instanceId replay restarts
            if (!sameFile || replayInstance)
            {
                var isPodcast = cmd.IsPodcast
                    ?? (await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: false, ct))?.IsPodcast
                    ?? false;
                state.StartPlaying(cmd.File, isPodcast, now);
            }
            else if (state.IsPlaying) state.Pause(now);
            else state.Resume(now);

            if (!state.CurrentIsPodcast)
                PlayerEvents.RaiseSongPlayed(cmd.UserId, cmd.File, state.CurrentPlaylistId);
        }

        await PendingEditHooks.ApplyForLeftFileAsync(pendingEdits, cmd.UserId, oldFile, state.CurrentFile, ct);
        // A toggle that lands paused must not park past the track end (see
        // PlaybackPositionClamp): wall-clock estimates outrun stalled
        // elements, and the client's drift correction would land on the
        // duration edge and auto-advance away.
        if (!state.IsPlaying)
            await PlaybackPositionClamp.ClampStoredAsync(state, songs, cmd.UserId, ct);
        PlaybackPushHooks.PushOnPause(scopes, cmd.UserId, state);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        return dto;
    }
}

public sealed record ToggleCommand(Guid UserId) : IRequest<PlayerStateDto>;

public sealed class ToggleHandler(IPlaybackStateRepository playback, ISongReadModel songs, PendingMetadataApplier pendingEdits, IServiceScopeFactory scopes, IPlaybackHub hub)
    : IRequestHandler<ToggleCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(ToggleCommand cmd, CancellationToken ct)
        => await new PlayHandler(playback, songs, pendingEdits, scopes, hub)
            .Handle(new PlayCommand(cmd.UserId, null, null, null), ct);
}

public sealed record PauseCommand(Guid UserId) : IRequest<PlayerStateDto>;
public sealed record NextCommand(Guid UserId, bool Auto = false, string? IdempotencyKey = null, string? ExpectedFile = null) : IRequest<PlayerStateDto>;
public sealed record PrevCommand(Guid UserId, string? IdempotencyKey = null) : IRequest<PlayerStateDto>;

public sealed class PauseHandler(IPlaybackStateRepository playback, ISongReadModel songs, IServiceScopeFactory scopes, IPlaybackHub hub)
    : IRequestHandler<PauseCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(PauseCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.Pause(DateTime.UtcNow);
        await PlaybackPositionClamp.ClampStoredAsync(state, songs, cmd.UserId, ct);
        PlaybackPushHooks.PushOnPause(scopes, cmd.UserId, state);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        return dto;
    }
}

public sealed class NextHandler(IPlaybackStateRepository playback, ISongReadModel songs, PendingMetadataApplier pendingEdits, IPlaybackHub hub)
    : IRequestHandler<NextCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(NextCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var now = DateTime.UtcNow;
        var oldFile = state.CurrentFile;
        var wasPlaying = state.IsPlaying;

        // Conditional advance (crossfade handoff): the fade computed its
        // target from a queue snapshot that a manual transport may have
        // already consumed. If the current track moved on, converge on it
        // without advancing again instead of skipping a song unheard.
        if (cmd.ExpectedFile is not null &&
            !string.Equals(state.CurrentFile, cmd.ExpectedFile, StringComparison.OrdinalIgnoreCase))
        {
            await playback.SaveChangesAsync(ct);
            var converged = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
            await hub.BroadcastStateAsync(cmd.UserId, converged, ct);
            await hub.BroadcastQueueAsync(cmd.UserId, converged.Queue, ct);
            return converged;
        }

        // Repeat replays the current song on auto-advance (desktop play_next auto+repeat).
        if (state.Repeat && cmd.Auto && state.CurrentFile is not null)
        {
            state.StartPlaying(state.CurrentFile, state.CurrentIsPodcast, now);
        }
        else
        {
            var next = state.Advance();
            if (next is null)
            {
                state.IsPlaying = false;
                state.PausePositionSec = 0;
                state.PositionOffsetSec = 0;
            }
            else
            {
                // Podcast-ness resolves per file so mixed queues stay correct.
                var resolved = await songs.GetByFileAsync(cmd.UserId, next, includeCover: false, ct);
                state.StartPlaying(next, resolved?.IsPodcast ?? false, now);
                if (!state.CurrentIsPodcast)
                    PlayerEvents.RaiseSongPlayed(cmd.UserId, next, state.CurrentPlaylistId);
            }
        }

        // Fade handoffs start while playing; a paused state here means the
        // user's pause toggle landed before this advance. Move the queue
        // but stay paused instead of resurrecting playback mid-fade.
        if (cmd.ExpectedFile is not null && !wasPlaying && state.IsPlaying && state.CurrentFile is not null)
        {
            state.Pause(now);
        }

        await PendingEditHooks.ApplyForLeftFileAsync(pendingEdits, cmd.UserId, oldFile, state.CurrentFile, ct);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        await hub.BroadcastQueueAsync(cmd.UserId, dto.Queue, ct);
        return dto;
    }
}

public sealed class PrevHandler(IPlaybackStateRepository playback, ISongReadModel songs, PendingMetadataApplier pendingEdits, IPlaybackHub hub)
    : IRequestHandler<PrevCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(PrevCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var oldFile = state.CurrentFile;
        var prev = state.Rewind();
        if (prev is not null)
        {
            var resolved = await songs.GetByFileAsync(cmd.UserId, prev, includeCover: false, ct);
            state.StartPlaying(prev, resolved?.IsPodcast ?? false, DateTime.UtcNow);
        }
        await PendingEditHooks.ApplyForLeftFileAsync(pendingEdits, cmd.UserId, oldFile, state.CurrentFile, ct);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        await hub.BroadcastQueueAsync(cmd.UserId, dto.Queue, ct);
        return dto;
    }
}

public sealed record SeekCommand(Guid UserId, double Seconds, string? IdempotencyKey = null) : IRequest<PlayerStateDto>;
public sealed record SeekByCommand(Guid UserId, double DeltaSeconds) : IRequest<PlayerStateDto>;

public sealed class SeekHandler(IPlaybackStateRepository playback, ISongReadModel songs, IPlaybackHub hub)
    : IRequestHandler<SeekCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(SeekCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        // Chapter marks are user-entered and can overshoot EOF: never store
        // a position past the measured track length (see
        // PlaybackPositionClamp).
        var seconds = cmd.Seconds;
        if (state.CurrentFile is not null)
        {
            var resolved = await songs.GetByFileAsync(cmd.UserId, state.CurrentFile, includeCover: false, ct);
            seconds = PlaybackPositionClamp.Clamp(seconds, resolved?.Duration);
        }
        state.Seek(seconds, DateTime.UtcNow);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        return dto;
    }
}

public sealed class SeekByHandler(IPlaybackStateRepository playback, ISongReadModel songs, IPlaybackHub hub)
    : IRequestHandler<SeekByCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(SeekByCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var now = DateTime.UtcNow;
        var target = state.EstimatedPositionSec(now) + cmd.DeltaSeconds;
        if (state.CurrentFile is not null)
        {
            var resolved = await songs.GetByFileAsync(cmd.UserId, state.CurrentFile, includeCover: false, ct);
            target = PlaybackPositionClamp.Clamp(target, resolved?.Duration);
        }
        state.Seek(target, now);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        return dto;
    }
}

public sealed record VolumeCommand(Guid UserId, double Volume) : IRequest<PlayerStateDto>;
public sealed record MuteCommand(Guid UserId) : IRequest<PlayerStateDto>;
public sealed record UnmuteCommand(Guid UserId) : IRequest<PlayerStateDto>;
public sealed record ShuffleCommand(Guid UserId) : IRequest<PlayerStateDto>;
public sealed record RepeatCommand(Guid UserId) : IRequest<PlayerStateDto>;

public sealed class PlayerPrefsHandler(
    IPlaybackStateRepository playback,
    ISongReadModel songs,
    IPlaybackHub hub) :
    IRequestHandler<VolumeCommand, PlayerStateDto>,
    IRequestHandler<MuteCommand, PlayerStateDto>,
    IRequestHandler<UnmuteCommand, PlayerStateDto>,
    IRequestHandler<ShuffleCommand, PlayerStateDto>,
    IRequestHandler<RepeatCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(VolumeCommand cmd, CancellationToken ct)
        => await Mutate(cmd.UserId, s => s.SetVolume(cmd.Volume), ct);

    public async Task<PlayerStateDto> Handle(MuteCommand cmd, CancellationToken ct)
        => await Mutate(cmd.UserId, s => s.Mute(), ct);

    public async Task<PlayerStateDto> Handle(UnmuteCommand cmd, CancellationToken ct)
        => await Mutate(cmd.UserId, s => s.Unmute(), ct);

    public async Task<PlayerStateDto> Handle(ShuffleCommand cmd, CancellationToken ct)
        => await Mutate(cmd.UserId, s =>
        {
            s.Shuffle = !s.Shuffle;
            if (s.Shuffle)
            {
                if (s.UnshuffledFiles.Count == 0) s.UnshuffledFiles = new List<string>(s.NextFiles);
                PlaybackState.ShuffleInPlace(s.NextFiles, Random.Shared);
                s.IsCustomQueue = true;
            }
            else if (s.UnshuffledFiles.Count > 0)
            {
                // Restore pre-shuffle order (desktop toggle_shuffle off path).
                s.NextFiles = new List<string>(s.UnshuffledFiles);
                s.UnshuffledFiles.Clear();
            }
        }, ct, broadcastQueue: true);

    public async Task<PlayerStateDto> Handle(RepeatCommand cmd, CancellationToken ct)
        => await Mutate(cmd.UserId, s => s.Repeat = !s.Repeat, ct);

    private async Task<PlayerStateDto> Mutate(Guid userId, Action<PlaybackState> apply,
        CancellationToken ct, bool broadcastQueue = false)
    {
        var state = await playback.GetOrCreateAsync(userId, ct);
        apply(state);
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, userId, ct);
        await hub.BroadcastStateAsync(userId, dto, ct);
        if (broadcastQueue) await hub.BroadcastQueueAsync(userId, dto.Queue, ct);
        return dto;
    }
}

// Queue endpoints (§4): full replace, add, play-next, clear, remove, reorder, jump.
public sealed record ReplaceQueueCommand(Guid UserId, string? CurrentFile, IReadOnlyList<string> Files,
    long? PlaylistId, QueueSourceDto? Source) : IRequest<PlayerStateDto>;

public sealed record AddToQueueCommand(Guid UserId, string File) : IRequest<PlayerStateDto>;
public sealed record PlayNextCommand(Guid UserId, string File) : IRequest<PlayerStateDto>;
public sealed record ClearQueueCommand(Guid UserId) : IRequest<PlayerStateDto>;
public sealed record RemoveFromQueueCommand(Guid UserId, int Index) : IRequest<PlayerStateDto>;
public sealed record ReorderQueueCommand(Guid UserId, int OldIndex, int NewIndex) : IRequest<PlayerStateDto>;
public sealed record JumpQueueCommand(Guid UserId, int Index) : IRequest<PlayerStateDto>;

// GET /player/queue/page: windowed queue resolve for huge queues (the full
// Queue field stays intact for existing clients; this is additive).
public sealed record GetQueuePageQuery(Guid UserId, int Page = 1, int PageSize = 50)
    : IRequest<QueuePageDto>;

public sealed record QueuePageDto(IReadOnlyList<SongDto> Items, int Total, int Page, int PageSize);

public sealed class GetQueuePageHandler(
    IPlaybackStateRepository playback,
    ISongReadModel songs) : IRequestHandler<GetQueuePageQuery, QueuePageDto>
{
    public async Task<QueuePageDto> Handle(GetQueuePageQuery q, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(q.UserId, ct);
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var slice = state.NextFiles
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        var items = await songs.GetManyAsync(q.UserId, slice, includeCover: false, ct);
        return new QueuePageDto(items, state.NextFiles.Count, page, pageSize);
    }
}

// Stored positions must never outlive the track: wall-clock estimates
// keep running while an element stalls (long-file seek stalls), chapter
// marks are user-entered and can overshoot EOF, and a tab left open keeps
// "playing" for hours. A past-the-end position makes the client's drift
// correction land on the duration edge — which fires `ended` and advances
// the queue away mid-track (podcast vanishes from Up Next, another song
// appears at 0:00). Clamp to the measured file length when known;
// unknown lengths (0) keep the legacy unclamped behavior.
public static class PlaybackPositionClamp
{
    public static double Clamp(double seconds, double? duration) =>
        duration is > 0 ? Math.Clamp(seconds, 0, duration.Value) : Math.Max(0, seconds);

    public static async Task ClampStoredAsync(
        PlaybackState state, ISongReadModel songs, Guid userId, CancellationToken ct = default)
    {
        if (state.CurrentFile is null) return;
        var resolved = await songs.GetByFileAsync(userId, state.CurrentFile, includeCover: false, ct);
        if (resolved is null || resolved.Duration <= 0) return;
        state.PositionOffsetSec = Math.Min(state.PositionOffsetSec, resolved.Duration);
        state.PausePositionSec = Math.Min(state.PausePositionSec, resolved.Duration);
    }
}

// Deferred-edit hook for track changes: applies the stashed metadata edit
// for the file that just stopped being current. No-op on toggles, pauses,
// and repeat-one replays where the file didn't change — the still-playing
// file keeps its deferral until it really leaves.
internal static class PendingEditHooks
{
    internal static Task ApplyForLeftFileAsync(
        PendingMetadataApplier applier, Guid userId, string? oldFile, string? newFile,
        CancellationToken ct) =>
        string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase)
            ? Task.CompletedTask
            : applier.ApplyForFileAsync(userId, oldFile, ct);
}

// Resume-state push (never blocks the response): snapshot this user's spot
// into the shared remote table whenever playback pauses with a loaded track
// (manual toggle-pause, pause endpoint, tab-close/unload beacon, boot pause
// of a stale session). 10s budget, all failures swallowed; no remote
// configured → instant false inside the handler. Playing states are never
// pushed (a live position would clobber a genuine paused spot elsewhere).
internal static class PlaybackPushHooks
{
    internal static void PushOnPause(
        IServiceScopeFactory scopes, Guid userId, PlaybackState state)
    {
        if (state.IsPlaying || state.CurrentFile is null || state.FirstPlay) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await scope.ServiceProvider.GetRequiredService<IMediator>()
                    .Send(new PushPlaybackStateCommand(userId), cts.Token);
            }
            catch
            {
                // Resume sync must never break playback.
            }
        }, CancellationToken.None);
    }
}

public sealed class QueueHandler(
    IPlaybackStateRepository playback,
    ISongReadModel songs,
    IPlaybackHub hub) :
    IRequestHandler<ReplaceQueueCommand, PlayerStateDto>,
    IRequestHandler<AddToQueueCommand, PlayerStateDto>,
    IRequestHandler<PlayNextCommand, PlayerStateDto>,
    IRequestHandler<ClearQueueCommand, PlayerStateDto>,
    IRequestHandler<RemoveFromQueueCommand, PlayerStateDto>,
    IRequestHandler<ReorderQueueCommand, PlayerStateDto>,
    IRequestHandler<JumpQueueCommand, PlayerStateDto>
{
    public async Task<PlayerStateDto> Handle(ReplaceQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var idx = cmd.Files.ToList().FindIndex(f =>
            string.Equals(f, cmd.CurrentFile, StringComparison.OrdinalIgnoreCase));
        var next = idx >= 0 ? cmd.Files.Skip(idx + 1) : cmd.Files;
        var prev = idx >= 0 ? cmd.Files.Take(idx) : Enumerable.Empty<string>();
        state.SetQueue(
            next,
            prev,
            cmd.Source is null ? null : new QueueSource(cmd.Source.Type, cmd.Source.Id),
            isCustom: false,
            fallback: cmd.Source is null,
            playlistId: cmd.PlaylistId,
            rng: Random.Shared);
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    public async Task<PlayerStateDto> Handle(AddToQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.AppendToQueue(cmd.File);
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    public async Task<PlayerStateDto> Handle(PlayNextCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.PlayNext(cmd.File);
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    public async Task<PlayerStateDto> Handle(ClearQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.ClearQueue();
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    public async Task<PlayerStateDto> Handle(RemoveFromQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.RemoveAt(cmd.Index);
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    public async Task<PlayerStateDto> Handle(ReorderQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.Reorder(cmd.OldIndex, cmd.NewIndex);
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    public async Task<PlayerStateDto> Handle(JumpQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.JumpToIndex(cmd.Index);
        return await SaveAndBroadcast(cmd.UserId, state, ct);
    }

    private async Task<PlayerStateDto> SaveAndBroadcast(Guid userId, PlaybackState state, CancellationToken ct)
    {
        await playback.SaveChangesAsync(ct);
        var dto = await PlayerHelpers.ToDtoAsync(state, songs, userId, ct);
        await hub.BroadcastStateAsync(userId, dto, ct);
        await hub.BroadcastQueueAsync(userId, dto.Queue, ct);
        return dto;
    }
}

public static class PlayerHelpers
{
    public static async Task<PlayerStateDto> ToDtoAsync(
        PlaybackState state, ISongReadModel songs, Guid userId, CancellationToken ct)
    {
        SongDto? current = null;
        if (state.CurrentFile is not null)
            current = await songs.GetByFileAsync(userId, state.CurrentFile, includeCover: true, ct);

        // One batched resolve (single date lookup) instead of per-file
        // queries — full-library queues would otherwise cost 1000+ DB
        // round trips per state mutation.
        var queue = (await songs.GetManyAsync(userId, state.NextFiles, includeCover: false, ct))
            .ToList();

        var position = state.EstimatedPositionSec(DateTime.UtcNow);
        // Wall-clock estimates must never outlive the track: a stalled
        // client element plus an overrun estimate makes the client's drift
        // correction yank playback to the duration edge and auto-advance
        // mid-song. Clamp to the resolved track length when known.
        if (current is not null && current.Duration > 0)
            position = Math.Clamp(position, 0, current.Duration);

        return new PlayerStateDto(
            current, state.IsPlaying, position,
            state.Volume, state.Shuffle, state.Repeat, queue,
            state.Source is null ? null : new QueueSourceDto(state.Source.Type, state.Source.Id),
            state.IsCustomQueue, state.FirstPlay, state.NextFiles.Count);
    }
}

// Deferred history-write hook: podcasts never enter music history (desktop rule).
// The Api layer subscribes and persists Music_History + Playlist_History rows.
public static class PlayerEvents
{
    public static event Action<Guid, string, long?>? SongPlayed;
    public static void RaiseSongPlayed(Guid userId, string file, long? playlistId = null) =>
        SongPlayed?.Invoke(userId, file, playlistId);
    public static void Reset() => SongPlayed = null;
}

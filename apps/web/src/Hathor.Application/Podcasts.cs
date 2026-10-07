using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Podcasts;

// Second library (desktop Podcasts table + podcasts folder): lightweight
// listing, on-demand details, shared edit modal, delete with tag cleanup.

public sealed record ListPodcastsQuery(Guid UserId) : IRequest<IReadOnlyList<SongDto>>;
public sealed record GetPodcastDetailsQuery(Guid UserId, string File) : IRequest<SongDto?>;

public sealed class PodcastQueryHandlers(IPodcastReadModel podcasts) :
    IRequestHandler<ListPodcastsQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<GetPodcastDetailsQuery, SongDto?>
{
    public Task<IReadOnlyList<SongDto>> Handle(ListPodcastsQuery q, CancellationToken ct) =>
        podcasts.ListAsync(q.UserId, ct);
    public Task<SongDto?> Handle(GetPodcastDetailsQuery q, CancellationToken ct) =>
        podcasts.GetByFileAsync(q.UserId, q.File, ct);
}

public sealed record UpdatePodcastMetadataCommand(
    Guid UserId, string File, string? Title, string? Artist, string? CoverArt)
    : IRequest<UpdatePodcastMetadataResult?>;

public sealed record UpdatePodcastMetadataResult(SongDto Song, bool Pending = false);

public sealed class UpdatePodcastMetadataHandler(
    ILibraryStorage storage,
    IMetadataWriter writer,
    IPodcastRecordRepository records,
    IPodcastReadModel podcasts,
    IPlaybackStateRepository playback,
    IPendingEditRepository pending) : IRequestHandler<UpdatePodcastMetadataCommand, UpdatePodcastMetadataResult?>
{
    public async Task<UpdatePodcastMetadataResult?> Handle(UpdatePodcastMetadataCommand cmd, CancellationToken ct)
    {
        if (!storage.PodcastExists(cmd.UserId, cmd.File)) return null;
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        if (string.Equals(state.CurrentFile, cmd.File, StringComparison.OrdinalIgnoreCase))
        {
            // Same gapless rule as songs: stash, applied on track change.
            await pending.UpsertAsync(new PendingMetadataEdit
            {
                UserId = cmd.UserId,
                File = cmd.File,
                IsPodcast = true,
                Title = cmd.Title,
                Artist = cmd.Artist,
                CoverArt = cmd.CoverArt,
                CreatedUtc = DateTime.UtcNow,
            }, ct);
            await pending.SaveChangesAsync(ct);
            var current = await podcasts.GetByFileAsync(cmd.UserId, cmd.File, ct);
            return current is null ? null : new UpdatePodcastMetadataResult(current, Pending: true);
        }
        await writer.WritePathAsync(storage.PodcastPath(cmd.UserId, cmd.File),
            cmd.Title, cmd.Artist, null, null, null, cmd.CoverArt, ct);
        var updated = await podcasts.GetByFileAsync(cmd.UserId, cmd.File, ct);
        if (updated is not null)
            await records.EnsureAsync(cmd.UserId, cmd.File, updated.Title, updated.Artist, null, ct);
        await records.SaveChangesAsync(ct);
        return updated is null ? null : new UpdatePodcastMetadataResult(updated);
    }
}

// Delete episode: file + tag links + Podcasts row + tombstone +
// queue eviction + stop-if-current (desktop delete_podcast).
public sealed record DeletePodcastCommand(Guid UserId, string File) : IRequest<bool>;

public sealed class DeletePodcastHandler(
    ILibraryStorage storage,
    IPodcastRecordRepository records,
    ILyricsRepository lyrics,
    ITombstoneRepository tombstones,
    IPlaybackStateRepository playback,
    IPlaybackHub hub,
    ISongReadModel songs) : IRequestHandler<DeletePodcastCommand, bool>
{
    public async Task<bool> Handle(DeletePodcastCommand cmd, CancellationToken ct)
    {
        var onDisk = storage.PodcastExists(cmd.UserId, cmd.File);
        var chapterKeys = await lyrics.ListChapterKeysAsync(cmd.UserId, cmd.File, ct);
        var removed = await records.DeleteCascadeAsync(cmd.UserId, cmd.File, ct);
        if (!onDisk && !removed) return false;

        if (onDisk)
        {
            try { File.Delete(storage.PodcastPath(cmd.UserId, cmd.File)); }
            catch (IOException) { return false; }
        }

        await tombstones.RecordAsync(cmd.UserId, "podcasts", cmd.File, ct);
        // Episode lyrics rows (previously leaked on episode delete) plus
        // per-chapter cache rows, one tombstone each for exact-match remotes.
        await tombstones.RecordAsync(cmd.UserId, "lyrics", cmd.File, ct);
        foreach (var key in chapterKeys)
            await tombstones.RecordAsync(cmd.UserId, "lyrics", key, ct);

        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        state.NextFiles.RemoveAll(f => string.Equals(f, cmd.File, StringComparison.OrdinalIgnoreCase));
        state.PrevFiles.RemoveAll(f => string.Equals(f, cmd.File, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(state.CurrentFile, cmd.File, StringComparison.OrdinalIgnoreCase))
        {
            state.IsPlaying = false;
            state.FirstPlay = true;
            state.PausePositionSec = 0;
            state.PositionOffsetSec = 0;
        }
        await records.SaveChangesAsync(ct);
        await tombstones.SaveChangesAsync(ct);
        await playback.SaveChangesAsync(ct);

        var dto = await Player.PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        await hub.BroadcastQueueAsync(cmd.UserId, dto.Queue, ct);
        return true;
    }
}

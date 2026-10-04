using Hathor.Application.Dtos;
using Hathor.Application.Mix;
using Hathor.Application.Ports;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Player;

// Restart resume: rebuild the queue from the persisted playback context
// (desktop Api._rebuild_queue_from_source). Returns false on stale sources
// (deleted playlist, reset daily mix, renamed artist) so the caller falls
// back to the general list. Custom queues are never rebuilt verbatim here —
// they persist as-is in the state row.
public sealed record RebuildQueueCommand(Guid UserId) : IRequest<RebuildQueueResult>;

public sealed record RebuildQueueResult(bool Rebuilt, PlayerStateDto State);

public sealed class RebuildQueueHandler(
    IPlaybackStateRepository playback,
    IPlaylistRepository playlists,
    DailyMixService mix,
    ISongReadModel songs,
    IHistoryReadModel history,
    ILibraryStorage storage,
    IPlaybackHub hub) : IRequestHandler<RebuildQueueCommand, RebuildQueueResult>
{
    public async Task<RebuildQueueResult> Handle(RebuildQueueCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var source = state.Source;
        var current = state.CurrentFile;

        if (source is null || current is null || state.NextFiles.Count > 0)
            return new RebuildQueueResult(false, await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct));

        var files = await ResolveSourceFilesAsync(cmd.UserId, source, current, ct);
        if (files is null || !files.Contains(current, StringComparer.OrdinalIgnoreCase))
            return new RebuildQueueResult(false, await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct));

        var idx = files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        state.SetQueue(
            files.Skip(idx + 1),
            files.Take(idx),
            source,
            isCustom: false,
            fallback: false,
            playlistId: source.Type == QueueSourceTypes.Playlist &&
                long.TryParse(source.Id, out var pid) ? pid : state.CurrentPlaylistId,
            rng: Random.Shared);
        await playback.SaveChangesAsync(ct);

        var dto = await PlayerHelpers.ToDtoAsync(state, songs, cmd.UserId, ct);
        await hub.BroadcastStateAsync(cmd.UserId, dto, ct);
        await hub.BroadcastQueueAsync(cmd.UserId, dto.Queue, ct);
        return new RebuildQueueResult(true, dto);
    }

    private async Task<List<string>?> ResolveSourceFilesAsync(
        Guid userId, QueueSource source, string current, CancellationToken ct)
    {
        switch (source.Type)
        {
            case QueueSourceTypes.Playlist:
                if (!long.TryParse(source.Id, out var pid)) return null;
                return (await playlists.GetSongFilesAsync(userId, pid, ct))
                    .Select(t => t.File)
                    .Where(f => storage.SongExists(userId, f))
                    .ToList();
            case QueueSourceTypes.DailyMix:
                // Reset daily mixes fall back gracefully when the song left the mix.
                return (await mix.GetTodayFilesAsync(userId, ct))
                    .Where(f => storage.SongExists(userId, f))
                    .ToList();
            case QueueSourceTypes.Artist when source.Id is not null:
                return (await songs.GetSongsByArtistAsync(userId, source.Id, ct))
                    .Select(s => s.File).ToList();
            case QueueSourceTypes.Album when source.Id is not null:
                return (await songs.GetSongsByAlbumAsync(userId, source.Id, ct))
                    .Select(s => s.File).ToList();
            case QueueSourceTypes.RecentlyPlayed:
                return (await history.GetRecentPlayedFilesAsync(userId, 500, ct)).ToList();
            case QueueSourceTypes.RecentlyDownloaded:
                return (await history.GetRecentDownloadedFilesAsync(userId, 500, ct)).ToList();
            case QueueSourceTypes.Podcast:
                return storage.PodcastExists(userId, current) ? [current] : null;
            default: // all_songs and unknown types fall back to the general list
                return null;
        }
    }
}

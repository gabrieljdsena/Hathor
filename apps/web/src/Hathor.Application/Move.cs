using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Library;

// Move entries between the songs and podcasts libraries: the file is moved
// on disk and its rows migrate tables (delete-cascade from the source —
// same rows and tombstones as a delete — plus ingest into the destination,
// preserving the download link). Queue filenames stay valid (both libraries
// share the bare filename), but a currently-playing move stops like a
// delete because the stream URL changes libraries.

public abstract record LibraryMoveResult
{
    public sealed record Moved(SongDto Song) : LibraryMoveResult;
    public sealed record NotFound : LibraryMoveResult;
    public sealed record Conflict(string Message) : LibraryMoveResult;

    private LibraryMoveResult() { }
}

public sealed record MoveSongToPodcastCommand(Guid UserId, string File) : IRequest<LibraryMoveResult>;
public sealed record MovePodcastToSongCommand(Guid UserId, string File) : IRequest<LibraryMoveResult>;

public sealed class MoveSongToPodcastHandler(
    ILibraryStorage storage,
    ISongRecordRepository songRecords,
    IPodcastRecordRepository podcastRecords,
    ITombstoneRepository tombstones,
    IPlaybackStateRepository playback,
    IPlaybackHub hub,
    ISongReadModel songs) : IRequestHandler<MoveSongToPodcastCommand, LibraryMoveResult>
{
    public async Task<LibraryMoveResult> Handle(MoveSongToPodcastCommand cmd, CancellationToken ct)
    {
        if (!storage.SongExists(cmd.UserId, cmd.File))
            return new LibraryMoveResult.NotFound();
        if (storage.PodcastExists(cmd.UserId, cmd.File))
            return new LibraryMoveResult.Conflict("An episode with the same filename already exists.");

        var meta = songs.ReadLocalSong(cmd.UserId, cmd.File);
        var link = (await songRecords.GetAsync(cmd.UserId, cmd.File, ct))?.DownloadedLink;

        try { File.Move(storage.SongPath(cmd.UserId, cmd.File), storage.PodcastPath(cmd.UserId, cmd.File)); }
        catch (IOException) { return new LibraryMoveResult.Conflict("File is in use and could not be moved."); }

        await songRecords.DeleteCascadeAsync(cmd.UserId, cmd.File, ct);
        await podcastRecords.EnsureAsync(cmd.UserId, cmd.File, meta.Title,
            meta.Artist == "Unknown" ? "" : meta.Artist, link, ct);
        await songRecords.SaveChangesAsync(ct);
        await podcastRecords.SaveChangesAsync(ct);

        await tombstones.RecordAsync(cmd.UserId, "songs", cmd.File, ct);
        await tombstones.RecordAsync(cmd.UserId, "lyrics", cmd.File, ct);
        await tombstones.RecordAsync(cmd.UserId, "music_history", cmd.File, ct);
        await tombstones.SaveChangesAsync(ct);

        await MoveHelpers.StopIfCurrentAsync(playback, hub, songs, cmd.UserId, cmd.File, isPodcast: true, ct);

        var moved = await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: false, ct);
        return moved is null
            ? new LibraryMoveResult.NotFound()
            : new LibraryMoveResult.Moved(moved);
    }
}

public sealed class MovePodcastToSongHandler(
    ILibraryStorage storage,
    ISongRecordRepository songRecords,
    IPodcastRecordRepository podcastRecords,
    IPodcastReadModel podcasts,
    ITombstoneRepository tombstones,
    IPlaybackStateRepository playback,
    IPlaybackHub hub,
    ISongReadModel songs) : IRequestHandler<MovePodcastToSongCommand, LibraryMoveResult>
{
    public async Task<LibraryMoveResult> Handle(MovePodcastToSongCommand cmd, CancellationToken ct)
    {
        if (!storage.PodcastExists(cmd.UserId, cmd.File))
            return new LibraryMoveResult.NotFound();
        if (storage.SongExists(cmd.UserId, cmd.File))
            return new LibraryMoveResult.Conflict("A song with the same filename already exists.");

        var meta = podcasts.ReadLocalEpisode(cmd.UserId, cmd.File);
        var link = await podcastRecords.GetDownloadLinkAsync(cmd.UserId, cmd.File, ct);

        try { File.Move(storage.PodcastPath(cmd.UserId, cmd.File), storage.SongPath(cmd.UserId, cmd.File)); }
        catch (IOException) { return new LibraryMoveResult.Conflict("File is in use and could not be moved."); }

        await podcastRecords.DeleteCascadeAsync(cmd.UserId, cmd.File, ct);
        await songRecords.UpsertDownloadedAsync(cmd.UserId, cmd.File, link, meta.Title,
            meta.Artist, meta.Album, meta.Year, meta.Genre, meta.Duration, ct);
        await podcastRecords.SaveChangesAsync(ct);
        await songRecords.SaveChangesAsync(ct);

        await tombstones.RecordAsync(cmd.UserId, "podcasts", cmd.File, ct);
        await tombstones.SaveChangesAsync(ct);

        await MoveHelpers.StopIfCurrentAsync(playback, hub, songs, cmd.UserId, cmd.File, isPodcast: false, ct);

        var moved = await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: false, ct);
        return moved is null
            ? new LibraryMoveResult.NotFound()
            : new LibraryMoveResult.Moved(moved);
    }
}

internal static class MoveHelpers
{
    // Queue filenames stay valid across libraries (bare names resolve on
    // either side), so only a currently-playing move stops — like a delete.
    internal static async Task StopIfCurrentAsync(
        IPlaybackStateRepository playback,
        IPlaybackHub hub,
        ISongReadModel songs,
        Guid userId,
        string file,
        bool isPodcast,
        CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(userId, ct);
        if (string.Equals(state.CurrentFile, file, StringComparison.OrdinalIgnoreCase))
        {
            state.IsPlaying = false;
            state.FirstPlay = true;
            state.PausePositionSec = 0;
            state.PositionOffsetSec = 0;
            state.CurrentIsPodcast = isPodcast;
        }
        await playback.SaveChangesAsync(ct);

        var dto = await Player.PlayerHelpers.ToDtoAsync(state, songs, userId, ct);
        await hub.BroadcastStateAsync(userId, dto, ct);
        await hub.BroadcastQueueAsync(userId, dto.Queue, ct);
    }
}

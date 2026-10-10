using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Metadata;

// PATCH /api/v1/songs/{file}: title/artist/album/year/genre + cover.
// Null = keep existing (genre preserved — plan G1); empty string clears
// the field (blank title falls back to filename, others to Unknown).
// CoverArt accepts a data: URL, an http(s) URL, or the "REMOVE" sentinel
// (desktop edit modal).
public sealed record UpdateSongMetadataCommand(
    Guid UserId,
    string File,
    string? Title,
    string? Artist,
    string? Album,
    string? Year,
    string? Genre,
    string? CoverArt) : IRequest<UpdateSongMetadataResult?>;

public sealed record UpdateSongMetadataResult(SongDto Song, double ResumeSec, bool Pending = false);

public sealed class UpdateSongMetadataValidator : AbstractValidator<UpdateSongMetadataCommand>
{
    public UpdateSongMetadataValidator()
    {
        RuleFor(x => x.File).NotEmpty();
        RuleFor(x => x.Title).MaximumLength(255);
        RuleFor(x => x.Artist).MaximumLength(255);
        RuleFor(x => x.Album).MaximumLength(255);
        RuleFor(x => x.Year).MaximumLength(16);
        RuleFor(x => x.Genre).MaximumLength(64);
    }
}

public sealed class UpdateSongMetadataHandler(
    ILibraryStorage storage,
    IMetadataWriter writer,
    ISongRecordRepository records,
    ISongReadModel songs,
    IPlaybackStateRepository playback,
    IPendingEditRepository pending) : IRequestHandler<UpdateSongMetadataCommand, UpdateSongMetadataResult?>
{
    public async Task<UpdateSongMetadataResult?> Handle(UpdateSongMetadataCommand cmd, CancellationToken ct)
    {
        if (!storage.SongExists(cmd.UserId, cmd.File)) return null;

        // Capture the live position when editing the currently playing song
        // so the client can resume after the tag rewrite (desktop G8 behavior).
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var isCurrent = string.Equals(state.CurrentFile, cmd.File, StringComparison.OrdinalIgnoreCase);
        if (isCurrent)
        {
            // Gapless playback: the streamer holds the file open and tag
            // rewrites shift audio offsets under the playing element — stash
            // the payload instead (applied on track change). Last wins.
            return await StashAsync(cmd, ct);
        }
        var resumeSec = state.EstimatedPositionSec(DateTime.UtcNow);

        try
        {
            await writer.WriteSongAsync(cmd.UserId, cmd.File,
                cmd.Title, cmd.Artist, cmd.Album, cmd.Year, cmd.Genre, cmd.CoverArt, ct);
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            // Locked by another holder (desktop player sharing this library,
            // an active stream, a scanner): stash like a playing file instead
            // of losing the payload. The applier writes it on the next track
            // change and keeps it across failures until the lock releases.
            return await StashAsync(cmd, ct);
        }

        var updated = await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: true, ct);
        if (updated is not null)
            await records.UpsertMetadataAsync(cmd.UserId, cmd.File, updated.Title, updated.Artist,
                updated.Album, updated.Year, updated.Genre, updated.Duration, ct);
        await records.SaveChangesAsync(ct);

        updated ??= await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: true, ct);
        return updated is null ? null : new UpdateSongMetadataResult(updated, resumeSec);
    }

    private async Task<UpdateSongMetadataResult?> StashAsync(UpdateSongMetadataCommand cmd, CancellationToken ct)
    {
        await pending.UpsertAsync(new PendingMetadataEdit
        {
            UserId = cmd.UserId,
            File = cmd.File,
            IsPodcast = false,
            Title = cmd.Title,
            Artist = cmd.Artist,
            Album = cmd.Album,
            Year = cmd.Year,
            Genre = cmd.Genre,
            CoverArt = cmd.CoverArt,
            CreatedUtc = DateTime.UtcNow,
        }, ct);
        await pending.SaveChangesAsync(ct);
        var current = await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: true, ct);
        return current is null ? null : new UpdateSongMetadataResult(current, 0, Pending: true);
    }
}

// DELETE /api/v1/songs/{file}: file + Song_Playlist/Lyrics/Music_History/Songs
// rows, tombstones (songs/lyrics/music_history), queue eviction, stop-if-current.
public sealed record DeleteSongCommand(Guid UserId, string File) : IRequest<bool>;

public sealed class DeleteSongHandler(
    ILibraryStorage storage,
    ISongRecordRepository records,
    ILyricsRepository lyrics,
    ITombstoneRepository tombstones,
    IPlaybackStateRepository playback,
    IPlaybackHub hub,
    ISongReadModel songs) : IRequestHandler<DeleteSongCommand, bool>
{
    public async Task<bool> Handle(DeleteSongCommand cmd, CancellationToken ct)
    {
        var onDisk = storage.SongExists(cmd.UserId, cmd.File);
        var chapterKeys = await lyrics.ListChapterKeysAsync(cmd.UserId, cmd.File, ct);
        var removed = await records.DeleteCascadeAsync(cmd.UserId, cmd.File, ct);
        if (!onDisk && !removed) return false;

        if (onDisk)
        {
            try { File.Delete(storage.SongPath(cmd.UserId, cmd.File)); }
            catch (IOException) { return false; }
        }

        await tombstones.RecordAsync(cmd.UserId, "songs", cmd.File, ct);
        await tombstones.RecordAsync(cmd.UserId, "lyrics", cmd.File, ct);
        foreach (var key in chapterKeys)
            await tombstones.RecordAsync(cmd.UserId, "lyrics", key, ct);
        await tombstones.RecordAsync(cmd.UserId, "music_history", cmd.File, ct);

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

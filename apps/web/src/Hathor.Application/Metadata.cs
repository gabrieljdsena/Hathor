using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
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

public sealed record UpdateSongMetadataResult(SongDto Song, double ResumeSec);

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
    IPlaybackStateRepository playback) : IRequestHandler<UpdateSongMetadataCommand, UpdateSongMetadataResult?>
{
    public async Task<UpdateSongMetadataResult?> Handle(UpdateSongMetadataCommand cmd, CancellationToken ct)
    {
        if (!storage.SongExists(cmd.UserId, cmd.File)) return null;

        // Capture the live position when editing the currently playing song
        // so the client can resume after the tag rewrite (desktop G8 behavior).
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        var isCurrent = string.Equals(state.CurrentFile, cmd.File, StringComparison.OrdinalIgnoreCase);
        var resumeSec = isCurrent ? state.EstimatedPositionSec(DateTime.UtcNow) : 0;

        await writer.WriteSongAsync(cmd.UserId, cmd.File,
            cmd.Title, cmd.Artist, cmd.Album, cmd.Year, cmd.Genre, cmd.CoverArt, ct);

        var updated = await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: true, ct);
        if (updated is not null)
            await records.UpdateTitleArtistAsync(cmd.UserId, cmd.File, updated.Title, updated.Artist, ct);
        await records.SaveChangesAsync(ct);

        updated ??= await songs.GetByFileAsync(cmd.UserId, cmd.File, includeCover: true, ct);
        return updated is null ? null : new UpdateSongMetadataResult(updated, resumeSec);
    }
}

// DELETE /api/v1/songs/{file}: file + Song_Playlist/Lyrics/Music_History/Songs
// rows, tombstones (songs/lyrics/music_history), queue eviction, stop-if-current.
public sealed record DeleteSongCommand(Guid UserId, string File) : IRequest<bool>;

public sealed class DeleteSongHandler(
    ILibraryStorage storage,
    ISongRecordRepository records,
    ITombstoneRepository tombstones,
    IPlaybackStateRepository playback,
    IPlaybackHub hub,
    ISongReadModel songs) : IRequestHandler<DeleteSongCommand, bool>
{
    public async Task<bool> Handle(DeleteSongCommand cmd, CancellationToken ct)
    {
        var onDisk = storage.SongExists(cmd.UserId, cmd.File);
        var removed = await records.DeleteCascadeAsync(cmd.UserId, cmd.File, ct);
        if (!onDisk && !removed) return false;

        if (onDisk)
        {
            try { File.Delete(storage.SongPath(cmd.UserId, cmd.File)); }
            catch (IOException) { return false; }
        }

        await tombstones.RecordAsync(cmd.UserId, "songs", cmd.File, ct);
        await tombstones.RecordAsync(cmd.UserId, "lyrics", cmd.File, ct);
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

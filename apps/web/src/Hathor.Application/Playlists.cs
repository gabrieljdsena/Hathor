using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Playlists;

// Playlist CRUD + song links + covers. Writes go through IPlaylistRepository
// (EF); lists/counts through IPlaylistReadModel (Dapper).

public sealed record ListPlaylistsQuery(Guid UserId) : IRequest<IReadOnlyList<PlaylistDto>>;
public sealed record ListPlaylistsWithCountsQuery(Guid UserId) : IRequest<IReadOnlyList<PlaylistWithCountDto>>;
public sealed record GetPlaylistQuery(Guid UserId, long Id) : IRequest<PlaylistDto?>;
public sealed record GetPlaylistSongsQuery(Guid UserId, long PlaylistId) : IRequest<IReadOnlyList<SongDto>>;
public sealed record GetSongPlaylistsQuery(Guid UserId, string File) : IRequest<IReadOnlyList<long>>;

public sealed class PlaylistQueryHandlers(
    IPlaylistReadModel reads,
    IPlaylistRepository playlists,
    ISongReadModel songs) :
    IRequestHandler<ListPlaylistsQuery, IReadOnlyList<PlaylistDto>>,
    IRequestHandler<ListPlaylistsWithCountsQuery, IReadOnlyList<PlaylistWithCountDto>>,
    IRequestHandler<GetPlaylistQuery, PlaylistDto?>,
    IRequestHandler<GetPlaylistSongsQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<GetSongPlaylistsQuery, IReadOnlyList<long>>
{
    public Task<IReadOnlyList<PlaylistDto>> Handle(ListPlaylistsQuery q, CancellationToken ct) =>
        reads.ListAsync(q.UserId, ct);
    public Task<IReadOnlyList<PlaylistWithCountDto>> Handle(ListPlaylistsWithCountsQuery q, CancellationToken ct) =>
        reads.ListWithCountsAsync(q.UserId, ct);
    public Task<PlaylistDto?> Handle(GetPlaylistQuery q, CancellationToken ct) =>
        reads.GetAsync(q.UserId, q.Id, ct);

    public async Task<IReadOnlyList<SongDto>> Handle(GetPlaylistSongsQuery q, CancellationToken ct)
    {
        var files = await playlists.GetSongFilesAsync(q.UserId, q.PlaylistId, ct);
        var result = new List<SongDto>();
        foreach (var (file, added) in files)
        {
            var song = await songs.GetByFileAsync(q.UserId, file, includeCover: false, ct);
            if (song is null) continue; // file deleted from disk — skip like desktop
            result.Add(song with { DateDownload = added.ToString("yyyy-MM-ddTHH:mm:ss") + "Z" });
        }
        return result;
    }

    public async Task<IReadOnlyList<long>> Handle(GetSongPlaylistsQuery q, CancellationToken ct) =>
        await playlists.GetIdsForSongAsync(q.UserId, q.File, ct);
}

public sealed record CreatePlaylistCommand(Guid UserId, string Title, string? Description, string? Cover)
    : IRequest<PlaylistDto>;

public sealed class CreatePlaylistValidator : AbstractValidator<CreatePlaylistCommand>
{
    public CreatePlaylistValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Description).MaximumLength(4000);
    }
}

public sealed class CreatePlaylistHandler(IPlaylistRepository playlists)
    : IRequestHandler<CreatePlaylistCommand, PlaylistDto>
{
    public async Task<PlaylistDto> Handle(CreatePlaylistCommand cmd, CancellationToken ct)
    {
        var playlist = new Playlist
        {
            UserId = cmd.UserId,
            Title = cmd.Title.Trim(),
            Description = cmd.Description,
            Thumbnail = cmd.Cover,
        };
        await playlists.AddAsync(playlist, ct);
        await playlists.SaveChangesAsync(ct);
        return new PlaylistDto(playlist.Id, playlist.Title, playlist.Description, playlist.Thumbnail);
    }
}

public sealed record UpdatePlaylistCommand(
    Guid UserId, long Id, string? Title, string? Description, string? Thumbnail)
    : IRequest<PlaylistDto?>;

public sealed class UpdatePlaylistHandler(IPlaylistRepository playlists)
    : IRequestHandler<UpdatePlaylistCommand, PlaylistDto?>
{
    public const string RemoveThumbnailSentinel = "REMOVE";

    public async Task<PlaylistDto?> Handle(UpdatePlaylistCommand cmd, CancellationToken ct)
    {
        var playlist = await playlists.GetAsync(cmd.UserId, cmd.Id, ct);
        if (playlist is null) return null;
        if (cmd.Title is not null) playlist.Title = cmd.Title.Trim();
        if (cmd.Description is not null) playlist.Description = cmd.Description;
        if (cmd.Thumbnail == RemoveThumbnailSentinel) playlist.Thumbnail = null;
        else if (cmd.Thumbnail is not null) playlist.Thumbnail = cmd.Thumbnail;
        await playlists.SaveChangesAsync(ct);
        return new PlaylistDto(playlist.Id, playlist.Title, playlist.Description, playlist.Thumbnail);
    }
}

// Delete cascades links + playlist history and records tombstones
// (desktop delete_playlist + record_deletion).
public sealed record DeletePlaylistCommand(Guid UserId, long Id) : IRequest<bool>;

public sealed class DeletePlaylistHandler(
    IPlaylistRepository playlists, ITombstoneRepository tombstones)
    : IRequestHandler<DeletePlaylistCommand, bool>
{
    public async Task<bool> Handle(DeletePlaylistCommand cmd, CancellationToken ct)
    {
        var playlist = await playlists.GetAsync(cmd.UserId, cmd.Id, ct);
        if (playlist is null) return false;
        await playlists.RemoveLinksAsync(cmd.UserId, cmd.Id, null, ct);
        await playlists.RemovePlaylistHistoryAsync(cmd.UserId, cmd.Id, ct);
        playlists.Remove(playlist);
        await tombstones.RecordAsync(cmd.UserId, "playlists", cmd.Id.ToString(), ct);
        await tombstones.RecordAsync(cmd.UserId, "playlist_history", cmd.Id.ToString(), ct);
        await playlists.SaveChangesAsync(ct);
        await tombstones.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record AddSongToPlaylistCommand(Guid UserId, long PlaylistId, string SongFile)
    : IRequest<bool>;

public sealed class AddSongToPlaylistHandler(IPlaylistRepository playlists)
    : IRequestHandler<AddSongToPlaylistCommand, bool>
{
    public async Task<bool> Handle(AddSongToPlaylistCommand cmd, CancellationToken ct)
    {
        if (!await playlists.ExistsAsync(cmd.UserId, cmd.PlaylistId, ct)) return false;
        if (await playlists.LinkExistsAsync(cmd.UserId, cmd.PlaylistId, cmd.SongFile, ct)) return true;
        await playlists.AddLinkAsync(cmd.UserId, cmd.SongFile, cmd.PlaylistId, DateTime.UtcNow, ct);
        await playlists.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record RemoveSongFromPlaylistCommand(Guid UserId, long PlaylistId, string SongFile)
    : IRequest<bool>;

public sealed class RemoveSongFromPlaylistHandler(IPlaylistRepository playlists)
    : IRequestHandler<RemoveSongFromPlaylistCommand, bool>
{
    public async Task<bool> Handle(RemoveSongFromPlaylistCommand cmd, CancellationToken ct)
    {
        var removed = await playlists.RemoveLinksAsync(cmd.UserId, cmd.PlaylistId, cmd.SongFile, ct);
        if (removed > 0) await playlists.SaveChangesAsync(ct);
        return removed > 0;
    }
}

// Multi-assign (desktop update_song_playlists): ensure the song row exists,
// replace all associations (unknown playlist ids are ignored).
public sealed record SetSongPlaylistsCommand(
    Guid UserId, string SongFile, string SongTitle, IReadOnlyList<long> PlaylistIds)
    : IRequest<bool>;

public sealed class SetSongPlaylistsHandler(
    IPlaylistRepository playlists, ISongRecordRepository records)
    : IRequestHandler<SetSongPlaylistsCommand, bool>
{
    public async Task<bool> Handle(SetSongPlaylistsCommand cmd, CancellationToken ct)
    {
        var validIds = await playlists.FilterValidIdsAsync(cmd.UserId, cmd.PlaylistIds, ct);
        await playlists.RemoveLinksAsync(cmd.UserId, null, cmd.SongFile, ct);
        foreach (var pid in validIds)
            await playlists.AddLinkAsync(cmd.UserId, cmd.SongFile, pid, DateTime.UtcNow, ct);
        await records.EnsureAsync(cmd.UserId, cmd.SongFile, cmd.SongTitle, ct);
        await playlists.SaveChangesAsync(ct);
        await records.SaveChangesAsync(ct);
        return true;
    }
}

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
// Paging is opt-in (null = whole list, legacy callers); search filters
// title/artist/album with the same fuzzy rules as the library.
public sealed record GetPlaylistSongsQuery(Guid UserId, long PlaylistId, string? Search = null,
    string? Sort = null, string? Dir = null,
    int? Page = null, int? PageSize = null) : IRequest<IReadOnlyList<SongDto>>;
public sealed record GetPlaylistSongsCountQuery(Guid UserId, long PlaylistId, string? Search = null)
    : IRequest<int>;
public sealed record GetPlaylistFilesQuery(Guid UserId, long PlaylistId, string? Search = null,
    string? Sort = null, string? Dir = null)
    : IRequest<IReadOnlyList<string>>;
public sealed record GetSongPlaylistsQuery(Guid UserId, string File) : IRequest<IReadOnlyList<long>>;

public sealed class GetPlaylistSongsValidator : AbstractValidator<GetPlaylistSongsQuery>
{
    private static readonly string[] Sorts = ["Title", "Album", "Duration", "DateDownload"];
    public GetPlaylistSongsValidator()
    {
        RuleFor(x => x.Page).Must(p => p is null || p >= 1).WithMessage("Page must be >= 1.");
        RuleFor(x => x.PageSize).Must(p => p is null || (p >= 1 && p <= 5000))
            .WithMessage("PageSize must be 1..5000.");
        RuleFor(x => x.Sort).Must(s => s is null || Sorts.Contains(s))
            .WithMessage("Sort must be Title, Album, Duration or DateDownload.");
        RuleFor(x => x.Dir).Must(d => d is null || d is "asc" or "desc")
            .WithMessage("Dir must be asc or desc.");
    }
}

public sealed class PlaylistQueryHandlers(
    IPlaylistReadModel reads,
    IPlaylistRepository playlists,
    ISongReadModel songs) :
    IRequestHandler<ListPlaylistsQuery, IReadOnlyList<PlaylistDto>>,
    IRequestHandler<ListPlaylistsWithCountsQuery, IReadOnlyList<PlaylistWithCountDto>>,
    IRequestHandler<GetPlaylistQuery, PlaylistDto?>,
    IRequestHandler<GetPlaylistSongsQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<GetPlaylistSongsCountQuery, int>,
    IRequestHandler<GetPlaylistFilesQuery, IReadOnlyList<string>>,
    IRequestHandler<GetSongPlaylistsQuery, IReadOnlyList<long>>
{
    public Task<IReadOnlyList<PlaylistDto>> Handle(ListPlaylistsQuery q, CancellationToken ct) =>
        reads.ListAsync(q.UserId, ct);
    public Task<IReadOnlyList<PlaylistWithCountDto>> Handle(ListPlaylistsWithCountsQuery q, CancellationToken ct) =>
        reads.ListWithCountsAsync(q.UserId, ct);
    public Task<PlaylistDto?> Handle(GetPlaylistQuery q, CancellationToken ct) =>
        reads.GetAsync(q.UserId, q.Id, ct);

    // Link order (DateAddedUtc) is the playlist order — filter/search/page
    // over it, resolving DTOs in one batched read (no per-file TagLib for
    // catalogued rows).
    public async Task<IReadOnlyList<SongDto>> Handle(GetPlaylistSongsQuery q, CancellationToken ct)
    {
        var ordered = await OrderedPlaylistSongsAsync(q.UserId, q.PlaylistId, q.Search, q.Sort, q.Dir, ct);
        if (q.Page is null || q.PageSize is null) return ordered.Select(x => x.Song).ToList();
        return ordered.Select(x => x.Song)
            .Skip((Math.Max(q.Page.Value, 1) - 1) * Math.Max(q.PageSize.Value, 1))
            .Take(Math.Max(q.PageSize.Value, 1))
            .ToList();
    }

    public async Task<int> Handle(GetPlaylistSongsCountQuery q, CancellationToken ct) =>
        (await OrderedPlaylistSongsAsync(q.UserId, q.PlaylistId, q.Search, null, null, ct)).Count;

    public async Task<IReadOnlyList<string>> Handle(GetPlaylistFilesQuery q, CancellationToken ct)
    {
        var files = (await OrderedPlaylistSongsAsync(q.UserId, q.PlaylistId, q.Search, q.Sort, q.Dir, ct))
            .Select(x => x.Song.File).ToList();
        if (files.Count > Library.LibraryLimits.MaxFiles)
            throw new ValidationException($"Too many files ({files.Count}, max {Library.LibraryLimits.MaxFiles}).");
        return files;
    }

    private async Task<List<(SongDto Song, DateTime Added)>> OrderedPlaylistSongsAsync(
        Guid userId, long playlistId, string? search, string? sort, string? dir, CancellationToken ct)
    {
        var files = await playlists.GetSongFilesAsync(userId, playlistId, ct);
        var dtos = await songs.GetManyAsync(userId, files.Select(f => f.File),
            includeCover: false, ct);
        var byFile = dtos.ToDictionary(s => s.File, StringComparer.OrdinalIgnoreCase);
        var result = new List<(SongDto Song, DateTime Added)>();
        foreach (var (file, added) in files)
        {
            if (!byFile.TryGetValue(file, out var song)) continue; // deleted from disk — skip like desktop
            song = song with { DateDownload = Domain.UtcIso.Format(added) };
            if (!MatchesSearch(song, search)) continue;
            result.Add((song, added));
        }
        // Default is link order (DateAddedUtc); explicit sorts mirror the
        // library table (a re-query per sort is slower than RAM sort but
        // keeps tab memory flat — the requested tradeoff).
        var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        var ordered = (sort switch
        {
            "Title" => desc
                ? result.OrderByDescending(x => x.Song.Title)
                : result.OrderBy(x => x.Song.Title),
            "Album" => desc
                ? result.OrderByDescending(x => x.Song.Album).ThenByDescending(x => x.Song.Title)
                : result.OrderBy(x => x.Song.Album).ThenBy(x => x.Song.Title),
            "Duration" => desc
                ? result.OrderByDescending(x => x.Song.Duration)
                : result.OrderBy(x => x.Song.Duration),
            "DateDownload" => desc
                ? result.OrderByDescending(x => x.Added)
                : result.OrderBy(x => x.Added),
            _ => desc
                ? result.OrderByDescending(x => x.Added)
                : result.OrderBy(x => x.Added),
        }).ToList();
        return ordered;
    }

    private static bool MatchesSearch(SongDto song, string? search) =>
        Hathor.Domain.Services.FuzzySearch.IsMatch([song.Title, song.Artist, song.Album], search);

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
            Thumbnail = PlaylistCover.Normalize(cmd.Cover),
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
        else if (cmd.Thumbnail is not null) playlist.Thumbnail = PlaylistCover.Normalize(cmd.Thumbnail);
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

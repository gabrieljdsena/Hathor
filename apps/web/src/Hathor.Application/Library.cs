using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.Library;

// Artist/album browsing: exact metadata matches, desktop-sorted
// (artist view Album+Title, album view Artist+Title).
// Paging is opt-in (null = whole list, legacy callers); infinite scroll
// passes page/pageSize, queue building uses the Files queries.

public sealed record ListArtistsQuery(Guid UserId) : IRequest<IReadOnlyList<string>>;
public sealed record ArtistSongsQuery(Guid UserId, string Artist, string? Search = null,
    string? Sort = null, string? Dir = null,
    int? Page = null, int? PageSize = null) : IRequest<IReadOnlyList<SongDto>>;
public sealed record ArtistSongCountQuery(Guid UserId, string Artist, string? Search = null)
    : IRequest<int>;
public sealed record ArtistFilesQuery(Guid UserId, string Artist, string? Search = null,
    string? Sort = null, string? Dir = null)
    : IRequest<IReadOnlyList<string>>;
public sealed record ListAlbumsQuery(Guid UserId) : IRequest<IReadOnlyList<string>>;
public sealed record AlbumSongsQuery(Guid UserId, string Album, string? Search = null,
    string? Sort = null, string? Dir = null,
    int? Page = null, int? PageSize = null) : IRequest<IReadOnlyList<SongDto>>;
public sealed record AlbumSongCountQuery(Guid UserId, string Album, string? Search = null)
    : IRequest<int>;
public sealed record AlbumFilesQuery(Guid UserId, string Album, string? Search = null,
    string? Sort = null, string? Dir = null)
    : IRequest<IReadOnlyList<string>>;

public abstract class PagedListValidator<T> : AbstractValidator<T> where T : class
{
    private static readonly string[] Sorts = ["Title", "Album", "Artist", "Duration", "DateDownload"];

    protected void PageRules(
        System.Linq.Expressions.Expression<System.Func<T, int?>> page,
        System.Linq.Expressions.Expression<System.Func<T, int?>> pageSize)
    {
        RuleFor(page).Must(p => p is null || p >= 1).WithMessage("Page must be >= 1.");
        RuleFor(pageSize).Must(p => p is null || (p >= 1 && p <= 5000))
            .WithMessage("PageSize must be 1..5000.");
    }

    protected void SortRules(
        System.Linq.Expressions.Expression<System.Func<T, string?>> sort,
        System.Linq.Expressions.Expression<System.Func<T, string?>> dir)
    {
        RuleFor(sort).Must(s => s is null || Sorts.Contains(s))
            .WithMessage("Sort must be Title, Album, Artist, Duration or DateDownload.");
        RuleFor(dir).Must(d => d is null || d is "asc" or "desc")
            .WithMessage("Dir must be asc or desc.");
    }
}

public sealed class ArtistSongsValidator : PagedListValidator<ArtistSongsQuery>
{
    public ArtistSongsValidator()
    {
        PageRules(x => x.Page, x => x.PageSize);
        SortRules(x => x.Sort, x => x.Dir);
    }
}

public sealed class AlbumSongsValidator : PagedListValidator<AlbumSongsQuery>
{
    public AlbumSongsValidator()
    {
        PageRules(x => x.Page, x => x.PageSize);
        SortRules(x => x.Sort, x => x.Dir);
    }
}

public static class LibraryLimits
{
    // Files-query guard (see ListSongFilesHandler): queue-size cap.
    public const int MaxFiles = 20000;
}

public sealed class LibraryQueryHandlers(ISongReadModel songs) :
    IRequestHandler<ListArtistsQuery, IReadOnlyList<string>>,
    IRequestHandler<ArtistSongsQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<ArtistSongCountQuery, int>,
    IRequestHandler<ArtistFilesQuery, IReadOnlyList<string>>,
    IRequestHandler<ListAlbumsQuery, IReadOnlyList<string>>,
    IRequestHandler<AlbumSongsQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<AlbumSongCountQuery, int>,
    IRequestHandler<AlbumFilesQuery, IReadOnlyList<string>>
{
    public Task<IReadOnlyList<string>> Handle(ListArtistsQuery q, CancellationToken ct) =>
        songs.GetArtistsAsync(q.UserId, ct);
    public Task<IReadOnlyList<SongDto>> Handle(ArtistSongsQuery q, CancellationToken ct) =>
        songs.GetSongsByArtistAsync(q.UserId, q.Artist, q.Search, q.Sort, q.Dir, q.Page, q.PageSize, ct);
    public Task<int> Handle(ArtistSongCountQuery q, CancellationToken ct) =>
        songs.GetArtistSongCountAsync(q.UserId, q.Artist, q.Search, ct);
    public async Task<IReadOnlyList<string>> Handle(ArtistFilesQuery q, CancellationToken ct)
    {
        var files = await songs.GetArtistFilesAsync(q.UserId, q.Artist, q.Search, q.Sort, q.Dir, ct);
        if (files.Count > LibraryLimits.MaxFiles)
            throw new ValidationException($"Too many files ({files.Count}, max {LibraryLimits.MaxFiles}).");
        return files;
    }
    public Task<IReadOnlyList<string>> Handle(ListAlbumsQuery q, CancellationToken ct) =>
        songs.GetAlbumsAsync(q.UserId, ct);
    public Task<IReadOnlyList<SongDto>> Handle(AlbumSongsQuery q, CancellationToken ct) =>
        songs.GetSongsByAlbumAsync(q.UserId, q.Album, q.Search, q.Sort, q.Dir, q.Page, q.PageSize, ct);
    public Task<int> Handle(AlbumSongCountQuery q, CancellationToken ct) =>
        songs.GetAlbumSongCountAsync(q.UserId, q.Album, q.Search, ct);
    public async Task<IReadOnlyList<string>> Handle(AlbumFilesQuery q, CancellationToken ct)
    {
        var files = await songs.GetAlbumFilesAsync(q.UserId, q.Album, q.Search, q.Sort, q.Dir, ct);
        if (files.Count > LibraryLimits.MaxFiles)
            throw new ValidationException($"Too many files ({files.Count}, max {LibraryLimits.MaxFiles}).");
        return files;
    }
}

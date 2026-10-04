using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.Songs;

// GET /api/v1/songs (Dapper read-model)
public sealed record ListSongsQuery(
    Guid UserId, string? Search, string? Sort, string? Dir,
    int Page = 1, int PageSize = 50) : IRequest<IReadOnlyList<SongDto>>;

public sealed class ListSongsQueryValidator : AbstractValidator<ListSongsQuery>
{
    private static readonly string[] Sorts = ["Title", "Album", "Duration", "DateDownload"];
    public ListSongsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        // Full-library fetch (desktop get_all_songs loads everything, covers
        // excluded); still guards absurd values.
        RuleFor(x => x.PageSize).InclusiveBetween(1, 5000);
        RuleFor(x => x.Sort).Must(s => s is null || Sorts.Contains(s))
            .WithMessage("Sort must be Title, Album, Duration or DateDownload.");
        RuleFor(x => x.Dir).Must(d => d is null || d is "asc" or "desc")
            .WithMessage("Dir must be asc or desc.");
    }
}

public sealed class ListSongsHandler(ISongReadModel songs) : IRequestHandler<ListSongsQuery, IReadOnlyList<SongDto>>
{
    public Task<IReadOnlyList<SongDto>> Handle(ListSongsQuery q, CancellationToken ct) =>
        songs.ListAsync(q.UserId, q.Search, q.Sort ?? "Title", q.Dir ?? "asc", q.Page, q.PageSize, ct);
}

// GET /api/v1/songs/count
public sealed record GetSongCountQuery(Guid UserId, string? Search) : IRequest<int>;

public sealed class GetSongCountHandler(ISongReadModel songs) : IRequestHandler<GetSongCountQuery, int>
{
    public Task<int> Handle(GetSongCountQuery q, CancellationToken ct) =>
        songs.CountAsync(q.UserId, q.Search, ct);
}

// GET /api/v1/songs/{file}
public sealed record GetSongQuery(Guid UserId, string File, bool IncludeCover) : IRequest<SongDto?>;

public sealed class GetSongHandler(ISongReadModel songs) : IRequestHandler<GetSongQuery, SongDto?>
{
    public Task<SongDto?> Handle(GetSongQuery q, CancellationToken ct) =>
        songs.GetByFileAsync(q.UserId, q.File, q.IncludeCover, ct);
}

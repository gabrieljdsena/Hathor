using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.History;

// History + recents. Rows whose files vanished from disk are skipped
// (desktop files-must-exist filter); pagination mirrors desktop
// {items, total_pages, current_page} (serialized camelCase).

public sealed record GetDownloadHistoryQuery(Guid UserId, int Page = 1, int PageSize = 10)
    : IRequest<PagedResult<HistoryItemDto>>;
public sealed record GetPlayedHistoryQuery(Guid UserId, int Page = 1, int PageSize = 10)
    : IRequest<PagedResult<HistoryItemDto>>;
public sealed record GetPlayedPlaylistHistoryQuery(Guid UserId, int Page = 1, int PageSize = 10)
    : IRequest<PagedResult<PlayedPlaylistItemDto>>;
public sealed record GetRecentlyPlayedQuery(Guid UserId, int Limit = 15)
    : IRequest<IReadOnlyList<SongDto>>;
public sealed record GetRecentlyDownloadedQuery(Guid UserId, int Limit = 15)
    : IRequest<IReadOnlyList<SongDto>>;

public sealed class HistoryHandlers(
    IHistoryReadModel history,
    ISongReadModel songs,
    IPlaylistReadModel playlists) :
    IRequestHandler<GetDownloadHistoryQuery, PagedResult<HistoryItemDto>>,
    IRequestHandler<GetPlayedHistoryQuery, PagedResult<HistoryItemDto>>,
    IRequestHandler<GetPlayedPlaylistHistoryQuery, PagedResult<PlayedPlaylistItemDto>>,
    IRequestHandler<GetRecentlyPlayedQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<GetRecentlyDownloadedQuery, IReadOnlyList<SongDto>>
{
    public async Task<PagedResult<HistoryItemDto>> Handle(GetDownloadHistoryQuery q, CancellationToken ct)
    {
        var (page, pageSize) = Clamp(q.Page, q.PageSize);
        var (rows, total) = await history.GetDownloadPageAsync(q.UserId, page, pageSize, ct);
        var items = new List<HistoryItemDto>();
        foreach (var (file, date, link) in rows)
        {
            var song = await songs.GetByFileAsync(q.UserId, file, includeCover: true, ct);
            if (song is null) continue;
            items.Add(new HistoryItemDto(song, null, Iso(date), link ?? ""));
        }
        return Page(items, total, page, pageSize);
    }

    public async Task<PagedResult<HistoryItemDto>> Handle(GetPlayedHistoryQuery q, CancellationToken ct)
    {
        var (page, pageSize) = Clamp(q.Page, q.PageSize);
        var (rows, total) = await history.GetPlayedPageAsync(q.UserId, page, pageSize, ct);
        var items = new List<HistoryItemDto>();
        foreach (var (file, date) in rows)
        {
            var song = await songs.GetByFileAsync(q.UserId, file, includeCover: true, ct);
            if (song is null) continue;
            items.Add(new HistoryItemDto(song, Iso(date), song.DateDownload, null));
        }
        return Page(items, total, page, pageSize);
    }

    public async Task<PagedResult<PlayedPlaylistItemDto>> Handle(
        GetPlayedPlaylistHistoryQuery q, CancellationToken ct)
    {
        var (page, pageSize) = Clamp(q.Page, q.PageSize);
        var (rows, total) = await history.GetPlayedPlaylistPageAsync(q.UserId, page, pageSize, ct);
        var items = new List<PlayedPlaylistItemDto>();
        foreach (var (playlistId, date) in rows)
        {
            var playlist = await playlists.GetAsync(q.UserId, playlistId, ct);
            if (playlist is null) continue; // deleted playlist — skip
            items.Add(new PlayedPlaylistItemDto(playlist, Iso(date)));
        }
        return Page(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<SongDto>> Handle(GetRecentlyPlayedQuery q, CancellationToken ct)
    {
        var files = await history.GetRecentPlayedFilesAsync(q.UserId, Math.Max(1, q.Limit), ct);
        return await songs.GetManyAsync(q.UserId, files, includeCover: false, ct);
    }

    public async Task<IReadOnlyList<SongDto>> Handle(GetRecentlyDownloadedQuery q, CancellationToken ct)
    {
        var files = await history.GetRecentDownloadedFilesAsync(q.UserId, Math.Max(1, q.Limit), ct);
        return await songs.GetManyAsync(q.UserId, files, includeCover: false, ct);
    }

    private static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 100));

    private static PagedResult<T> Page<T>(List<T> items, int total, int page, int pageSize)
    {
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize);
        var current = Math.Min(page, totalPages);
        return new PagedResult<T>(items, totalPages, current);
    }

    private static string Iso(DateTime dt) =>
        dt.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
}

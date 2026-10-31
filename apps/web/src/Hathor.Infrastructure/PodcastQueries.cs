using Dapper;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;

namespace Hathor.Infrastructure.Library;

// Podcast read-model: lightweight listing (DB titles + disk files, newest
// first — desktop get_podcasts) with on-demand full details
// (desktop get_podcast_details).
public sealed class DapperPodcastReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory,
    ILibraryStorage storage,
    SongMetadataReader metadata) : IPodcastReadModel
{
    public async Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, string? search = null,
        long? tagId = null, int? page = null, int? pageSize = null, CancellationToken ct = default)
    {
        var ordered = await OrderedAsync(userId, search, tagId, ct);
        // Null page/pageSize = whole list (legacy callers).
        if (page is null || pageSize is null) return ordered;
        return ordered.Skip((Math.Max(page.Value, 1) - 1) * Math.Max(pageSize.Value, 1))
            .Take(Math.Max(pageSize.Value, 1))
            .ToList();
    }

    public async Task<int> CountAsync(Guid userId, string? search = null, long? tagId = null,
        CancellationToken ct = default) =>
        (await OrderedAsync(userId, search, tagId, ct)).Count;

    public async Task<IReadOnlyList<string>> ListFilesAsync(Guid userId, string? search = null,
        long? tagId = null, CancellationToken ct = default) =>
        (await OrderedAsync(userId, search, tagId, ct)).Select(s => s.File).ToList();

    private async Task<List<SongDto>> OrderedAsync(
        Guid userId, string? search, long? tagId, CancellationToken ct)
    {
        var dates = await DateLookupAsync(userId, ct);
        HashSet<string>? tagged = null;
        if (tagId.HasValue) tagged = await FilesForTagAsync(userId, tagId.Value, ct);
        var list = new List<(SongDto Song, string SortKey)>();
        foreach (var file in storage.ListPodcastFiles(userId))
        {
            if (tagged is not null && !tagged.Contains(file)) continue;
            var title = dates.TryGetValue(file, out var row) && !string.IsNullOrWhiteSpace(row.Title)
                ? row.Title
                : Path.GetFileNameWithoutExtension(file);
            var artist = dates.TryGetValue(file, out var r2) ? r2.Artist ?? "" : "";
            var dto = new SongDto(file, artist, title, "", "", 0, null,
                dates.TryGetValue(file, out var r3) ? Iso(r3.Date) : null, IsPodcast: true);
            if (!FuzzyMatches(dto, search)) continue;
            list.Add((dto, dates.TryGetValue(file, out var r4) ? Iso(r4.Date) : ""));
        }
        return list.OrderByDescending(x => x.SortKey, StringComparer.Ordinal).Select(x => x.Song).ToList();
    }

    // Live file names carrying a tag (scoped to the tag — no full-table map).
    private async Task<HashSet<string>> FilesForTagAsync(Guid userId, long tagId, CancellationToken ct)
    {
        var sql = $"SELECT {factory.Quote("PodcastFile")} " +
            $"FROM {factory.Quote("PodcastTagLinks")} WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("TagId")} = @TagId";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql,
                new
                {
                    UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                    TagId = tagId,
                },
                cancellationToken: ct));
        return new HashSet<string>(rows, StringComparer.OrdinalIgnoreCase);
    }

    // Typo-tolerant episode filter (same fuzzy rules as the song search).
    private static bool FuzzyMatches(SongDto dto, string? search) =>
        Hathor.Domain.Services.FuzzySearch.IsMatch([dto.Title, dto.Artist], search);

    public async Task<SongDto?> GetByFileAsync(Guid userId, string file, CancellationToken ct = default)
    {
        if (!storage.PodcastExists(userId, file)) return null;
        var dto = metadata.ReadPodcastFile(userId, file, includeCover: true);
        // RAM/IO: single-row lookup (was: full Podcasts table per details call,
        // and cover fetches hit this endpoint per row).
        var row = await RowForFileAsync(userId, file, ct);
        return dto with
        {
            IsPodcast = true,
            DateDownload = row is null ? dto.DateDownload : Iso(row.Value.Date),
        };
    }

    public SongDto ReadLocalEpisode(Guid userId, string file)
    {
        var dto = metadata.ReadPodcastFile(userId, file, includeCover: false);
        return dto with { IsPodcast = true };
    }

    public (byte[] Bytes, string Mime, string Version)? ReadCover(Guid userId, string file)
    {
        // PodcastExists guards path traversal (bare filenames only).
        if (!storage.PodcastExists(userId, file)) return null;
        return SongMetadataReader.ReadCoverBytes(storage.PodcastPath(userId, file));
    }

    private async Task<(string Title, string? Artist, DateTime Date)?> RowForFileAsync(
        Guid userId, string file, CancellationToken ct)
    {
        // File is selected as the existence marker (Title itself may be NULL).
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")}, {factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Podcasts")} WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("File")} = @File LIMIT 1";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string? File, string? Title, string? Artist, DateTime Date)>(
            new CommandDefinition(sql,
                new
                {
                    UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                    File = file,
                },
                cancellationToken: ct));
        var row = rows.FirstOrDefault();
        return row.File is null ? null : (row.Title ?? "", row.Artist, row.Date);
    }

    private async Task<Dictionary<string, (string Title, string? Artist, DateTime Date)>> DateLookupAsync(
        Guid userId, CancellationToken ct)
    {
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")}, {factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Podcasts")} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string File, string Title, string? Artist, DateTime Date)>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        var lookup = new Dictionary<string, (string Title, string? Artist, DateTime Date)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) lookup[r.File] = (r.Title, r.Artist, r.Date);
        return lookup;
    }

    private static string Iso(DateTime dt) => Domain.UtcIso.Format(dt);
}

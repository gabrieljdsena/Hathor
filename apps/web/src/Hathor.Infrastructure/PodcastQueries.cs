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
    public async Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var dates = await DateLookupAsync(userId, ct);
        var list = new List<(SongDto Song, string SortKey)>();
        foreach (var file in storage.ListPodcastFiles(userId))
        {
            var title = dates.TryGetValue(file, out var row) && !string.IsNullOrWhiteSpace(row.Title)
                ? row.Title
                : Path.GetFileNameWithoutExtension(file);
            var artist = dates.TryGetValue(file, out var r2) ? r2.Artist ?? "" : "";
            list.Add((new SongDto(file, artist, title, "", "", 0, null,
                dates.TryGetValue(file, out var r3) ? Iso(r3.Date) : null, IsPodcast: true),
                dates.TryGetValue(file, out var r4) ? Iso(r4.Date) : ""));
        }
        return list.OrderByDescending(x => x.SortKey, StringComparer.Ordinal).Select(x => x.Song).ToList();
    }

    public async Task<SongDto?> GetByFileAsync(Guid userId, string file, CancellationToken ct = default)
    {
        if (!storage.PodcastExists(userId, file)) return null;
        var dto = metadata.ReadPodcastFile(userId, file, includeCover: true);
        var dates = await DateLookupAsync(userId, ct);
        return dto with
        {
            IsPodcast = true,
            DateDownload = dates.TryGetValue(file, out var r) ? Iso(r.Date) : dto.DateDownload,
        };
    }

    public SongDto ReadLocalEpisode(Guid userId, string file)
    {
        var dto = metadata.ReadPodcastFile(userId, file, includeCover: false);
        return dto with { IsPodcast = true };
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

    private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
}

using Dapper;
using Hathor.Application.Ports;

namespace Hathor.Infrastructure.Library;

// History pages + recents (desktop get_download/played_history, recents strips).
// Identifiers are quoted (Postgres needs quoted PascalCase); UserId compares
// uppercase (EF Guids are native uuid on Postgres — cast to text first).
public sealed class DapperHistoryReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory) : IHistoryReadModel
{
    private static object P(Guid userId) =>
        new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) };

    public async Task<(IReadOnlyList<(string File, DateTime DateDownload, string? Link)> Items, int Total)>
        GetDownloadPageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var t = factory.Quote("Songs");
        var rows = $"SELECT {factory.Quote("File")}, {factory.Quote("DateDownloadUtc")}, " +
            $"{factory.Quote("DownloadedLink")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("DownloadedLink")} IS NOT NULL AND {factory.Quote("DownloadedLink")} != '' " +
            $"ORDER BY {factory.Quote("DateDownloadUtc")} DESC LIMIT @Limit OFFSET @Offset";
        var count = $"SELECT COUNT(*) FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("DownloadedLink")} IS NOT NULL AND {factory.Quote("DownloadedLink")} != ''";
        using var conn = factory.Create();
        var items = (await conn.QueryAsync<(string File, DateTime DateDownload, string? Link)>(
            new CommandDefinition(rows, new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = pageSize,
                Offset = (page - 1) * pageSize,
            }, cancellationToken: ct))).ToList();
        var total = await conn.QuerySingleAsync<int>(
            new CommandDefinition(count, P(userId), cancellationToken: ct));
        return (items, total);
    }

    public async Task<(IReadOnlyList<(string File, DateTime DatePlayed)> Items, int Total)>
        GetPlayedPageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var t = factory.Quote("Music_History");
        var rows = $"SELECT {factory.Quote("SongFile")} AS {factory.Quote("File")}, " +
            $"{factory.Quote("DatePlayedUtc")} AS {factory.Quote("DatePlayed")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"ORDER BY {factory.Quote("DatePlayedUtc")} DESC, {factory.Quote("Id")} DESC " +
            "LIMIT @Limit OFFSET @Offset";
        var count = $"SELECT COUNT(*) FROM {t} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var items = (await conn.QueryAsync<(string File, DateTime DatePlayed)>(
            new CommandDefinition(rows, new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = pageSize,
                Offset = (page - 1) * pageSize,
            }, cancellationToken: ct))).ToList();
        var total = await conn.QuerySingleAsync<int>(
            new CommandDefinition(count, P(userId), cancellationToken: ct));
        return (items, total);
    }

    public async Task<(IReadOnlyList<(long PlaylistId, DateTime DatePlayed)> Items, int Total)>
        GetPlayedPlaylistPageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var t = factory.Quote("Playlist_History");
        var rows = $"SELECT {factory.Quote("PlaylistId")}, " +
            $"{factory.Quote("DatePlayedUtc")} AS {factory.Quote("DatePlayed")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"ORDER BY {factory.Quote("DatePlayedUtc")} DESC, {factory.Quote("Id")} DESC " +
            "LIMIT @Limit OFFSET @Offset";
        var count = $"SELECT COUNT(*) FROM {t} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var items = (await conn.QueryAsync<(long PlaylistId, DateTime DatePlayed)>(
            new CommandDefinition(rows, new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = pageSize,
                Offset = (page - 1) * pageSize,
            }, cancellationToken: ct))).ToList();
        var total = await conn.QuerySingleAsync<int>(
            new CommandDefinition(count, P(userId), cancellationToken: ct));
        return (items, total);
    }

    public async Task<IReadOnlyList<string>> GetRecentPlayedFilesAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        // Unique files, newest first — deduped client-side in ORDER (desktop seen-set).
        var t = factory.Quote("Music_History");
        var sql = $"SELECT {factory.Quote("SongFile")} AS {factory.Quote("File")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"ORDER BY {factory.Quote("DatePlayedUtc")} DESC, {factory.Quote("Id")} DESC LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql, new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Max(limit * 3, limit),
            }, cancellationToken: ct));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var file in rows)
        {
            if (!seen.Add(file)) continue;
            files.Add(file);
            if (files.Count >= limit) break;
        }
        return files;
    }

    public async Task<IReadOnlyList<string>> GetRecentDownloadedFilesAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var t = factory.Quote("Songs");
        var sql = $"SELECT {factory.Quote("File")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"ORDER BY {factory.Quote("DateDownloadUtc")} DESC LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql, new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = limit,
            }, cancellationToken: ct));
        return rows.ToList();
    }
}

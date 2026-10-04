using Dapper;
using Hathor.Application.Ports;

namespace Hathor.Infrastructure.Library;

// Discover taste read-model (packages/contracts/discover.md rules 1-2):
// top artists by Music_History plays joined to Songs for artist names,
// owned title/artist pairs, and in-flight download pairs for dedupe.
// Same quoting/UserId conventions as the other Dapper read models.
public sealed class DapperDiscoverTasteReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory) : IDiscoverTasteReadModel
{
    private static object P(Guid userId) =>
        new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) };

    public async Task<IReadOnlyList<(string Artist, long Plays)>> GetTopArtistsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var h = factory.Quote("Music_History");
        var s = factory.Quote("Songs");
        var sql = $"SELECT s.{factory.Quote("Artist")} AS {factory.Quote("Artist")}, " +
            $"COUNT(*) AS {factory.Quote("Plays")} FROM {h} h " +
            $"JOIN {s} s ON s.{factory.Quote("File")} = h.{factory.Quote("SongFile")} " +
            $"AND s.{factory.Quote("UserId")} = h.{factory.Quote("UserId")} " +
            $"WHERE {factory.UserIdPredicate("h")} " +
            $"AND s.{factory.Quote("Artist")} IS NOT NULL " +
            $"AND s.{factory.Quote("Artist")} NOT IN ('', 'Unknown') " +
            $"GROUP BY s.{factory.Quote("Artist")} " +
            $"ORDER BY COUNT(*) DESC LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string Artist, long Plays)>(
            new CommandDefinition(sql,
                new
                {
                    UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                    Limit = limit,
                }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<(string Title, string Artist)>> GetLibraryPairsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var t = factory.Quote("Songs");
        var sql = $"SELECT {factory.Quote("Title")}, {factory.Quote("Artist")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string Title, string? Artist)>(
            new CommandDefinition(sql, P(userId), cancellationToken: ct));
        return rows.Select(r => (r.Title, r.Artist ?? "")).ToList();
    }

    public async Task<IReadOnlyList<(string Title, string Artist)>> GetActiveDownloadPairsAsync(
        Guid userId, CancellationToken ct = default)
    {
        // EF maps DownloadJob to Download_Queue (desktop table name) — not "DownloadJobs".
        var t = factory.Quote("Download_Queue");
        var sql = $"SELECT {factory.Quote("Title")}, {factory.Quote("Artist")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("Status")} IN ('queued', 'downloading', 'processing')";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string? Title, string? Artist)>(
            new CommandDefinition(sql, P(userId), cancellationToken: ct));
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .Select(r => (r.Title!, r.Artist ?? ""))
            .ToList();
    }
}

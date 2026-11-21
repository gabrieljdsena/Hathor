using Dapper;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Hathor.Domain.Services;

namespace Hathor.Infrastructure.Library;

// Discover taste read-model (packages/contracts/discover.md rules 1-2):
// recency-weighted top artists/tracks/tags from the shared play stream
// (Playback_Events with Music_History fallback), plus owned title+artist
// pairs and in-flight download pairs for dedupe. Local-only signals: no
// external profile data is involved.
public sealed class DapperDiscoverTasteReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory,
    DiscoveryOptions options) : IDiscoverTasteReadModel
{
    private static object P(Guid userId) =>
        new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) };

    private string Cte() =>
        EffectivePlayQueries.Cte(factory, options.InferPlaybackQuality);

    public async Task<IReadOnlyList<TasteArtist>> GetTopArtistsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var s = factory.Quote("Songs");
        var artist = factory.Quote("Artist");
        var plays = factory.Quote("Plays");
        var weight = factory.Quote("Weight");
        var sql = $"{Cte()} " +
            $"SELECT s.{artist} AS {artist}, " +
            $"COUNT(*) AS {plays}, " +
            $"SUM({EffectivePlayQueries.Decay("p")} * p.{EffectivePlayQueries.QualityColumn})::double precision AS {weight} " +
            $"FROM plays p JOIN {s} s ON s.{factory.Quote("File")} = p.{EffectivePlayQueries.FileColumn} " +
            $"AND {factory.UserIdPredicate("s")} " +
            $"WHERE s.{artist} IS NOT NULL " +
            $"AND s.{artist} NOT IN ('', 'Unknown') " +
            $"GROUP BY s.{artist} " +
            $"ORDER BY {weight} DESC LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<TasteArtist>(new CommandDefinition(sql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 100),
                HalfLifeDays = Math.Clamp(options.ArtistHalfLifeDays, 1, 3650),
                PlaybackSince = DateTime.UtcNow.AddDays(
                    -Math.Clamp(options.PlaybackWindowDays, 1, 3650)),
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TasteTrack>> GetTopTracksAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var s = factory.Quote("Songs");
        var title = factory.Quote("Title");
        var artist = factory.Quote("Artist");
        var plays = factory.Quote("Plays");
        var weight = factory.Quote("Weight");
        var sql = $"{Cte()} " +
            $"SELECT s.{title} AS {title}, " +
            $"s.{artist} AS {artist}, " +
            $"COUNT(*) AS {plays}, " +
            $"SUM({EffectivePlayQueries.Decay("p")} * p.{EffectivePlayQueries.QualityColumn})::double precision AS {weight} " +
            $"FROM plays p JOIN {s} s ON s.{factory.Quote("File")} = p.{EffectivePlayQueries.FileColumn} " +
            $"AND {factory.UserIdPredicate("s")} " +
            $"WHERE s.{title} IS NOT NULL AND s.{title} <> '' " +
            $"AND s.{artist} IS NOT NULL " +
            $"AND s.{artist} NOT IN ('', 'Unknown') " +
            $"GROUP BY s.{title}, s.{artist} " +
            $"ORDER BY {weight} DESC LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<TasteTrack>(new CommandDefinition(sql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 100),
                HalfLifeDays = Math.Clamp(options.ArtistHalfLifeDays, 1, 3650),
                PlaybackSince = DateTime.UtcNow.AddDays(
                    -Math.Clamp(options.PlaybackWindowDays, 1, 3650)),
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TasteFile>> GetTopPlayedFilesAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var s = factory.Quote("Songs");
        var file = factory.Quote("File");
        var weight = factory.Quote("Weight");
        var sql = $"{Cte()} " +
            $"SELECT s.{file} AS {file}, " +
            $"SUM({EffectivePlayQueries.Decay("p")} * p.{EffectivePlayQueries.QualityColumn})::double precision AS {weight} " +
            $"FROM plays p JOIN {s} s ON s.{file} = p.{EffectivePlayQueries.FileColumn} " +
            $"AND {factory.UserIdPredicate("s")} " +
            $"WHERE s.{file} IS NOT NULL " +
            $"GROUP BY s.{file} " +
            $"ORDER BY {weight} DESC LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<TasteFile>(new CommandDefinition(sql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 100),
                HalfLifeDays = Math.Clamp(options.ArtistHalfLifeDays, 1, 3650),
                PlaybackSince = DateTime.UtcNow.AddDays(
                    -Math.Clamp(options.PlaybackWindowDays, 1, 3650)),
            }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetTopTagsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var s = factory.Quote("Songs");
        var genre = factory.Quote("Genre");
        var tag = factory.Quote("Tag");
        var sql = $"{Cte()} " +
            $"SELECT s.{genre} AS {tag} FROM plays p " +
            $"JOIN {s} s ON s.{factory.Quote("File")} = p.{EffectivePlayQueries.FileColumn} " +
            $"AND {factory.UserIdPredicate("s")} " +
            $"WHERE s.{genre} IS NOT NULL AND s.{genre} <> '' " +
            $"GROUP BY s.{genre} " +
            $"ORDER BY SUM({EffectivePlayQueries.Decay("p")} * p.{EffectivePlayQueries.QualityColumn}) DESC " +
            $"LIMIT @Limit";
        using var conn = factory.Create();
        var rows = (await conn.QueryAsync<string>(new CommandDefinition(sql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 50),
                HalfLifeDays = Math.Clamp(options.ArtistHalfLifeDays, 1, 3650),
                PlaybackSince = DateTime.UtcNow.AddDays(
                    -Math.Clamp(options.PlaybackWindowDays, 1, 3650)),
            }, cancellationToken: ct))).ToList();
        if (rows.Count > 0) return rows;

        // Cold start without history: use library genres so a brand-new user
        // still gets tag-based open-data picks, not only generic charts.
        var fallbackSql =
            $"SELECT {genre} AS {tag} FROM {s} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"AND {genre} IS NOT NULL AND {genre} <> '' " +
            $"GROUP BY {genre} " +
            $"ORDER BY COUNT(*) DESC LIMIT @Limit";
        var fallback = await conn.QueryAsync<string>(new CommandDefinition(fallbackSql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 50),
            }, cancellationToken: ct));
        return fallback.ToList();
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

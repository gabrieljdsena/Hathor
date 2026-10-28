using Dapper;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Hathor.Domain.Services;

namespace Hathor.Infrastructure.Library;

// Discover taste read-model (packages/contracts/discover.md rules 1-2):
// recency-weighted top artists/tracks/tags from Music_History joined to
// Songs, plus owned title+artist pairs and in-flight download pairs for
// dedupe. Local-only signals: no external profile data is involved.
public sealed class DapperDiscoverTasteReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory,
    DiscoveryOptions options) : IDiscoverTasteReadModel
{
    private static object P(Guid userId) =>
        new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) };

    // Half-life decay: 0.5 ^ (ageDays / halfLifeDays). Postgres evaluates it
    // in SQL so the read model never materializes the full history.
    private static string Decay(string historyAlias) =>
        "POWER(0.5::double precision, " +
        $"EXTRACT(EPOCH FROM (NOW() - {historyAlias}.\"DatePlayedUtc\"))::double precision " +
        "/ 86400.0 / @HalfLifeDays)";

    // Playback-quality multiplier per file: 1.0 with no recent telemetry
    // (desktop/mobile clients), 0.2..1.0 from completed/partial/skipped web
    // segments. It multiplies the recency decay of every history play.
    private string PlaybackQuality()
    {
        var explicitEvents =
            "CASE WHEN pe.\"Events\" > 0 THEN " +
            "(0.2::double precision + 0.8::double precision * " +
            "pe.\"Completions\"::double precision / pe.\"Events\") ";
        if (!options.InferPlaybackQuality)
            return explicitEvents + "ELSE 1.0::double precision END";

        return explicitEvents +
            "WHEN pi.\"Events\" > 0 THEN " +
            "(0.2::double precision + 0.8::double precision * " +
            "pi.\"Completions\"::double precision / pi.\"Events\") " +
            "ELSE 1.0::double precision END";
    }

    private string PlaybackQualityJoin()
    {
        var table = factory.Quote("Playback_Events");
        var file = factory.Quote("SongFile");
        var completed = factory.Quote("Completed");
        var started = factory.Quote("StartedAtUtc");
        var explicitJoin =
            $"LEFT JOIN (SELECT {file}, COUNT(*) AS \"Events\", " +
            $"SUM(CASE WHEN {completed} THEN 1 ELSE 0 END) AS \"Completions\" " +
            $"FROM {table} WHERE {factory.UserIdPredicate()} " +
            $"AND {started} >= @PlaybackSince GROUP BY {file}) pe " +
            $"ON pe.{file} = s.{factory.Quote("File")}";
        if (!options.InferPlaybackQuality) return explicitJoin;

        var history = factory.Quote("Music_History");
        var songs = factory.Quote("Songs");
        var playedAt = factory.Quote("DatePlayedUtc");
        var id = factory.Quote("Id");
        var duration = factory.Quote("DurationSecs");
        var inferredJoin =
            "LEFT JOIN (" +
            $"SELECT {file}, COUNT(*) AS \"Events\", " +
            $"SUM(CASE WHEN \"ElapsedSec\" >= {duration} * 0.9 THEN 1 ELSE 0 END) AS \"Completions\" " +
            "FROM (" +
            $"SELECT played.{file}, played.{duration}, " +
            $"EXTRACT(EPOCH FROM (played.\"NextPlayedUtc\" - played.{playedAt})) AS \"ElapsedSec\" " +
            "FROM (" +
            $"SELECT h2.{file}, h2.{playedAt}, s2.{duration}, " +
            $"LEAD(h2.{playedAt}) OVER (PARTITION BY h2.{factory.Quote("UserId")} " +
            $"ORDER BY h2.{playedAt}, h2.{id}) AS \"NextPlayedUtc\" " +
            $"FROM {history} h2 " +
            $"JOIN {songs} s2 ON s2.{factory.Quote("File")} = h2.{file} " +
            $"AND s2.{factory.Quote("UserId")} = h2.{factory.Quote("UserId")} " +
            $"WHERE {factory.UserIdPredicate("h2")} " +
            $"AND h2.{playedAt} >= @PlaybackSince" +
            ") played " +
            "WHERE played.\"NextPlayedUtc\" IS NOT NULL " +
            $"AND played.{duration} > 5" +
            ") timing " +
            "WHERE timing.\"ElapsedSec\" >= 0 " +
            $"AND timing.\"ElapsedSec\" <= timing.{duration} + 60 " +
            $"GROUP BY timing.{file}) pi " +
            $"ON pi.{file} = s.{factory.Quote("File")}";
        return explicitJoin + " " + inferredJoin;
    }

    public async Task<IReadOnlyList<TasteArtist>> GetTopArtistsAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var h = factory.Quote("Music_History");
        var s = factory.Quote("Songs");
        var sql = $"SELECT s.{factory.Quote("Artist")} AS {factory.Quote("Artist")}, " +
            $"COUNT(*) AS {factory.Quote("Plays")}, " +
            $"SUM({Decay("h")} * {PlaybackQuality()})::double precision AS {factory.Quote("Weight")} " +
            $"FROM {h} h " +
            $"JOIN {s} s ON s.{factory.Quote("File")} = h.{factory.Quote("SongFile")} " +
            $"AND s.{factory.Quote("UserId")} = h.{factory.Quote("UserId")} " +
            $"{PlaybackQualityJoin()} " +
            $"WHERE {factory.UserIdPredicate("h")} " +
            $"AND s.{factory.Quote("Artist")} IS NOT NULL " +
            $"AND s.{factory.Quote("Artist")} NOT IN ('', 'Unknown') " +
            $"GROUP BY s.{factory.Quote("Artist")} " +
            $"ORDER BY {factory.Quote("Weight")} DESC LIMIT @Limit";
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
        var h = factory.Quote("Music_History");
        var s = factory.Quote("Songs");
        var sql = $"SELECT s.{factory.Quote("Title")} AS {factory.Quote("Title")}, " +
            $"s.{factory.Quote("Artist")} AS {factory.Quote("Artist")}, " +
            $"COUNT(*) AS {factory.Quote("Plays")}, " +
            $"SUM({Decay("h")} * {PlaybackQuality()})::double precision AS {factory.Quote("Weight")} " +
            $"FROM {h} h " +
            $"JOIN {s} s ON s.{factory.Quote("File")} = h.{factory.Quote("SongFile")} " +
            $"AND s.{factory.Quote("UserId")} = h.{factory.Quote("UserId")} " +
            $"{PlaybackQualityJoin()} " +
            $"WHERE {factory.UserIdPredicate("h")} " +
            $"AND s.{factory.Quote("Title")} IS NOT NULL AND s.{factory.Quote("Title")} <> '' " +
            $"AND s.{factory.Quote("Artist")} IS NOT NULL " +
            $"AND s.{factory.Quote("Artist")} NOT IN ('', 'Unknown') " +
            $"GROUP BY s.{factory.Quote("Title")}, s.{factory.Quote("Artist")} " +
            $"ORDER BY {factory.Quote("Weight")} DESC LIMIT @Limit";
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
        var h = factory.Quote("Music_History");
        var s = factory.Quote("Songs");
        var sql = $"SELECT s.{factory.Quote("File")} AS {factory.Quote("File")}, " +
            $"SUM({Decay("h")} * {PlaybackQuality()})::double precision AS {factory.Quote("Weight")} " +
            $"FROM {h} h " +
            $"JOIN {s} s ON s.{factory.Quote("File")} = h.{factory.Quote("SongFile")} " +
            $"AND s.{factory.Quote("UserId")} = h.{factory.Quote("UserId")} " +
            $"{PlaybackQualityJoin()} " +
            $"WHERE {factory.UserIdPredicate("h")} " +
            $"AND s.{factory.Quote("File")} IS NOT NULL " +
            $"GROUP BY s.{factory.Quote("File")} " +
            $"ORDER BY {factory.Quote("Weight")} DESC LIMIT @Limit";
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
        var h = factory.Quote("Music_History");
        var s = factory.Quote("Songs");
        var sql = $"SELECT s.{factory.Quote("Genre")} AS {factory.Quote("Tag")} FROM {h} h " +
            $"JOIN {s} s ON s.{factory.Quote("File")} = h.{factory.Quote("SongFile")} " +
            $"AND s.{factory.Quote("UserId")} = h.{factory.Quote("UserId")} " +
            $"{PlaybackQualityJoin()} " +
            $"WHERE {factory.UserIdPredicate("h")} " +
            $"AND s.{factory.Quote("Genre")} IS NOT NULL AND s.{factory.Quote("Genre")} <> '' " +
            $"GROUP BY s.{factory.Quote("Genre")} " +
            $"ORDER BY SUM({Decay("h")} * {PlaybackQuality()}) DESC LIMIT @Limit";
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
            $"SELECT {factory.Quote("Genre")} AS {factory.Quote("Tag")} FROM {s} " +
            $"WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("Genre")} IS NOT NULL AND {factory.Quote("Genre")} <> '' " +
            $"GROUP BY {factory.Quote("Genre")} " +
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

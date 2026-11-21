using Dapper;
using Hathor.Application.AiDj;
using Hathor.Application.Ports;

namespace Hathor.Infrastructure.Library;

public sealed class DapperAiDjReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory) : IAiDjReadModel
{
    public async Task<IReadOnlyList<AiDjCandidateRow>> GetCandidatesAsync(
        Guid userId, int limit, CancellationToken ct = default)
    {
        var songs = factory.Quote("Songs");
        var links = factory.Quote("Song_Playlist");
        var file = factory.Quote("File");
        var title = factory.Quote("Title");
        var artist = factory.Quote("Artist");
        var album = factory.Quote("Album");
        var genre = factory.Quote("Genre");
        var year = factory.Quote("Year");
        var duration = factory.Quote("DurationSecs");
        var playCount = factory.Quote("PlayCount");
        var eventsLabel = factory.Quote("Events");
        var completionsLabel = factory.Quote("Completions");
        var lastPlayed = factory.Quote("LastPlayedUtc");
        var inPlaylists = factory.Quote("InPlaylists");

        var sql =
            $"{EffectivePlayQueries.Cte(factory, inferHistoryQuality: true)} " +
            $"SELECT s.{file} AS {file}, " +
            $"s.{title} AS {title}, " +
            $"s.{artist} AS {artist}, " +
            $"s.{album} AS {album}, " +
            $"s.{genre} AS {genre}, " +
            $"s.{year} AS {year}, " +
            $"s.{duration} AS {duration}, " +
            $"COALESCE(pa.\"Plays\", 0)::int AS {playCount}, " +
            $"COALESCE(eq.\"Events\", 0)::int AS {eventsLabel}, " +
            $"COALESCE(eq.\"Completions\", 0)::int AS {completionsLabel}, " +
            $"pa.\"LastPlayedUtc\" AS {lastPlayed}, " +
            $"EXISTS (SELECT 1 FROM {links} sp WHERE sp.{factory.Quote("UserId")} = s.{factory.Quote("UserId")} " +
            $"AND sp.{factory.Quote("SongFile")} = s.{file}) AS {inPlaylists} " +
            $"FROM {songs} s " +
            $"LEFT JOIN (SELECT p.{EffectivePlayQueries.FileColumn} AS {EffectivePlayQueries.FileColumn}, " +
            $"COUNT(*) AS \"Plays\", MAX(p.{EffectivePlayQueries.PlayedAtColumn}) AS \"LastPlayedUtc\" " +
            $"FROM plays p GROUP BY p.{EffectivePlayQueries.FileColumn}) pa " +
            $"ON pa.{EffectivePlayQueries.FileColumn} = s.{file} " +
            $"LEFT JOIN event_quality eq ON eq.{EffectivePlayQueries.FileColumn} = s.{file} " +
            $"WHERE {factory.UserIdPredicate("s")} " +
            $"ORDER BY COALESCE(pa.\"Plays\", 0) DESC, pa.\"LastPlayedUtc\" DESC NULLS LAST, s.{title} " +
            $"LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<AiDjCandidateRow>(new CommandDefinition(sql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 2000),
                PlaybackSince = DateTime.UtcNow.AddDays(-180),
            }, cancellationToken: ct));
        return rows.ToList();
    }
}

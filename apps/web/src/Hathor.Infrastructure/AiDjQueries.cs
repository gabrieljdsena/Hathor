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
        var history = factory.Quote("Music_History");
        var events = factory.Quote("Playback_Events");
        var links = factory.Quote("Song_Playlist");
        var sql =
            $"SELECT s.{factory.Quote("File")} AS {factory.Quote("File")}, " +
            $"s.{factory.Quote("Title")} AS {factory.Quote("Title")}, " +
            $"s.{factory.Quote("Artist")} AS {factory.Quote("Artist")}, " +
            $"s.{factory.Quote("Album")} AS {factory.Quote("Album")}, " +
            $"s.{factory.Quote("Genre")} AS {factory.Quote("Genre")}, " +
            $"s.{factory.Quote("Year")} AS {factory.Quote("Year")}, " +
            $"s.{factory.Quote("DurationSecs")} AS {factory.Quote("DurationSecs")}, " +
            $"COALESCE(h.\"Plays\", 0)::int AS {factory.Quote("PlayCount")}, " +
            $"COALESCE(pe.\"Events\", 0)::int AS {factory.Quote("Events")}, " +
            $"COALESCE(pe.\"Completions\", 0)::int AS {factory.Quote("Completions")}, " +
            $"h.\"LastPlayedUtc\" AS {factory.Quote("LastPlayedUtc")}, " +
            $"EXISTS (SELECT 1 FROM {links} sp WHERE sp.{factory.Quote("UserId")} = s.{factory.Quote("UserId")} " +
            $"AND sp.{factory.Quote("SongFile")} = s.{factory.Quote("File")}) AS {factory.Quote("InPlaylists")} " +
            $"FROM {songs} s " +
            $"LEFT JOIN (SELECT {factory.Quote("SongFile")} AS \"File\", COUNT(*)::int AS \"Plays\", " +
            $"MAX({factory.Quote("DatePlayedUtc")}) AS \"LastPlayedUtc\" FROM {history} " +
            $"WHERE {factory.UserIdPredicate()} GROUP BY {factory.Quote("SongFile")}) h " +
            $"ON h.\"File\" = s.{factory.Quote("File")} " +
            $"LEFT JOIN (SELECT {factory.Quote("SongFile")} AS \"File\", COUNT(*)::int AS \"Events\", " +
            $"SUM(CASE WHEN {factory.Quote("Completed")} THEN 1 ELSE 0 END)::int AS \"Completions\" " +
            $"FROM {events} WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("StartedAtUtc")} >= @Since GROUP BY {factory.Quote("SongFile")}) pe " +
            $"ON pe.\"File\" = s.{factory.Quote("File")} " +
            $"WHERE {factory.UserIdPredicate("s")} " +
            $"ORDER BY COALESCE(h.\"Plays\", 0) DESC, h.\"LastPlayedUtc\" DESC NULLS LAST, s.{factory.Quote("Title")} " +
            $"LIMIT @Limit";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<AiDjCandidateRow>(new CommandDefinition(sql,
            new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Limit = Math.Clamp(limit, 1, 2000),
                Since = DateTime.UtcNow.AddDays(-180),
            }, cancellationToken: ct));
        return rows.ToList();
    }
}

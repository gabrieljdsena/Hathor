using System.Text;
using Hathor.Infrastructure.Dapper;

namespace Hathor.Infrastructure.Library;

// Shared recommendation play stream. Playback_Events are the preferred
// signal for a play, while Music_History keeps legacy/offline plays useful.
//
// A history row is dropped only when an event for the same file lands within
// five minutes of it, so a client that has already upgraded does not count
// the same play twice. Older history plays survive the transition.
internal static class EffectivePlayQueries
{
    public const string FileColumn = "\"File\"";
    public const string PlayedAtColumn = "\"PlayedAtUtc\"";
    public const string QualityColumn = "\"Quality\"";

    // Half-life decay: 0.5 ^ (ageDays / halfLifeDays), evaluated in Postgres.
    public static string Decay(string alias) =>
        "POWER(0.5::double precision, " +
        $"EXTRACT(EPOCH FROM (NOW() - {alias}.{PlayedAtColumn}))::double precision " +
        "/ 86400.0 / @HalfLifeDays)";

    // CTE columns: File, PlayedAtUtc, Quality (0.2..1.0).
    public static string Cte(DapperConnectionFactory factory, bool inferHistoryQuality)
    {
        var history = factory.Quote("Music_History");
        var events = factory.Quote("Playback_Events");
        var songs = factory.Quote("Songs");
        var songFile = factory.Quote("SongFile");
        var playedAt = factory.Quote("DatePlayedUtc");
        var startedAt = factory.Quote("StartedAtUtc");
        var id = factory.Quote("Id");
        var duration = factory.Quote("DurationSecs");
        var completed = factory.Quote("Completed");
        var userId = factory.Quote("UserId");
        var file = factory.Quote("File");

        var historyQuality = inferHistoryQuality
            ? "COALESCE(0.2::double precision + 0.8::double precision * hq.\"CompletionRate\", 1.0::double precision)"
            : "1.0::double precision";

        var sql = new StringBuilder();
        sql.Append("WITH event_quality AS (");
        sql.Append($"SELECT {songFile} AS {FileColumn}, COUNT(*) AS \"Events\", ");
        sql.Append($"SUM(CASE WHEN {completed} THEN 1 ELSE 0 END) AS \"Completions\" ");
        sql.Append($"FROM {events} WHERE {factory.UserIdPredicate()} ");
        sql.Append($"AND {startedAt} >= @PlaybackSince GROUP BY {songFile}), ");

        sql.Append("history_quality AS (");
        sql.Append($"SELECT timing.{songFile} AS {FileColumn}, ");
        sql.Append("SUM(CASE WHEN timing.\"ElapsedSec\" >= timing.");
        sql.Append($"{duration} * 0.9 THEN 1 ELSE 0 END)::double precision ");
        sql.Append("/ NULLIF(COUNT(*), 0)::double precision AS \"CompletionRate\" ");
        sql.Append("FROM (");
        sql.Append($"SELECT played.{songFile}, played.{duration}, ");
        sql.Append("EXTRACT(EPOCH FROM (played.\"NextPlayedUtc\" - played.");
        sql.Append($"{playedAt})) AS \"ElapsedSec\" ");
        sql.Append("FROM (");
        sql.Append($"SELECT h2.{songFile}, h2.{playedAt}, s2.{duration}, ");
        sql.Append($"LEAD(h2.{playedAt}) OVER (PARTITION BY h2.{userId} ");
        sql.Append($"ORDER BY h2.{playedAt}, h2.{id}) AS \"NextPlayedUtc\" ");
        sql.Append($"FROM {history} h2 JOIN {songs} s2 ON s2.{file} = h2.{songFile} ");
        sql.Append($"AND s2.{userId} = h2.{userId} ");
        sql.Append($"WHERE {factory.UserIdPredicate("h2")} ");
        sql.Append($"AND h2.{playedAt} >= @PlaybackSince");
        sql.Append(") played WHERE played.\"NextPlayedUtc\" IS NOT NULL ");
        sql.Append($"AND played.{duration} > 5");
        sql.Append(") timing WHERE timing.\"ElapsedSec\" >= 0 ");
        sql.Append($"AND timing.\"ElapsedSec\" <= timing.{duration} + 60 ");
        sql.Append($"GROUP BY timing.{songFile}), ");

        sql.Append("plays AS (");
        sql.Append($"SELECT e.{songFile} AS {FileColumn}, e.{startedAt} AS {PlayedAtColumn}, ");
        sql.Append("COALESCE(0.2::double precision + 0.8::double precision * ");
        sql.Append("eq.\"Completions\"::double precision / ");
        sql.Append("NULLIF(eq.\"Events\", 0)::double precision, 1.0::double precision) AS ");
        sql.Append($"{QualityColumn} FROM {events} e ");
        sql.Append($"LEFT JOIN event_quality eq ON eq.{FileColumn} = e.{songFile} ");
        sql.Append($"WHERE {factory.UserIdPredicate("e")} ");
        sql.Append("UNION ALL ");
        sql.Append($"SELECT h.{songFile}, h.{playedAt}, {historyQuality} ");
        sql.Append($"FROM {history} h LEFT JOIN history_quality hq ");
        sql.Append($"ON hq.{FileColumn} = h.{songFile} ");
        sql.Append($"WHERE {factory.UserIdPredicate("h")} ");
        sql.Append($"AND NOT EXISTS (SELECT 1 FROM {events} e2 ");
        sql.Append($"WHERE {factory.UserIdPredicate("e2")} ");
        sql.Append($"AND e2.{songFile} = h.{songFile} ");
        sql.Append($"AND e2.{startedAt} BETWEEN h.{playedAt} - INTERVAL '5 minutes' ");
        sql.Append($"AND h.{playedAt} + INTERVAL '5 minutes'))");
        return sql.ToString();
    }
}

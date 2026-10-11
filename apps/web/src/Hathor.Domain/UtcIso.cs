using System.Globalization;

namespace Hathor.Domain;

// Wire instants: every timestamp the server emits for display or polling
// is a UTC ISO-8601 string with an explicit Z. All writes use UtcNow, but
// reads can surface Unspecified Kind (Dapper over timestamp columns,
// desktop-imported rows) — formatting those raw would print a local wall
// time mislabeled as UTC and shift every displayed date by the server
// offset. Normalize first: Unspecified is assumed UTC (matches write
// semantics), Local is converted.
public static class UtcIso
{
    public static DateTime EnsureUtc(DateTime dt) =>
        dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };

    public static string Format(DateTime dt) =>
        EnsureUtc(dt).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

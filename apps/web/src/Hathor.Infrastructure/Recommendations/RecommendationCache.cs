using System.Text.Json;
using Hathor.Application.Ports;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Infrastructure.Recommendations;

// Postgres-backed cache for public recommendation data (MusicBrainz /
// ListenBrainz). Upsert is a single SQL statement so concurrent refreshes
// cannot race each other into duplicate-key errors.
public sealed class EfRecommendationCache(HathorDbContext db) : IRecommendationCache
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        where T : class
    {
        var row = await db.RecommendationCache.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == key, ct);
        if (row is null) return null;

        if (row.ExpiresAtUtc <= DateTime.UtcNow)
        {
            await db.RecommendationCache.Where(x => x.Key == key)
                .ExecuteDeleteAsync(ct);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(row.PayloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl,
        CancellationToken ct = default) where T : class
    {
        var payload = JsonSerializer.Serialize(value);
        var now = DateTime.UtcNow;
        var expires = now.Add(ttl);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Rec_HttpCache" ("Key", "PayloadJson", "ExpiresAtUtc", "CreatedAtUtc")
            VALUES ({key}, {payload}, {expires}, {now})
            ON CONFLICT ("Key") DO UPDATE SET
                "PayloadJson" = EXCLUDED."PayloadJson",
                "ExpiresAtUtc" = EXCLUDED."ExpiresAtUtc"
            """, ct);
    }
}

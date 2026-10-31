using System.Text.Json;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Infrastructure.Repositories;

// Cross-device resume spots: one row per (user, device), last write wins.
// Readers exclude their own device id so a browser never echoes its own
// paused track back as a resume pill.
public sealed class EfPlaybackSpotRepository(HathorDbContext db) : IPlaybackSpotRepository
{
    public async Task UpsertAsync(PlaybackSpot spot, CancellationToken ct = default)
    {
        var row = await db.PlaybackSpots.FindAsync([spot.UserId, spot.DeviceId], ct);
        if (row is null)
        {
            row = new PlaybackSpotRow { UserId = spot.UserId, DeviceId = spot.DeviceId };
            await db.PlaybackSpots.AddAsync(row, ct);
        }
        row.File = spot.File;
        row.PositionSec = spot.PositionSec;
        row.IsPodcast = spot.IsPodcast;
        row.DeviceLabel = spot.DeviceLabel;
        row.UpdatedUtc = spot.UpdatedUtc;
        row.QueueFilesJson = JsonSerializer.Serialize(spot.QueueFiles ?? []);
        row.QueueSourceJson = spot.QueueSourceJson;
    }

    public async Task<PlaybackSpot?> LatestForeignAsync(
        Guid userId, string? excludeDeviceId, DateTime minUpdatedUtc,
        CancellationToken ct = default)
    {
        var query = db.PlaybackSpots
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.UpdatedUtc >= minUpdatedUtc);
        if (!string.IsNullOrWhiteSpace(excludeDeviceId))
            query = query.Where(x => x.DeviceId != excludeDeviceId);
        var row = await query
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new PlaybackSpot
            {
                UserId = row.UserId,
                DeviceId = row.DeviceId,
                File = row.File,
                PositionSec = row.PositionSec,
                IsPodcast = row.IsPodcast,
                DeviceLabel = row.DeviceLabel,
                UpdatedUtc = row.UpdatedUtc,
                QueueFiles = ParseFiles(row.QueueFilesJson),
                QueueSourceJson = row.QueueSourceJson,
            };
    }

    private static List<string> ParseFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}

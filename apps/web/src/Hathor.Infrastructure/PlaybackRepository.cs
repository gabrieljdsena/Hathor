using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Infrastructure.Repositories;

// The PlaybackState aggregate is tracked via its PlaybackStateRow: mutations
// are copied row-ward on SaveChanges (desktop persists queue JSON on every op).
public sealed class EfPlaybackStateRepository(HathorDbContext db) : IPlaybackStateRepository
{
    public async Task<PlaybackState> GetOrCreateAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await db.PlaybackStates.FindAsync([userId], ct);
        if (row is null)
        {
            row = new PlaybackStateRow { UserId = userId, LastPlayUtc = DateTime.UtcNow };
            await db.PlaybackStates.AddAsync(row, ct);
            await db.SaveChangesAsync(ct);
        }
        var state = new TrackedPlaybackState(row);
        MapFromRow(state, row);
        return state;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in db.ChangeTracker.Entries<PlaybackStateRow>())
        {
            // no-op: rows are updated below from their tracked aggregates
            _ = entry.Entity.UserId;
        }
        foreach (var state in _tracked)
        {
            MapToRow(state, state.Row);
        }
        _tracked.Clear();
        await db.SaveChangesAsync(ct);
    }

    private readonly List<TrackedPlaybackState> _tracked = new();

    private sealed class TrackedPlaybackState(PlaybackStateRow row) : PlaybackState
    {
        public PlaybackStateRow Row { get; } = row;
    }

    private void MapFromRow(PlaybackState state, PlaybackStateRow row)
    {
        state.UserId = row.UserId;
        state.CurrentFile = row.CurrentFile;
        state.CurrentIsPodcast = row.CurrentIsPodcast;
        state.CurrentPlaylistId = row.CurrentPlaylistId;
        state.IsPlaying = row.IsPlaying;
        state.FirstPlay = row.FirstPlay;
        state.Shuffle = row.Shuffle;
        state.Repeat = row.Repeat;
        state.Volume = row.Volume;
        state.PreviousVolume = row.PreviousVolume;
        state.NextFiles = FromJson(row.NextFilesJson);
        state.PrevFiles = FromJson(row.PrevFilesJson);
        state.UnshuffledFiles = FromJson(row.UnshuffledFilesJson);
        state.Source = QueueSource.Parse(row.QueueSourceJson);
        state.IsCustomQueue = row.IsCustomQueue;
        state.FallbackToGeneralList = row.FallbackToGeneralList;
        state.PositionOffsetSec = row.PositionOffsetSec;
        state.PausePositionSec = row.PausePositionSec;
        state.LastPlayUtc = row.LastPlayUtc;
        if (state is TrackedPlaybackState tracked) _tracked.Add(tracked);
    }

    private static void MapToRow(PlaybackState state, PlaybackStateRow row)
    {
        row.CurrentFile = state.CurrentFile;
        row.CurrentIsPodcast = state.CurrentIsPodcast;
        row.CurrentPlaylistId = state.CurrentPlaylistId;
        row.IsPlaying = state.IsPlaying;
        row.FirstPlay = state.FirstPlay;
        row.Shuffle = state.Shuffle;
        row.Repeat = state.Repeat;
        row.Volume = state.Volume;
        row.PreviousVolume = state.PreviousVolume;
        row.NextFilesJson = ToJson(state.NextFiles);
        row.PrevFilesJson = ToJson(state.PrevFiles);
        row.UnshuffledFilesJson = ToJson(state.UnshuffledFiles);
        row.QueueSourceJson = state.Source?.ToJson();
        row.IsCustomQueue = state.IsCustomQueue;
        row.FallbackToGeneralList = state.FallbackToGeneralList;
        row.PositionOffsetSec = state.PositionOffsetSec;
        row.PausePositionSec = state.PausePositionSec;
        row.LastPlayUtc = state.LastPlayUtc;
    }

    private static List<string> FromJson(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
        }
        catch (System.Text.Json.JsonException)
        {
            return new();
        }
    }

    private static string ToJson(List<string> list) =>
        System.Text.Json.JsonSerializer.Serialize(list);
}

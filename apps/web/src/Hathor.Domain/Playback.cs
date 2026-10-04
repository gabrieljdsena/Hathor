using System.Text.Json;

namespace Hathor.Domain.Playback;

// Queue source types — mirrors PlaybackController.VALID_SOURCE_TYPES.
// Anything else falls back to the general list.
public static class QueueSourceTypes
{
    public const string Playlist = "playlist";
    public const string DailyMix = "daily_mix";
    public const string AllSongs = "all_songs";
    public const string Artist = "artist";
    public const string Album = "album";
    public const string RecentlyPlayed = "recently_played";
    public const string RecentlyDownloaded = "recently_downloaded";
    public const string Podcast = "podcast";

    public static readonly IReadOnlySet<string> Valid = new HashSet<string>(StringComparer.Ordinal)
    {
        Playlist, DailyMix, AllSongs, Artist, Album,
        RecentlyPlayed, RecentlyDownloaded, Podcast,
    };

    public static bool IsValid(string? type) => type is not null && Valid.Contains(type);
}

public sealed record QueueSource(string Type, string? Id)
{
    public static QueueSource? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<QueueSource>(json);
            if (parsed is null || !QueueSourceTypes.IsValid(parsed.Type)) return null;
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

// Per-user runtime playback state. Queue is persisted as filename lists
// (desktop Settings.queue_songs JSON + custom_queue flag), full metadata
// is resolved at read time from the library.
// Non-sealed so Infrastructure can track it against its persistence row.
public class PlaybackState
{
    public Guid UserId { get; set; }
    public string? CurrentFile { get; set; }
    public bool CurrentIsPodcast { get; set; }
    public long? CurrentPlaylistId { get; set; }
    public bool IsPlaying { get; set; }
    public bool FirstPlay { get; set; } = true; // opening/paused preload on startup
    public bool Shuffle { get; set; }
    public bool Repeat { get; set; }
    public double Volume { get; set; } = 0.7;
    public double PreviousVolume { get; set; } = 1.0; // mute restore (index.html:_previousVolume)
    public bool Muted => Volume <= 0;

    public List<string> NextFiles { get; set; } = new();
    public List<string> PrevFiles { get; set; } = new();
    public List<string> UnshuffledFiles { get; set; } = new();
    public QueueSource? Source { get; set; }
    public bool IsCustomQueue { get; set; }
    public bool FallbackToGeneralList { get; set; } = true;

    public double PositionOffsetSec { get; set; }
    public double PausePositionSec { get; set; }
    public DateTime LastPlayUtc { get; set; }

    // Server-estimated position so headless remotes stay in sync without audio.
    public double EstimatedPositionSec(DateTime utcNow)
    {
        if (FirstPlay) return 0;
        if (!IsPlaying) return PausePositionSec;
        return Math.Max(0, PositionOffsetSec + (utcNow - LastPlayUtc).TotalSeconds);
    }

    public void SetQueue(IEnumerable<string> next, IEnumerable<string> prev,
        QueueSource? source, bool isCustom, bool fallback,
        long? playlistId = null, Random? rng = null)
    {
        NextFiles = next.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        PrevFiles = prev.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        Source = source;
        IsCustomQueue = isCustom;
        FallbackToGeneralList = fallback;
        if (playlistId.HasValue) CurrentPlaylistId = playlistId;
        // A fresh context replaces the queue: stale pre-shuffle snapshots
        // from a previous context must go, or toggling shuffle off later
        // would restore the wrong (e.g. all-songs) order. When shuffle mode
        // is on, the new context is snapshotted and shuffled immediately so
        // artist/album/etc. views actually play shuffled.
        if (Shuffle)
        {
            UnshuffledFiles = new List<string>(NextFiles);
            if (rng is not null) ShuffleInPlace(NextFiles, rng);
        }
        else
        {
            UnshuffledFiles.Clear();
        }
    }

    // Fisher-Yates shuffle (desktop toggle_shuffle on path).
    public static void ShuffleInPlace(List<string> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public void AppendToQueue(string file)
    {
        NextFiles.Add(file);
        IsCustomQueue = true;
    }

    public void PlayNext(string file)
    {
        NextFiles.Insert(0, file);
        IsCustomQueue = true;
    }

    // Desktop jump_to_queue_index: next = next[index+1:].
    public void JumpToIndex(int index)
    {
        if (index < 0 || index >= NextFiles.Count) return;
        NextFiles = NextFiles.Skip(index + 1).ToList();
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= NextFiles.Count) return;
        NextFiles.RemoveAt(index);
        IsCustomQueue = true;
    }

    public void Reorder(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= NextFiles.Count) return;
        if (newIndex < 0 || newIndex > NextFiles.Count) return;
        var item = NextFiles[oldIndex];
        NextFiles.RemoveAt(oldIndex);
        NextFiles.Insert(Math.Min(newIndex, NextFiles.Count), item);
        IsCustomQueue = true;
    }

    public void ClearQueue()
    {
        NextFiles.Clear();
        UnshuffledFiles.Clear();
        FallbackToGeneralList = false;
        IsCustomQueue = false;
    }

    // Natural advance: shift next head into current, push old current to prev.
    // Returns the new current file, or null when the queue is exhausted.
    public string? Advance()
    {
        if (NextFiles.Count == 0) return null;
        if (!string.IsNullOrEmpty(CurrentFile)) PrevFiles.Add(CurrentFile);
        var next = NextFiles[0];
        NextFiles.RemoveAt(0);
        CurrentFile = next;
        // A spent queue has no order left to restore on toggle-off.
        if (NextFiles.Count == 0) UnshuffledFiles.Clear();
        return next;
    }

    public string? Rewind()
    {
        if (PrevFiles.Count == 0) return null;
        if (!string.IsNullOrEmpty(CurrentFile)) NextFiles.Insert(0, CurrentFile);
        var prev = PrevFiles[^1];
        PrevFiles.RemoveAt(PrevFiles.Count - 1);
        CurrentFile = prev;
        return prev;
    }

    public void StartPlaying(string file, bool isPodcast, DateTime utcNow)
    {
        CurrentFile = file;
        CurrentIsPodcast = isPodcast;
        FirstPlay = false;
        IsPlaying = true;
        PositionOffsetSec = 0;
        PausePositionSec = 0;
        LastPlayUtc = utcNow;
    }

    public void Pause(DateTime utcNow)
    {
        PausePositionSec = EstimatedPositionSec(utcNow);
        IsPlaying = false;
    }

    public void Resume(DateTime utcNow)
    {
        LastPlayUtc = utcNow;
        PositionOffsetSec = PausePositionSec;
        IsPlaying = true;
    }

    public void Seek(double sec, DateTime utcNow)
    {
        var clamped = Math.Max(0, sec);
        PositionOffsetSec = clamped;
        PausePositionSec = clamped;
        LastPlayUtc = utcNow;
    }

    public void SetVolume(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
    }

    public void Mute()
    {
        if (Volume > 0) PreviousVolume = Volume;
        Volume = 0;
    }

    public void Unmute()
    {
        Volume = PreviousVolume > 0 ? PreviousVolume : 1.0;
    }
}

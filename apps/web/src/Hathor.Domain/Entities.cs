namespace Hathor.Domain.Entities;

// Mirrors desktop database.sql tables + lowercase remote schema (sync.py).
// All user-scoped tables carry UserId (multi-user delta vs desktop SQLite).

public sealed class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool Revoked { get; set; }
}

// Server-side login session (multi-login: one row per device). Rotation
// chains via ReplacedById so presenting an already-rotated token (theft
// replay) is detectable — the whole family is revoked on reuse.
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string RefreshTokenHash { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? DeviceLabel { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastUsedAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? ReplacedById { get; set; }
}

// Personal access token for headless clients (plan §4: hth_ prefix, scopes).
public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = ""; // first 8 chars for lookup display
    public string TokenHash { get; set; } = "";
    public string Scopes { get; set; } = ""; // space-separated: player:read player:control library:read
    public DateTime CreatedAtUtc { get; set; }
    public bool Revoked { get; set; }
}

public sealed class Song
{
    public Guid UserId { get; set; }
    public string File { get; set; } = ""; // PK with UserId; filename in songs folder
    public string? DownloadedLink { get; set; }
    public string Title { get; set; } = "";
    public DateTime DateDownloadUtc { get; set; }
    public string? Artist { get; set; }
    // Materialized file tags (indexed search/sort without opening MP3s).
    // File tags stay the source of truth; these mirror them (see Scan).
    public string? Album { get; set; }
    public string? Year { get; set; }
    public string? Genre { get; set; }
    public double DurationSecs { get; set; }
}

public sealed class Podcast
{
    public Guid UserId { get; set; }
    public string File { get; set; } = "";
    public string? DownloadedLink { get; set; }
    public string Title { get; set; } = "";
    public DateTime DateDownloadUtc { get; set; }
    public string? Artist { get; set; }
}

public sealed class Playlist
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Thumbnail { get; set; } // TEXT base64 (sync.py normalizes BLOB→text)
}

public sealed class SongPlaylist
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string SongFile { get; set; } = "";
    public long PlaylistId { get; set; }
    public DateTime DateAddedUtc { get; set; }
}

public sealed class PodcastTag
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class PodcastTagLink
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string PodcastFile { get; set; } = "";
    public long TagId { get; set; }
}

public sealed class DownloadJob
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string Qid { get; set; } = ""; // unique
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string Status { get; set; } = "queued";
    public double Progress { get; set; }
    public string? Error { get; set; }
    public string? Filename { get; set; }
    public bool IsPodcast { get; set; }
    // Exact disk filename this job must produce (pull: the remote `file`
    // so the merged row resolves). Null = derive from the title as usual.
    public string? TargetFile { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class Lyric
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string SongFile { get; set; } = "";
    public string? LyricsJson { get; set; } // {synced, plain}
    // Per-song highlight timing correction, milliseconds (-10000..10000).
    // Zero = none (no row needed, but harmless when present).
    public int OffsetMs { get; set; }
}

public sealed class MusicHistoryEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string SongFile { get; set; } = "";
    public DateTime DatePlayedUtc { get; set; }
}

public sealed class PlaylistHistoryEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public long PlaylistId { get; set; }
    public DateTime DatePlayedUtc { get; set; }
}

public sealed class SyncDeletion
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public string TableName { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTime DeletedAtUtc { get; set; }
}

public sealed class DailyMix
{
    public Guid UserId { get; set; }
    public string MixDate { get; set; } = ""; // YYYY-MM-DD, PK with UserId
    public string SongFilesJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
}

// Discover cache (packages/contracts/discover.md): one row per user per day,
// items as JSON (out-of-library candidates expire fast — no tombstones).
public sealed class DiscoverCache
{
    public Guid UserId { get; set; }
    public string Date { get; set; } = ""; // YYYY-MM-DD, PK with UserId
    public string ItemsJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; }
}

// Persisted prefs (desktop Settings row id=1 → per-user).
public sealed class UserSettings
{
    public Guid UserId { get; set; }
    public double Volume { get; set; } = 0.7;
    public int LimitDownloads { get; set; } = 3; // clamp 1..20
    public string? BackgroundPath { get; set; }
    public bool CrossfadeEnabled { get; set; }
    public double CrossfadeSeconds { get; set; } = 5; // 1..12
    public string? LastRoute { get; set; } // ex-current_tab
    public string? Browser { get; set; } // yt-dlp --cookies-from-browser
}

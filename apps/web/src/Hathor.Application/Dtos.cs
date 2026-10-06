using Hathor.Domain.Playback;

namespace Hathor.Application.Dtos;

// Mirrors desktop get_song_metadata dict (File/Artist/Title/Album/Year/Genre/Duration/CoverArt).
// Genre is written by downloads (TCON) and preserved by metadata edits (plan G1).
public sealed record SongDto(
    string File,
    string Artist,
    string Title,
    string Album,
    string Year,
    double Duration,
    string? CoverArt,
    string? DateDownload,
    bool IsPodcast = false,
    string Genre = "Unknown",
    double? LoudnessDb = null);

public sealed record QueueSourceDto(string Type, string? Id);

public sealed record PlayerStateDto(
    SongDto? CurrentSong,
    bool IsPlaying,
    double PositionSec,
    double Volume,
    bool Shuffle,
    bool Repeat,
    IReadOnlyList<SongDto> Queue,
    QueueSourceDto? Source,
    bool IsCustomQueue,
    bool FirstPlay,
    int QueueTotal = 0);

public sealed record NowPlayingDto(
    string? Title,
    string? Artist,
    string? CoverUrl,
    double PositionSec,
    double? DurationSec);

public sealed record AuthTokensDto(string AccessToken, string RefreshToken, string Username);

public sealed record ApiKeyDto(Guid Id, string Name, string Prefix, string Scopes, DateTime CreatedAtUtc);

public sealed record ApiInfoDto(string App, string Version, DateTime ServerTimeUtc, string[] Features);

// Per-user settings (desktop Settings row: volume, limits, background,
// crossfade prefs, last route, yt-dlp browser cookies).
public sealed record UserSettingsDto(
    double Volume,
    int LimitDownloads,
    string? BackgroundPath,
    bool CrossfadeEnabled,
    double CrossfadeSeconds,
    string? LastRoute,
    string? Browser,
    bool ChapterSkip);

// Player-scoped prefs (crossfade lives server-side for all clients).
public sealed record PlayerSettingsDto(bool CrossfadeEnabled, double CrossfadeSeconds);

// System health (desktop startup_maintenance status surface).
public sealed record FFmpegStatusDto(bool Found, string? Exe, string? Dir, string? Error);
public sealed record LibraryStatusDto(string Package, string? Current, string? Latest, string Status);
// FFmpeg self-install progress (Settings → Download FFmpeg).
// State: idle|downloading|extracting|ready|failed. Progress 0..1 (-1 unknown).
public sealed record FfmpegDownloadDto(string State, double Progress, string? Exe, string? Error);
public sealed record MaintenanceResultDto(FFmpegStatusDto FFmpeg, IReadOnlyList<LibraryStatusDto> Libraries, bool AllOk);

public sealed record PlaylistDto(long Id, string Title, string? Description, string? Thumbnail);

public sealed record PlaylistWithCountDto(
    long Id, string Title, string? Description, string? Thumbnail, int SongCount);

// Podcast tags mirror playlists (plan §1.4): unique names + live episode counts.
public sealed record PodcastTagDto(long Id, string Name, int EpisodeCount);

// Podcast chapter marks ("timestamps"): media offsets in seconds with a
// display name; EndSecs null means the chapter runs open-ended.
public sealed record PodcastTimestampDto(
    long Id,
    string PodcastFile,
    string Name,
    double StartSecs,
    double? EndSecs);

// History rows: song + play/download date + re-download source link (G17).
public sealed record HistoryItemDto(
    SongDto Song,
    string? DatePlayed,
    string? DateDownload,
    string? DownloadedLink);

public sealed record PlayedPlaylistItemDto(
    PlaylistDto Playlist,
    string? DatePlayed);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalPages,
    int CurrentPage);

public sealed record DailyMixDto(
    string Date,
    IReadOnlyList<SongDto> Songs,
    bool Cached);

// Discover items (packages/contracts/discover.md): out-of-library
// recommendations. Source is artist|chart|llm, score is 0..1 descending.
public sealed record DiscoverItemDto(
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string ArtworkUrl,
    string Source,
    double Score);

public sealed record DiscoverDto(
    string Date,
    IReadOnlyList<DiscoverItemDto> Items,
    bool Cached);

// YouTube search hits (desktop search_yt: id/title/uploader/duration/thumbnail).
public sealed record VideoHitDto(
    string Id,
    string Title,
    string Uploader,
    double DurationSec,
    string Thumbnail);

// Download jobs (desktop Download_Queue log + QueueRepository.JobItem).
public sealed record DownloadJobDto(
    string Qid,
    string? Url,
    string Title,
    string? Artist,
    string Status,
    double Progress,
    string? Error,
    string? Filename,
    bool IsPodcast);

// iTunes candidates (desktop search_itunes_multi).
public sealed record ITunesHitDto(
    string Title,
    string Artist,
    string Album,
    string Year,
    string Genre,
    string ArtworkUrl);

// Download ownership pre-check (already-in-library confirm).
public sealed record OwnedCheckDto(bool Owned, string? File);

// Loudness backfill progress (resumable: repeat until remaining hits 0).
public sealed record LoudnessBackfillDto(int Scanned, int Remaining);

// lrclib candidates (desktop search_lyrics suggestions, max 10 deduped).
public sealed record LyricsHitDto(
    long Id,
    string TrackName,
    string ArtistName,
    string? AlbumName,
    double? Duration,
    string? SyncedLyrics,
    string? PlainLyrics);

// Lyrics payload (desktop {synced, plain}).
public sealed record LyricsDto(string? Synced, string? Plain);

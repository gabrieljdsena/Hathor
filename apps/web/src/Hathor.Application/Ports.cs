using Hathor.Application.Dtos;

namespace Hathor.Application.Ports;

// Read-model port (Dapper in Infrastructure). All hot list queries go here.
public interface ISongReadModel
{
    Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, string? search, string? sort, string? dir,
        int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(Guid userId, string? search, CancellationToken ct = default);
    Task<SongDto?> GetByFileAsync(Guid userId, string file, bool includeCover, CancellationToken ct = default);
    Task<IReadOnlyList<SongDto>> GetManyAsync(Guid userId, IEnumerable<string> files,
        bool includeCover, CancellationToken ct = default);
    // Raw file metadata without DB (library scans).
    SongDto ReadLocalSong(Guid userId, string file);
    // Exact-metadata matches, desktop-sorted (artist view: Album+Title, album view: Artist+Title).
    Task<IReadOnlyList<string>> GetArtistsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<SongDto>> GetSongsByArtistAsync(Guid userId, string artist, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAlbumsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<SongDto>> GetSongsByAlbumAsync(Guid userId, string album, CancellationToken ct = default);
}

// Playlist read-model port (Dapper): lists + song counts for grids/submenus.
public interface IPlaylistReadModel
{
    Task<IReadOnlyList<PlaylistDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistWithCountDto>> ListWithCountsAsync(Guid userId, CancellationToken ct = default);
    Task<PlaylistDto?> GetAsync(Guid userId, long id, CancellationToken ct = default);
}

// Podcast-tag read-model port (Dapper): tags with live episode counts + file→ids map.
public interface IPodcastTagReadModel
{
    Task<IReadOnlyList<PodcastTagDto>> ListWithCountsAsync(Guid userId, CancellationToken ct = default);
    Task<Dictionary<string, List<long>>> GetTagMapAsync(Guid userId, CancellationToken ct = default);
}

// History read-model port (Dapper): raw rows; handlers resolve SongDto
// and drop files missing from disk (desktop files-must-exist filter).
public interface IHistoryReadModel
{
    Task<(IReadOnlyList<(string File, DateTime DateDownload, string? Link)> Items, int Total)>
        GetDownloadPageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
    Task<(IReadOnlyList<(string File, DateTime DatePlayed)> Items, int Total)>
        GetPlayedPageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
    Task<(IReadOnlyList<(long PlaylistId, DateTime DatePlayed)> Items, int Total)>
        GetPlayedPlaylistPageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRecentPlayedFilesAsync(Guid userId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRecentDownloadedFilesAsync(Guid userId, int limit, CancellationToken ct = default);
}
public interface ILibraryStorage
{
    string SongsDir(Guid userId);
    string PodcastsDir(Guid userId);
    IReadOnlyList<string> ListSongFiles(Guid userId);
    string SongPath(Guid userId, string file);
    bool SongExists(Guid userId, string file);
    IReadOnlyList<string> ListPodcastFiles(Guid userId);
    string PodcastPath(Guid userId, string file);
    bool PodcastExists(Guid userId, string file);
    // Custom background image (desktop background_path): single file per user.
    string? FindBackground(Guid userId);
    Task<string> SaveBackgroundAsync(Guid userId, byte[] bytes, string contentType,
        CancellationToken ct = default);
    Task DeleteBackgroundAsync(Guid userId, CancellationToken ct = default);
    string BackgroundPath(Guid userId, string file);
}

// Podcast read-model port: lightweight list (DB titles + disk files,
// no durations/covers until details) + full per-episode details.
public interface IPodcastReadModel
{
    Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<SongDto?> GetByFileAsync(Guid userId, string file, CancellationToken ct = default);
    // Raw file metadata without DB (library scans).
    SongDto ReadLocalEpisode(Guid userId, string file);
}

// Remote-library pull port (desktop DatabaseManager.
// sync_remote_to_local_and_download, split per library): read rows from the
// desktop TiDB remote, merge them into this user's tables, enqueue
// downloads for files missing on disk.
public interface IRemotePullService
{
    Task<Application.Sync.RemotePullResult> PullSongsAsync(Guid userId,
        CancellationToken ct = default);
    Task<Application.Sync.RemotePullResult> PullPodcastsAsync(Guid userId,
        CancellationToken ct = default);
}

// Remote-library push port (desktop DatabaseSync.sync_once/_run_sync,
// split per library): write this user's rows into the desktop TiDB remote
// (single global namespace — last writer wins, like desktop multi-device).
public interface IRemotePushService
{
    Task<Application.Sync.RemotePushResult> PushSongsAsync(Guid userId,
        CancellationToken ct = default);
    Task<Application.Sync.RemotePushResult> PushPodcastsAsync(Guid userId,
        CancellationToken ct = default);
}

// Snapshot exchange port (desktop sync.py pull/push semantics).
public interface ISyncService
{
    Task<Application.Sync.SyncSnapshot> ExportAsync(Guid userId, long sinceId,
        CancellationToken ct = default);
    Task<Application.Sync.SyncSummary> ImportAsync(Guid userId,
        Application.Sync.SyncSnapshot snapshot, CancellationToken ct = default);
}

// Startup-maintenance probe port (desktop startup_maintenance status
// surface): FFmpeg presence + managed-library versions. No auto-installs
// server-side — Docker bakes FFmpeg in; the endpoint only reports.
public interface ISystemProbe
{
    FFmpegStatusDto GetFFmpegStatus();
    IReadOnlyList<LibraryStatusDto> GetLibraryStatus();
    MaintenanceResultDto RunChecks();
    // Live NuGet check for YoutubeExplode/TagLibSharp (latest stable +
    // up-to-date/update-available/unknown). Libraries are compiled in, so
    // an update means "update the package and redeploy".
    Task<IReadOnlyList<LibraryStatusDto>> CheckLibraryUpdatesAsync(CancellationToken ct = default);
}

// TagLib-backed metadata write port (title/artist/album/year/genre/cover).
public interface IMetadataWriter
{
    Task WriteSongAsync(Guid userId, string file,
        string? title, string? artist, string? album, string? year, string? genre,
        string? coverArt, // data: URL, http(s) URL, "REMOVE", or null = keep
        CancellationToken ct = default);
    // Same writes against an arbitrary file path (download temp files).
    Task WritePathAsync(string path,
        string? title, string? artist, string? album, string? year, string? genre,
        string? coverArt,
        CancellationToken ct = default);
}

// Realtime broadcast port (SignalR hub in Api).
public interface IPlaybackHub
{
    Task BroadcastStateAsync(Guid userId, PlayerStateDto state, CancellationToken ct = default);
    Task BroadcastQueueAsync(Guid userId, IReadOnlyList<SongDto> queue, CancellationToken ct = default);
    Task BroadcastDownloadAsync(Guid userId, DownloadJobDto job, CancellationToken ct = default);
}

// YouTube ingest engine port (YoutubeExplode in Infrastructure):
// search hits, audio-only download + MP3 transcode with 0..1 progress.
public interface IDownloadEngine
{
    Task<IReadOnlyList<VideoHitDto>> SearchAsync(string query, int limit, CancellationToken ct = default);
    Task<VideoInfoDto> GetInfoAsync(string url, CancellationToken ct = default);
    // Downloads best audio, transcodes to 320k MP3 at destMp3Path.
    // Progress is 0..1 (scaled to 0..90 by the caller, desktop convention).
    Task DownloadAudioAsync(string url, string destMp3Path,
        Action<double> progress, CancellationToken ct = default);
}

public sealed record VideoInfoDto(
    string Id,
    string Title,
    string? Uploader,
    string PageUrl);

// iTunes Search API port (desktop search_itunes[_multi] + trending RSS).
public interface IITunesClient
{
    Task<ITunesHitDto?> SearchSingleAsync(string title, string? artist, CancellationToken ct = default);
    Task<IReadOnlyList<ITunesHitDto>> SearchMultiAsync(
        string title, string? artist, int limit, CancellationToken ct = default);
    Task<byte[]?> FetchArtworkAsync(string artworkUrl, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTrendingAsync(int limit, CancellationToken ct = default);
}

// lrclib port (desktop LyricsService fetch paths).
public interface ILrclibClient
{
    Task<LyricsDto?> GetExactAsync(string track, string artist, int? durationSec,
        CancellationToken ct = default);
    Task<LyricsDto?> SearchTrackOnlyAsync(string track, int? durationSec,
        CancellationToken ct = default);
    Task<IReadOnlyList<LyricsHitDto>> SearchAsync(string track, string artist,
        string? album, int? durationSec, CancellationToken ct = default);
}

public interface IJwtTokenService
{
    string CreateAccessToken(Guid userId, string username, IEnumerable<string> scopes, Guid? sessionId = null);
}

public interface IApiKeyService
{
    int PrefixChars { get; } // hth_ + 8-char prefix scheme
    (string Token, string Prefix, string Hash) Create();
    string Hash(string token);
}

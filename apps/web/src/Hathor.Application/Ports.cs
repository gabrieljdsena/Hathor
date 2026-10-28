using Hathor.Application.Dtos;
using Hathor.Domain.Services;

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
    // Raw cover bytes + mime + version stamp (mtime/size) for the
    // browser-cached /cover endpoint. Null when the file has no art.
    (byte[] Bytes, string Mime, string Version)? ReadCover(Guid userId, string file);
    // Raw file metadata without DB (library scans).
    SongDto ReadLocalSong(Guid userId, string file);
    // Exact-metadata matches, desktop-sorted (artist view: Album+Title, album view: Artist+Title).
    // Paging is opt-in (null = whole list, legacy callers); infinite scroll
    // passes page/pageSize, queue building uses the Files variants.
    Task<IReadOnlyList<string>> GetArtistsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<SongDto>> GetSongsByArtistAsync(Guid userId, string artist,
        string? search = null, string? sort = null, string? dir = null,
        int? page = null, int? pageSize = null, CancellationToken ct = default);
    Task<int> GetArtistSongCountAsync(Guid userId, string artist, string? search = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetArtistFilesAsync(Guid userId, string artist, string? search = null,
        string? sort = null, string? dir = null, CancellationToken ct = default);
    // Owned file for a title/artist pair (normalized MediaKeys match),
    // or null — drives the "already in your library?" download confirm.
    Task<string?> FindFileByMetadataAsync(Guid userId, string title, string? artist,
        CancellationToken ct = default);
    // Files whose rows lack measured loudness (bounded backfill).
    Task<IReadOnlyList<string>> GetFilesMissingLoudnessAsync(Guid userId, int limit,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAlbumsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<SongDto>> GetSongsByAlbumAsync(Guid userId, string album,
        string? search = null, string? sort = null, string? dir = null,
        int? page = null, int? pageSize = null, CancellationToken ct = default);
    Task<int> GetAlbumSongCountAsync(Guid userId, string album, string? search = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetAlbumFilesAsync(Guid userId, string album, string? search = null,
        string? sort = null, string? dir = null, CancellationToken ct = default);
    // Ordered file names only (same search/sort as ListAsync, no metadata
    // reads): queue building for play-all without holding full Song[] lists.
    Task<IReadOnlyList<string>> ListFilesAsync(Guid userId, string? search, string? sort, string? dir,
        CancellationToken ct = default);
}

// Playlist read-model port (Dapper): lists + song counts for grids/submenus.
public interface IPlaylistReadModel
{
    Task<IReadOnlyList<PlaylistDto>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<PlaylistWithCountDto>> ListWithCountsAsync(Guid userId, CancellationToken ct = default);
    Task<PlaylistDto?> GetAsync(Guid userId, long id, CancellationToken ct = default);
}

// Discover taste read-model port (Dapper): recency-weighted top artists /
// tracks / tags, owned title+artist pairs, and in-flight download pairs for
// dedupe (packages/contracts/discover.md rules 1-2). Local-only signals.
public interface IDiscoverTasteReadModel
{
    Task<IReadOnlyList<Hathor.Domain.Services.TasteArtist>> GetTopArtistsAsync(
        Guid userId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<Hathor.Domain.Services.TasteTrack>> GetTopTracksAsync(
        Guid userId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTopTagsAsync(
        Guid userId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<TasteFile>> GetTopPlayedFilesAsync(
        Guid userId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<(string Title, string Artist)>> GetLibraryPairsAsync(
        Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<(string Title, string Artist)>> GetActiveDownloadPairsAsync(
        Guid userId, CancellationToken ct = default);
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
    // Same filter/order as the Download History tab (DownloadedLink present).
    Task<IReadOnlyList<string>> GetDownloadHistoryFilesAsync(Guid userId, int limit, CancellationToken ct = default);
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
    // Streamed upload (no byte[] buffering): the handler enforces the size
    // cap, the implementation deletes stale files then moves into place.
    string? FindBackground(Guid userId);
    Task<string> SaveBackgroundFromStreamAsync(Guid userId, Stream content, string contentType,
        CancellationToken ct = default);
    Task DeleteBackgroundAsync(Guid userId, CancellationToken ct = default);
    string BackgroundPath(Guid userId, string file);
}

// Podcast read-model port: lightweight list (DB titles + disk files,
// no durations/covers until details) + full per-episode details.
// Paging is opt-in (null = whole list, legacy callers).
public interface IPodcastReadModel
{
    Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, string? search = null, long? tagId = null,
        int? page = null, int? pageSize = null, CancellationToken ct = default);
    Task<int> CountAsync(Guid userId, string? search = null, long? tagId = null,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListFilesAsync(Guid userId, string? search = null, long? tagId = null,
        CancellationToken ct = default);
    Task<SongDto?> GetByFileAsync(Guid userId, string file, CancellationToken ct = default);
    // Raw cover bytes + mime + version stamp (see ISongReadModel.ReadCover).
    (byte[] Bytes, string Mime, string Version)? ReadCover(Guid userId, string file);
    // Raw file metadata without DB (library scans).
    SongDto ReadLocalEpisode(Guid userId, string file);
}

// Snapshot exchange port (device sync semantics).
public interface ISyncService
{
    Task<Application.Sync.SyncSnapshot> ExportAsync(Guid userId, long sinceId,
        CancellationToken ct = default);
    // reconcileLinks (pull path only): drop local link rows absent from a
    // full snapshot. Never true for /sync/import — partial snapshots must
    // not wipe links.
    Task<Application.Sync.ImportResult> ImportAsync(Guid userId,
        Application.Sync.SyncSnapshot snapshot, CancellationToken ct = default,
        bool reconcileLinks = false);
    // Incremental delta for device sync: rows with UpdatedAtUtc >= the
    // cursor time (overlap is idempotent client-side), append-only history
    // by id, tombstones by DeletedAtUtc. Opaque cursor round-trips.
    Task<Application.Sync.SyncDelta> GetDeltaAsync(Guid userId, string cursor,
        CancellationToken ct = default);
    // Persist raw MP3 bytes pushed by a device (PUT /sync/files/{file}):
    // streamed from the request body to the shared library folder (no
    // MemoryStream buffering) and upserts the catalog row.
    Task SaveFileFromStreamAsync(Guid userId, string file, bool isPodcast, Stream content,
        CancellationToken ct = default);
}

// Startup-maintenance probe port (desktop startup_maintenance status
// surface): FFmpeg presence + managed-library versions. No auto-installs
// server-side — Docker bakes FFmpeg in; the endpoint only reports.
public interface ISystemProbe
{
    FFmpegStatusDto GetFFmpegStatus();
    // yt-dlp fallback binary (same shape as FFmpeg: found/exe/version-or-error).
    FFmpegStatusDto GetYtDlpStatus();
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
    // Extra SignalR args are ignored by old external clients, so the
    // version rides along without breaking them.
    Task BroadcastQueueAsync(Guid userId, IReadOnlyList<SongDto> queue,
        int queueVersion = 0, CancellationToken ct = default);
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
    // onPhase reports transitions ("manifest", "download", "transcode",
    // "ytdlp", …) so the caller can attribute stalls in job errors.
    // Optional for source compatibility; the queue always passes one.
    Task DownloadAudioAsync(string url, string destMp3Path,
        Action<double> progress, Action<string>? onPhase = null, CancellationToken ct = default);
    // Best-effort direct audio stream URL for previews (no download, no
    // transcode). Null when unresolvable (age-restricted/private/deleted).
    // URLs expire (~6h) and are loosely IP-bound: resolve on demand.
    Task<string?> GetPreviewUrlAsync(string url, CancellationToken ct = default);
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
    // Raw term search (entity=song): artist-discography expansion for Discover.
    Task<IReadOnlyList<ITunesHitDto>> SearchTermAsync(
        string term, int limit, CancellationToken ct = default);
    Task<byte[]?> FetchArtworkAsync(string artworkUrl, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTrendingAsync(int limit, CancellationToken ct = default);
}

// Local-LLM taste-expansion port (llama.cpp server / any OpenAI-compatible /v1).
// Suggests title/artist pairs for a taste profile; every suggestion must be
// iTunes-verified before display (packages/contracts/discover.md rule 5).
// Never throws: LLM down/disabled/misconfigured yields [] (iTunes-only).
public interface IDiscoverySuggester
{
    Task<IReadOnlyList<(string Title, string Artist)>> SuggestAsync(
        IReadOnlyList<(string Artist, long Plays)> taste, int maxSuggestions,
        CancellationToken ct = default);
}

// Open-data candidate source (packages/contracts/discover.md): each source
// contributes candidates from raw public data; the local ranker decides the
// final order. Sources never call a provider's recommendation endpoint.
public interface ICandidateSource
{
    string Name { get; }
    // Fallback sources run only when the open-data pool is empty or too
    // small (cold start), so charts/same-artist expansion never dominate.
    bool IsFallback { get; }
    Task<IReadOnlyList<DiscoverCandidate>> GetCandidatesAsync(
        TasteProfile taste, int limit, CancellationToken ct = default);
}

// Fills artwork/catalog metadata for ranked candidates (iTunes is a
// resolver here, never a recommendation source).
public interface ICandidateResolver
{
    Task<DiscoverCandidate> ResolveAsync(
        DiscoverCandidate candidate, CancellationToken ct = default);
}

// MusicBrainz catalog/graph client (CC0 core data). Callers see parsed
// records only; HTTP/rate-limit/caching details stay in Infrastructure.
public interface IMusicBrainzClient
{
    Task<MbArtistDto?> SearchArtistAsync(string name, CancellationToken ct = default);
    Task<MbRecordingDto?> SearchRecordingAsync(
        string title, string artist, CancellationToken ct = default);
    Task<MbRecordingDto?> GetRecordingAsync(string mbid, CancellationToken ct = default);
    Task<IReadOnlyList<MbRecordingDto>> GetArtistRecordingsAsync(
        string artistMbid, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MbArtistDto>> GetRelatedArtistsAsync(
        string artistMbid, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<MbRecordingDto>> GetTagRecordingsAsync(
        string tag, int limit, CancellationToken ct = default);
}

// ListenBrainz Labs public similarity + open listen counts. No user token,
// no profile: queries carry seed MBIDs only.
public interface IListenBrainzClient
{
    Task<IReadOnlyList<SimilarArtistDto>> GetSimilarArtistsAsync(
        string artistMbid, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<SimilarRecordingDto>> GetSimilarRecordingsAsync(
        string recordingMbid, int limit, CancellationToken ct = default);
    // Global listen counts for a batch of recordings (main ListenBrainz API,
    // POST /1/popularity/recording). Known counts only.
    Task<IReadOnlyDictionary<string, long>> GetRecordingPopularityAsync(
        IReadOnlyList<string> recordingMbids, CancellationToken ct = default);
}

// Batch audio embedding provider (CLAP sidecar). Returns null entries when
// the sidecar/model is unavailable; callers treat that as "no audio signal".
public interface IEmbeddingProvider
{
    Task<IReadOnlyList<float[]?>> EmbedFilesAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default);
}

// Adds content similarity to candidates that have cached CLAP embeddings.
// Best-effort: missing embeddings/model leave candidates unchanged.
public interface IAudioSimilarityScorer
{
    Task<IReadOnlyList<DiscoverCandidate>> ScoreAsync(
        Guid userId, TasteProfile taste, IReadOnlyList<DiscoverCandidate> candidates,
        CancellationToken ct = default);
}

// Global recording popularity (LB listen counts), cached by MBID.
public interface ITrackPopularityService
{
    Task<IReadOnlyDictionary<string, long>> GetAsync(
        IReadOnlyList<string> recordingMbids, CancellationToken ct = default);
}

// Builds the user's historical popularity band from top played tracks.
public interface IPopularityProfileBuilder
{
    Task<PopularityProfile?> BuildAsync(
        Guid userId, IReadOnlyList<TasteTrack> tracks, CancellationToken ct = default);
}

// Resolves the ranker weights for a user: tuned model when fresh/valid,
// otherwise defaults. CacheVersion changes when a tuned model is applied so
// the daily Discover cache invalidates immediately.
public sealed record RankingOptionsSnapshot(
    DiscoverRankingOptions Options, string CacheVersion, string? PresetName);

public interface IRankingOptionsProvider
{
    Task<RankingOptionsSnapshot> GetAsync(
        Guid userId, CancellationToken ct = default);
}

// Postgres-backed cache for public-data HTTP responses (survives restarts
// and keeps MusicBrainz/ListenBrainz calls inside their rate limits).
public interface IRecommendationCache
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
        where T : class;
}

// Loudness analyzer port (ffmpeg loudnorm): integrated LUFS for a file,
// or null when analysis fails. Never throws — callers treat null as
// "unmeasured" and the leveler covers the track.
public interface ILoudnessAnalyzer
{
    Task<double?> AnalyzeAsync(string path, CancellationToken ct = default);
}

// MeCab romanization backend port (fugashi + cutlet via sidecar).
// Optional: null means unavailable (no Python/packages/timeout) and the
// caller falls back to KanaRomaji. Never throws.
public interface IRomanizerBackend
{
    Task<string?> RomanizeAsync(string text, bool isLrc, CancellationToken ct = default);
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

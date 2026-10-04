using Hathor.Domain.Entities;

namespace Hathor.Domain.Repositories;

// Command-side repository contracts (EF Core implementations live in Infrastructure).
// Read-model (list/search/count/history) contracts live in Application as Dapper queries.

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task AddAsync(User user, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Server-side sessions (one row per login; refresh-token hashes only).
public interface ISessionRepository
{
    Task AddAsync(Session session, CancellationToken ct = default);
    Task<Session?> GetByHashAsync(string tokenHash, CancellationToken ct = default);
    Task<IReadOnlyList<Session>> ListActiveAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task RevokeAllAsync(Guid userId, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct = default);
    Task<RefreshToken?> GetValidAsync(string tokenHash, CancellationToken ct = default);
    Task RevokeAsync(Guid id, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IApiKeyRepository
{
    Task AddAsync(ApiKey key, CancellationToken ct = default);
    Task<ApiKey?> GetByPrefixAsync(string prefix, CancellationToken ct = default);
    Task<IReadOnlyList<ApiKey>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    Task RevokeAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IPlaybackStateRepository
{
    Task<Playback.PlaybackState> GetOrCreateAsync(Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IUserSettingsRepository
{
    Task<UserSettings> GetOrCreateAsync(Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Tombstone log for remote deletion propagation (desktop Sync_Deletions).
public interface ITombstoneRepository
{
    Task RecordAsync(Guid userId, string tableName, string rowKey, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Daily mix cache + play-count ranking (EF). Generation quotas live in
// DailyMixGenerator; lazy once-per-day semantics in Application.
public interface IDailyMixRepository
{
    Task<DailyMix?> GetAsync(Guid userId, string date, CancellationToken ct = default);
    Task SaveAsync(Guid userId, string date, string songFilesJson, CancellationToken ct = default);
    Task PruneOthersAsync(Guid userId, string today, CancellationToken ct = default);
    // Files ordered by total play count, most played first (desktop _get_ranked_files).
    Task<IReadOnlyList<string>> GetRankedFilesAsync(Guid userId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Playlist + song-link writes (EF). Hot reads live in IPlaylistReadModel (Dapper).
public interface IPlaylistRepository
{
    Task<List<Playlist>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<Playlist?> GetAsync(Guid userId, long id, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid userId, long id, CancellationToken ct = default);
    Task AddAsync(Playlist playlist, CancellationToken ct = default);
    void Remove(Playlist playlist);
    Task<bool> LinkExistsAsync(Guid userId, long playlistId, string songFile, CancellationToken ct = default);
    Task AddLinkAsync(Guid userId, string songFile, long playlistId, DateTime dateUtc, CancellationToken ct = default);
    Task<int> RemoveLinksAsync(Guid userId, long? playlistId, string? songFile, CancellationToken ct = default);
    Task RemovePlaylistHistoryAsync(Guid userId, long playlistId, CancellationToken ct = default);
    Task<List<long>> GetIdsForSongAsync(Guid userId, string file, CancellationToken ct = default);
    Task<List<(string File, DateTime DateAdded)>> GetSongFilesAsync(Guid userId, long playlistId, CancellationToken ct = default);
    Task<List<long>> FilterValidIdsAsync(Guid userId, IEnumerable<long> ids, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Podcast-tag writes (EF). Counts + map live in IPodcastTagReadModel (Dapper).
public interface IPodcastTagRepository
{
    Task<List<PodcastTag>> ListAsync(Guid userId, CancellationToken ct = default);
    Task<PodcastTag?> GetByNameAsync(Guid userId, string name, CancellationToken ct = default);
    Task<PodcastTag?> GetByIdAsync(Guid userId, long id, CancellationToken ct = default);
    Task AddAsync(PodcastTag tag, CancellationToken ct = default);
    Task RemoveWithLinksAsync(PodcastTag tag, CancellationToken ct = default);
    Task AssignAsync(Guid userId, string file, long tagId, CancellationToken ct = default);
    Task UnassignAsync(Guid userId, string file, long tagId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Songs-table writes (EF). File metadata itself is TagLib + filesystem.
public interface ISongRecordRepository
{
    Task<Song?> GetAsync(Guid userId, string file, CancellationToken ct = default);
    Task EnsureAsync(Guid userId, string file, string title, CancellationToken ct = default);
    Task UpdateTitleArtistAsync(Guid userId, string file, string title, string artist, CancellationToken ct = default);
    // Download upsert (desktop DownloadManager._save_song_record): fresh timestamp.
    Task UpsertDownloadedAsync(Guid userId, string file, string? link, string title,
        string? artist, CancellationToken ct = default);
    // Deletes links + lyrics + history + song row. Returns false when no row existed.
    Task<bool> DeleteCascadeAsync(Guid userId, string file, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Podcast-table writes (EF). Episode file tags are written by IMetadataWriter.
public interface IPodcastRecordRepository
{
    Task EnsureAsync(Guid userId, string file, string title, string? artist,
        string? link, CancellationToken ct = default);
    Task<bool> DeleteCascadeAsync(Guid userId, string file, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Download-job log (EF). The queue pump runs through these (all user-scoped).
public interface IDownloadJobRepository
{
    Task AddAsync(DownloadJob job, CancellationToken ct = default);
    Task<DownloadJob?> GetByQidAsync(string qid, CancellationToken ct = default);
    Task<DownloadJob?> ActiveByUrlAsync(Guid userId, string url, CancellationToken ct = default);
    Task<DownloadJob?> LatestByUrlAsync(Guid userId, string url, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadJob>> RecentAsync(Guid userId, int limit, CancellationToken ct = default);
    Task<int> CountActiveAsync(Guid userId, CancellationToken ct = default);
    Task<int> ResetStuckAsync(Guid userId, CancellationToken ct = default);
    Task<int> DeleteTerminalAsync(Guid userId, CancellationToken ct = default);
    // Atomically claim the oldest queued job for execution (pump).
    Task<DownloadJob?> ClaimNextAsync(CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

// Lyrics cache (EF): one JSON row per song file (desktop Lyrics table).
public interface ILyricsRepository
{
    Task<Lyric?> GetByFileAsync(Guid userId, string file, CancellationToken ct = default);
    Task UpsertAsync(Guid userId, string file, string lyricsJson, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid userId, string file, CancellationToken ct = default);
    Task<int> GetOffsetAsync(Guid userId, string file, CancellationToken ct = default);
    // Zero removes the row when it holds no lyrics (sparse storage).
    Task SetOffsetAsync(Guid userId, string file, int offsetMs, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}

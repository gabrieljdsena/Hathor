using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hathor.Infrastructure.Repositories;

public sealed class EfUserRepository(HathorDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public Task<bool> AnyAsync(CancellationToken ct = default) =>
        db.Users.AnyAsync(ct);

    public async Task AddAsync(User user, CancellationToken ct = default) =>
        await db.Users.AddAsync(user, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfPendingEditRepository(HathorDbContext db) : IPendingEditRepository
{
    public Task<PendingMetadataEdit?> GetAsync(Guid userId, string file, CancellationToken ct = default) =>
        db.PendingMetadataEdits.FirstOrDefaultAsync(
            p => p.UserId == userId && p.File == file, ct);

    public Task<List<PendingMetadataEdit>> ListAsync(Guid userId, CancellationToken ct = default) =>
        db.PendingMetadataEdits.Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedUtc).ToListAsync(ct);

    public async Task UpsertAsync(PendingMetadataEdit edit, CancellationToken ct = default)
    {
        var existing = await GetAsync(edit.UserId, edit.File, ct);
        if (existing is null)
        {
            await db.PendingMetadataEdits.AddAsync(edit, ct);
            return;
        }
        existing.IsPodcast = edit.IsPodcast;
        existing.Title = edit.Title;
        existing.Artist = edit.Artist;
        existing.Album = edit.Album;
        existing.Year = edit.Year;
        existing.Genre = edit.Genre;
        existing.CoverArt = edit.CoverArt;
        existing.CreatedUtc = edit.CreatedUtc;
    }

    public async Task<bool> DeleteAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var existing = await GetAsync(userId, file, ct);
        if (existing is null) return false;
        db.PendingMetadataEdits.Remove(existing);
        return true;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfSessionRepository(HathorDbContext db) : ISessionRepository
{
    public async Task AddAsync(Session session, CancellationToken ct = default) =>
        await db.Sessions.AddAsync(session, ct);

    public Task<Session?> GetByHashAsync(string tokenHash, CancellationToken ct = default) =>
        db.Sessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == tokenHash, ct);

    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Session>> ListActiveAsync(Guid userId, CancellationToken ct = default) =>
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(s => s.LastUsedAtUtc).ToListAsync(ct);

    public async Task RevokeAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is not null && session.RevokedAtUtc is null)
            session.RevokedAtUtc = DateTime.UtcNow;
    }

    public async Task RevokeAllAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now), ct);
    }

    // Rotation creates one row per access-token refresh, so old rows must be
    // swept. Revoked rows are kept for a month (reuse-detection forensics)
    // before being deleted alongside expired ones.
    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var revokedCutoff = now.AddDays(-30);
        return await db.Sessions
            .Where(s => s.ExpiresAtUtc <= now ||
                (s.RevokedAtUtc != null && s.RevokedAtUtc <= revokedCutoff))
            .ExecuteDeleteAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfApiKeyRepository(HathorDbContext db) : IApiKeyRepository
{
    public async Task AddAsync(ApiKey key, CancellationToken ct = default) =>
        await db.ApiKeys.AddAsync(key, ct);

    public async Task<ApiKey?> GetByPrefixAsync(string prefix, CancellationToken ct = default) =>
        await db.ApiKeys.FirstOrDefaultAsync(k => k.Prefix == prefix && !k.Revoked, ct);

    public async Task<IReadOnlyList<ApiKey>> ListForUserAsync(Guid userId, CancellationToken ct = default) =>
        await db.ApiKeys.Where(k => k.UserId == userId && !k.Revoked)
            .OrderByDescending(k => k.CreatedAtUtc).ToListAsync(ct);

    public async Task RevokeAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId, ct);
        if (key is not null)
        {
            key.Revoked = true;
            await db.SaveChangesAsync(ct);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfUserSettingsRepository(HathorDbContext db) : IUserSettingsRepository
{
    public async Task<UserSettings> GetOrCreateAsync(Guid userId, CancellationToken ct = default)
    {
        var settings = await db.Settings.FindAsync([userId], ct);
        if (settings is null)
        {
            settings = new UserSettings { UserId = userId };
            await db.Settings.AddAsync(settings, ct);
            await db.SaveChangesAsync(ct);
        }
        return settings;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfTombstoneRepository(HathorDbContext db) : ITombstoneRepository
{
    public async Task RecordAsync(Guid userId, string tableName, string rowKey, CancellationToken ct = default) =>
        await db.SyncDeletions.AddAsync(new SyncDeletion
        {
            UserId = userId,
            TableName = tableName,
            RowKey = rowKey,
            DeletedAtUtc = DateTime.UtcNow,
        }, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfPlaylistRepository(HathorDbContext db) : IPlaylistRepository
{
    public Task<List<Playlist>> ListAsync(Guid userId, CancellationToken ct = default) =>
        db.Playlists.Where(p => p.UserId == userId).OrderBy(p => p.Title).ToListAsync(ct);

    public Task<Playlist?> GetAsync(Guid userId, long id, CancellationToken ct = default) =>
        db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);

    public Task<bool> ExistsAsync(Guid userId, long id, CancellationToken ct = default) =>
        db.Playlists.AnyAsync(p => p.Id == id && p.UserId == userId, ct);

    public async Task AddAsync(Playlist playlist, CancellationToken ct = default) =>
        await db.Playlists.AddAsync(playlist, ct);

    public void Remove(Playlist playlist) => db.Playlists.Remove(playlist);

    public Task<bool> LinkExistsAsync(Guid userId, long playlistId, string songFile, CancellationToken ct = default) =>
        db.SongPlaylists.AnyAsync(l =>
            l.UserId == userId && l.PlaylistId == playlistId && l.SongFile == songFile, ct);

    public async Task AddLinkAsync(Guid userId, string songFile, long playlistId, DateTime dateUtc, CancellationToken ct = default) =>
        await db.SongPlaylists.AddAsync(new SongPlaylist
        {
            UserId = userId,
            SongFile = songFile,
            PlaylistId = playlistId,
            DateAddedUtc = dateUtc,
        }, ct);

    public Task<int> RemoveLinksAsync(Guid userId, long? playlistId, string? songFile, CancellationToken ct = default) =>
        db.SongPlaylists
            .Where(l => l.UserId == userId
                && (!playlistId.HasValue || l.PlaylistId == playlistId.Value)
                && (songFile == null || l.SongFile == songFile))
            .ExecuteDeleteAsync(ct);

    public Task RemovePlaylistHistoryAsync(Guid userId, long playlistId, CancellationToken ct = default) =>
        db.PlaylistHistory
            .Where(h => h.UserId == userId && h.PlaylistId == playlistId)
            .ExecuteDeleteAsync(ct);

    public async Task<List<long>> GetIdsForSongAsync(Guid userId, string file, CancellationToken ct = default) =>
        await db.SongPlaylists
            .Where(l => l.UserId == userId && l.SongFile == file)
            .Select(l => l.PlaylistId)
            .ToListAsync(ct);

    public async Task<List<(string File, DateTime DateAdded)>> GetSongFilesAsync(
        Guid userId, long playlistId, CancellationToken ct = default) =>
        await db.SongPlaylists
            .Where(l => l.UserId == userId && l.PlaylistId == playlistId)
            .OrderBy(l => l.DateAddedUtc)
            .Select(l => new ValueTuple<string, DateTime>(l.SongFile, l.DateAddedUtc))
            .ToListAsync(ct);

    public async Task<List<long>> FilterValidIdsAsync(Guid userId, IEnumerable<long> ids, CancellationToken ct = default)
    {
        var set = ids.ToHashSet();
        return await db.Playlists
            .Where(p => p.UserId == userId && set.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfPodcastTagRepository(HathorDbContext db) : IPodcastTagRepository
{
    public Task<List<PodcastTag>> ListAsync(Guid userId, CancellationToken ct = default) =>
        db.PodcastTags.Where(t => t.UserId == userId).OrderBy(t => t.Name).ToListAsync(ct);

    public Task<PodcastTag?> GetByNameAsync(Guid userId, string name, CancellationToken ct = default) =>
        db.PodcastTags.FirstOrDefaultAsync(t => t.UserId == userId && t.Name == name, ct);

    public Task<PodcastTag?> GetByIdAsync(Guid userId, long id, CancellationToken ct = default) =>
        db.PodcastTags.FirstOrDefaultAsync(t => t.UserId == userId && t.Id == id, ct);

    public async Task AddAsync(PodcastTag tag, CancellationToken ct = default) =>
        await db.PodcastTags.AddAsync(tag, ct);

    public async Task RemoveWithLinksAsync(PodcastTag tag, CancellationToken ct = default)
    {
        await db.PodcastTagLinks
            .Where(l => l.UserId == tag.UserId && l.TagId == tag.Id)
            .ExecuteDeleteAsync(ct);
        db.PodcastTags.Remove(tag);
    }

    public async Task AssignAsync(Guid userId, string file, long tagId, CancellationToken ct = default)
    {
        var exists = await db.PodcastTagLinks.AnyAsync(l =>
            l.UserId == userId && l.PodcastFile == file && l.TagId == tagId, ct);
        if (!exists)
            await db.PodcastTagLinks.AddAsync(new PodcastTagLink
            {
                UserId = userId,
                PodcastFile = file,
                TagId = tagId,
            }, ct);
    }

    public Task UnassignAsync(Guid userId, string file, long tagId, CancellationToken ct = default) =>
        db.PodcastTagLinks
            .Where(l => l.UserId == userId && l.PodcastFile == file && l.TagId == tagId)
            .ExecuteDeleteAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfSongRecordRepository(HathorDbContext db) : ISongRecordRepository
{    public Task<Song?> GetAsync(Guid userId, string file, CancellationToken ct = default) =>
        db.Songs.FirstOrDefaultAsync(s => s.UserId == userId && s.File == file, ct);

    public async Task EnsureAsync(Guid userId, string file, string title, CancellationToken ct = default)
    {
        if (!await db.Songs.AnyAsync(s => s.UserId == userId && s.File == file, ct))
            await db.Songs.AddAsync(new Song
            {
                UserId = userId,
                File = file,
                Title = title,
                DateDownloadUtc = DateTime.UtcNow,
            }, ct);
    }

    public async Task UpsertMetadataAsync(Guid userId, string file, string title,
        string? artist, string? album, string? year, string? genre, double durationSecs,
        CancellationToken ct = default)
    {
        var song = await GetAsync(userId, file, ct);
        if (song is null)
        {
            await db.Songs.AddAsync(new Song
            {
                UserId = userId,
                File = file,
                Title = title,
                DateDownloadUtc = DateTime.UtcNow,
                Artist = artist,
                Album = album,
                Year = year,
                Genre = genre,
                DurationSecs = durationSecs,
            }, ct);
            return;
        }
        song.Title = title;
        song.Artist = artist;
        song.Album = album;
        song.Year = year;
        song.Genre = genre;
        song.DurationSecs = durationSecs;
    }

    public async Task SetLoudnessAsync(Guid userId, string file, double loudnessDb,
        CancellationToken ct = default)
    {
        var song = await GetAsync(userId, file, ct);
        if (song is null) return;
        song.LoudnessDb = loudnessDb;
    }

    public async Task UpsertDownloadedAsync(Guid userId, string file, string? link,
        string title, string? artist, string? album, string? year, string? genre,
        double durationSecs, CancellationToken ct = default)
    {
        var song = await GetAsync(userId, file, ct);
        if (song is null)
        {
            await db.Songs.AddAsync(new Song
            {
                UserId = userId,
                File = file,
                DownloadedLink = link,
                Title = title,
                DateDownloadUtc = DateTime.UtcNow,
                Artist = artist,
                Album = album,
                Year = year,
                Genre = genre,
                DurationSecs = durationSecs,
            }, ct);
        }
        else
        {
            song.DownloadedLink = link;
            song.Title = title;
            song.Artist = artist;
            song.Album = album;
            song.Year = year;
            song.Genre = genre;
            song.DurationSecs = durationSecs;
            song.DateDownloadUtc = DateTime.UtcNow;
        }
    }

    public async Task<bool> DeleteCascadeAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var existed = await db.Songs.AnyAsync(s => s.UserId == userId && s.File == file, ct);
        await db.SongPlaylists.Where(l => l.UserId == userId && l.SongFile == file).ExecuteDeleteAsync(ct);
        // Base row plus per-chapter cache rows ("{file}::chapter:{id}").
        // Filenames may hold LIKE wildcards: escape before prefix use.
        var chapterPrefix = EfLyricsRepository.LikeEscape(file) + "::chapter:";
        await db.Lyrics.Where(l => l.UserId == userId
            && (l.SongFile == file || EF.Functions.Like(l.SongFile, chapterPrefix + "%")))
            .ExecuteDeleteAsync(ct);
        await db.MusicHistory.Where(h => h.UserId == userId && h.SongFile == file).ExecuteDeleteAsync(ct);
        await db.Songs.Where(s => s.UserId == userId && s.File == file).ExecuteDeleteAsync(ct);
        return existed;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfDailyMixRepository(HathorDbContext db) : IDailyMixRepository
{    public Task<DailyMix?> GetAsync(Guid userId, string date, CancellationToken ct = default) =>
        db.DailyMixes.FirstOrDefaultAsync(m => m.UserId == userId && m.MixDate == date, ct);

    public async Task SaveAsync(Guid userId, string date, string songFilesJson, CancellationToken ct = default)
    {
        var existing = await GetAsync(userId, date, ct);
        if (existing is null)
            await db.DailyMixes.AddAsync(new DailyMix
            {
                UserId = userId,
                MixDate = date,
                SongFilesJson = songFilesJson,
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);
        else
            existing.SongFilesJson = songFilesJson;
    }

    public Task PruneOthersAsync(Guid userId, string today, CancellationToken ct = default) =>
        db.DailyMixes
            .Where(m => m.UserId == userId && m.MixDate != today)
            .ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<string>> GetRankedFilesAsync(Guid userId, CancellationToken ct = default) =>
        await db.MusicHistory
            .Where(h => h.UserId == userId)
            .GroupBy(h => h.SongFile)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(h => h.DatePlayedUtc))
            .Select(g => g.Key)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfDiscoverCacheRepository(HathorDbContext db) : IDiscoverCacheRepository
{
    public Task<DiscoverCache?> GetAsync(Guid userId, string date, CancellationToken ct = default) =>
        db.DiscoverCaches.FirstOrDefaultAsync(m => m.UserId == userId && m.Date == date, ct);

    public async Task SaveAsync(
        Guid userId, string date, string modelVersion, string itemsJson,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(userId, date, ct);
        if (existing is null)
            await db.DiscoverCaches.AddAsync(new DiscoverCache
            {
                UserId = userId,
                Date = date,
                ModelVersion = modelVersion,
                ItemsJson = itemsJson,
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);
        else
        {
            existing.ModelVersion = modelVersion;
            existing.ItemsJson = itemsJson;
            existing.CreatedAtUtc = DateTime.UtcNow;
        }
    }

    public Task PruneOthersAsync(Guid userId, string today, CancellationToken ct = default) =>
        db.DiscoverCaches
            .Where(m => m.UserId == userId && m.Date != today)
            .ExecuteDeleteAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

// Per-user tuned ranker weights (EF). One row per user; stale models are
// ignored by the ranking options provider.
public sealed class EfRankingModelRepository(HathorDbContext db)
    : IRankingModelRepository
{
    public Task<RankingModel?> GetAsync(
        Guid userId, CancellationToken ct = default) =>
        db.RankingModels.FirstOrDefaultAsync(x => x.UserId == userId, ct);

    public async Task UpsertAsync(RankingModel model, CancellationToken ct = default)
    {
        var existing = await GetAsync(model.UserId, ct);
        if (existing is null)
        {
            await db.RankingModels.AddAsync(model, ct);
            return;
        }
        existing.PresetName = model.PresetName;
        existing.WeightsJson = model.WeightsJson;
        existing.Samples = model.Samples;
        existing.Snapshots = model.Snapshots;
        existing.UpdatedAtUtc = model.UpdatedAtUtc;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}

// Global ListenBrainz popularity cache (EF), keyed by recording MBID.
public sealed class EfTrackPopularityRepository(HathorDbContext db)
    : ITrackPopularityRepository
{
    public async Task<IReadOnlyDictionary<string, long>> GetManyAsync(
        IEnumerable<string> mbids, CancellationToken ct = default)
    {
        var keys = mbids.Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return new Dictionary<string, long>();
        var rows = await db.TrackPopularities.AsNoTracking()
            .Where(x => keys.Contains(x.Mbid))
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Mbid, x => x.ListenCount, StringComparer.Ordinal);
    }

    public async Task UpsertAsync(
        IReadOnlyDictionary<string, long> counts, CancellationToken ct = default)
    {
        if (counts.Count == 0) return;
        var keys = counts.Keys.ToList();
        var existing = await db.TrackPopularities
            .Where(x => keys.Contains(x.Mbid))
            .ToListAsync(ct);
        var map = existing.ToDictionary(x => x.Mbid, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var (mbid, count) in counts)
        {
            if (map.TryGetValue(mbid, out var row))
            {
                row.ListenCount = count;
                row.UpdatedAtUtc = now;
            }
            else
            {
                await db.TrackPopularities.AddAsync(new TrackPopularity
                {
                    Mbid = mbid,
                    ListenCount = count,
                    UpdatedAtUtc = now,
                }, ct);
            }
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Concurrent popularity fills: first insert wins.
            db.ChangeTracker.Clear();
        }
    }
}

// CLAP embedding cache (EF). Song keys are library filenames, preview keys
// are recording MBIDs. Vectors are JSON float arrays (portable, no pgvector).
public sealed class EfTrackEmbeddingRepository(HathorDbContext db)
    : ITrackEmbeddingRepository
{
    public Task<TrackEmbedding?> GetAsync(
        Guid userId, string ownerType, string ownerKey, string model,
        CancellationToken ct = default) =>
        db.TrackEmbeddings.FirstOrDefaultAsync(
            x => x.UserId == userId && x.OwnerType == ownerType &&
                 x.OwnerKey == ownerKey && x.Model == model, ct);

    public async Task<IReadOnlyList<TrackEmbedding>> GetManyAsync(
        Guid userId, string ownerType, IEnumerable<string> ownerKeys, string model,
        CancellationToken ct = default)
    {
        var keys = ownerKeys.Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0) return [];
        return await db.TrackEmbeddings.AsNoTracking()
            .Where(x => x.UserId == userId && x.OwnerType == ownerType &&
                        x.Model == model && keys.Contains(x.OwnerKey))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TrackEmbedding>> GetAllAsync(
        Guid userId, string ownerType, string model, CancellationToken ct = default) =>
        await db.TrackEmbeddings.AsNoTracking()
            .Where(x => x.UserId == userId && x.OwnerType == ownerType && x.Model == model)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> GetMissingSongFilesAsync(
        Guid userId, string model, int limit, CancellationToken ct = default)
    {
        var files = await db.Songs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.File)
            .ToListAsync(ct);
        var existing = (await db.TrackEmbeddings.AsNoTracking()
                .Where(x => x.UserId == userId && x.OwnerType == "song" && x.Model == model)
                .Select(x => x.OwnerKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files
            .Where(file => !existing.Contains(file))
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    public async Task SaveAsync(TrackEmbedding embedding, CancellationToken ct = default)
    {
        var existing = await GetAsync(embedding.UserId, embedding.OwnerType,
            embedding.OwnerKey, embedding.Model, ct);
        if (existing is null)
        {
            await db.TrackEmbeddings.AddAsync(embedding, ct);
            return;
        }
        existing.VectorJson = embedding.VectorJson;
        existing.Dimension = embedding.Dimension;
        existing.CreatedAtUtc = embedding.CreatedAtUtc;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}

// Daily ranker snapshots (EF): upsert today's row; reads are bounded to the
// most recent ~400 days and filtered by ISO date in memory.
public sealed class EfDiscoverImpressionRepository(HathorDbContext db)
    : IDiscoverImpressionRepository
{
    public async Task SaveAsync(
        Guid userId, string date, string modelVersion, string itemsJson,
        CancellationToken ct = default)
    {
        var existing = await db.DiscoverImpressions.FirstOrDefaultAsync(
            x => x.UserId == userId && x.Date == date && x.ModelVersion == modelVersion, ct);
        if (existing is null)
        {
            await db.DiscoverImpressions.AddAsync(new DiscoverImpression
            {
                UserId = userId,
                Date = date,
                ModelVersion = modelVersion,
                ItemsJson = itemsJson,
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);
            return;
        }
        existing.ItemsJson = itemsJson;
        existing.CreatedAtUtc = DateTime.UtcNow;
    }

    public async Task<IReadOnlyList<DiscoverImpression>> GetSinceAsync(
        Guid userId, string sinceDate, CancellationToken ct = default)
    {
        var rows = await db.DiscoverImpressions.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Date)
            .Take(400)
            .ToListAsync(ct);
        return rows
            .Where(x => string.CompareOrdinal(x.Date, sinceDate) >= 0)
            .OrderBy(x => x.Date)
            .ToList();
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}

// Playback telemetry (EF): (UserId, SegmentId) is unique, so an unload
// report racing a track-change report cannot create duplicate rows.
public sealed class EfPlaybackEventRepository(HathorDbContext db)
    : IPlaybackEventRepository
{
    public async Task AddAsync(PlaybackEvent playbackEvent, CancellationToken ct = default)
    {
        var exists = await db.PlaybackEvents.AsNoTracking().AnyAsync(
            x => x.UserId == playbackEvent.UserId &&
                 x.SegmentId == playbackEvent.SegmentId, ct);
        if (exists) return;
        await db.PlaybackEvents.AddAsync(playbackEvent, ct);
    }

    public async Task<PlaybackSummary> GetSummaryAsync(
        Guid userId, DateTime sinceUtc, CancellationToken ct = default)
    {
        var rows = db.PlaybackEvents.AsNoTracking()
            .Where(x => x.UserId == userId && x.StartedAtUtc >= sinceUtc);
        var summary = await rows.GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Completed = g.Count(x => x.Completed),
                Skipped = g.Count(x => x.Skipped),
            })
            .FirstOrDefaultAsync(ct);
        return summary is null
            ? new PlaybackSummary(0, 0, 0)
            : new PlaybackSummary(summary.Total, summary.Completed, summary.Skipped);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Concurrent unload/track-change reports for the same segment:
            // the first insert wins, the duplicate is a no-op.
            db.ChangeTracker.Clear();
        }
    }
}

// Discover feedback is command-side (EF): one row per event, queried by the
// ranker within a bounded lookback window. Never shared across users.
public sealed class EfDiscoverFeedbackRepository(HathorDbContext db)
    : IDiscoverFeedbackRepository
{
    public async Task AddAsync(DiscoverFeedback feedback, CancellationToken ct = default) =>
        await db.DiscoverFeedback.AddAsync(feedback, ct);

    public async Task<IReadOnlyList<DiscoverFeedback>> GetSinceAsync(
        Guid userId, DateTime sinceUtc, int limit, CancellationToken ct = default) =>
        await db.DiscoverFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && x.CreatedAtUtc >= sinceUtc)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}

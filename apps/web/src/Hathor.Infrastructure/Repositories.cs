using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

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

public sealed class EfRefreshTokenRepository(HathorDbContext db) : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken token, CancellationToken ct = default) =>
        await db.RefreshTokens.AddAsync(token, ct);

    public Task<RefreshToken?> GetValidAsync(string tokenHash, CancellationToken ct = default) =>
        db.RefreshTokens.FirstOrDefaultAsync(t =>
            t.TokenHash == tokenHash && !t.Revoked && t.ExpiresAtUtc > DateTime.UtcNow, ct);

    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        var token = await db.RefreshTokens.FindAsync([id], ct);
        if (token is not null)
        {
            token.Revoked = true;
            await db.SaveChangesAsync(ct);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfSessionRepository(HathorDbContext db) : ISessionRepository
{
    public async Task AddAsync(Session session, CancellationToken ct = default) =>
        await db.Sessions.AddAsync(session, ct);

    public Task<Session?> GetByHashAsync(string tokenHash, CancellationToken ct = default) =>
        db.Sessions.FirstOrDefaultAsync(s => s.RefreshTokenHash == tokenHash, ct);

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

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default) =>
        await db.Sessions.Where(s => s.ExpiresAtUtc <= DateTime.UtcNow).ExecuteDeleteAsync(ct);

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

    public async Task UpdateTitleArtistAsync(
        Guid userId, string file, string title, string artist, CancellationToken ct = default)
    {
        var song = await GetAsync(userId, file, ct);
        if (song is null)
        {
            await EnsureAsync(userId, file, title, ct);
            song = await GetAsync(userId, file, ct);
        }
        if (song is not null)
        {
            song.Title = title;
            song.Artist = artist;
        }
    }

    public async Task UpsertDownloadedAsync(Guid userId, string file, string? link,
        string title, string? artist, CancellationToken ct = default)
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
            }, ct);
        }
        else
        {
            song.DownloadedLink = link;
            song.Title = title;
            song.Artist = artist;
            song.DateDownloadUtc = DateTime.UtcNow;
        }
    }

    public async Task<bool> DeleteCascadeAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var existed = await db.Songs.AnyAsync(s => s.UserId == userId && s.File == file, ct);
        await db.SongPlaylists.Where(l => l.UserId == userId && l.SongFile == file).ExecuteDeleteAsync(ct);
        await db.Lyrics.Where(l => l.UserId == userId && l.SongFile == file).ExecuteDeleteAsync(ct);
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

    public async Task SaveAsync(Guid userId, string date, string itemsJson, CancellationToken ct = default)
    {
        var existing = await GetAsync(userId, date, ct);
        if (existing is null)
            await db.DiscoverCaches.AddAsync(new DiscoverCache
            {
                UserId = userId,
                Date = date,
                ItemsJson = itemsJson,
                CreatedAtUtc = DateTime.UtcNow,
            }, ct);
        else
            existing.ItemsJson = itemsJson;
    }

    public Task PruneOthersAsync(Guid userId, string today, CancellationToken ct = default) =>
        db.DiscoverCaches
            .Where(m => m.UserId == userId && m.Date != today)
            .ExecuteDeleteAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Infrastructure.Repositories;

public sealed class EfDownloadJobRepository(HathorDbContext db) : IDownloadJobRepository
{
    private static readonly string[] Active = ["queued", "downloading"];
    private static readonly string[] Terminal = ["done", "failed", "cancelled"];

    public async Task AddAsync(DownloadJob job, CancellationToken ct = default) =>
        await db.DownloadJobs.AddAsync(job, ct);

    public Task<DownloadJob?> GetByQidAsync(string qid, CancellationToken ct = default) =>
        db.DownloadJobs.FirstOrDefaultAsync(j => j.Qid == qid, ct);

    public Task<DownloadJob?> ActiveByUrlAsync(Guid userId, string url, CancellationToken ct = default) =>
        db.DownloadJobs.FirstOrDefaultAsync(j =>
            j.UserId == userId && j.Url == url && Active.Contains(j.Status), ct);

    public Task<DownloadJob?> LatestByUrlAsync(Guid userId, string url, CancellationToken ct = default) =>
        db.DownloadJobs.Where(j => j.UserId == userId && j.Url == url)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<DownloadJob>> RecentAsync(Guid userId, int limit, CancellationToken ct = default) =>
        await db.DownloadJobs.Where(j => j.UserId == userId)
            .OrderByDescending(j => j.Id).Take(limit).ToListAsync(ct);

    public Task<int> CountActiveAsync(Guid userId, CancellationToken ct = default) =>
        db.DownloadJobs.CountAsync(j => j.UserId == userId && Active.Contains(j.Status), ct);

    public async Task<int> ResetStuckAsync(Guid userId, CancellationToken ct = default)
    {
        var stuck = await db.DownloadJobs
            .Where(j => j.UserId == userId && j.Status == "downloading")
            .ToListAsync(ct);
        foreach (var job in stuck)
        {
            job.Status = "queued";
            job.Progress = 0;
            job.Error = null;
            job.UpdatedAtUtc = DateTime.UtcNow;
        }
        return stuck.Count;
    }

    public Task<int> DeleteTerminalAsync(Guid userId, CancellationToken ct = default) =>
        db.DownloadJobs
            .Where(j => j.UserId == userId && Terminal.Contains(j.Status))
            .ExecuteDeleteAsync(ct);

    public async Task<DownloadJob?> ClaimNextAsync(CancellationToken ct = default)
    {
        var next = await db.DownloadJobs
            .Where(j => j.Status == "queued")
            .OrderBy(j => j.Id)
            .FirstOrDefaultAsync(ct);
        if (next is null) return null;
        next.Status = "downloading";
        next.UpdatedAtUtc = DateTime.UtcNow;
        return next;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfLyricsRepository(HathorDbContext db) : ILyricsRepository
{
    public Task<Lyric?> GetByFileAsync(Guid userId, string file, CancellationToken ct = default) =>
        db.Lyrics.FirstOrDefaultAsync(l => l.UserId == userId && l.SongFile == file, ct);

    public async Task UpsertAsync(Guid userId, string file, string lyricsJson, CancellationToken ct = default)
    {
        var existing = await GetByFileAsync(userId, file, ct);
        if (existing is null)
            await db.Lyrics.AddAsync(new Lyric
            {
                UserId = userId,
                SongFile = file,
                LyricsJson = lyricsJson,
            }, ct);
        else
            existing.LyricsJson = lyricsJson;
    }

    public async Task<bool> DeleteAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var existing = await GetByFileAsync(userId, file, ct);
        if (existing is null) return false;
        // Deleting lyrics must not wipe a tuned offset: clear the JSON and
        // drop the row only when nothing remains.
        existing.LyricsJson = null;
        if (existing.OffsetMs == 0) db.Lyrics.Remove(existing);
        return true;
    }

    public async Task<int> GetOffsetAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var existing = await GetByFileAsync(userId, file, ct);
        return existing?.OffsetMs ?? 0;
    }

    public async Task SetOffsetAsync(Guid userId, string file, int offsetMs, CancellationToken ct = default)
    {
        var existing = await GetByFileAsync(userId, file, ct);
        if (existing is null)
        {
            if (offsetMs == 0) return; // sparse: nothing to store
            await db.Lyrics.AddAsync(new Lyric
            {
                UserId = userId,
                SongFile = file,
                OffsetMs = offsetMs,
            }, ct);
        }
        else if (offsetMs == 0 && existing.LyricsJson is null)
        {
            db.Lyrics.Remove(existing); // sparse: no lyrics and no offset
        }
        else
        {
            existing.OffsetMs = offsetMs;
        }
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfPodcastTimestampRepository(HathorDbContext db) : IPodcastTimestampRepository
{
    public Task<List<PodcastTimestamp>> ListAsync(Guid userId, string file, CancellationToken ct = default) =>
        db.PodcastTimestamps
            .Where(t => t.UserId == userId && t.PodcastFile == file)
            .OrderBy(t => t.StartSecs)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

    public Task<PodcastTimestamp?> GetAsync(Guid userId, string file, long id, CancellationToken ct = default) =>
        db.PodcastTimestamps
            .FirstOrDefaultAsync(t => t.UserId == userId && t.PodcastFile == file && t.Id == id, ct);

    public async Task AddAsync(PodcastTimestamp timestamp, CancellationToken ct = default) =>
        await db.PodcastTimestamps.AddAsync(timestamp, ct);

    public void Remove(PodcastTimestamp timestamp) => db.PodcastTimestamps.Remove(timestamp);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

public sealed class EfPodcastRecordRepository(HathorDbContext db) : IPodcastRecordRepository
{
    public async Task EnsureAsync(Guid userId, string file, string title,
        string? artist, string? link, CancellationToken ct = default)
    {
        var existing = await db.Podcasts
            .FirstOrDefaultAsync(p => p.UserId == userId && p.File == file, ct);
        if (existing is null)
            await db.Podcasts.AddAsync(new Podcast
            {
                UserId = userId,
                File = file,
                Title = title,
                Artist = artist,
                DownloadedLink = link,
                DateDownloadUtc = DateTime.UtcNow,
            }, ct);
        else
        {
            existing.Title = title;
            existing.Artist = artist;
            existing.DownloadedLink = link;
            existing.DateDownloadUtc = DateTime.UtcNow;
        }
    }

    public async Task<string?> GetDownloadLinkAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var row = await db.Podcasts
            .FirstOrDefaultAsync(p => p.UserId == userId && p.File == file, ct);
        return row?.DownloadedLink;
    }

    public async Task<bool> DeleteCascadeAsync(Guid userId, string file, CancellationToken ct = default)
    {
        var existed = await db.Podcasts.AnyAsync(p => p.UserId == userId && p.File == file, ct);
        await db.PodcastTagLinks
            .Where(l => l.UserId == userId && l.PodcastFile == file)
            .ExecuteDeleteAsync(ct);
        // Timestamps are episode children with no remote-table counterpart:
        // drop them with the episode (no tombstone — desktop sync only
        // understands the podcasts/podcast_tags tables).
        await db.PodcastTimestamps
            .Where(t => t.UserId == userId && t.PodcastFile == file)
            .ExecuteDeleteAsync(ct);
        await db.Podcasts
            .Where(p => p.UserId == userId && p.File == file)
            .ExecuteDeleteAsync(ct);
        return existed;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}

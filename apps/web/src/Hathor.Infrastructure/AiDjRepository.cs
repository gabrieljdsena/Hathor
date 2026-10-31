using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Infrastructure.Repositories;

public sealed class EfAiDjRepository(HathorDbContext db) : IAiDjRepository
{
    public Task<AiDjSessionRecord?> GetSessionAsync(Guid userId, CancellationToken ct = default) =>
        db.AiDjSessions.FirstOrDefaultAsync(s => s.UserId == userId, ct);

    public async Task SaveSessionAsync(AiDjSessionRecord session, CancellationToken ct = default)
    {
        var existing = await db.AiDjSessions.FindAsync([session.UserId], ct);
        if (existing is null)
        {
            await db.AiDjSessions.AddAsync(session, ct);
        }
        else
        {
            existing.SessionId = session.SessionId;
            existing.Status = session.Status;
            existing.StateJson = session.StateJson;
            existing.StartedAtUtc = session.StartedAtUtc;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task StopSessionAsync(Guid userId, DateTime endedAtUtc, CancellationToken ct = default)
    {
        var existing = await db.AiDjSessions.FindAsync([userId], ct);
        if (existing is null) return;
        existing.Status = "stopped";
        existing.UpdatedAtUtc = endedAtUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task AddClipAsync(AiDjClipRecord clip, CancellationToken ct = default)
    {
        await db.AiDjClips.AddAsync(clip, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task<AiDjClipRecord?> GetClipAsync(Guid userId, Guid clipId, CancellationToken ct = default) =>
        db.AiDjClips.FirstOrDefaultAsync(c => c.UserId == userId && c.Id == clipId, ct);

    public async Task AddFeedbackAsync(AiDjFeedbackRecord feedback, CancellationToken ct = default)
    {
        await db.AiDjFeedback.AddAsync(feedback, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AiDjFeedbackRecord>> GetFeedbackAsync(
        Guid userId, Guid sessionId, int limit, CancellationToken ct = default) =>
        await db.AiDjFeedback
            .Where(f => f.UserId == userId && f.SessionId == sessionId)
            .OrderByDescending(f => f.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);

    public async Task<int> DeleteExpiredClipsAsync(DateTime cutoffUtc, CancellationToken ct = default) =>
        await db.AiDjClips
            .Where(c => c.CreatedAtUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct);
}

using System.Text.Json;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Mix;

// Daily mix: generated once per day and cached (desktop get_daily_mix).
// Lazy on read — no scheduler needed for single-server Phase 3 correctness;
// a Hangfire daily pre-warm can be added with the Phase 4 job host.
public sealed record GetDailyMixQuery(Guid UserId) : IRequest<DailyMixDto>;
public sealed record RegenerateDailyMixCommand(Guid UserId) : IRequest<DailyMixDto>;

public sealed class DailyMixService(
    IDailyMixRepository mixes,
    ISongReadModel songs,
    ILibraryStorage storage)
{
    public async Task<DailyMixDto> GetAsync(Guid userId, bool forceRegenerate, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        if (!forceRegenerate)
        {
            var cached = await mixes.GetAsync(userId, today, ct);
            if (cached is not null)
            {
                var files = ParseFiles(cached.SongFilesJson);
                var resolved = await songs.GetManyAsync(userId, files, includeCover: false, ct);
                if (resolved.Count > 0)
                    return new DailyMixDto(today, resolved, true);
                // Stored mix no longer valid (files deleted) — regenerate below.
            }
        }

        var allFiles = storage.ListSongFiles(userId);
        var ranked = await mixes.GetRankedFilesAsync(userId, ct);
        var picked = DailyMixGenerator.Build(allFiles, ranked);

        await mixes.PruneOthersAsync(userId, today, ct);
        await mixes.SaveAsync(userId, today, JsonSerializer.Serialize(picked), ct);
        await mixes.SaveChangesAsync(ct);

        var fresh = await songs.GetManyAsync(userId, picked, includeCover: false, ct);
        return new DailyMixDto(today, fresh, false);
    }

    // Ordered file list behind today's mix (for queue rebuilds).
    public async Task<IReadOnlyList<string>> GetTodayFilesAsync(Guid userId, CancellationToken ct)
    {
        var dto = await GetAsync(userId, forceRegenerate: false, ct);
        return dto.Songs.Select(s => s.File).ToList();
    }

    private static List<string> ParseFiles(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed class GetDailyMixHandler(DailyMixService mix) : IRequestHandler<GetDailyMixQuery, DailyMixDto>
{
    public Task<DailyMixDto> Handle(GetDailyMixQuery q, CancellationToken ct) =>
        mix.GetAsync(q.UserId, forceRegenerate: false, ct);
}

public sealed class RegenerateDailyMixHandler(DailyMixService mix)
    : IRequestHandler<RegenerateDailyMixCommand, DailyMixDto>
{
    public Task<DailyMixDto> Handle(RegenerateDailyMixCommand q, CancellationToken ct) =>
        mix.GetAsync(q.UserId, forceRegenerate: true, ct);
}

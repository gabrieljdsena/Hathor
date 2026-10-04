using System.Text.Json;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Discover;

// Discover: out-of-library recommendations (packages/contracts/discover.md).
// Persistent once-per-day cache (Discover_Cache table, like Daily_Mix): lazy
// recompute on read, forced recompute on refresh. No scheduler — same pattern
// as the Daily Mix (there is no Hangfire in this codebase yet).
public sealed record GetDiscoverQuery(Guid UserId) : IRequest<DiscoverDto>;
public sealed record RegenerateDiscoverCommand(Guid UserId) : IRequest<DiscoverDto>;

public sealed class DiscoverService(
    IDiscoverTasteReadModel taste,
    IITunesClient itunes,
    IDiscoverySuggester suggester,
    IDiscoverCacheRepository cache)
{
    private const int TopArtists = 8;
    private const int PerArtistLimit = 10;
    private const int TrendingLimit = 10;
    private const int LlmSuggestions = 30;

    public async Task<DiscoverDto> GetAsync(Guid userId, bool forceRegenerate, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        if (!forceRegenerate)
        {
            var cached = await cache.GetAsync(userId, today, ct);
            if (cached is not null)
            {
                var items = ParseItems(cached.ItemsJson);
                if (items.Count > 0)
                    return new DiscoverDto(today, items, Cached: true);
                // Stored row no longer valid — recompute below.
            }
        }

        var dto = await ComputeAsync(userId, today, ct);

        await cache.PruneOthersAsync(userId, today, ct);
        await cache.SaveAsync(userId, today,
            JsonSerializer.Serialize(dto.Items), ct);
        await cache.SaveChangesAsync(ct);

        return dto;
    }

    private async Task<DiscoverDto> ComputeAsync(Guid userId, string today, CancellationToken ct)
    {
        var topArtists = await taste.GetTopArtistsAsync(userId, TopArtists, ct);

        var candidates = new List<DiscoverCandidate>();
        foreach (var (artist, _) in topArtists)
        {
            var hits = await itunes.SearchTermAsync(artist, PerArtistLimit, ct);
            candidates.AddRange(hits
                .Where(h => DiscoverRanker.IsSameArtist(h.Artist, artist))
                .Select(h => new DiscoverCandidate(
                    h.Title, h.Artist, h.Album, h.Year, h.Genre, h.ArtworkUrl,
                    DiscoverSources.Artist)));
        }

        foreach (var entry in await itunes.GetTrendingAsync(TrendingLimit, ct))
        {
            var resolved = await itunes.SearchSingleAsync(entry, null, ct);
            if (resolved is not null)
                candidates.Add(new DiscoverCandidate(
                    resolved.Title, resolved.Artist, resolved.Album, resolved.Year,
                    resolved.Genre, resolved.ArtworkUrl, DiscoverSources.Chart));
        }

        // Local-LLM expansion (contract rule 5): suggestions are unverified
        // until iTunes resolves them — hallucinations are dropped here.
        foreach (var (title, artist) in await suggester.SuggestAsync(topArtists, LlmSuggestions, ct))
        {
            var verified = await itunes.SearchSingleAsync(title, artist, ct);
            if (verified is not null)
                candidates.Add(new DiscoverCandidate(
                    verified.Title, verified.Artist, verified.Album, verified.Year,
                    verified.Genre, verified.ArtworkUrl, DiscoverSources.Llm));
        }

        var library = await taste.GetLibraryPairsAsync(userId, ct);
        var inFlight = await taste.GetActiveDownloadPairsAsync(userId, ct);
        var ranked = DiscoverRanker.Rank(topArtists, candidates, library, inFlight);

        return new DiscoverDto(today, ranked.Select(i => new DiscoverItemDto(
            i.Title, i.Artist, i.Album, i.Year, i.Genre, i.ArtworkUrl, i.Source, i.Score)
        ).ToList(), Cached: false);
    }

    private static List<DiscoverItemDto> ParseItems(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DiscoverItemDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed class GetDiscoverHandler(DiscoverService discover)
    : IRequestHandler<GetDiscoverQuery, DiscoverDto>
{
    public Task<DiscoverDto> Handle(GetDiscoverQuery q, CancellationToken ct) =>
        discover.GetAsync(q.UserId, forceRegenerate: false, ct);
}

public sealed class RegenerateDiscoverHandler(DiscoverService discover)
    : IRequestHandler<RegenerateDiscoverCommand, DiscoverDto>
{
    public Task<DiscoverDto> Handle(RegenerateDiscoverCommand q, CancellationToken ct) =>
        discover.GetAsync(q.UserId, forceRegenerate: true, ct);
}

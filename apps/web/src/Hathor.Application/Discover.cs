using System.Collections.Concurrent;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Discover;

// Discover: out-of-library recommendations (packages/contracts/discover.md).
// Phase 1: computed on demand with a 6h in-memory cache (no cache table yet);
// candidates from iTunes artist expansion + RSS chart. LLM expansion and the
// persistent cache + refresh endpoint land in Phases 2-3.
public sealed record GetDiscoverQuery(Guid UserId) : IRequest<DiscoverDto>;

public sealed class DiscoverService(
    IDiscoverTasteReadModel taste, IITunesClient itunes, IDiscoverySuggester suggester)
{
    private const int TopArtists = 8;
    private const int PerArtistLimit = 10;
    private const int TrendingLimit = 10;
    private const int LlmSuggestions = 30;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly ConcurrentDictionary<Guid, (string Date, DiscoverDto Dto, DateTime StoredUtc)> Cache
        = new();

    public async Task<DiscoverDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        if (Cache.TryGetValue(userId, out var hit) &&
            hit.Date == today && DateTime.UtcNow - hit.StoredUtc < CacheTtl)
            return hit.Dto with { Cached = true };

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

        var dto = new DiscoverDto(today, ranked.Select(i => new DiscoverItemDto(
            i.Title, i.Artist, i.Album, i.Year, i.Genre, i.ArtworkUrl, i.Source, i.Score)
        ).ToList(), Cached: false);
        Cache[userId] = (today, dto, DateTime.UtcNow);
        return dto;
    }
}

public sealed class GetDiscoverHandler(DiscoverService discover)
    : IRequestHandler<GetDiscoverQuery, DiscoverDto>
{
    public Task<DiscoverDto> Handle(GetDiscoverQuery q, CancellationToken ct) =>
        discover.GetAsync(q.UserId, ct);
}

using System.Text.Json;
using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Discover;

// Discover: out-of-library recommendations (packages/contracts/discover.md).
// Persistent once-per-day cache (Discover_Cache table, like Daily_Mix): lazy
// recompute on read, forced recompute on refresh. No scheduler — same pattern
// as the Daily Mix (there is no Hangfire in this codebase yet).
//
// PR 1 pipeline: local taste profile -> open-data candidate sources
// (MusicBrainz graph + ListenBrainz public similarity) -> our own feature
// ranker -> iTunes artwork resolver. The local LLM is not part of the
// candidate path anymore; it stays behind IDiscoverySuggester for future
// intent parsing only.
public sealed record GetDiscoverQuery(Guid UserId) : IRequest<DiscoverDto>;
public sealed record RegenerateDiscoverCommand(Guid UserId) : IRequest<DiscoverDto>;
public sealed record GetDiscoverInsightsQuery(Guid UserId, int? WindowDays = null)
    : IRequest<DiscoverInsightsDto>;

public sealed class DiscoverService(
    IDiscoverTasteReadModel taste,
    IEnumerable<ICandidateSource> sources,
    ICandidateResolver resolver,
    IDiscoverCacheRepository cache,
    IDiscoverFeedbackRepository feedback,
    IDiscoverImpressionRepository impressions,
    IAudioSimilarityScorer audio,
    ITrackPopularityService popularity,
    IPopularityProfileBuilder popularityProfile,
    IRankingOptionsProvider rankingOptions,
    DiscoveryOptions options)
{
    public const string ModelVersion = "heuristic-v6";

    public async Task<DiscoverDto> GetAsync(Guid userId, bool forceRegenerate, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var ranking = await rankingOptions.GetAsync(userId, ct);

        if (!forceRegenerate)
        {
            var cached = await cache.GetAsync(userId, today, ct);
            if (cached is not null && cached.ModelVersion == ranking.CacheVersion)
            {
                var items = ParseItems(cached.ItemsJson);
                if (items.Count > 0)
                {
                    // Dismissals/downloads must hide cards immediately, even
                    // when today's response is already cached.
                    var visible = await FilterVisibleAsync(userId, items, ct);
                    // Never strand the page with an empty cached list: if
                    // everything was dismissed/owned, recompute below.
                    if (visible.Count > 0)
                        return new DiscoverDto(today, visible, Cached: true);
                }
                // Empty/old-shaped row — recompute below.
            }
        }

        var dto = await ComputeAsync(userId, today, ranking.Options, ct);

        await cache.PruneOthersAsync(userId, today, ct);
        await cache.SaveAsync(userId, today, ranking.CacheVersion,
            JsonSerializer.Serialize(dto.Items), ct);
        await cache.SaveChangesAsync(ct);

        return dto;
    }

    private async Task<DiscoverDto> ComputeAsync(
        Guid userId, string today, DiscoverRankingOptions rankingOptions,
        CancellationToken ct)
    {
        // Overall budget keeps a slow MusicBrainz/ListenBrainz day from
        // hanging the read endpoint; partial candidates still rank.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeBudgetSec, 5, 300)));

        var profile = await BuildTasteProfileAsync(userId, ct);
        var signals = await LoadFeedbackAsync(userId, ct);

        var candidates = new List<DiscoverCandidate>();
        var primary = sources.Where(s => !s.IsFallback).ToList();
        var fallback = sources.Where(s => s.IsFallback).ToList();

        foreach (var source in primary)
        {
            if (budget.IsCancellationRequested) break;
            await CollectAsync(source, profile, candidates, budget.Token, ct);
        }

        // CLAP content similarity is optional and best-effort: no cached
        // embeddings/model means candidates keep their metadata-only score.
        try
        {
            candidates = (await audio.ScoreAsync(userId, profile, candidates, budget.Token)).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Time budget hit — rank metadata-only candidates.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Audio sidecar failure must never break Discover.
        }

        // Historical popularity band + global counts for every candidate
        // source (not just LB similar recordings). Best-effort.
        PopularityProfile? popularityModel = null;
        try
        {
            popularityModel = await popularityProfile.BuildAsync(
                userId, profile.Tracks, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Time budget hit — use the absolute popularity band.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Popularity provider failure must not break Discover.
        }

        try
        {
            var mbids = candidates
                .Select(c => c.Mbid)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (mbids.Count > 0)
            {
                var counts = await popularity.GetAsync(mbids, budget.Token);
                if (counts.Count > 0)
                {
                    candidates = candidates.Select(c =>
                        c.Mbid is not null && counts.TryGetValue(c.Mbid, out var count)
                            ? c with { Popularity = count }
                            : c).ToList();
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Time budget hit — rank with existing popularity values.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Popularity provider failure must not break Discover.
        }

        // Popularity floor: drop open-data obscurities when enough known
        // candidates remain. Fallback artist/chart picks are exempt.
        var effectiveFloor = EffectivePopularityFloor(popularityModel);
        var rankingForRun = rankingOptions with
        {
            PopularityFloor = options.PopularityFilterEnabled ? effectiveFloor : 0,
        };
        if (options.PopularityFilterEnabled && candidates.Count > 0)
        {
            candidates = candidates
                .Where(c => KeepByPopularity(c, effectiveFloor))
                .ToList();
        }

        // Charts/same-artist expansion are cold-start fallbacks only. Run
        // them when the primary pool is small after popularity filtering.
        var fallbacksRan = false;
        if (profile.IsEmpty || candidates.Count < options.FallbackThreshold)
        {
            fallbacksRan = true;
            foreach (var source in fallback)
            {
                if (budget.IsCancellationRequested) break;
                await CollectAsync(source, profile, candidates, budget.Token, ct);
            }
        }

        var library = await taste.GetLibraryPairsAsync(userId, ct);
        var inFlight = await taste.GetActiveDownloadPairsAsync(userId, ct);
        var ranked = DiscoverRanker.Rank(
            profile, candidates, library, inFlight, dismissed: null,
            feedback: signals, popularity: popularityModel,
            rankingForRun);

        // The open-data pool can be entirely owned. Give the fallback a
        // chance before returning an empty Discover.
        if (ranked.Count == 0 && !fallbacksRan && fallback.Count > 0)
        {
            foreach (var source in fallback)
            {
                if (budget.IsCancellationRequested) break;
                await CollectAsync(source, profile, candidates, budget.Token, ct);
            }
            ranked = DiscoverRanker.Rank(
                profile, candidates, library, inFlight, dismissed: null,
                feedback: signals, popularity: popularityModel,
                rankingForRun);
        }

        // Snapshot the pre-resolution ranking (features + order) for offline
        // evaluation; artwork resolution does not change ranking identity.
        await SaveImpressionAsync(userId, today, ranked, ct);

        var items = await ResolveAsync(ranked, budget.Token);
        return new DiscoverDto(today, items, Cached: false);
    }

    private async Task SaveImpressionAsync(
        Guid userId, string date, IReadOnlyList<RankedDiscoverItem> ranked,
        CancellationToken ct)
    {
        var snapshot = ranked.Select(item => new DiscoverImpressionItem(
            item.Title,
            item.Artist,
            item.Mbid,
            item.Source,
            item.SeedArtist,
            item.Tags,
            item.Score,
            item.Features ?? new DiscoverItemFeatures(
                0, 0.5, 0, 0.5, 0.5, 0, 0, 0, 0, 0, item.Score, 0, item.Score)))
            .ToList();

        await impressions.SaveAsync(userId, date, ModelVersion,
            JsonSerializer.Serialize(snapshot), ct);
        await impressions.SaveChangesAsync(ct);
    }

    private long EffectivePopularityFloor(PopularityProfile? profile)
    {
        var floor = Math.Max(1, options.PopularityFloor);
        if (profile is not null)
        {
            // Respect a niche but coherent taste profile: never filter much
            // below the user's own median popularity.
            var relative = (long)Math.Max(1,
                Math.Pow(10, profile.MedianLog -
                    Math.Max(0, options.PopularityFloorProfileMargin)));
            floor = Math.Min(floor, relative);
        }
        return Math.Max(1, floor);
    }

    private static bool KeepByPopularity(DiscoverCandidate candidate, long floor)
    {
        // Same-artist and chart/popular fallback items are not "unknown
        // artists"; the floor targets open-data expansion candidates.
        if (candidate.Source is DiscoverSources.Artist or DiscoverSources.Chart
            or DiscoverSources.Popular or DiscoverSources.Llm)
            return true;
        return candidate.Popularity >= floor;
    }

    private async Task CollectAsync(
        ICandidateSource source, TasteProfile profile,
        List<DiscoverCandidate> candidates, CancellationToken budget, CancellationToken ct)
    {
        try
        {
            var batch = await source.GetCandidatesAsync(profile, options.CandidateLimit, budget);
            if (batch.Count > 0) candidates.AddRange(batch);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Time budget hit — keep candidates from earlier sources.
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // A provider outage must never break Discover; other sources
            // (or the fallback) still contribute candidates.
        }
    }

    private async Task<TasteProfile> BuildTasteProfileAsync(Guid userId, CancellationToken ct)
    {
        var artists = await taste.GetTopArtistsAsync(userId, options.TopArtists, ct);
        var tracks = await taste.GetTopTracksAsync(userId, options.TopTracks, ct);
        var tags = await taste.GetTopTagsAsync(userId, options.TopTags, ct);
        var files = await taste.GetTopPlayedFilesAsync(userId, options.TopTracks, ct);
        return new TasteProfile(artists, tracks, tags, files);
    }

    private async Task<List<DiscoverItemDto>> ResolveAsync(
        IReadOnlyList<RankedDiscoverItem> ranked, CancellationToken ct)
    {
        var resolved = await Task.WhenAll(ranked.Select(async item =>
        {
            var candidate = new DiscoverCandidate(
                item.Title, item.Artist, item.Album, item.Year, item.Genre,
                item.ArtworkUrl, item.Source, item.Mbid, SeedArtist: null);
            try
            {
                return await resolver.ResolveAsync(candidate, ct);
            }
            catch (Exception)
            {
                // Budget/provider failure: keep the ranked item without
                // artwork rather than failing the whole response.
                return candidate;
            }
        }));

        return ranked.Select((item, i) =>
        {
            var c = resolved[i];
            return new DiscoverItemDto(
                c.Title, c.Artist, c.Album, c.Year, c.Genre, c.ArtworkUrl,
                item.Source, item.Score, item.Mbid ?? c.Mbid, item.Reason,
                item.SeedArtist);
        }).ToList();
    }

    private async Task<List<DiscoverItemDto>> FilterVisibleAsync(
        Guid userId, IReadOnlyList<DiscoverItemDto> items, CancellationToken ct)
    {
        var signals = await LoadFeedbackAsync(userId, ct);
        var hidden = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (title, artist) in await taste.GetLibraryPairsAsync(userId, ct))
            hidden.Add(MediaKeys.Key(title, artist));
        foreach (var (title, artist) in await taste.GetActiveDownloadPairsAsync(userId, ct))
            hidden.Add(MediaKeys.Key(title, artist));

        return items.Where(item =>
        {
            var key = MediaKeys.Key(item.Title, item.Artist);
            if (hidden.Contains(key) || signals.DismissedKeys.Contains(key))
                return false;
            return !signals.DismissedArtists.Contains(MediaKeys.Normalize(item.Artist));
        }).ToList();
    }

    private async Task<DiscoverFeedbackSignals> LoadFeedbackAsync(
        Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lookback = Math.Max(
            Math.Clamp(options.FeedbackDays, 1, 3650),
            Math.Clamp(options.DismissDays, 1, 3650));
        var feedbackSince = now.AddDays(-lookback);
        var dismissSince = now.AddDays(-Math.Clamp(options.DismissDays, 1, 3650));

        var rows = await feedback.GetSinceAsync(userId, feedbackSince, 2000, ct);
        if (rows.Count == 0) return DiscoverFeedbackSignals.Empty;

        var dismissedKeys = new HashSet<string>(StringComparer.Ordinal);
        var dismissedArtists = new HashSet<string>(StringComparer.Ordinal);
        var likedArtists = new HashSet<string>(StringComparer.Ordinal);
        var likedSeeds = new HashSet<string>(StringComparer.Ordinal);
        var engagedArtists = new HashSet<string>(StringComparer.Ordinal);
        var sourceOutcomes = new Dictionary<string, (int Pos, int Neg)>(
            StringComparer.OrdinalIgnoreCase);
        var artistOutcomes = new Dictionary<string, (int Pos, int Neg)>(
            StringComparer.Ordinal);
        var seedOutcomes = new Dictionary<string, (int Pos, int Neg)>(
            StringComparer.Ordinal);

        void Outcome(Dictionary<string, (int Pos, int Neg)> map, string key, bool positive)
        {
            if (key.Length == 0) return;
            map.TryGetValue(key, out var current);
            map[key] = positive
                ? (current.Pos + 1, current.Neg)
                : (current.Pos, current.Neg + 1);
        }

        foreach (var row in rows)
        {
            var key = string.IsNullOrWhiteSpace(row.ItemKey)
                ? MediaKeys.Key(row.Title, row.Artist)
                : row.ItemKey;
            var artistKey = MediaKeys.Normalize(row.Artist);
            var seedKey = string.IsNullOrWhiteSpace(row.SeedArtist)
                ? ""
                : MediaKeys.Normalize(row.SeedArtist);
            var source = string.IsNullOrWhiteSpace(row.Source) ? "unknown" : row.Source;

            switch (row.Event)
            {
                case DiscoverFeedbackEvents.Dismiss:
                    Outcome(sourceOutcomes, source, positive: false);
                    Outcome(artistOutcomes, artistKey, positive: false);
                    Outcome(seedOutcomes, seedKey, positive: false);
                    if (row.CreatedAtUtc >= dismissSince)
                    {
                        dismissedKeys.Add(key);
                        if (artistKey.Length > 0) dismissedArtists.Add(artistKey);
                    }
                    break;
                case DiscoverFeedbackEvents.Download:
                    Outcome(sourceOutcomes, source, positive: true);
                    Outcome(artistOutcomes, artistKey, positive: true);
                    Outcome(seedOutcomes, seedKey, positive: true);
                    if (artistKey.Length > 0) likedArtists.Add(artistKey);
                    if (seedKey.Length > 0) likedSeeds.Add(seedKey);
                    break;
                case DiscoverFeedbackEvents.Preview:
                case DiscoverFeedbackEvents.Click:
                    if (artistKey.Length > 0) engagedArtists.Add(artistKey);
                    break;
            }
        }

        var sourcePriors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, (pos, neg)) in sourceOutcomes)
            sourcePriors[source] = (pos + 1.0) / (pos + neg + 2.0);

        var artistBoosts = BuildBoosts(artistOutcomes);
        var seedBoosts = BuildBoosts(seedOutcomes);

        return new DiscoverFeedbackSignals(
            dismissedKeys, dismissedArtists, likedArtists, likedSeeds, engagedArtists,
            sourcePriors, artistBoosts, seedBoosts);
    }

    private static Dictionary<string, double> BuildBoosts(
        Dictionary<string, (int Pos, int Neg)> outcomes)
    {
        var boosts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, (pos, neg)) in outcomes)
        {
            // Small samples are noisy; wait for at least two outcomes.
            if (pos + neg < 2) continue;
            var rate = (pos + 1.0) / (pos + neg + 2.0);
            boosts[key] = Math.Clamp((rate - 0.5) * 0.2, -0.10, 0.10);
        }
        return boosts;
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

// Feedback is user-scoped implicit signal, never a shared/global model.
public sealed record RecordDiscoverFeedbackCommand(
    Guid UserId,
    string Title,
    string Artist,
    string? Mbid,
    string Source,
    string Event,
    string? SeedArtist,
    double? Score) : IRequest<bool>;

public sealed class RecordDiscoverFeedbackValidator
    : AbstractValidator<RecordDiscoverFeedbackCommand>
{
    public RecordDiscoverFeedbackValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Artist).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Source).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Event)
            .NotEmpty()
            .Must(e => DiscoverFeedbackEvents.IsValid(e?.Trim().ToLowerInvariant()))
            .WithMessage("Event must be dismiss, preview, click or download.");
        RuleFor(x => x.Mbid).MaximumLength(64);
        RuleFor(x => x.SeedArtist).MaximumLength(255);
        RuleFor(x => x.Score).InclusiveBetween(0, 1).When(x => x.Score.HasValue);
    }
}

// Evaluation surface: local feedback vs playback quality, by source. This is
// also where the ranker's learned priors come from, so the numbers match the
// behavior users actually get.
public sealed class GetDiscoverInsightsHandler(
    IDiscoverFeedbackRepository feedback,
    IPlaybackEventRepository playback,
    DiscoveryOptions options) : IRequestHandler<GetDiscoverInsightsQuery, DiscoverInsightsDto>
{
    public async Task<DiscoverInsightsDto> Handle(
        GetDiscoverInsightsQuery q, CancellationToken ct)
    {
        var window = Math.Clamp(q.WindowDays ?? options.FeedbackDays, 1, 365);
        var since = DateTime.UtcNow.AddDays(-window);

        var rows = await feedback.GetSinceAsync(q.UserId, since, 5000, ct);
        var playbackSummary = await playback.GetSummaryAsync(q.UserId, since, ct);
        var facts = rows
            .Select(r => new DiscoverOutcomeFact(r.Source, r.Event, r.Artist))
            .ToList();
        var insight = DiscoverInsightCalculator.Build(
            facts, window, playbackSummary.Completed, playbackSummary.Skipped);

        return new DiscoverInsightsDto(
            insight.WindowDays,
            insight.Downloads,
            insight.Dismissals,
            insight.Previews,
            insight.Clicks,
            insight.ConversionRate,
            insight.CompletedPlays,
            insight.SkippedPlays,
            insight.PlaybackCompletionRate,
            insight.UniqueArtists,
            insight.Sources.Select(s => new DiscoverSourceInsightDto(
                s.Source, s.Downloads, s.Dismissals, s.Previews, s.Clicks,
                s.ConversionRate, s.LearnedPrior)).ToList());
    }
}

public sealed class RecordDiscoverFeedbackHandler(IDiscoverFeedbackRepository feedback)
    : IRequestHandler<RecordDiscoverFeedbackCommand, bool>
{
    public async Task<bool> Handle(RecordDiscoverFeedbackCommand cmd, CancellationToken ct)
    {
        var title = cmd.Title.Trim();
        var artist = cmd.Artist.Trim();
        await feedback.AddAsync(new DiscoverFeedback
        {
            UserId = cmd.UserId,
            ItemKey = MediaKeys.Key(title, artist),
            Mbid = string.IsNullOrWhiteSpace(cmd.Mbid) ? null : cmd.Mbid.Trim(),
            Title = title,
            Artist = artist,
            Source = cmd.Source.Trim(),
            Event = cmd.Event.Trim().ToLowerInvariant(),
            SeedArtist = string.IsNullOrWhiteSpace(cmd.SeedArtist) ? null : cmd.SeedArtist.Trim(),
            Score = cmd.Score,
            CreatedAtUtc = DateTime.UtcNow,
        }, ct);
        await feedback.SaveChangesAsync(ct);
        return true;
    }
}

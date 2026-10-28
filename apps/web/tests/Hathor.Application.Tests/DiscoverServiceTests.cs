using System.Text.Json;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class DiscoverServiceTests
{
    private static Guid NewUserId() => Guid.NewGuid();

    private static TasteArtist Artist(string name, double weight = 1, long plays = 10) =>
        new(name, plays, weight);

    private static DiscoverItemDto Item(string title, string artist, string source) =>
        new(title, artist, "Album", "2020", "Rock", "http://art", source, 0.9);

    private static DiscoverCandidate Cand(
        string title, string artist, string source = DiscoverSources.Similar,
        string? seed = null) =>
        new(title, artist, "Album", "2020", "Rock", "", source,
            Mbid: $"mb-{title}", SeedArtist: seed, Similarity: 0.9);

    private static (IDiscoverTasteReadModel Taste, IDiscoverCacheRepository Cache) Fakes(
        Guid userId,
        IReadOnlyList<TasteArtist>? artists = null,
        IReadOnlyList<TasteTrack>? tracks = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<(string Title, string Artist)>? library = null,
        string? cachedJson = null,
        string? cachedModelVersion = null,
        IReadOnlyList<TasteFile>? files = null)
    {
        var t = Substitute.For<IDiscoverTasteReadModel>();
        t.GetTopArtistsAsync(userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(artists ?? []);
        t.GetTopTracksAsync(userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(tracks ?? []);
        t.GetTopTagsAsync(userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(tags ?? []);
        t.GetTopPlayedFilesAsync(userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(files ?? []);
        t.GetLibraryPairsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(library ?? []);
        t.GetActiveDownloadPairsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var c = Substitute.For<IDiscoverCacheRepository>();
        c.GetAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedJson is null ? null : new Hathor.Domain.Entities.DiscoverCache
            {
                UserId = userId,
                Date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                ModelVersion = cachedModelVersion ?? DiscoverService.ModelVersion,
                ItemsJson = cachedJson,
                CreatedAtUtc = DateTime.UtcNow,
            });
        return (t, c);
    }

    private sealed class FakeSource(
        string name, bool fallback, IReadOnlyList<DiscoverCandidate> items,
        Exception? error = null) : ICandidateSource
    {
        public string Name => name;
        public bool IsFallback => fallback;
        public bool Called { get; private set; }

        public Task<IReadOnlyList<DiscoverCandidate>> GetCandidatesAsync(
            TasteProfile taste, int limit, CancellationToken ct = default)
        {
            Called = true;
            if (error is not null) throw error;
            return Task.FromResult(items);
        }
    }

    private sealed class NoAudioScorer : IAudioSimilarityScorer
    {
        public Task<IReadOnlyList<DiscoverCandidate>> ScoreAsync(
            Guid userId, TasteProfile taste, IReadOnlyList<DiscoverCandidate> candidates,
            CancellationToken ct = default) =>
            Task.FromResult(candidates);
    }

    private sealed class NoPopularityService : ITrackPopularityService
    {
        public Task<IReadOnlyDictionary<string, long>> GetAsync(
            IReadOnlyList<string> recordingMbids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(
                new Dictionary<string, long>());
    }

    private sealed class NoPopularityProfile : IPopularityProfileBuilder
    {
        public Task<PopularityProfile?> BuildAsync(
            Guid userId, IReadOnlyList<TasteTrack> tracks, CancellationToken ct = default) =>
            Task.FromResult<PopularityProfile?>(null);
    }

    private sealed class DefaultRankingOptions(DiscoveryOptions options)
        : IRankingOptionsProvider
    {
        public Task<RankingOptionsSnapshot> GetAsync(
            Guid userId, CancellationToken ct = default) =>
            Task.FromResult(new RankingOptionsSnapshot(
                options.ToRankingOptions(), DiscoverService.ModelVersion, null));
    }

    private sealed class FakeResolver : ICandidateResolver
    {
        public Task<DiscoverCandidate> ResolveAsync(
            DiscoverCandidate candidate, CancellationToken ct = default) =>
            Task.FromResult(candidate with
            {
                ArtworkUrl = string.IsNullOrWhiteSpace(candidate.ArtworkUrl)
                    ? "http://resolved-art"
                    : candidate.ArtworkUrl,
            });
    }

    private sealed class FakeImpressionRepo : IDiscoverImpressionRepository
    {
        public List<(Guid UserId, string Date, string Model, string Json)> Saved { get; } = [];
        private readonly List<DiscoverImpression> _rows = [];

        public Task SaveAsync(Guid userId, string date, string modelVersion,
            string itemsJson, CancellationToken ct = default)
        {
            Saved.Add((userId, date, modelVersion, itemsJson));
            _rows.RemoveAll(r => r.UserId == userId && r.Date == date &&
                r.ModelVersion == modelVersion);
            _rows.Add(new DiscoverImpression
            {
                UserId = userId,
                Date = date,
                ModelVersion = modelVersion,
                ItemsJson = itemsJson,
                CreatedAtUtc = DateTime.UtcNow,
            });
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DiscoverImpression>> GetSinceAsync(
            Guid userId, string sinceDate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoverImpression>>(
                _rows.Where(r => r.UserId == userId &&
                    string.CompareOrdinal(r.Date, sinceDate) >= 0).ToList());

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeFeedbackRepo : IDiscoverFeedbackRepository
    {
        public List<DiscoverFeedback> Rows { get; } = [];

        public Task AddAsync(DiscoverFeedback feedback, CancellationToken ct = default)
        {
            Rows.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DiscoverFeedback>> GetSinceAsync(
            Guid userId, DateTime sinceUtc, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoverFeedback>>(
                Rows.Where(r => r.UserId == userId && r.CreatedAtUtc >= sinceUtc)
                    .Take(limit).ToList());

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static DiscoverFeedback Feedback(
        Guid userId, string title, string artist, string @event,
        string? seed = null, DateTime? at = null, string source = DiscoverSources.Similar) =>
        new()
        {
            UserId = userId,
            ItemKey = MediaKeys.Key(title, artist),
            Title = title,
            Artist = artist,
            Source = source,
            Event = @event,
            SeedArtist = seed,
            CreatedAtUtc = at ?? DateTime.UtcNow,
        };

    private static DiscoveryOptions Options(int fallbackThreshold = 1) => new()
    {
        TimeBudgetSec = 5,
        FallbackThreshold = fallbackThreshold,
    };

    private static DiscoverService Service(
        IDiscoverTasteReadModel taste,
        IEnumerable<ICandidateSource> sources,
        IDiscoverCacheRepository cache,
        IDiscoverFeedbackRepository? feedback = null,
        IDiscoverImpressionRepository? impressions = null,
        IAudioSimilarityScorer? audio = null,
        ITrackPopularityService? popularity = null,
        IPopularityProfileBuilder? popularityProfile = null,
        IRankingOptionsProvider? ranking = null,
        DiscoveryOptions? options = null)
    {
        var opts = options ?? Options();
        return new(taste, sources, new FakeResolver(), cache,
            feedback ?? new FakeFeedbackRepo(),
            impressions ?? new FakeImpressionRepo(),
            audio ?? new NoAudioScorer(),
            popularity ?? new NoPopularityService(),
            popularityProfile ?? new NoPopularityProfile(),
            ranking ?? new DefaultRankingOptions(opts),
            opts);
    }

    [Fact]
    public async Task GetAsync_RanksPrimarySource_AndResolvesArtwork()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)]);
        var source = new FakeSource("open", false,
            [Cand("Midnight City", "M83", seed: "M83")]);

        var dto = await Service(taste, [source], cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeFalse();
        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Midnight City");
        dto.Items[0].Source.Should().Be(DiscoverSources.Similar);
        dto.Items[0].Mbid.Should().Be("mb-Midnight City");
        dto.Items[0].Reason.Should().Be("Because you play M83");
        dto.Items[0].ArtworkUrl.Should().Be("http://resolved-art");
        await cache.Received(1).SaveAsync(
            userId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_EmptyHistory_UsesFallbackSource()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId);
        var primary = new FakeSource("open", false, []);
        var fallback = new FakeSource("fallback", true,
            [Cand("Chart Song", "Artist", DiscoverSources.Chart)]);

        var dto = await Service(taste, [primary, fallback], cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Source.Should().Be(DiscoverSources.Chart);
        fallback.Called.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_FiltersOwnedLibrary()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)],
            library: [("Midnight City", "M83")]);
        var source = new FakeSource("open", false, [Cand("Midnight City", "M83")]);

        var dto = await Service(taste, [source], cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_TableCacheHit_ReturnsCachedWithoutSources()
    {
        var userId = NewUserId();
        var stored = JsonSerializer.Serialize(new List<DiscoverItemDto>
        {
            Item("Midnight City", "M83", DiscoverSources.Similar),
        });
        var (taste, cache) = Fakes(userId, cachedJson: stored);
        var source = new FakeSource("open", false, []);

        var dto = await Service(taste, [source], cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeTrue();
        dto.Items.Should().HaveCount(1);
        source.Called.Should().BeFalse();
        await cache.DidNotReceive().SaveAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ForceRegenerate_RecomputesDespiteCache()
    {
        var userId = NewUserId();
        var stored = JsonSerializer.Serialize(new List<DiscoverItemDto>
        {
            Item("Stale Song", "Stale Artist", DiscoverSources.Chart),
        });
        var (taste, cache) = Fakes(userId, [Artist("M83", 7)], cachedJson: stored);
        var source = new FakeSource("open", false, [Cand("Midnight City", "M83")]);

        var dto = await Service(taste, [source], cache)
            .GetAsync(userId, forceRegenerate: true, CancellationToken.None);

        dto.Cached.Should().BeFalse();
        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Midnight City");
    }

    [Fact]
    public async Task GetAsync_PrimarySourceFailure_FallsBack()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 7)]);
        var primary = new FakeSource("open", false, [],
            error: new HttpRequestException("provider down"));
        var fallback = new FakeSource("fallback", true,
            [Cand("Afterglow", "M83", DiscoverSources.Artist, seed: "M83")]);

        var dto = await Service(taste, [primary, fallback], cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Afterglow");
    }

    [Fact]
    public async Task GetAsync_CachedAllFiltered_RecomputesWithFreshItems()
    {
        var userId = NewUserId();
        var stored = JsonSerializer.Serialize(new List<DiscoverItemDto>
        {
            Item("Midnight City", "M83", DiscoverSources.Similar),
            Item("Alison", "Slowdive", DiscoverSources.Similar),
        });
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)], cachedJson: stored);
        var feedback = new FakeFeedbackRepo();
        feedback.Rows.Add(Feedback(userId, "Midnight City", "M83",
            DiscoverFeedbackEvents.Dismiss));
        feedback.Rows.Add(Feedback(userId, "Any Song", "Slowdive",
            DiscoverFeedbackEvents.Dismiss));
        var source = new FakeSource("open", false,
            [Cand("Afterglow", "Fresh Artist", seed: "M83")]);

        var dto = await Service(taste, [source], cache, feedback)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeFalse();
        dto.Items.Should().ContainSingle(i => i.Title == "Afterglow");
        dto.Items.Should().NotContain(i => i.Title == "Midnight City");
    }

    [Fact]
    public async Task GetAsync_OldCacheModelVersion_Recomputes()
    {
        var userId = NewUserId();
        var stored = JsonSerializer.Serialize(new List<DiscoverItemDto>
        {
            Item("Old Algorithm Song", "Old Artist", DiscoverSources.Llm),
        });
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)],
            cachedJson: stored, cachedModelVersion: "pre-pr1");
        var source = new FakeSource("open", false,
            [Cand("Fresh Song", "M83", seed: "M83")]);

        var dto = await Service(taste, [source], cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeFalse();
        dto.Items.Should().ContainSingle(i => i.Title == "Fresh Song");
        dto.Items.Should().NotContain(i => i.Title == "Old Algorithm Song");
    }

    [Fact]
    public async Task GetAsync_FeedbackDismiss_ExcludesCandidateBeforeRanking()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)]);
        var source = new FakeSource("open", false,
        [
            Cand("Midnight City", "M83", seed: "M83"),
            Cand("Alison", "Slowdive", seed: "M83"),
        ]);
        var feedback = new FakeFeedbackRepo();
        feedback.Rows.Add(Feedback(userId, "Midnight City", "M83",
            DiscoverFeedbackEvents.Dismiss));

        var dto = await Service(taste, [source], cache, feedback)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().ContainSingle(i => i.Title == "Alison");
        dto.Items.Should().NotContain(i => i.Title == "Midnight City");
    }

    [Fact]
    public async Task GetAsync_DownloadFeedback_BoostsLikedSeed()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)]);
        var source = new FakeSource("open", false,
        [
            Cand("Other Song", "Other Artist", seed: "Other"),
            Cand("Alison", "Slowdive", seed: "M83"),
        ]);
        var feedback = new FakeFeedbackRepo();
        feedback.Rows.Add(Feedback(userId, "Midnight City", "M83",
            DiscoverFeedbackEvents.Download, seed: "M83"));

        var dto = await Service(taste, [source], cache, feedback)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items[0].Title.Should().Be("Alison");
    }

    [Fact]
    public async Task GetAsync_WritesEvaluationSnapshot()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)]);
        var source = new FakeSource("open", false,
            [Cand("Midnight City", "M83", seed: "M83")]);
        var impressions = new FakeImpressionRepo();

        await Service(taste, [source], cache, impressions: impressions)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        impressions.Saved.Should().HaveCount(1);
        impressions.Saved[0].Model.Should().Be(DiscoverService.ModelVersion);
        var items = JsonSerializer.Deserialize<List<DiscoverImpressionItem>>(
            impressions.Saved[0].Json);
        items.Should().ContainSingle();
        items![0].Features.Should().NotBeNull();
        items[0].Features.BaseScore.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetAsync_HistoricalPopularity_ReordersCandidates()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)]);
        var source = new FakeSource("open", false,
        [
            Cand("Mainstream", "Artist A"),
            Cand("In Band", "Artist B"),
        ]);
        var popularity = Substitute.For<ITrackPopularityService>();
        popularity.GetAsync(Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long>
            {
                ["mb-Mainstream"] = 50_000_000,
                ["mb-In Band"] = 100_000,
            });
        var profile = Substitute.For<IPopularityProfileBuilder>();
        profile.BuildAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<TasteTrack>>(),
                Arg.Any<CancellationToken>())
            .Returns(new PopularityProfile(5.0, 5));

        var dto = await Service(taste, [source], cache,
                popularity: popularity, popularityProfile: profile)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items[0].Title.Should().Be("In Band");
    }

    [Fact]
    public async Task GetAsync_LearnedSourcePrior_ReordersCandidates()
    {
        var userId = NewUserId();
        var (taste, cache) = Fakes(userId, [Artist("M83", 10)]);
        var source = new FakeSource("open", false,
        [
            Cand("Graph Song", "A", DiscoverSources.Graph),
            Cand("Tag Song", "B", DiscoverSources.Tag),
        ]);
        var feedback = new FakeFeedbackRepo();
        for (var i = 0; i < 3; i++)
            feedback.Rows.Add(Feedback(userId, $"Tag {i}", $"Tag Artist {i}",
                DiscoverFeedbackEvents.Download, source: DiscoverSources.Tag));
        for (var i = 0; i < 3; i++)
            feedback.Rows.Add(Feedback(userId, $"Graph {i}", $"Graph Artist {i}",
                DiscoverFeedbackEvents.Dismiss, source: DiscoverSources.Graph));

        var dto = await Service(taste, [source], cache, feedback)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items[0].Title.Should().Be("Tag Song");
    }

    [Fact]
    public async Task Insights_ComputesSourceAndPlaybackSummary()
    {
        var userId = NewUserId();
        var feedback = new FakeFeedbackRepo();
        feedback.Rows.Add(Feedback(userId, "Downloaded", "M83",
            DiscoverFeedbackEvents.Download, source: DiscoverSources.Similar));
        feedback.Rows.Add(Feedback(userId, "Dismissed", "Slowdive",
            DiscoverFeedbackEvents.Dismiss, source: DiscoverSources.Graph));
        var playback = Substitute.For<IPlaybackEventRepository>();
        playback.GetSummaryAsync(userId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new PlaybackSummary(10, 8, 2));

        var handler = new GetDiscoverInsightsHandler(
            feedback, playback, new DiscoveryOptions());
        var dto = await handler.Handle(
            new GetDiscoverInsightsQuery(userId, 30), CancellationToken.None);

        dto.WindowDays.Should().Be(30);
        dto.Downloads.Should().Be(1);
        dto.Dismissals.Should().Be(1);
        dto.CompletedPlays.Should().Be(8);
        dto.SkippedPlays.Should().Be(2);
        dto.PlaybackCompletionRate.Should().BeApproximately(0.8, 0.001);
        dto.Sources.Should().Contain(s =>
            s.Source == DiscoverSources.Similar && s.Downloads == 1);
    }

    [Fact]
    public async Task RecordFeedback_PersistsNormalizedSignal()
    {
        var userId = NewUserId();
        var repo = new FakeFeedbackRepo();
        var handler = new RecordDiscoverFeedbackHandler(repo);

        var ok = await handler.Handle(new RecordDiscoverFeedbackCommand(
            userId, "  Midnight City ", " M83 ", null, "similar",
            "DISMISS", " M83 ", 0.8), CancellationToken.None);

        ok.Should().BeTrue();
        repo.Rows.Should().HaveCount(1);
        var row = repo.Rows[0];
        row.UserId.Should().Be(userId);
        row.Title.Should().Be("Midnight City");
        row.Artist.Should().Be("M83");
        row.Event.Should().Be(DiscoverFeedbackEvents.Dismiss);
        row.ItemKey.Should().Be(MediaKeys.Key("Midnight City", "M83"));
    }

    [Fact]
    public void FeedbackValidator_RejectsUnknownEvent()
    {
        var validator = new RecordDiscoverFeedbackValidator();
        var result = validator.Validate(new RecordDiscoverFeedbackCommand(
            NewUserId(), "Song", "Artist", null, "similar",
            "promote", null, null));

        result.IsValid.Should().BeFalse();
    }
}

using System.Net;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using Hathor.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class OpenDataClientTests
{
    private static DiscoveryOptions Options() => new()
    {
        SeedArtistLimit = 2,
        SimilarArtistLimit = 3,
        SimilarArtistsToBrowse = 2,
        RelatedArtistsToBrowse = 1,
        RecordingsPerArtist = 2,
        SimilarRecordingSeeds = 1,
        SimilarRecordingsPerSeed = 2,
        TagRecordingsLimit = 2,
        TopTags = 2,
        CandidateLimit = 50,
    };

    private sealed class FakeCache : IRecommendationCache
    {
        public Dictionary<string, object> Values { get; } = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            where T : class =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value as T : null);

        public Task SetAsync<T>(string key, T value, TimeSpan ttl,
            CancellationToken ct = default) where T : class
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class JsonHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("down"));
    }

    private static IHttpClientFactory Factory(string client, HttpMessageHandler handler,
        string baseUrl) =>
        Factory(client, new HttpClient(handler) { BaseAddress = new Uri(baseUrl) });

    private static IHttpClientFactory Factory(string client, HttpClient http)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(client).Returns(http);
        return factory;
    }

    [Fact]
    public async Task MusicBrainz_SearchArtist_ParsesAndCaches()
    {
        const string json = """
            {"artists":[{"id":"a1","name":"Slowdive","score":"100",
              "tags":[{"name":"shoegaze"},{"name":"dream pop"}]}]}
            """;
        var handler = new JsonHandler(json);
        var cache = new FakeCache();
        var client = new MusicBrainzClient(
            Factory("musicbrainz", handler, "https://musicbrainz.org/ws/2/"),
            cache, new MusicBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<MusicBrainzClient>>());

        var first = await client.SearchArtistAsync("Slowdive", CancellationToken.None);
        var second = await client.SearchArtistAsync("Slowdive", CancellationToken.None);

        first.Should().NotBeNull();
        first!.Mbid.Should().Be("a1");
        first.Name.Should().Be("Slowdive");
        first.Tags.Should().Contain("shoegaze");
        second.Should().BeEquivalentTo(first);
        handler.Calls.Should().Be(1);
        handler.Seen!.RequestUri!.ToString().Should().Contain("/artist?query=");
        handler.Seen.Headers.UserAgent.ToString().Should().Contain("Hathor/1.0");
    }

    [Fact]
    public async Task MusicBrainz_GetArtistRecordings_ParsesMetadata()
    {
        const string json = """
            {"recordings":[{"id":"r1","title":"Alison",
              "artist-credit":[{"name":"Slowdive"}],
              "releases":[{"title":"Souvlaki","date":"1993-05-17"}],
              "tags":[{"name":"shoegaze"}]}]}
            """;
        var client = new MusicBrainzClient(
            Factory("musicbrainz", new JsonHandler(json), "https://musicbrainz.org/ws/2/"),
            new FakeCache(), new MusicBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<MusicBrainzClient>>());

        var recordings = await client.GetArtistRecordingsAsync("a1", 5, CancellationToken.None);

        recordings.Should().HaveCount(1);
        recordings[0].Mbid.Should().Be("r1");
        recordings[0].Artist.Should().Be("Slowdive");
        recordings[0].Album.Should().Be("Souvlaki");
        recordings[0].Year.Should().Be("1993");
        recordings[0].Tags.Should().Contain("shoegaze");
    }

    [Fact]
    public async Task MusicBrainz_ProviderFailure_YieldsNull()
    {
        var client = new MusicBrainzClient(
            Factory("musicbrainz", new ThrowingHandler(), "https://musicbrainz.org/ws/2/"),
            new FakeCache(), new MusicBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<MusicBrainzClient>>());

        var result = await client.SearchArtistAsync("Slowdive", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ListenBrainz_SimilarArtists_ParsesArray()
    {
        const string json = """
            {"artists":[{"artist_mbid":"lb1","name":"Ride","score":0.87}]}
            """;
        var client = new ListenBrainzClient(
            Factory("listenbrainz", new JsonHandler(json), "https://labs.api.listenbrainz.org/"),
            new FakeCache(), new ListenBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<ListenBrainzClient>>());

        var artists = await client.GetSimilarArtistsAsync("seed", 5, CancellationToken.None);

        artists.Should().HaveCount(1);
        artists[0].Mbid.Should().Be("lb1");
        artists[0].Name.Should().Be("Ride");
        artists[0].Score.Should().BeApproximately(0.87, 0.001);
    }

    [Fact]
    public async Task ListenBrainz_Popularity_ParsesKnownCounts()
    {
        const string json = """
            [{"recording_mbid":"mb-a","total_listen_count":12345},
             {"recording_mbid":"mb-b","total_listen_count":null}]
            """;
        var client = new ListenBrainzClient(
            Factory("listenbrainz-api", new JsonHandler(json),
                "https://api.listenbrainz.org/"),
            new FakeCache(), new ListenBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<ListenBrainzClient>>());

        var counts = await client.GetRecordingPopularityAsync(
            ["mb-a", "mb-b"], CancellationToken.None);

        counts["mb-a"].Should().Be(12345);
        counts.Should().NotContainKey("mb-b");
    }

    [Fact]
    public async Task ListenBrainz_RelativeIntegerScores_AreNormalized()
    {
        const string json = """
            [{"artist_mbid":"a","name":"A","score":7000},
             {"artist_mbid":"b","name":"B","score":3500}]
            """;
        var client = new ListenBrainzClient(
            Factory("listenbrainz", new JsonHandler(json), "https://labs.api.listenbrainz.org/"),
            new FakeCache(), new ListenBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<ListenBrainzClient>>());

        var artists = await client.GetSimilarArtistsAsync("seed", 5, CancellationToken.None);

        artists[0].Score.Should().BeApproximately(1.0, 0.001);
        artists[1].Score.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public async Task ListenBrainz_SimilarRecordings_ParsesListenCount()
    {
        const string json = """
            [{"recording_mbid":"r2","score":0.77,"total_listen_count":12345}]
            """;
        var client = new ListenBrainzClient(
            Factory("listenbrainz", new JsonHandler(json), "https://labs.api.listenbrainz.org/"),
            new FakeCache(), new ListenBrainzRateLimiter(), Options(),
            Substitute.For<ILogger<ListenBrainzClient>>());

        var recordings = await client.GetSimilarRecordingsAsync(
            "seed", 5, CancellationToken.None);

        recordings.Should().HaveCount(1);
        recordings[0].Mbid.Should().Be("r2");
        recordings[0].ListenCount.Should().Be(12345);
    }

    [Fact]
    public async Task OpenDataSource_ProducesSimilarGraphAndTagCandidates()
    {
        var mb = Substitute.For<IMusicBrainzClient>();
        var lb = Substitute.For<IListenBrainzClient>();
        mb.SearchArtistAsync("M83", Arg.Any<CancellationToken>())
            .Returns(new MbArtistDto("seed-mb", "M83", 100, []));
        lb.GetSimilarArtistsAsync("seed-mb", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new SimilarArtistDto("sim-mb", "Similar Artist", 0.9)]);
        mb.GetRelatedArtistsAsync("seed-mb", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new MbArtistDto("rel-mb", "Related Artist", 0.7, [])]);
        mb.GetArtistRecordingsAsync("sim-mb", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Recording("Similar Song", "Similar Artist")]);
        mb.GetArtistRecordingsAsync("rel-mb", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Recording("Related Song", "Related Artist")]);
        mb.GetTagRecordingsAsync("shoegaze", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Recording("Tag Song", "Tag Artist")]);
        mb.SearchRecordingAsync("Midnight City", "M83", Arg.Any<CancellationToken>())
            .Returns(Recording("Midnight City", "M83"));
        lb.GetSimilarRecordingsAsync("rec-Midnight City", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new SimilarRecordingDto("rec-sim", 0.8, 50_000)]);
        mb.GetRecordingAsync("rec-sim", Arg.Any<CancellationToken>())
            .Returns(Recording("Track Similar", "Track Artist"));

        var taste = new TasteProfile(
            [new TasteArtist("M83", 10, 10)],
            [new TasteTrack("Midnight City", "M83", 10, 10)],
            ["shoegaze"]);

        var source = new OpenDataCandidateSource(
            mb, lb, Options(), Substitute.For<ILogger<OpenDataCandidateSource>>());
        var candidates = await source.GetCandidatesAsync(taste, 50, CancellationToken.None);

        candidates.Should().Contain(c => c.Title == "Similar Song" &&
            c.Source == DiscoverSources.Similar && c.SeedArtist == "M83");
        candidates.Should().Contain(c => c.Title == "Related Song" &&
            c.Source == DiscoverSources.Graph);
        candidates.Should().Contain(c => c.Title == "Tag Song" &&
            c.Source == DiscoverSources.Tag);
        candidates.Should().Contain(c => c.Title == "Track Similar" &&
            c.Source == DiscoverSources.Similar && c.Popularity == 50_000);
    }

    [Fact]
    public async Task OpenDataSource_EmptyTaste_UsesColdStartTags()
    {
        var mb = Substitute.For<IMusicBrainzClient>();
        var lb = Substitute.For<IListenBrainzClient>();
        mb.GetTagRecordingsAsync("pop", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Recording("Cold Start Song", "Cold Artist")]);

        var options = Options();
        options.ColdStartTags = ["pop"];
        var source = new OpenDataCandidateSource(
            mb, lb, options, Substitute.For<ILogger<OpenDataCandidateSource>>());

        var candidates = await source.GetCandidatesAsync(
            TasteProfile.Empty, 10, CancellationToken.None);

        candidates.Should().ContainSingle(c =>
            c.Title == "Cold Start Song" && c.Source == DiscoverSources.Tag);
    }

    [Fact]
    public async Task FallbackSource_EmptyTaste_ReturnsChartPicks()
    {
        var itunes = Substitute.For<IITunesClient>();
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["Midnight City M83"]);
        itunes.SearchSingleAsync("Midnight City M83", null, Arg.Any<CancellationToken>())
            .Returns(new ITunesHitDto(
                "Midnight City", "M83", "Hurry Up", "2011", "Electronic", "art"));

        var source = new FallbackCandidateSource(itunes);
        var candidates = await source.GetCandidatesAsync(
            TasteProfile.Empty, 10, CancellationToken.None);

        candidates.Should().HaveCount(1);
        candidates[0].Source.Should().Be(DiscoverSources.Chart);
        candidates[0].Title.Should().Be("Midnight City");
    }

    [Fact]
    public async Task FallbackSource_WithTaste_ReturnsSameArtistDeepCuts()
    {
        var itunes = Substitute.For<IITunesClient>();
        itunes.SearchTermAsync("M83", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                new ITunesHitDto("Outro", "M83", "Hurry Up", "2011", "Electronic", "art"),
                new ITunesHitDto("Other", "Someone Else", "X", "2020", "Pop", "art2"),
            ]);

        var taste = new TasteProfile(
            [new TasteArtist("M83", 10, 1)], [], []);
        var source = new FallbackCandidateSource(itunes);
        var candidates = await source.GetCandidatesAsync(taste, 10, CancellationToken.None);

        candidates.Should().HaveCount(1);
        candidates[0].Title.Should().Be("Outro");
        candidates[0].Source.Should().Be(DiscoverSources.Artist);
        candidates[0].SeedArtist.Should().Be("M83");
    }

    private static MbRecordingDto Recording(string title, string artist) =>
        new("rec-" + title, title, artist, "Album", "2020", ["shoegaze"]);
}

using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using Hathor.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class PopularityProfileBuilderTests
{
    private sealed class FakeCache : IRecommendationCache
    {
        private readonly Dictionary<string, object> _values = [];

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
            where T : class =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value as T : null);

        public Task SetAsync<T>(string key, T value, TimeSpan ttl,
            CancellationToken ct = default) where T : class
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Build_UsesMedianOfTopTrackPopularity()
    {
        var userId = Guid.NewGuid();
        var musicBrainz = Substitute.For<IMusicBrainzClient>();
        MbRecordingDto Recording(string mbid, string title) =>
            new(mbid, title, "Artist", "Album", "2020", []);

        musicBrainz.SearchRecordingAsync("Song A", "Artist", Arg.Any<CancellationToken>())
            .Returns(Recording("mb-a", "Song A"));
        musicBrainz.SearchRecordingAsync("Song B", "Artist", Arg.Any<CancellationToken>())
            .Returns(Recording("mb-b", "Song B"));
        musicBrainz.SearchRecordingAsync("Song C", "Artist", Arg.Any<CancellationToken>())
            .Returns(Recording("mb-c", "Song C"));

        var popularity = Substitute.For<ITrackPopularityService>();
        popularity.GetAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long>
            {
                ["mb-a"] = 1_000,
                ["mb-b"] = 10_000,
                ["mb-c"] = 100_000,
            });

        var builder = new PopularityProfileBuilder(
            musicBrainz, popularity, new FakeCache(), new DiscoveryOptions(),
            Substitute.For<ILogger<PopularityProfileBuilder>>());
        var tracks = new List<TasteTrack>
        {
            new("Song A", "Artist", 3, 3),
            new("Song B", "Artist", 2, 2),
            new("Song C", "Artist", 1, 1),
        };

        var profile = await builder.BuildAsync(userId, tracks, CancellationToken.None);

        profile.Should().NotBeNull();
        profile!.Samples.Should().Be(3);
        profile.MedianLog.Should().BeApproximately(4.0, 0.001);
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class DiscoverInsightsTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<HttpClient> AuthedAsync()
    {
        var token = await _lib.RegisterAsync($"di-{Guid.NewGuid():N}");
        _client.DefaultRequestHeaders.Authorization =
            TempLibraryFixture.Bearer(token);
        return _client;
    }

    private sealed record SourceInsight(
        string Source, int Downloads, int Dismissals, int Previews, int Clicks,
        double ConversionRate, double LearnedPrior);

    private sealed record Insights(
        int WindowDays, int Downloads, int Dismissals, int Previews, int Clicks,
        double ConversionRate, int CompletedPlays, int SkippedPlays,
        double PlaybackCompletionRate, int UniqueArtists,
        List<SourceInsight> Sources);

    [Fact]
    public async Task Insights_EmptyAccount_ReturnsNeutralZeros()
    {
        var client = await AuthedAsync();

        var response = await client.GetAsync("/api/v1/discover/insights");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<Insights>();

        dto.Should().NotBeNull();
        dto!.Downloads.Should().Be(0);
        dto.Dismissals.Should().Be(0);
        dto.ConversionRate.Should().Be(0.5);
        dto.PlaybackCompletionRate.Should().Be(0.5);
        dto.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task Insights_AggregatesFeedbackAndPlaybackEvents()
    {
        var client = await AuthedAsync();

        (await client.PostAsJsonAsync("/api/v1/discover/feedback", new
        {
            title = "Midnight City",
            artist = "M83",
            source = "similar",
            @event = "download",
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/api/v1/discover/feedback", new
        {
            title = "Chart Song",
            artist = "Someone",
            source = "chart",
            @event = "dismiss",
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/api/v1/player/playback-events", new
        {
            segmentId = "seg-insights",
            file = "song.mp3",
            positionSec = 200,
            durationSec = 200,
            completed = true,
            skipped = false,
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await client.GetAsync("/api/v1/discover/insights?windowDays=30");
        var dto = await response.Content.ReadFromJsonAsync<Insights>();

        dto.Should().NotBeNull();
        dto!.WindowDays.Should().Be(30);
        dto.Downloads.Should().Be(1);
        dto.Dismissals.Should().Be(1);
        dto.CompletedPlays.Should().Be(1);
        dto.PlaybackCompletionRate.Should().Be(1.0);
        dto.Sources.Should().Contain(s => s.Source == "similar" && s.Downloads == 1);
        dto.Sources.Should().Contain(s => s.Source == "chart" && s.Dismissals == 1);
    }
}

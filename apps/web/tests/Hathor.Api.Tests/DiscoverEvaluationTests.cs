using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class DiscoverEvaluationTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<HttpClient> AuthedAsync()
    {
        var token = await _lib.RegisterAsync($"de-{Guid.NewGuid():N}");
        _client.DefaultRequestHeaders.Authorization =
            TempLibraryFixture.Bearer(token);
        return _client;
    }

    private sealed record Preset(string Name, double PrecisionAtK, double NdcgAtK, double ConversionAtK);

    private sealed record Evaluation(
        int WindowDays, int K, int Snapshots, int Items, int Labeled,
        int Downloads, int Dismissals, int Previews, int Clicks,
        double PrecisionAtK, double NdcgAtK, double ConversionAtK,
        double ArtistCoverage, bool InsufficientData,
        List<Preset> Presets);

    [Fact]
    public async Task EmptyAccount_ReturnsInsufficientData()
    {
        var client = await AuthedAsync();

        var response = await client.GetAsync("/api/v1/discover/evaluation?windowDays=30&k=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<Evaluation>();

        dto.Should().NotBeNull();
        dto!.WindowDays.Should().Be(30);
        dto.K.Should().Be(10);
        dto.Snapshots.Should().Be(0);
        dto.InsufficientData.Should().BeTrue();
        dto.Presets.Should().Contain(p => p.Name == "current");
    }
}

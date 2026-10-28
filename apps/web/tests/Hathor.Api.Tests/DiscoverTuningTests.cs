using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class DiscoverTuningTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<HttpClient> AuthedAsync()
    {
        var token = await _lib.RegisterAsync($"dt-{Guid.NewGuid():N}");
        _client.DefaultRequestHeaders.Authorization =
            TempLibraryFixture.Bearer(token);
        return _client;
    }

    private sealed record Weights(
        double Similarity, double Tag, double Affinity,
        double Popularity, double Source, double Audio);

    private sealed record Tuning(
        bool Applied, bool InsufficientData, string? Preset,
        int Snapshots, int Labeled, double CurrentScore, double BestScore,
        Weights? Weights);

    private sealed record Status(
        bool HasTunedModel, string? Preset, int Samples, int Snapshots,
        DateTime? UpdatedAtUtc, Weights? Weights);

    [Fact]
    public async Task EmptyAccount_ReportsNoModel_AndNoTuning()
    {
        var client = await AuthedAsync();

        var statusResponse = await client.GetAsync("/api/v1/discover/tuning");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await statusResponse.Content.ReadFromJsonAsync<Status>();
        status!.HasTunedModel.Should().BeFalse();

        var tuneResponse = await client.PostAsJsonAsync(
            "/api/v1/discover/tuning?windowDays=30&k=10", new { });
        tuneResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var tuning = await tuneResponse.Content.ReadFromJsonAsync<Tuning>();
        tuning!.Applied.Should().BeFalse();
        tuning.InsufficientData.Should().BeTrue();
        tuning.Weights.Should().BeNull();
    }
}

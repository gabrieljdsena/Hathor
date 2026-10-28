using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class PlaybackEventEndpointTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<HttpClient> AuthedAsync()
    {
        var token = await _lib.RegisterAsync($"pe-{Guid.NewGuid():N}");
        _client.DefaultRequestHeaders.Authorization =
            TempLibraryFixture.Bearer(token);
        return _client;
    }

    [Fact]
    public async Task ValidEvent_ReturnsNoContent()
    {
        var client = await AuthedAsync();

        var response = await client.PostAsJsonAsync("/api/v1/player/playback-events",
            new
            {
                segmentId = "seg-1",
                file = "song.mp3",
                positionSec = 10,
                durationSec = 200,
                completed = false,
                skipped = true,
                startedAtUtc = DateTime.UtcNow,
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task PathTraversalFile_ReturnsBadRequest()
    {
        var client = await AuthedAsync();

        var response = await client.PostAsJsonAsync("/api/v1/player/playback-events",
            new
            {
                segmentId = "seg-1",
                file = "../song.mp3",
                positionSec = 10,
                durationSec = 200,
                completed = false,
                skipped = true,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CompletedAndSkipped_ReturnsBadRequest()
    {
        var client = await AuthedAsync();

        var response = await client.PostAsJsonAsync("/api/v1/player/playback-events",
            new
            {
                segmentId = "seg-1",
                file = "song.mp3",
                positionSec = 200,
                durationSec = 200,
                completed = true,
                skipped = true,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

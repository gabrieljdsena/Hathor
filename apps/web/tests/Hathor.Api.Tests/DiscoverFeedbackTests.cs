using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

// POST /discover/feedback: user-scoped signal, library:write only, validated
// event names. The Application tests cover the row shape; these cover the
// public HTTP surface.
public sealed class DiscoverFeedbackTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<HttpClient> AuthedAsync()
    {
        var token = await _lib.RegisterAsync($"df-{Guid.NewGuid():N}");
        _client.DefaultRequestHeaders.Authorization =
            TempLibraryFixture.Bearer(token);
        return _client;
    }

    [Fact]
    public async Task ValidDismiss_ReturnsNoContent()
    {
        var client = await AuthedAsync();

        var response = await client.PostAsJsonAsync("/api/v1/discover/feedback",
            new
            {
                title = "Midnight City",
                artist = "M83",
                mbid = "rec-1",
                source = "similar",
                @event = "dismiss",
                seedArtist = "M83",
                score = 0.8,
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task UnknownEvent_ReturnsBadRequest()
    {
        var client = await AuthedAsync();

        var response = await client.PostAsJsonAsync("/api/v1/discover/feedback",
            new
            {
                title = "Midnight City",
                artist = "M83",
                source = "similar",
                @event = "promote",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BlankTitle_ReturnsBadRequest()
    {
        var client = await AuthedAsync();

        var response = await client.PostAsJsonAsync("/api/v1/discover/feedback",
            new
            {
                title = " ",
                artist = "M83",
                source = "similar",
                @event = "dismiss",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

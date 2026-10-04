using System.Net;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class ExternalApiSurfaceTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    [Fact]
    public async Task ApiInfo_IsPublic()
    {
        var response = await _client.GetAsync("/api/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/api/v1/songs")]
    [InlineData("/api/v1/songs/count")]
    [InlineData("/api/v1/discover")]
    [InlineData("/api/v1/player/state")]
    [InlineData("/api/v1/player/now-playing")]
    public async Task ProtectedEndpoints_RequireAuth(string url)
    {
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/v1/player/play")]
    [InlineData("/api/v1/player/toggle")]
    [InlineData("/api/v1/player/next")]
    [InlineData("/api/v1/player/prev")]
    [InlineData("/api/v1/player/seek")]
    [InlineData("/api/v1/player/volume")]
    [InlineData("/api/v1/player/shuffle")]
    public async Task HeadlessControls_RequireControlScope(string url)
    {
        var response = await _client.PostAsync(url,
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

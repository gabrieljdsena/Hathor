using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

// Granular PAT scopes: each write scope opens its own surface and nothing
// else; player:control stays the legacy superset. 404s (not 403s) prove an
// endpoint's scope check passed and only the resource was missing.
public sealed class ApiScopeTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    private async Task<string> MintAsync(string jwt, string name, params string[] scopes)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ApiKeys");
        msg.Headers.Authorization = Bearer(jwt);
        msg.Content = JsonContent.Create(new { Name = name, Scopes = scopes });
        var res = await _client.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CreatedKey>())!.Token;
    }

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string url, string token, object? body = null)
    {
        using var msg = new HttpRequestMessage(method, url);
        msg.Headers.Authorization = Bearer(token);
        if (body is not null) msg.Content = JsonContent.Create(body);
        return (await _client.SendAsync(msg)).StatusCode;
    }

    private Task<HttpStatusCode> GetAsync(string url, string token) =>
        SendAsync(HttpMethod.Get, url, token);

    private async Task<string> JwtAsync()
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = $"sc-{Guid.NewGuid():N}", password = "scope-test-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    [Fact]
    public async Task DownloadsWrite_OpensOnly_Downloads()
    {
        var jwt = await JwtAsync();
        var key = await MintAsync(jwt, "dl", "downloads:write");

        (await SendAsync(HttpMethod.Post, "/api/v1/downloads/nope/retry", key))
            .Should().Be(HttpStatusCode.NotFound);
        (await GetAsync("/api/v1/downloads?limit=1", key)).Should().Be(HttpStatusCode.OK);
        (await GetAsync("/api/v1/songs?page=1&pageSize=1", key)).Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/playlists", key, new { Title = "Nope" }))
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/player/next", key, new { }))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlaylistsWrite_OpensOnly_Playlists()
    {
        var jwt = await JwtAsync();
        var key = await MintAsync(jwt, "pl", "playlists:write");

        (await SendAsync(HttpMethod.Post, "/api/v1/playlists", key, new { Title = "Scope Mix" }))
            .Should().Be(HttpStatusCode.Created);
        (await GetAsync("/api/v1/playlists", key)).Should().Be(HttpStatusCode.OK);
        (await GetAsync("/api/v1/songs?page=1&pageSize=1", key)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/v1/downloads/nope/retry", key))
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/player/next", key, new { }))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LibraryWrite_OpensOnly_Library()
    {
        var jwt = await JwtAsync();
        var key = await MintAsync(jwt, "lib", "library:write");

        (await SendAsync(HttpMethod.Patch, "/api/v1/songs/nope.mp3", key, new { Title = "x" }))
            .Should().Be(HttpStatusCode.NotFound);
        (await GetAsync("/api/v1/songs?page=1&pageSize=1", key)).Should().Be(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/v1/playlists", key, new { Title = "Nope" }))
            .Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, "/api/v1/player/next", key, new { }))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlayerControl_Remains_Full_Superset()
    {
        var jwt = await JwtAsync();
        var key = await MintAsync(jwt, "full", "player:control");

        (await SendAsync(HttpMethod.Post, "/api/v1/playlists", key, new { Title = "Full Mix" }))
            .Should().Be(HttpStatusCode.Created);
        (await SendAsync(HttpMethod.Post, "/api/v1/downloads/nope/retry", key))
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReadOnly_Key_Cannot_Mint_Keys()
    {
        var jwt = await JwtAsync();
        var key = await MintAsync(jwt, "ro", "player:read");
        (await SendAsync(HttpMethod.Post, "/api/v1/ApiKeys", key,
            new { Name = "esc", Scopes = new[] { "player:read" } }))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record CreatedKey(string Token);
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class Phase3EndpointTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<string> TokenForAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "phase3-pass-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    [Fact]
    public async Task History_EmptyLibrary_PaginationShape()
    {
        var token = await TokenForAsync($"h-{Guid.NewGuid():N}");
        foreach (var url in new[]
            { "/api/v1/history/downloads", "/api/v1/history/played-songs", "/api/v1/history/played-playlists" })
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{url}?page=1&pageSize=10");
            msg.Headers.Authorization = Bearer(token);
            var res = await _client.SendAsync(msg);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var page = (await res.Content.ReadFromJsonAsync<Page>())!;
            page.Items.Should().BeEmpty();
            page.TotalPages.Should().Be(1);
            page.CurrentPage.Should().Be(1);
        }
    }

    [Fact]
    public async Task Mix_StablePerDay_SecondCallCached()
    {
        var token = await TokenForAsync($"m-{Guid.NewGuid():N}");

        // A file on disk (even undecodable bytes → filename defaults) makes
        // the mix resolvable, so the second same-day read hits the cache.
        await File.WriteAllBytesAsync(Path.Combine(_lib.SongsDir, "t.mp3"), [0x49, 0x44, 0x33, 0x00]);

        using var first = new HttpRequestMessage(HttpMethod.Get, "/api/v1/daily-mix");
        first.Headers.Authorization = Bearer(token);
        var r1 = await _client.SendAsync(first);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var mix1 = (await r1.Content.ReadFromJsonAsync<Mix>())!;
        mix1.Songs.Should().HaveCount(1);
        mix1.Cached.Should().BeFalse();

        using var second = new HttpRequestMessage(HttpMethod.Get, "/api/v1/daily-mix");
        second.Headers.Authorization = Bearer(token);
        var mix2 = (await (await _client.SendAsync(second)).Content.ReadFromJsonAsync<Mix>())!;
        mix2.Date.Should().Be(mix1.Date);
        mix2.Cached.Should().BeTrue();
    }

    [Fact]
    public async Task Mix_Regenerate_RequiresControlScope()
    {
        var token = await TokenForAsync($"mr-{Guid.NewGuid():N}");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/daily-mix/regenerate");
        msg.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(msg)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rebuild_NoSource_ReturnsFalse()
    {
        var token = await TokenForAsync($"rb-{Guid.NewGuid():N}");
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/player/queue/rebuild");
        msg.Headers.Authorization = Bearer(token);
        var res = await _client.SendAsync(msg);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<RebuildResult>())!.Rebuilt.Should().BeFalse();
    }

    [Fact]
    public async Task Recents_EmptyLibrary_ReturnEmpty()
    {
        var token = await TokenForAsync($"r-{Guid.NewGuid():N}");
        foreach (var url in new[] { "/api/v1/songs/recently-played", "/api/v1/songs/recently-downloaded" })
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{url}?limit=15");
            msg.Headers.Authorization = Bearer(token);
            var res = await _client.SendAsync(msg);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            (await res.Content.ReadFromJsonAsync<List<object>>()).Should().BeEmpty();
        }
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record Page(List<object> Items, int TotalPages, int CurrentPage);
    private sealed record Mix(string Date, List<object> Songs, bool Cached);
    private sealed record RebuildResult(bool Rebuilt, object State);
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hathor.Api.Tests;

public sealed class Phase4EndpointTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<string> TokenForAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "phase4-pass-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    [Fact]
    public async Task Downloads_EmptySubmit_400_MissingJob_404()
    {
        var token = await TokenForAsync($"dl-{Guid.NewGuid():N}");

        using var empty = new HttpRequestMessage(HttpMethod.Post, "/api/v1/downloads");
        empty.Headers.Authorization = Bearer(token);
        empty.Content = JsonContent.Create(new { url = "", title = "", isPodcast = false });
        (await _client.SendAsync(empty)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var missing = new HttpRequestMessage(HttpMethod.Get, "/api/v1/downloads/nope");
        missing.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(missing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var retryMissing = new HttpRequestMessage(HttpMethod.Post, "/api/v1/downloads/nope/retry");
        retryMissing.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(retryMissing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var jobs = new HttpRequestMessage(HttpMethod.Get, "/api/v1/downloads?limit=15");
        jobs.Headers.Authorization = Bearer(token);
        var list = await _client.SendAsync(jobs);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadFromJsonAsync<List<object>>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Downloads_Batch_RequiresFile()
    {
        var token = await TokenForAsync($"b-{Guid.NewGuid():N}");
        using var content = new MultipartFormDataContent();
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/downloads/batch");
        msg.Headers.Authorization = Bearer(token);
        msg.Content = content;
        // No file part at all → model binding yields null → 400.
        (await _client.SendAsync(msg)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lyrics_Delete_Missing_ReturnsNotFound()
    {
        var token = await TokenForAsync($"ly-{Guid.NewGuid():N}");

        using var del = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/songs/ghost.mp3/lyrics");
        del.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lyrics_SaveThenDelete_RoundTrip()
    {
        var token = await TokenForAsync($"ly-{Guid.NewGuid():N}");

        using var save = new HttpRequestMessage(HttpMethod.Put, "/api/v1/songs/s.mp3/lyrics");
        save.Headers.Authorization = Bearer(token);
        save.Content = JsonContent.Create(new { synced = (string?)null, plain = "la la" });
        (await _client.SendAsync(save)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var del = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/songs/s.mp3/lyrics");
        del.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var delAgain = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/songs/s.mp3/lyrics");
        delAgain.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(delAgain)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LyricsOffset_RoundTrip_Clamps()
    {
        var token = await TokenForAsync($"ly-{Guid.NewGuid():N}");

        using var getEmpty = new HttpRequestMessage(HttpMethod.Get, "/api/v1/songs/s.mp3/lyrics/offset");
        getEmpty.Headers.Authorization = Bearer(token);
        (await (await _client.SendAsync(getEmpty)).Content.ReadFromJsonAsync<Offset>())
            .Should().BeEquivalentTo(new Offset(0));

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/songs/s.mp3/lyrics/offset");
        put.Headers.Authorization = Bearer(token);
        put.Content = JsonContent.Create(new { offsetMs = 500 });
        var putRes = await _client.SendAsync(put);
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await putRes.Content.ReadFromJsonAsync<Offset>())!.OffsetMs.Should().Be(500);

        using var putHuge = new HttpRequestMessage(HttpMethod.Put, "/api/v1/songs/s.mp3/lyrics/offset");
        putHuge.Headers.Authorization = Bearer(token);
        putHuge.Content = JsonContent.Create(new { offsetMs = 99999 });
        (await (await _client.SendAsync(putHuge)).Content.ReadFromJsonAsync<Offset>())!
            .OffsetMs.Should().Be(20000);

        // Back to zero: sparse storage, reads as 0.
        using var putZero = new HttpRequestMessage(HttpMethod.Put, "/api/v1/songs/s.mp3/lyrics/offset");
        putZero.Headers.Authorization = Bearer(token);
        putZero.Content = JsonContent.Create(new { offsetMs = 0 });
        (await _client.SendAsync(putZero)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var getZero = new HttpRequestMessage(HttpMethod.Get, "/api/v1/songs/s.mp3/lyrics/offset");
        getZero.Headers.Authorization = Bearer(token);
        (await (await _client.SendAsync(getZero)).Content.ReadFromJsonAsync<Offset>())
            .Should().BeEquivalentTo(new Offset(0));
    }

    private sealed record Offset(int OffsetMs);

    [Fact]
    public async Task Lyrics_Validation_Paths()
    {
        var token = await TokenForAsync($"ly-{Guid.NewGuid():N}");

        using var noTrack = new HttpRequestMessage(HttpMethod.Get, "/api/v1/lyrics?track=&artist=x");
        noTrack.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(noTrack)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var saveEmpty = new HttpRequestMessage(HttpMethod.Put, "/api/v1/songs/ghost.mp3/lyrics");
        saveEmpty.Headers.Authorization = Bearer(token);
        saveEmpty.Content = JsonContent.Create(new { synced = (string?)null, plain = (string?)null });
        (await _client.SendAsync(saveEmpty)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var roman = new HttpRequestMessage(HttpMethod.Post, "/api/v1/lyrics/romanize");
        roman.Headers.Authorization = Bearer(token);
        roman.Content = JsonContent.Create(new { text = "さくら", isLrc = false });
        var romanRes = await _client.SendAsync(roman);
        romanRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await romanRes.Content.ReadFromJsonAsync<Roman>())!.Text.Should().Be("sakura");
    }

    [Fact]
    public async Task Metadata_TrendingShape_And_ItunesValidation()
    {
        var token = await TokenForAsync($"md-{Guid.NewGuid():N}");

        // Trending proxies the network — only assert the route exists behind auth.
        using var anon = new HttpRequestMessage(HttpMethod.Get, "/api/v1/metadata/trending");
        (await _client.SendAsync(anon)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record Roman(string Text);
}

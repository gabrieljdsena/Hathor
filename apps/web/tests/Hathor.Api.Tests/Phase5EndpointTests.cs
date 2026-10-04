using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class Phase5EndpointTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<(string Token, string UserId)> RegisterAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "phase5-pass-123" });
        reg.EnsureSuccessStatusCode();
        var tokens = (await reg.Content.ReadFromJsonAsync<Tokens>())!;
        var userId = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(tokens.AccessToken).Claims
            .First(c => c.Type == System.Security.Claims.ClaimTypes.NameIdentifier).Value;
        return (tokens.AccessToken, userId);
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    private void DropEpisode(string file)
    {
        File.WriteAllBytes(Path.Combine(_lib.PodcastsDir, file), [0x49, 0x44, 0x33, 0x00]);
    }

    [Fact]
    public async Task Podcasts_FullCycle_WithLiveTagCounts()
    {
        var (token, _) = await RegisterAsync($"pc-{Guid.NewGuid():N}");
        DropEpisode("ep1.mp3");
        DropEpisode("ep2.mp3");

        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts");
        list.Headers.Authorization = Bearer(token);
        var listRes = await _client.SendAsync(list);
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listRes.Content.ReadFromJsonAsync<List<object>>()).Should().HaveCount(2);

        using var details = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts/ep1.mp3");
        details.Headers.Authorization = Bearer(token);
        var detRes = await _client.SendAsync(details);
        detRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var missing = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts/nope.mp3");
        missing.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(missing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Tag + assign via API, counts reflect live files only.
        using var tag = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasttags");
        tag.Headers.Authorization = Bearer(token);
        tag.Content = JsonContent.Create(new { name = "Tech" });
        var tagId = (await (await _client.SendAsync(tag)).Content.ReadFromJsonAsync<CreatedTag>())!.Id;

        using var assign = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/podcasttags/{tagId}/episodes");
        assign.Headers.Authorization = Bearer(token);
        assign.Content = JsonContent.Create(new { file = "ep1.mp3" });
        (await _client.SendAsync(assign)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var tags = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasttags");
        tags.Headers.Authorization = Bearer(token);
        var tagsRes = await _client.SendAsync(tags);
        var tagList = (await tagsRes.Content.ReadFromJsonAsync<List<TagCount>>())!;
        tagList.Should().ContainSingle(t => t.Id == tagId && t.EpisodeCount == 1);

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/podcasts/ep1.mp3");
        patch.Headers.Authorization = Bearer(token);
        patch.Content = JsonContent.Create(new { title = "Renamed", artist = (string?)null, coverArt = (string?)null });
        // Fixture bytes are not a real MP3 → tag write fails cleanly (422, never 500).
        (await _client.SendAsync(patch)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        using var del = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/podcasts/ep1.mp3");
        del.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var tagsAfter = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasttags");
        tagsAfter.Headers.Authorization = Bearer(token);
        var after = (await (await _client.SendAsync(tagsAfter)).Content.ReadFromJsonAsync<List<TagCount>>())!;
        after.Should().ContainSingle(t => t.Id == tagId && t.EpisodeCount == 0);
    }

    [Fact]
    public async Task Settings_Clamp_And_Background_Roundtrip()
    {
        var (token, _) = await RegisterAsync($"se-{Guid.NewGuid():N}");

        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        get.Headers.Authorization = Bearer(token);
        var getRes = await _client.SendAsync(get);
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var settings = (await getRes.Content.ReadFromJsonAsync<Settings>())!;
        settings.Volume.Should().Be(0.7);
        settings.LimitDownloads.Should().Be(3);

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings");
        put.Headers.Authorization = Bearer(token);
        put.Content = JsonContent.Create(new { volume = 0.5, limitDownloads = 5, crossfadeEnabled = true, crossfadeSeconds = 8.0 });
        var putRes = await _client.SendAsync(put);
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await putRes.Content.ReadFromJsonAsync<Settings>())!;
        updated.Volume.Should().Be(0.5);
        updated.CrossfadeSeconds.Should().Be(8);

        using var bad = new HttpRequestMessage(HttpMethod.Put, "/api/v1/settings");
        bad.Headers.Authorization = Bearer(token);
        bad.Content = JsonContent.Create(new { volume = 99.0 });
        (await _client.SendAsync(bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var png = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/background");
        png.Headers.Authorization = Bearer(token);
        var form = new MultipartFormDataContent();
        var pngBytes = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        pngBytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(pngBytes, "file", "bg.png");
        png.Content = form;
        var bgRes = await _client.SendAsync(png);
        bgRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var bgFile = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/background/file");
        bgFile.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(bgFile)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var bgDel = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/settings/background");
        bgDel.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(bgDel)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var bgGone = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/background/file");
        bgGone.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(bgGone)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Background_Webp_Served_Anonymously_With_Token()
    {
        // CSS url() cannot set headers — the Shell background relies on
        // ?token= exactly like media streaming does.
        var (token, _) = await RegisterAsync($"bg-{Guid.NewGuid():N}");

        using var up = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/background");
        up.Headers.Authorization = Bearer(token);
        var form = new MultipartFormDataContent();
        // Minimal RIFF....WEBP header (serve path doesn't decode pixels).
        var webpBytes = new ByteArrayContent(
            [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50]);
        webpBytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
        form.Add(webpBytes, "file", "bg.webp");
        up.Content = form;
        (await _client.SendAsync(up)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var anon = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/settings/background/file?token={Uri.EscapeDataString(token)}");
        var res = await _client.SendAsync(anon);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task PlayerSettings_And_System_And_Logs()
    {
        var (token, _) = await RegisterAsync($"sy-{Guid.NewGuid():N}");

        using var ps = new HttpRequestMessage(HttpMethod.Get, "/api/v1/player/settings");
        ps.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(ps)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var set = new HttpRequestMessage(HttpMethod.Put, "/api/v1/player/settings");
        set.Headers.Authorization = Bearer(token);
        set.Content = JsonContent.Create(new { crossfadeEnabled = true, crossfadeSeconds = 99.0 });
        var setRes = await _client.SendAsync(set);
        setRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await setRes.Content.ReadFromJsonAsync<PlayerPrefs>())!.CrossfadeSeconds.Should().Be(12);

        using var ff = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/ffmpeg");
        ff.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(ff)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var libs = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/libraries");
        libs.Headers.Authorization = Bearer(token);
        var libsRes = await _client.SendAsync(libs);
        libsRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await libsRes.Content.ReadFromJsonAsync<List<object>>()).Should().NotBeEmpty();

        using var maint = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system/maintenance/run");
        maint.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(maint)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var log = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs/client");
        log.Headers.Authorization = Bearer(token);
        log.Content = JsonContent.Create(new { message = "test error", route = "/songs" });
        (await _client.SendAsync(log)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var anon = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        (await _client.SendAsync(anon)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FfmpegDownload_Status_StartsIdle()
    {
        var (token, _) = await RegisterAsync($"ff-{Guid.NewGuid():N}");

        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/ffmpeg/download");
        status.Headers.Authorization = Bearer(token);
        var res = await _client.SendAsync(status);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await res.Content.ReadFromJsonAsync<DownloadStatus>())!;
        body.State.Should().BeOneOf("idle", "ready");
    }

    private sealed record DownloadStatus(string State, double Progress, string? Exe, string? Error);

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record CreatedTag(long Id);
    private sealed record TagCount(long Id, string Name, int EpisodeCount);
    private sealed record Settings(
        double Volume, int LimitDownloads, string? BackgroundPath,
        bool CrossfadeEnabled, double CrossfadeSeconds, string? LastRoute, string? Browser);
    private sealed record PlayerPrefs(bool CrossfadeEnabled, double CrossfadeSeconds);
}

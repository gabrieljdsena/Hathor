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

    private void DropSong(string file)
    {
        File.WriteAllBytes(Path.Combine(_lib.SongsDir, file), [0x49, 0x44, 0x33, 0x00]);
    }

    [Fact]
    public async Task LibraryMove_BetweenSongsAndPodcasts()
    {
        var (token, _) = await RegisterAsync($"mv-{Guid.NewGuid():N}");
        DropSong("move.mp3");

        async Task<HttpStatusCode> Post(string url, object? _ = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = Bearer(token);
            return (await _client.SendAsync(req)).StatusCode;
        }

        // Song → podcast: 200 with the episode DTO, gone from songs.
        HttpResponseMessage movedRes;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/songs/move.mp3/move-to-podcasts"))
        {
            req.Headers.Authorization = Bearer(token);
            movedRes = await _client.SendAsync(req);
        }
        movedRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var moved = (await movedRes.Content.ReadFromJsonAsync<MovedSong>())!;
        moved.File.Should().Be("move.mp3");
        moved.IsPodcast.Should().BeTrue();

        using (var gone = new HttpRequestMessage(HttpMethod.Get, "/api/v1/songs?search=move"))
        {
            gone.Headers.Authorization = Bearer(token);
            var goneRes = await _client.SendAsync(gone);
            goneRes.StatusCode.Should().Be(HttpStatusCode.OK);
            (await goneRes.Content.ReadFromJsonAsync<List<MovedSong>>()).Should().BeEmpty();
        }
        using (var listed = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts"))
        {
            listed.Headers.Authorization = Bearer(token);
            var eps = (await (await _client.SendAsync(listed)).Content.ReadFromJsonAsync<List<MovedSong>>())!;
            eps.Should().ContainSingle(e => e.File == "move.mp3");
        }

        // Already moved: 404 on the song side.
        (await Post("/api/v1/songs/move.mp3/move-to-podcasts")).Should().Be(HttpStatusCode.NotFound);

        // Podcast → song: back to a song DTO.
        HttpResponseMessage backRes;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasts/move.mp3/move-to-songs"))
        {
            req.Headers.Authorization = Bearer(token);
            backRes = await _client.SendAsync(req);
        }
        backRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var back = (await backRes.Content.ReadFromJsonAsync<MovedSong>())!;
        back.File.Should().Be("move.mp3");
        back.IsPodcast.Should().BeFalse();

        // Missing on both sides: 404.
        (await Post("/api/v1/songs/nope.mp3/move-to-podcasts")).Should().Be(HttpStatusCode.NotFound);
        (await Post("/api/v1/podcasts/nope.mp3/move-to-songs")).Should().Be(HttpStatusCode.NotFound);

        // Filename taken on the destination side: 409 both ways.
        DropSong("clash.mp3");
        DropEpisode("clash.mp3");
        (await Post("/api/v1/songs/clash.mp3/move-to-podcasts")).Should().Be(HttpStatusCode.Conflict);
        (await Post("/api/v1/podcasts/clash.mp3/move-to-songs")).Should().Be(HttpStatusCode.Conflict);
    }

    private sealed record MovedSong(string File, bool IsPodcast, string Title);

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
        put.Content = JsonContent.Create(new { volume = 0.5, limitDownloads = 5, crossfadeEnabled = true, crossfadeSeconds = 8.0, chapterSkip = true });
        var putRes = await _client.SendAsync(put);
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await putRes.Content.ReadFromJsonAsync<Settings>())!;
        updated.Volume.Should().Be(0.5);
        updated.CrossfadeSeconds.Should().Be(8);
        updated.ChapterSkip.Should().BeTrue();

        using var getAfter = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        getAfter.Headers.Authorization = Bearer(token);
        (await (await _client.SendAsync(getAfter)).Content.ReadFromJsonAsync<Settings>())!
            .ChapterSkip.Should().BeTrue();

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

    [Fact]
    public async Task PodcastTimestamps_FullCycle()
    {
        var (token, _) = await RegisterAsync($"ts-{Guid.NewGuid():N}");
        DropEpisode("chap.mp3");

        // Missing episode → 404 everywhere.
        using var missingList = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts/nope.mp3/timestamps");
        missingList.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(missingList)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Two chapters, created out of order; list comes back sorted by start.
        async Task<Timestamp> Create(object body, HttpStatusCode expect)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasts/chap.mp3/timestamps");
            req.Headers.Authorization = Bearer(token);
            req.Content = JsonContent.Create(body);
            var res = await _client.SendAsync(req);
            res.StatusCode.Should().Be(expect);
            return (await res.Content.ReadFromJsonAsync<Timestamp>())!;
        }
        var second = await Create(new { name = "Second", startSecs = 120.0, endSecs = (double?)null },
            HttpStatusCode.Created);
        var first = await Create(new { name = "Intro", startSecs = 0.0, endSecs = 60.0 },
            HttpStatusCode.Created);
        first.StartSecs.Should().Be(0);
        first.EndSecs.Should().Be(60);

        // Validation → 400 (blank name, negative start, end <= start).
        await Create(new { name = " ", startSecs = 10.0, endSecs = (double?)null },
            HttpStatusCode.BadRequest);
        await Create(new { name = "Bad", startSecs = -5.0, endSecs = (double?)null },
            HttpStatusCode.BadRequest);
        await Create(new { name = "Bad", startSecs = 30.0, endSecs = 30.0 },
            HttpStatusCode.BadRequest);
        await Create(new { name = "Bad", startSecs = 30.0, endSecs = 10.0 },
            HttpStatusCode.BadRequest);

        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts/chap.mp3/timestamps");
        list.Headers.Authorization = Bearer(token);
        var listRes = await _client.SendAsync(list);
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = (await listRes.Content.ReadFromJsonAsync<List<Timestamp>>())!;
        rows.Select(r => r.Name).Should().Equal("Intro", "Second");

        // Update + update-missing → 404.
        using var put = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/podcasts/chap.mp3/timestamps/{first.Id}");
        put.Headers.Authorization = Bearer(token);
        put.Content = JsonContent.Create(new { name = "Cold open", startSecs = 5.0, endSecs = (double?)null });
        var putRes = await _client.SendAsync(put);
        putRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await putRes.Content.ReadFromJsonAsync<Timestamp>())!.Name.Should().Be("Cold open");

        using var putMissing = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/podcasts/chap.mp3/timestamps/999999");
        putMissing.Headers.Authorization = Bearer(token);
        putMissing.Content = JsonContent.Create(new { name = "Ghost", startSecs = 5.0, endSecs = (double?)null });
        (await _client.SendAsync(putMissing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Delete one, verify the other survives.
        using var del = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/podcasts/chap.mp3/timestamps/{first.Id}");
        del.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var delAgain = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/v1/podcasts/chap.mp3/timestamps/{first.Id}");
        delAgain.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(delAgain)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var list2 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts/chap.mp3/timestamps");
        list2.Headers.Authorization = Bearer(token);
        (await (await _client.SendAsync(list2)).Content.ReadFromJsonAsync<List<Timestamp>>())!
            .Should().ContainSingle().Which.Id.Should().Be(second.Id);

        // Deleting the episode cascades its timestamps.
        using var delEp = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/podcasts/chap.mp3");
        delEp.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(delEp)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var list3 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasts/chap.mp3/timestamps");
        list3.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(list3)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record Timestamp(long Id, string PodcastFile, string Name, double StartSecs, double? EndSecs);

    private sealed record DownloadStatus(string State, double Progress, string? Exe, string? Error);

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record CreatedTag(long Id);
    private sealed record TagCount(long Id, string Name, int EpisodeCount);
    private sealed record Settings(
        double Volume, int LimitDownloads, string? BackgroundPath,
        bool CrossfadeEnabled, double CrossfadeSeconds, string? LastRoute, string? Browser,
        bool ChapterSkip = false);
    private sealed record PlayerPrefs(bool CrossfadeEnabled, double CrossfadeSeconds);
}

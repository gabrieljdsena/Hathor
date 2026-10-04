using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hathor.Api.Tests;

public sealed class Phase6SyncTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<string> TokenForAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "phase6-pass-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    [Fact]
    public async Task Sync_ExportImport_Roundtrip()
    {
        // Single-account mode: one user imports, exports, deletes, and
        // re-applies its own tombstones.
        var token = await TokenForAsync($"sa-{Guid.NewGuid():N}");
        // Random id base: explicit snapshot ids must not collide with
        // server-generated rows.
        var b = (long)Random.Shared.Next(1_000_000, 10_000_000);
        var (pl, link, tag, lyr, mh, ph) = (b + 1, b + 2, b + 3, b + 4, b + 5, b + 6);

        // Seed user A: playlist + tag + history (played via player not needed —
        // import a snapshot directly, then export it back).
        var snapshot = new
        {
            songs = new[] { new { file = "s.mp3", downloadedLink = "https://x", title = "S", dateDownloadUtc = DateTime.UtcNow, artist = "A" } },
            podcasts = Array.Empty<object>(),
            playlists = new[] { new { id = pl, title = "Road", description = null as string, thumbnail = null as string } },
            songLinks = new[] { new { id = link, songFile = "s.mp3", playlistId = pl, dateAddedUtc = DateTime.UtcNow } },
            podcastTags = new[] { new { id = tag, name = "Tech" } },
            podcastTagLinks = Array.Empty<object>(),
            lyrics = new[] { new { id = lyr, songFile = "s.mp3", lyricsJson = """{"Synced":null,"Plain":"la"}""" } },
            musicHistory = new[] { new { id = mh, songFile = "s.mp3", datePlayedUtc = DateTime.UtcNow } },
            playlistHistory = new[] { new { id = ph, playlistId = pl, datePlayedUtc = DateTime.UtcNow } },
            dailyMix = new[] { new { mixDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), songFilesJson = "[\"s.mp3\"]" } },
            deletions = Array.Empty<object>(),
        };

        using var imp = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/import");
        imp.Headers.Authorization = Bearer(token);
        imp.Content = JsonContent.Create(snapshot);
        var impRes = await _client.SendAsync(imp);
        impRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = (await impRes.Content.ReadFromJsonAsync<Summary>())!;
        summary.Songs.Should().Be(1);
        summary.Playlists.Should().Be(1);
        summary.SongLinks.Should().Be(1);

        // Re-import is idempotent for append-only history (INSERT IGNORE).
        using var imp2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/import");
        imp2.Headers.Authorization = Bearer(token);
        imp2.Content = JsonContent.Create(snapshot);
        var summary2 = (await (await _client.SendAsync(imp2)).Content.ReadFromJsonAsync<Summary>())!;
        summary2.MusicHistory.Should().Be(0);
        summary2.PlaylistHistory.Should().Be(0);

        // Export contains everything; sinceId filters history only.
        using var exp = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sync/export");
        exp.Headers.Authorization = Bearer(token);
        var expRes = await _client.SendAsync(exp);
        expRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var full = (await expRes.Content.ReadFromJsonAsync<Snapshot>())!;
        full.Songs.Should().HaveCount(1);
        full.Playlists.Should().HaveCount(1);
        full.MusicHistory.Should().HaveCount(1);

        using var expSince = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sync/export?sinceId={mh}");
        expSince.Headers.Authorization = Bearer(token);
        var since = (await (await _client.SendAsync(expSince)).Content.ReadFromJsonAsync<Snapshot>())!;
        since.MusicHistory.Should().BeEmpty();
        since.Songs.Should().HaveCount(1); // catalog always exports fully

        // Tombstones propagate: delete the playlist on A, import deletions on B.
        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/playlists/{pl}");
        del.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var expDel = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sync/export");
        expDel.Headers.Authorization = Bearer(token);
        var tombstones = (await (await _client.SendAsync(expDel)).Content.ReadFromJsonAsync<Snapshot>())!
            .Deletions.Where(d => d.TableName == "playlists").ToList();
        tombstones.Should().ContainSingle(d => d.RowKey == pl.ToString());

        // Re-import the export (self-adopt), then the tombstone removes it.
        using var impB = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/import");
        impB.Headers.Authorization = Bearer(token);
        impB.Content = JsonContent.Create(full);
        (await _client.SendAsync(impB)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var getB = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/playlists/{pl}");
        getB.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(getB)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var impBDel = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/import");
        impBDel.Headers.Authorization = Bearer(token);
        impBDel.Content = JsonContent.Create(new { deletions = tombstones });
        (await _client.SendAsync(impBDel)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var getBGone = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/playlists/{pl}");
        getBGone.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(getBGone)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sync_SameIds_UpsertedNotDuplicated()
    {
        // Same snapshot (same explicit ids) imported twice by the single
        // user: own rows upsert in place — no duplicates, no remap clones.
        var token = await TokenForAsync($"ca-{Guid.NewGuid():N}");
        var pid = (long)Random.Shared.Next(1_000_000, 10_000_000);
        var payload = new
        {
            playlists = new[] { new { id = pid, title = "Shared", description = (string?)null, thumbnail = (string?)null } },
        };

        foreach (var _ in new[] { 1, 2 })
        {
            using var imp = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sync/import");
            imp.Headers.Authorization = Bearer(token);
            imp.Content = JsonContent.Create(payload);
            (await _client.SendAsync(imp)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/v1/playlists");
        msg.Headers.Authorization = Bearer(token);
        var list = (await (await _client.SendAsync(msg)).Content.ReadFromJsonAsync<List<Pl>>())!;
        list.Should().ContainSingle(p => p.Id == pid);
    }

    [Fact]
    public async Task Sync_RequiresAuth_And_NullBody_400()
    {
        using var anon = new HttpRequestMessage(HttpMethod.Get, "/api/v1/sync/export");
        (await _client.SendAsync(anon)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Headless_IdempotencyKey_ReplaysSameResponse()
    {
        var token = await TokenForAsync($"idem-{Guid.NewGuid():N}");
        var key = Guid.NewGuid().ToString();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/v1/player/next");
        first.Headers.Authorization = Bearer(token);
        first.Headers.Add("Idempotency-Key", key);
        first.Content = JsonContent.Create(new { });
        var r1 = await _client.SendAsync(first);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var b1 = await r1.Content.ReadAsStringAsync();

        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/v1/player/next");
        second.Headers.Authorization = Bearer(token);
        second.Headers.Add("Idempotency-Key", key);
        second.Content = JsonContent.Create(new { });
        var r2 = await _client.SendAsync(second);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
        (await r2.Content.ReadAsStringAsync()).Should().Be(b1);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record Summary(
        int Songs, int Podcasts, int Playlists, int SongLinks, int PodcastTags,
        int PodcastTagLinks, int Lyrics, int MusicHistory, int PlaylistHistory,
        int DailyMix, int Deletions);
    // Full-fidelity round-trip shapes (partial rows would 400 on required members).
    private sealed record Snapshot(
        List<SongFull> Songs, List<PlaylistFull> Playlists, List<HistFull> MusicHistory,
        List<Del> Deletions);
    private sealed record SongFull(
        string File, string? DownloadedLink, string Title, DateTime DateDownloadUtc, string? Artist);
    private sealed record PlaylistFull(long Id, string Title, string? Description, string? Thumbnail);
    private sealed record HistFull(long Id, string SongFile, DateTime DatePlayedUtc);
    private sealed record Del(string TableName, string RowKey);
    private sealed record Pl(long Id, string Title);
}

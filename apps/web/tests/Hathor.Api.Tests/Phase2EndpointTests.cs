using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class Phase2EndpointTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<HttpClient> AuthedClientAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "phase2-pass-123" });
        reg.EnsureSuccessStatusCode();
        var tokens = (await reg.Content.ReadFromJsonAsync<Tokens>())!;
        var authed = _client;
        return authed;
    }

    private async Task<string> TokenForAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "phase2-pass-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    [Fact]
    public async Task Playlists_FullCrud_WithCounts()
    {
        var token = await TokenForAsync($"pl-{Guid.NewGuid():N}");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/playlists/with-counts");
        req.Headers.Authorization = Bearer(token);
        var list = await _client.SendAsync(req);
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using var createMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/playlists");
        createMsg.Headers.Authorization = Bearer(token);
        createMsg.Content = JsonContent.Create(new { title = "Road", description = "d", cover = null as string });
        var created = await _client.SendAsync(createMsg);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var playlist = (await created.Content.ReadFromJsonAsync<Playlist>())!;
        playlist.Title.Should().Be("Road");

        using var badMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/playlists");
        badMsg.Headers.Authorization = Bearer(token);
        badMsg.Content = JsonContent.Create(new { title = "", description = "", cover = (string?)null });
        (await _client.SendAsync(badMsg)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var getMsg = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/playlists/{playlist.Id}/songs");
        getMsg.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(getMsg)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var delMsg = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/playlists/{playlist.Id}");
        delMsg.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(delMsg)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var delAgain = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/playlists/{playlist.Id}");
        delAgain.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(delAgain)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PodcastTags_Blank400_RenameConflict409()
    {
        var token = await TokenForAsync($"tag-{Guid.NewGuid():N}");

        using var blank = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasttags");
        blank.Headers.Authorization = Bearer(token);
        blank.Content = JsonContent.Create(new { name = "  " });
        (await _client.SendAsync(blank)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var a = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasttags");
        a.Headers.Authorization = Bearer(token);
        a.Content = JsonContent.Create(new { name = "Tech" });
        var createdA = await _client.SendAsync(a);
        createdA.StatusCode.Should().Be(HttpStatusCode.OK);
        var idA = (await createdA.Content.ReadFromJsonAsync<CreatedTag>())!.Id;

        using var dup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasttags");
        dup.Headers.Authorization = Bearer(token);
        dup.Content = JsonContent.Create(new { name = "Tech" });
        var dupRes = await _client.SendAsync(dup);
        dupRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await dupRes.Content.ReadFromJsonAsync<CreatedTag>())!.Id.Should().Be(idA);

        using var b = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasttags");
        b.Headers.Authorization = Bearer(token);
        b.Content = JsonContent.Create(new { name = "News" });
        (await _client.SendAsync(b)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var clash = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/podcasttags/{idA}");
        clash.Headers.Authorization = Bearer(token);
        clash.Content = JsonContent.Create(new { name = "News" });
        (await _client.SendAsync(clash)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var map = new HttpRequestMessage(HttpMethod.Get, "/api/v1/podcasttags/map");
        map.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(map)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Songs_PatchValidation_DeleteNotFound_ArtistsEmpty()
    {
        var token = await TokenForAsync($"lib-{Guid.NewGuid():N}");

        using var patchMissing = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/songs/nope.mp3");
        patchMissing.Headers.Authorization = Bearer(token);
        patchMissing.Content = JsonContent.Create(new { title = "x" });
        (await _client.SendAsync(patchMissing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var delMissing = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/songs/nope.mp3");
        delMissing.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(delMissing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var artists = new HttpRequestMessage(HttpMethod.Get, "/api/v1/artists");
        artists.Headers.Authorization = Bearer(token);
        var artistsRes = await _client.SendAsync(artists);
        artistsRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await artistsRes.Content.ReadFromJsonAsync<List<string>>()).Should().BeEmpty();

        using var albums = new HttpRequestMessage(HttpMethod.Get, "/api/v1/albums");
        albums.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(albums)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Songs_Patch_Persists_AndReadsBack()
    {
        var token = await TokenForAsync($"md-{Guid.NewGuid():N}");
        DropSong("edit.mp3");

        // Prime the read cache (like the library list does), then edit.
        using var before = new HttpRequestMessage(HttpMethod.Get, "/api/v1/songs/edit.mp3");
        before.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(before)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/songs/edit.mp3");
        patch.Headers.Authorization = Bearer(token);
        patch.Content = JsonContent.Create(new
        {
            title = "New Title",
            artist = "New Artist",
            album = (string?)null,
            year = (string?)null,
            genre = (string?)null,
            coverArt = (string?)null,
        });
        var patched = await _client.SendAsync(patch);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);

        using var after = new HttpRequestMessage(HttpMethod.Get, "/api/v1/songs/edit.mp3");
        after.Headers.Authorization = Bearer(token);
        var song = await (await _client.SendAsync(after)).Content.ReadFromJsonAsync<EditedSong>();
        song!.Title.Should().Be("New Title");
        song!.Artist.Should().Be("New Artist");
    }

    [Fact]
    public async Task Artist_And_Album_Songs_RoundTrip()
    {
        var token = await TokenForAsync($"detail-{Guid.NewGuid():N}");
        DropSong("detail.mp3");

        using var scan = new HttpRequestMessage(HttpMethod.Post, "/api/v1/songs/scan");
        scan.Headers.Authorization = Bearer(token);
        (await _client.SendAsync(scan)).EnsureSuccessStatusCode();

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/songs/detail.mp3");
        patch.Headers.Authorization = Bearer(token);
        patch.Content = JsonContent.Create(new
        {
            title = "Detail Song",
            artist = "Detail Artist",
            album = "Detail Album",
            year = (string?)null,
            genre = (string?)null,
            coverArt = (string?)null,
        });
        (await _client.SendAsync(patch)).EnsureSuccessStatusCode();

        using var artists = new HttpRequestMessage(HttpMethod.Get, "/api/v1/artists");
        artists.Headers.Authorization = Bearer(token);
        (await (await _client.SendAsync(artists)).Content.ReadFromJsonAsync<List<string>>())!
            .Should().Contain("Detail Artist");

        using var artistSongs = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/artists/Detail%20Artist/songs");
        artistSongs.Headers.Authorization = Bearer(token);
        var artistRes = await _client.SendAsync(artistSongs);
        artistRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await artistRes.Content.ReadFromJsonAsync<List<DetailSong>>())!
            .Should().ContainSingle().Which.Title.Should().Be("Detail Song");

        using var albums = new HttpRequestMessage(HttpMethod.Get, "/api/v1/albums");
        albums.Headers.Authorization = Bearer(token);
        (await (await _client.SendAsync(albums)).Content.ReadFromJsonAsync<List<string>>())!
            .Should().Contain("Detail Album");

        using var albumSongs = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/albums/Detail%20Album/songs");
        albumSongs.Headers.Authorization = Bearer(token);
        var albumRes = await _client.SendAsync(albumSongs);
        albumRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await albumRes.Content.ReadFromJsonAsync<List<DetailSong>>())!
            .Should().ContainSingle().Which.Title.Should().Be("Detail Song");
    }

    private sealed record DetailSong(string File, string Artist, string Title);

    private void DropSong(string file)
    {
        // Minimal valid MPEG frame TagLibSharp round-trips (see MetadataWriterTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        new Random(42).NextBytes(frame.AsSpan(4));
        File.WriteAllBytes(Path.Combine(_lib.SongsDir, file), frame);
    }

    private sealed record EditedSong(string File, string Artist, string Title);

    [Fact]
    public async Task ReadOnlyKey_BlockedFromPlaylistAndTagWrites()
    {
        var token = await TokenForAsync($"ro-{Guid.NewGuid():N}");
        using var keyMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/apikeys");
        keyMsg.Headers.Authorization = Bearer(token);
        keyMsg.Content = JsonContent.Create(new { name = "ro", scopes = new[] { "player:read", "library:read" } });
        var keyRes = await _client.SendAsync(keyMsg);
        keyRes.EnsureSuccessStatusCode();
        var pat = (await keyRes.Content.ReadFromJsonAsync<CreatedKey>())!.Token;

        using var write = new HttpRequestMessage(HttpMethod.Post, "/api/v1/playlists");
        write.Headers.Authorization = Bearer(pat);
        write.Content = JsonContent.Create(new { title = "x" });
        (await _client.SendAsync(write)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var tagWrite = new HttpRequestMessage(HttpMethod.Post, "/api/v1/podcasttags");
        tagWrite.Headers.Authorization = Bearer(pat);
        tagWrite.Content = JsonContent.Create(new { name = "x" });
        (await _client.SendAsync(tagWrite)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/v1/playlists");
        read.Headers.Authorization = Bearer(pat);
        (await _client.SendAsync(read)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record Playlist(long Id, string Title, string? Description, string? Thumbnail);
    private sealed record CreatedTag(long Id);
    private sealed record CreatedKey(string Token);
}

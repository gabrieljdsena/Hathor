using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;

namespace Hathor.Api.Tests;

public sealed class StreamEndpointTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient Client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<(string Token, string File, byte[] Bytes)> SeedSongAsync()
    {
        var token = await _lib.RegisterAsync($"stream-{Guid.NewGuid():N}");
        var file = "range.mp3";
        var bytes = Encoding.ASCII.GetBytes("0123456789abcdef");
        await File.WriteAllBytesAsync(Path.Combine(_lib.SongsDir, file), bytes);
        return (token, file, bytes);
    }

    private static HttpRequestMessage StreamRequest(string file, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/songs/{file}/stream");
        if (token is not null) request.Headers.Authorization = TempLibraryFixture.Bearer(token);
        return request;
    }

    [Fact]
    public async Task Stream_WithoutToken_ReturnsUnauthorized()
    {
        var (_, file, _) = await SeedSongAsync();
        using var request = StreamRequest(file);
        using var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Stream_WithBearerToken_ServesFullAudio()
    {
        var (token, file, bytes) = await SeedSongAsync();
        using var request = StreamRequest(file, token);
        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("audio/mpeg");
        response.Headers.AcceptRanges.Should().Contain("bytes");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Fact]
    public async Task Stream_WithQueryToken_ServesAudio()
    {
        var (token, file, bytes) = await SeedSongAsync();
        using var response = await Client.GetAsync(
            $"/api/v1/songs/{file}/stream?token={Uri.EscapeDataString(token)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Fact]
    public async Task Stream_WithRange_ReturnsPartialContent()
    {
        var (token, file, bytes) = await SeedSongAsync();
        using var request = StreamRequest(file, token);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange.Should().NotBeNull();
        response.Content.Headers.ContentRange!.From.Should().Be(0);
        response.Content.Headers.ContentRange.To.Should().Be(3);
        response.Content.Headers.ContentRange.Length.Should().Be(bytes.Length);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[..4]);
    }

    [Fact]
    public async Task Stream_WithInvalidRange_ReturnsRequestedRangeNotSatisfiable()
    {
        var (token, file, _) = await SeedSongAsync();
        using var request = StreamRequest(file, token);
        request.Headers.Range = new RangeHeaderValue(100, 200);
        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Fact]
    public async Task Stream_MissingFile_ReturnsNotFound()
    {
        var token = await _lib.RegisterAsync($"stream-{Guid.NewGuid():N}");
        using var request = StreamRequest("missing.mp3", token);
        using var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PodcastStream_WithRange_ReturnsPartialContent()
    {
        var token = await _lib.RegisterAsync($"stream-{Guid.NewGuid():N}");
        const string file = "episode.mp3";
        var bytes = Encoding.ASCII.GetBytes("podcast-bytes");
        await File.WriteAllBytesAsync(Path.Combine(_lib.PodcastsDir, file), bytes);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/podcasts/{file}/stream");
        request.Headers.Authorization = TempLibraryFixture.Bearer(token);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        using var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[..4]);
    }
}

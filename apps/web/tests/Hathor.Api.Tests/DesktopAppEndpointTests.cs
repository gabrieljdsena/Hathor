using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

// Endpoint contract used by the desktop updater: auth is required, metadata
// reports the highest published version, and downloads stream the exact zip.
public sealed class DesktopAppEndpointTests : IAsyncLifetime
{
    private readonly string _packageDir = Path.Combine(
        Path.GetTempPath(), "hathor-desktop-it-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibraryFixture _lib;
    private HttpClient Client => _lib.Client;

    public DesktopAppEndpointTests()
    {
        Directory.CreateDirectory(_packageDir);
        _lib = new TempLibraryFixture(new Dictionary<string, string?>
        {
            ["DesktopApp:PackageDirectory"] = _packageDir,
        });
    }

    public Task InitializeAsync() => _lib.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _lib.DisposeAsync();
        try { Directory.Delete(_packageDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Latest_RequiresAuth()
    {
        (await Client.GetAsync("/api/v1/desktop/latest"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Latest_NotFound_WhenNoPackagePublished()
    {
        var token = await _lib.RegisterAsync($"desktop-{Guid.NewGuid():N}");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/desktop/latest");
        req.Headers.Authorization = TempLibraryFixture.Bearer(token);

        (await Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Latest_And_Download_Roundtrip()
    {
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 0xde, 0xad, 0xbe, 0xef };
        File.WriteAllBytes(Path.Combine(_packageDir, "hathor-desktop-6-1.0.3.zip"), bytes);
        File.WriteAllBytes(Path.Combine(_packageDir, "hathor-desktop-9-0.9.9-older.zip"), [1]);
        var token = await _lib.RegisterAsync($"desktop-{Guid.NewGuid():N}");

        using var latestReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/desktop/latest");
        latestReq.Headers.Authorization = TempLibraryFixture.Bearer(token);
        var latestRes = await Client.SendAsync(latestReq);
        latestRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var release = await latestRes.Content.ReadFromJsonAsync<Release>();
        release!.VersionCode.Should().Be(9);
        release.VersionName.Should().Be("0.9.9-older");
        release.FileName.Should().Be("hathor-desktop-9-0.9.9-older.zip");
        release.SizeBytes.Should().Be(1);
        release.DownloadPath.Should().Be(
            "/api/v1/desktop/download/hathor-desktop-9-0.9.9-older.zip");

        using var dlReq = new HttpRequestMessage(HttpMethod.Get, release.DownloadPath);
        dlReq.Headers.Authorization = TempLibraryFixture.Bearer(token);
        var dlRes = await Client.SendAsync(dlReq);
        dlRes.StatusCode.Should().Be(HttpStatusCode.OK);
        dlRes.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        (await dlRes.Content.ReadAsByteArrayAsync()).Should().Equal([1]);

        using var rangeReq = new HttpRequestMessage(HttpMethod.Get, release.DownloadPath);
        rangeReq.Headers.Authorization = TempLibraryFixture.Bearer(token);
        rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        var rangeRes = await Client.SendAsync(rangeReq);
        rangeRes.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await rangeRes.Content.ReadAsByteArrayAsync()).Should().Equal([1]);
    }

    [Fact]
    public async Task Download_RejectsUnknownNames()
    {
        var token = await _lib.RegisterAsync($"desktop-{Guid.NewGuid():N}");
        foreach (var file in new[] { "hathor-desktop-6-1.0.3.zip", "other.zip" })
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/desktop/download/{file}");
            req.Headers.Authorization = TempLibraryFixture.Bearer(token);
            var res = await Client.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private sealed record Release(
        int VersionCode,
        string VersionName,
        string FileName,
        long SizeBytes,
        string Sha256,
        DateTimeOffset PublishedAtUtc,
        string DownloadPath);
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

// Endpoint contract used by the Android updater: auth is required, metadata
// reports the highest published version, and downloads stream the exact APK.
public sealed class MobileAppEndpointTests : IAsyncLifetime
{
    private readonly string _apkDir = Path.Combine(
        Path.GetTempPath(), "hathor-apk-it-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibraryFixture _lib;
    private HttpClient Client => _lib.Client;

    public MobileAppEndpointTests()
    {
        Directory.CreateDirectory(_apkDir);
        _lib = new TempLibraryFixture(new Dictionary<string, string?>
        {
            ["MobileApp:ApkDirectory"] = _apkDir,
        });
    }

    public Task InitializeAsync() => _lib.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _lib.DisposeAsync();
        try { Directory.Delete(_apkDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Latest_RequiresAuth()
    {
        (await Client.GetAsync("/api/v1/app/latest"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Latest_NotFound_WhenNoApkPublished()
    {
        var token = await _lib.RegisterAsync($"app-{Guid.NewGuid():N}");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/app/latest");
        req.Headers.Authorization = TempLibraryFixture.Bearer(token);

        (await Client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Latest_And_Download_Roundtrip()
    {
        var bytes = new byte[] { 0x50, 0x4b, 0x03, 0x04, 0xde, 0xad, 0xbe, 0xef };
        File.WriteAllBytes(Path.Combine(_apkDir, "hathor-6-0.4.3-hathor.apk"), bytes);
        File.WriteAllBytes(Path.Combine(_apkDir, "hathor-9-0.9.9-older.apk"), [1]);
        var token = await _lib.RegisterAsync($"app-{Guid.NewGuid():N}");

        using var latestReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/app/latest");
        latestReq.Headers.Authorization = TempLibraryFixture.Bearer(token);
        var latestRes = await Client.SendAsync(latestReq);
        latestRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var release = await latestRes.Content.ReadFromJsonAsync<Release>();
        release!.VersionCode.Should().Be(9);
        release.VersionName.Should().Be("0.9.9-older");
        release.FileName.Should().Be("hathor-9-0.9.9-older.apk");
        release.SizeBytes.Should().Be(1);
        release.DownloadPath.Should().Be("/api/v1/app/download/hathor-9-0.9.9-older.apk");

        using var dlReq = new HttpRequestMessage(HttpMethod.Get, release.DownloadPath);
        dlReq.Headers.Authorization = TempLibraryFixture.Bearer(token);
        var dlRes = await Client.SendAsync(dlReq);
        dlRes.StatusCode.Should().Be(HttpStatusCode.OK);
        dlRes.Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.android.package-archive");
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
        var token = await _lib.RegisterAsync($"app-{Guid.NewGuid():N}");
        foreach (var file in new[] { "hathor-6-0.4.3-hathor.apk", "other.apk" })
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/app/download/{file}");
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

using System.Net;
using FluentAssertions;
using Hathor.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class MaintenanceTests
{
    [Fact]
    public void ToolsDir_ExplicitConfig_Wins()
    {
        FfmpegPaths.ToolsDir("C:/custom/tools", "data")
            .Should().Be(Path.GetFullPath("C:/custom/tools"));
    }

    [Fact]
    public void ToolsDir_Defaults_ToSiblingOfStorageRoot()
    {
        // Sibling "tools" of the media root (Docker: /data/library → /data/tools).
        var storage = Path.Combine("media", "library");
        var expected = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storage))!, "tools");
        FfmpegPaths.ToolsDir(null, storage).Should().Be(expected);
    }

    [Fact]
    public void FindOnPath_Finds_Executable_In_Given_Path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hathor-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            File.WriteAllText(Path.Combine(dir, exe), "stub");
            FfmpegPaths.FindOnPath(exe, dir).Should().Be(Path.Combine(dir, exe));
            FfmpegPaths.FindOnPath("nope-missing-xyz", dir).Should().BeNull();
            FfmpegPaths.FindOnPath("nope-missing-xyz", "").Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DownloadSource_WindowsX64_IsGyanZip()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = FfmpegInstaller.DownloadSource();
        source.Should().NotBeNull();
        source!.Value.Url.Should().Contain("gyan.dev");
        source.Value.ArchiveName.Should().EndWith(".zip");
        source.Value.ExeRelPath.Should().EndWith("ffmpeg.exe");
    }

    [Fact]
    public void Installer_Status_StartsIdle()
    {
        var installer = new FfmpegInstaller(
            Substitute.For<IConfiguration>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<FfmpegInstaller>>());
        installer.Status().State.Should().Be("idle");
    }

    [Theory]
    [InlineData("6.6.2", "6.6.2", 0)]
    [InlineData("6.5.0", "6.6.2", -1)]
    [InlineData("6.6.2", "6.5.0", 1)]
    [InlineData(null, "6.6.2", 0)]
    [InlineData("6.6.2", null, 0)]
    public void CompareVersions_OrdersReleases(string? current, string? latest, int expected) =>
        Math.Sign(SystemProbe.CompareVersions(current, latest)).Should().Be(expected);

    [Fact]
    public async Task CheckLibraryUpdates_PicksLatestStable()
    {
        const string nuget = """{"versions":["6.5.0","6.6.2","9.9.9-beta"]}""";
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("nuget").Returns(new HttpClient(new FakeHandler(nuget)));
        var probe = new SystemProbe(
            Substitute.For<IConfiguration>(), factory, Substitute.For<ILogger<SystemProbe>>());

        var libs = await probe.CheckLibraryUpdatesAsync();

        var yt = libs.Should().ContainSingle(l => l.Package == "YoutubeExplode").Subject;
        yt.Latest.Should().Be("6.6.2"); // prereleases skipped
        yt.Status.Should().BeOneOf("up-to-date", "update-available", "unknown");
    }

    [Fact]
    public async Task CheckLibraryUpdates_NuGetDown_ReportsUnknown()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("nuget").Returns(new HttpClient(new FailingHandler()));
        var probe = new SystemProbe(
            Substitute.For<IConfiguration>(), factory, Substitute.For<ILogger<SystemProbe>>());

        var libs = await probe.CheckLibraryUpdatesAsync();

        libs.Should().OnlyContain(l => l.Status == "unknown" && l.Latest == null);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("nope"));
    }
}

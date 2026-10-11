using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Hathor.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class YoutubeProtectionTests
{
    private static IConfiguration TempConfig(out string toolsDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "hathor-pot-" + Guid.NewGuid().ToString("N"));
        toolsDir = Path.Combine(root, "tools");
        // Note: GetValue reads via GetSection (not the indexer), so stub
        // the section value — an indexer stub would be silently ignored.
        var config = Substitute.For<IConfiguration>();
        var section = Substitute.For<IConfigurationSection>();
        section.Value.Returns(toolsDir);
        config.GetSection("FFmpeg:ToolsDir").Returns(section);
        return config;
    }

    [Fact]
    public void PotArgs_DefaultPort_OnlyPluginDirs()
    {
        PotArgs.Build("/tools/plugins", 4416)
            .Should().Equal("--plugin-dirs", "/tools/plugins");
    }

    [Fact]
    public void PotArgs_CustomPort_AddsBaseUrl()
    {
        PotArgs.Build("/tools/plugins", 4417).Should().Equal(
            "--plugin-dirs", "/tools/plugins",
            "--extractor-args", "youtubepot-bgutilhttp:base_url=http://127.0.0.1:4417");
    }

    [Fact]
    public void PotArgs_NoStateFile_Empty()
    {
        var config = TempConfig(out _);
        PotArgs.FromState(config).Should().BeEmpty();
    }

    [Fact]
    public void PotArgs_EnabledWithoutPlugin_Empty()
    {
        var config = TempConfig(out _);
        new PotState(true, "2.0.2", 4416).Save(config);
        PotArgs.FromState(config).Should().BeEmpty();
    }

    [Fact]
    public void PotArgs_EnabledWithPlugin_PluginDirs()
    {
        var config = TempConfig(out var toolsDir);
        new PotState(true, "2.0.2", 4416).Save(config);
        var pluginDir = Path.Combine(toolsDir, "youtube-pot", "plugins");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "bgutil-ytdlp-pot-provider-2.0.2.zip"), "stub");
        PotArgs.FromState(config)
            .Should().Equal("--plugin-dirs", pluginDir);
    }

    [Fact]
    public void PotArgs_ExtractedTreeAlone_DoesNotCount()
    {
        // Regression: an extracted yt_dlp_plugins tree at the plugin-dir
        // root is invisible to yt-dlp's loader ("Plugin directories: none").
        var config = TempConfig(out var toolsDir);
        new PotState(true, "2.0.2", 4416).Save(config);
        var tree = Path.Combine(toolsDir, "youtube-pot", "plugins", "yt_dlp_plugins", "extractor");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "getpot_bgutil_http.py"), "stub");
        PotArgs.FromState(config).Should().BeEmpty();
    }

    [Fact]
    public void RemoveLegacyExtractedPlugin_ClearsTheTree()
    {
        var config = TempConfig(out var toolsDir);
        var tree = Path.Combine(toolsDir, "youtube-pot", "plugins", "yt_dlp_plugins");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "x.py"), "stub");
        PotProtectionService.RemoveLegacyExtractedPlugin(config);
        Directory.Exists(tree).Should().BeFalse();
    }

    [Fact]
    public void DeleteDirResilient_MissingDir_IsNoOp()
    {
        var missing = Path.Combine(Path.GetTempPath(), "hathor-pot-missing-" + Guid.NewGuid().ToString("N"));
        FluentActions.Invoking(() => PotProtectionService.DeleteDirResilient(missing)).Should().NotThrow();
    }

    [Fact]
    public void StdoutShowsPotProviders_MatchesLoaderLine()
    {
        PotProtectionService.StdoutShowsPotProviders(
            "[debug] [youtube] [pot] PO Token Providers: bgutil:http-2.0.2 (external)").Should().BeTrue();
        PotProtectionService.StdoutShowsPotProviders(
            "[debug] [youtube] [pot] PO Token Providers: none").Should().BeFalse();
        PotProtectionService.StdoutShowsPotProviders("").Should().BeFalse();
    }

    [Fact]
    public void PotPaths_Urls_PinVersion()
    {
        PotPaths.PluginZipUrl("2.0.2").Should().Be(
            "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/2.0.2/bgutil-ytdlp-pot-provider.zip");
        PotPaths.ServerTarballUrl("2.0.2").Should().Be(
            "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/2.0.2.tar.gz");
    }

    [Fact]
    public void PotState_RoundTrips_ThroughTempToolsDir()
    {
        var config = TempConfig(out _);
        PotState.TryLoad(config).Should().BeNull();
        new PotState(true, "2.0.2", 4417).Save(config);
        PotState.TryLoad(config).Should().Be(new PotState(true, "2.0.2", 4417));
    }

    [Fact]
    public void ExtractServerSubtree_TakesOnlyServerFiles_SkipsTraversal()
    {
        var dest = Path.Combine(Path.GetTempPath(), "hathor-pot-tar-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var tarball = MakeTarball();
            using var gz = new GZipStream(tarball, CompressionMode.Decompress);
            PotProtectionService.ExtractServerSubtree(gz, dest);

            File.ReadAllText(Path.Combine(dest, "build", "main.js")).Should().Be("js");
            File.Exists(Path.Combine(dest, "..", "evil.txt")).Should().BeFalse();
            File.Exists(Path.Combine(dest, "README.md")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Service_Status_StartsDisabled_Idle()
    {
        var config = TempConfig(out _);
        // Disabled state on a port nothing listens on (the dev machine may
        // run a real provider on 4416 — never assert on the default port).
        new PotState(false, "2.0.2", ClosedPort()).Save(config);
        var svc = new PotProtectionService(
            config,
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<PotProtectionService>>());
        var status = svc.Status();
        status.Enabled.Should().BeFalse();
        status.ProvisionState.Should().Be("idle");
        status.ProviderRunning.Should().BeFalse();
    }

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Service_Disable_WhenNeverEnabled_StaysDisabled()
    {
        var config = TempConfig(out _);
        var svc = new PotProtectionService(
            config,
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<PotProtectionService>>());
        var status = svc.Disable();
        status.Enabled.Should().BeFalse();
    }

    [Fact]
    public void BuildArgumentList_AppendsPotArgs_AfterAuth()
    {
        var args = Hathor.Infrastructure.Ingest.YtDlpDownloader.BuildArgumentList(
            "https://www.youtube.com/watch?v=abc", "o.mp3", null, null, null, null,
            ["--plugin-dirs", "/tools/plugins"]);
        args.Should().ContainInOrder("--plugin-dirs", "/tools/plugins");
        args.Last().Should().Be("https://www.youtube.com/watch?v=abc");
    }

    private static MemoryStream MakeTarball()
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gz, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                };
                writer.WriteEntry(entry);
            }
            Add("bgutil-ytdlp-pot-provider-2.0.2/server/build/main.js", "js");
            Add("bgutil-ytdlp-pot-provider-2.0.2/server/package.json", "{}");
            Add("bgutil-ytdlp-pot-provider-2.0.2/README.md", "readme");
            Add("bgutil-ytdlp-pot-provider-2.0.2/server/../../evil.txt", "evil");
        }
        ms.Position = 0;
        return ms;
    }
}

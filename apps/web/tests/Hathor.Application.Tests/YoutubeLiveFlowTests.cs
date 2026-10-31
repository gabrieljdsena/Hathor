using FluentAssertions;
using Hathor.Infrastructure.Ingest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit.Abstractions;

namespace Hathor.Application.Tests;

// Live end-to-end check of the application's own YouTube pipeline
// (YtDlpDownloader with app-built args, incl. --plugin-dirs for the
// PO-token provider). Runs ONLY when HATHOR_LIVE_YOUTUBE=1 — it hits
// youtube.com and downloads a real MP3 — otherwise skipped so CI stays
// hermetic. HATHOR_TEST_YTDLP can point at a specific yt-dlp binary.
public sealed class YoutubeLiveFlowTests(ITestOutputHelper output)
{
    private const string VideoUrl = "https://www.youtube.com/watch?v=Z3HV_tdUfUw";

    private bool LiveEnabled()
    {
        if (Environment.GetEnvironmentVariable("HATHOR_LIVE_YOUTUBE") == "1") return true;
        output.WriteLine("Skipped: set HATHOR_LIVE_YOUTUBE=1 to run the live YouTube flow test.");
        return false;
    }

    private static IConfiguration LiveConfig(out string workDir)
    {
        workDir = Path.Combine(Path.GetTempPath(), "hathor-live-" + Guid.NewGuid().ToString("N"));
        var toolsDir = Path.Combine(workDir, "tools");
        var potDir = Path.Combine(toolsDir, "youtube-pot", "plugins");
        Directory.CreateDirectory(potDir);

        // Mirror the provisioned server layout: enabled state + plugin zip.
        // The shared PO-token provider on 127.0.0.1:4416 is reused.
        var realZip = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "tools", "youtube-pot", "plugins", "bgutil-ytdlp-pot-provider-2.0.2.zip");
        File.Copy(realZip, Path.Combine(potDir, Path.GetFileName(realZip)));
        File.WriteAllText(
            Path.Combine(toolsDir, "youtube-pot", "state.json"),
            """{"Enabled":true,"Version":"2.0.2","Port":4416,"Verified":false}""");

        var config = Substitute.For<IConfiguration>();
        StubSection(config, "FFmpeg:ToolsDir", toolsDir);
        StubSection(config, "Downloads:ResolveTimeoutSec", "180");
        var exe = Environment.GetEnvironmentVariable("HATHOR_TEST_YTDLP");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            throw new FileNotFoundException(
                "Set HATHOR_TEST_YTDLP to a yt-dlp binary to run the live YouTube flow test.");
        // Note: YtDlpDownloader reads YtDlp:Path via the indexer (while
        // ToolsDir resolves via GetSection) — stub both paths.
        config["YtDlp:Path"].Returns(exe);
        return config;
    }

    private static void StubSection(IConfiguration config, string key, string value)
    {
        var section = Substitute.For<IConfigurationSection>();
        section.Value.Returns(value);
        config.GetSection(key).Returns(section);
    }

    [Fact]
    public async Task Live_InfoFetch_ResolvesTitle()
    {
        if (!LiveEnabled()) return;
        var config = LiveConfig(out var workDir);
        try
        {
            var engine = new YtDlpDownloader(config, NullLogger<YtDlpDownloader>.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var info = await engine.GetInfoAsync(VideoUrl, cts.Token);
            info.Id.Should().Be("Z3HV_tdUfUw");
            info.Title.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Live_DownloadAudio_ProducesMp3()
    {
        if (!LiveEnabled()) return;
        var config = LiveConfig(out var workDir);
        try
        {
            var engine = new YtDlpDownloader(config, NullLogger<YtDlpDownloader>.Instance);
            var dest = Path.Combine(workDir, "test.mp3");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await engine.DownloadAudioAsync(VideoUrl, dest, _ => { }, null, cts.Token);
            File.Exists(dest).Should().BeTrue();
            new FileInfo(dest).Length.Should().BeGreaterThan(100_000);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }
}

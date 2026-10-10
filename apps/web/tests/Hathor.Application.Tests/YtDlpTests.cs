using FluentAssertions;
using Hathor.Application.Ports;
using Hathor.Infrastructure.Ingest;
using Hathor.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Hathor.Application.Tests;

public sealed class YtDlpTests
{
    [Theory]
    [InlineData("PROG  45.2%", 0.452)]
    [InlineData("PROG 100%", 1.0)]
    [InlineData("PROG   0.0%", 0.0)]
    public void TryParseProgress_ParsesTemplateLines(string line, double expected)
    {
        YtDlpDownloader.TryParseProgress(line, out var value).Should().BeTrue();
        value.Should().BeApproximately(expected, 0.0001);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[download]  45.2% of song.mp3")]
    [InlineData("PROG N/A%")]
    public void TryParseProgress_RejectsNonTemplateLines(string? line) =>
        YtDlpDownloader.TryParseProgress(line, out _).Should().BeFalse();

    [Fact]
    public void BuildArgumentList_ExtractsMp3_WithOutputLast_Url()
    {
        var args = YtDlpDownloader.BuildArgumentList(
            "https://www.youtube.com/watch?v=abc", "/tmp/x/qid.mp3",
            null, null, null, null);

        args.Should().ContainInOrder("-x", "--audio-format", "mp3");
        args.Should().Contain("--force-overwrites");
        args.Should().Contain("--no-playlist");
        args.Should().ContainInOrder("-o", "/tmp/x/qid.mp3");
        args.Last().Should().Be("https://www.youtube.com/watch?v=abc");
        // Multi-word values (progress template) stay single ArgumentList
        // entries — no shell quoting needed.
        args.Should().Contain("PROG %(progress._percent_str)s");
    }

    [Fact]
    public void BuildArgumentList_AddsCookiesAndFfmpegLocation()
    {
        var args = YtDlpDownloader.BuildArgumentList(
            "https://example/u", "o.mp3", "/tools",
            "chrome", "/data/cookies.txt", null);

        args.Should().ContainInOrder("--ffmpeg-location", "/tools");
        args.Should().ContainInOrder("--cookies-from-browser", "chrome");
        args.Should().ContainInOrder("--cookies", "/data/cookies.txt");
    }

    [Fact]
    public void BuildArgumentList_SplitsExtraArgs_RespectingQuotes()
    {
        var args = YtDlpDownloader.BuildArgumentList(
            "u", "o.mp3", null, null, null, "--proxy http://x:8080 --user-agent \"My Agent\"");

        args.Should().ContainInOrder("--proxy", "http://x:8080");
        args.Should().Contain("My Agent");
    }

    [Theory]
    [InlineData("--a b --c", new[] { "--a", "b", "--c" })]
    [InlineData("  ", new string[0])]
    [InlineData(null, new string[0])]
    [InlineData("'single quoted' \"double quoted\"", new[] { "single quoted", "double quoted" })]
    public void SplitExtraArgs_Splits(string? extra, string[] expected) =>
        YtDlpDownloader.SplitExtraArgs(extra).Should().Equal(expected);

    [Fact]
    public void Resolve_ExplicitConfig_Wins()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var config = new StubConfig(new Dictionary<string, string?>
            {
                ["YtDlp:Path"] = exe,
            });
            YtDlpPaths.Resolve(config).Should().Be(exe);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void Resolve_FallsBack_ToToolsDirInstall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hathor-ytdlp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, YtDlpPaths.ExeName), "stub");
            var config = new StubConfig(new Dictionary<string, string?>
            {
                ["FFmpeg:ToolsDir"] = dir,
            });
            YtDlpPaths.Resolve(config).Should().Be(Path.Combine(dir, YtDlpPaths.ExeName));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Resolve_Missing_ThrowsActionableError()
    {
        var config = new StubConfig(new Dictionary<string, string?>
        {
            ["FFmpeg:ToolsDir"] = Path.Combine(Path.GetTempPath(), "hathor-ytdlp-nope-" + Guid.NewGuid().ToString("N")),
        });
        // Empty PATH forces a miss regardless of the machine's installs.
        YtDlpPaths.FindOnPath(YtDlpPaths.ExeName, "").Should().BeNull();
        var act = () => YtDlpPaths.Resolve(config);
        // Resolve consults the real PATH, so it may find a machine install;
        // when it misses, the error must point at Settings/env.
        try { act(); }
        catch (InvalidOperationException ex)
        {
            ex.Message.Should().Contain("yt-dlp");
        }
    }

    [Fact]
    public void DownloadUrl_MatchesPlatform()
    {
        var url = YtDlpInstaller.DownloadUrl();
        url.Should().NotBeNull();
        if (OperatingSystem.IsWindows())
            url.Should().EndWith("yt-dlp.exe");
        else
            url.Should().Contain("yt-dlp");
    }

    [Fact]
    public void YtDlpInstaller_Status_StartsIdle()
    {
        var installer = new YtDlpInstaller(
            Substitute.For<IConfiguration>(),
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ILogger<YtDlpInstaller>>());
        installer.Status().State.Should().Be("idle");
    }

    [Fact]
    public void YtDlpInstaller_ForceRedownload_DeletesInstalledBinary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hathor-ytdlp-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, YtDlpPaths.ExeName);
            File.WriteAllText(exe, "stale");
            var config = new StubConfig(new Dictionary<string, string?>
            {
                ["FFmpeg:ToolsDir"] = dir,
            });
            var installer = new YtDlpInstaller(
                config,
                Substitute.For<IHttpClientFactory>(),
                Substitute.For<ILogger<YtDlpInstaller>>());

            var start = installer.StartDownload(force: true);

            start.State.Should().Be("downloading");
            File.Exists(exe).Should().BeFalse("force update deletes the stale binary first");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void YtDlpInstaller_ForceRedownload_RefusesPinnedPath()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var config = new StubConfig(new Dictionary<string, string?>
            {
                ["YtDlp:Path"] = exe,
            });
            var installer = new YtDlpInstaller(
                config,
                Substitute.For<IHttpClientFactory>(),
                Substitute.For<ILogger<YtDlpInstaller>>());

            var act = () => installer.StartDownload(force: true);

            act.Should().Throw<InvalidOperationException>().WithMessage("*YtDlp:Path*");
            File.Exists(exe).Should().BeTrue("pinned user-managed binary is never deleted");
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public async Task Fallback_PrimarySuccess_NeverTouchesYtDlp()
    {
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        await engine.DownloadAudioAsync("u", "o.mp3", _ => { });

        await fallback.DidNotReceive().DownloadAudioAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_PrimaryFailure_FallsBackToYtDlp()
    {
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("403 Forbidden"));
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        fallback.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        await engine.DownloadAudioAsync("u", "o.mp3", _ => { });

        await fallback.Received(1).DownloadAudioAsync(
            "u", "o.mp3", Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_BothFail_ReportsBothErrors()
    {
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("403 Forbidden"));
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        fallback.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("yt-dlp failed (exit 1)"));
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        var act = () => engine.DownloadAudioAsync("u", "o.mp3", _ => { });

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*YoutubeExplode*403*yt-dlp*exit 1*");
    }

    [Fact]
    public async Task Fallback_TrueCancellation_SkipsFallback_AndRethrows()
    {
        // User cancel / silence watchdog / shutdown: the token is dead, so
        // a fallback would die instantly with the same confusing message —
        // and wrapping it would mark the job "failed" instead of cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        var act = () => engine.DownloadAudioAsync("u", "o.mp3", _ => { }, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await fallback.DidNotReceive().DownloadAudioAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_PrimaryInternalTimeout_StillFallsBack()
    {
        // YoutubeExplode can time out internally (TaskCanceledException)
        // while our token is alive — the fallback stays viable.
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("A task was canceled."));
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        fallback.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        await engine.DownloadAudioAsync("u", "o.mp3", _ => { }, ct: CancellationToken.None);

        await fallback.Received(1).DownloadAudioAsync(
            "u", "o.mp3", Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_CancelledMidFallback_RethrowsInsteadOfWrapping()
    {
        using var cts = new CancellationTokenSource();
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("403 Forbidden"));
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        fallback.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OperationCanceledException()));
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());
        cts.Cancel();

        var act = () => engine.DownloadAudioAsync("u", "o.mp3", _ => { }, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Fallback_DelegatesSearchInfoPreview_ToPrimary()
    {
        var primary = Substitute.For<IDownloadEngine>();
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        await engine.SearchAsync("q", 5);
        await engine.GetInfoAsync("u");
        await engine.GetPreviewUrlAsync("u");

        await primary.Received(1).SearchAsync("q", 5, Arg.Any<CancellationToken>());
        await primary.Received(1).GetInfoAsync("u", Arg.Any<CancellationToken>());
        await primary.Received(1).GetPreviewUrlAsync("u", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_InfoFailure_FallsBackToYtDlp()
    {
        // YoutubeExplode misreports existing videos as unavailable on
        // bot-check pages: info must fall back instead of failing the job.
        var primary = Substitute.For<IDownloadEngine>();
        primary.GetInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Video 'x' is not available."));
        var info = new VideoInfoDto("x", "Title", "Uploader", "https://u");
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        fallback.GetInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(info);
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        (await engine.GetInfoAsync("u")).Should().Be(info);
    }

    [Fact]
    public async Task Fallback_InfoCancellation_RethrowsWithoutFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var primary = Substitute.For<IDownloadEngine>();
        primary.GetInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        var act = () => engine.GetInfoAsync("u", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await fallback.DidNotReceive().GetInfoAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_ForwardsQualifiedPhases()
    {
        var seen = new List<string>();
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => ci.ArgAt<Action<string>?>(3)?.Invoke("manifest"));
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        await engine.DownloadAudioAsync("u", "o.mp3", _ => { }, seen.Add);

        seen.Should().ContainInOrder("primary", "primary/manifest");
        await fallback.DidNotReceive().DownloadAudioAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Fallback_QualifiesFallbackPhases()
    {
        var seen = new List<string>();
        var primary = Substitute.For<IDownloadEngine>();
        primary.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("403"));
        var fallback = Substitute.For<YtDlpDownloader>(
            Substitute.For<IConfiguration>(), Substitute.For<ILogger<YtDlpDownloader>>());
        fallback.DownloadAudioAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Action<double>>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => ci.ArgAt<Action<string>?>(3)?.Invoke("ytdlp"));
        var engine = new FallbackDownloadEngine(
            primary, fallback, Substitute.For<ILogger<FallbackDownloadEngine>>());

        await engine.DownloadAudioAsync("u", "o.mp3", _ => { }, seen.Add);

        seen.Should().ContainInOrder("primary", "fallback", "fallback/ytdlp");
    }

    [Theory]
    [InlineData(null, 120)]
    [InlineData("10", 30)]
    [InlineData("120", 120)]
    [InlineData("9999", 600)]
    public void ManifestTimeout_ClampsConfig(string? seconds, int expected)
    {
        var values = new Dictionary<string, string?>();
        if (seconds is not null) values["Downloads:ManifestTimeoutSec"] = seconds;
        Hathor.Infrastructure.Ingest.YoutubeExplodeEngine.ManifestTimeout(new StubConfig(values))
            .Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Theory]
    [InlineData(null, 180)]
    [InlineData("10", 30)]
    [InlineData("180", 180)]
    [InlineData("9999", 600)]
    public void ResolveTimeout_ClampsConfig(string? seconds, int expected)
    {
        var values = new Dictionary<string, string?>();
        if (seconds is not null) values["Downloads:ResolveTimeoutSec"] = seconds;
        YtDlpDownloader.ResolveTimeout(new StubConfig(values))
            .Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public void ParseVideoJson_ParsesDumpSingleJson()
    {
        const string json = """
            {"id": "56fJw8HUGuU", "title": "Tiny Desk", "uploader": "NPR Music",
             "webpage_url": "https://www.youtube.com/watch?v=56fJw8HUGuU"}
            """;
        var dto = YtDlpDownloader.ParseVideoJson(json);
        dto.Id.Should().Be("56fJw8HUGuU");
        dto.Title.Should().Be("Tiny Desk");
        dto.Uploader.Should().Be("NPR Music");
        dto.PageUrl.Should().Be("https://www.youtube.com/watch?v=56fJw8HUGuU");
    }

    [Fact]
    public void ParseVideoJson_MapsMissingUploaderToNull()
    {
        const string json = """{"id": "x", "title": "T", "uploader": "NA", "webpage_url": ""}""";
        YtDlpDownloader.ParseVideoJson(json).Uploader.Should().BeNull();
    }

    [Fact]
    public void ParseVideoJson_FallsBackToId_WithoutTitle()
    {
        const string json = """{"id": "x"}""";
        var dto = YtDlpDownloader.ParseVideoJson(json);
        dto.Title.Should().Be("x");
        dto.Uploader.Should().BeNull();
    }

    [Fact]
    public void ParseVideoJson_RejectsGarbage()
    {
        var act = () => YtDlpDownloader.ParseVideoJson("not json at all");
        act.Should().Throw<InvalidOperationException>();
    }

    // Minimal IConfiguration stub (same shape as SongReadModelTests').
    private sealed class StubConfig(Dictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key]
        {
            get => values.TryGetValue(key, out var v) ? v : null;
            set => values[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
        public IConfigurationSection GetSection(string key) => new StubSection(this, key);

        private sealed class StubSection(StubConfig root, string key) : IConfigurationSection
        {
            public string? this[string k]
            {
                get => root[$"{key}:{k}"];
                set => root[$"{key}:{k}"] = value;
            }

            public string Key => key;
            public string Path => key;
            public string? Value { get => root[key]; set => root[key] = value; }
            public IEnumerable<IConfigurationSection> GetChildren() => [];
            public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
                new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
            public IConfigurationSection GetSection(string k) => new StubSection(root, $"{key}:{k}");
        }
    }
}

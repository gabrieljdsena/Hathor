using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Ingest;

// yt-dlp audio download (fallback when YoutubeExplode stream URLs are
// rejected with 403/bot-checks). Shells out to a yt-dlp binary resolved
// as YtDlp:Path → app-downloaded (tools dir) → PATH, and extracts audio
// straight to MP3 so no separate FFmpeg transcode step is needed
// (yt-dlp still uses FFmpeg under the hood — its location is passed via
// --ffmpeg-location when we can resolve one).
// (Unsealed so tests can substitute the process calls; the virtual
// members are DownloadAudioAsync and GetInfoAsync.)
public class YtDlpDownloader(
    IConfiguration config,
    ILogger<YtDlpDownloader> log)
{
    private string? _exe;
    private bool _probed;

    public virtual async Task DownloadAudioAsync(string url, string destMp3Path,
        Action<double> progress, Action<string>? onPhase = null, CancellationToken ct = default)
    {
        var exe = Locate();
        var dir = Path.GetDirectoryName(destMp3Path)!;
        Directory.CreateDirectory(dir);
        log.LogDebug("yt-dlp downloading {Url} to {Dest}", url, destMp3Path);
        try { onPhase?.Invoke("ytdlp"); } catch { }
        // Phase-transition heartbeat for the queue's silence watchdog
        // (resolve can take a while; progress lines only flow mid-download).
        try { progress(0.01); } catch { }
        // Stale partials from a previous attempt must not confuse this run
        // (--force-overwrites handles the target, but .part files linger).
        TryDelete(destMp3Path);
        TryDelete(destMp3Path + ".part");

        var args = BuildArgumentList(
            url, destMp3Path, FfmpegDir(),
            config["YtDlp:CookiesFromBrowser"],
            config["YtDlp:CookiesFile"],
            config["YtDlp:ExtraArgs"]);
        var tail = new List<string>();
        var progressSeen = 0;
        void OnLine(string? line)
        {
            if (line is null) return;
            if (TryParseProgress(line, out var p))
            {
                Interlocked.Exchange(ref progressSeen, 1);
                try { progress(Math.Clamp(p, 0, 1)); } catch { }
                return;
            }
            lock (tail)
            {
                tail.Add(line);
                if (tail.Count > 20) tail.RemoveAt(0);
            }
        }
        // Pre-progress resolve bound (Downloads:ResolveTimeoutSec, default
        // 180s): yt-dlp prints nothing while resolving, so a hung resolve
        // would otherwise burn the queue's whole 5-minute silence window.
        // Kills only when zero progress lines have been seen.
        var resolveTimeout = ResolveTimeout(config);
        var (exitCode, _, stalled) = await RunCaptureWithOutputAsync(exe, args, OnLine, ct,
            resolveTimeout, () => Interlocked.CompareExchange(ref progressSeen, 0, 0) == 1);
        if (exitCode != 0)
        {
            TryDelete(destMp3Path);
            TryDelete(destMp3Path + ".part");
            string detail;
            lock (tail) detail = string.Join(Environment.NewLine, tail);
            if (stalled)
                throw new InvalidOperationException(
                    $"yt-dlp produced no progress in {resolveTimeout.TotalSeconds:0}s " +
                    $"(resolve stalled, no bytes started): {Tail(detail)}");
            log.LogWarning("yt-dlp failed for {Url} (exit {Exit}): {Tail}", url, exitCode, Tail(detail));
            throw new InvalidOperationException(
                $"yt-dlp failed (exit {exitCode}): {Tail(detail)}");
        }
        if (!File.Exists(destMp3Path) || new FileInfo(destMp3Path).Length == 0)
            throw new InvalidOperationException("yt-dlp finished but produced no audio file.");
        progress(1.0);
    }

    // Metadata resolve for the info fallback (YoutubeExplode misreports
    // existing videos as unavailable when it hits a bot-check page).
    // Single JSON object via --dump-single-json; never downloads media.
    public virtual async Task<VideoInfoDto> GetInfoAsync(string url, CancellationToken ct = default)
    {
        var exe = Locate();
        var args = new List<string>
        {
            "--no-playlist", "--no-download", "--dump-single-json",
            "--no-progress", "--socket-timeout", "30", "--retries", "3",
        };
        AddAuthArgs(args, config["YtDlp:CookiesFromBrowser"],
            config["YtDlp:CookiesFile"], config["YtDlp:ExtraArgs"]);
        args.Add(url);
        var tail = new List<string>();
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (tail)
            {
                tail.Add(line);
                if (tail.Count > 20) tail.RemoveAt(0);
            }
        }
        var (exitCode, stdout, _) = await RunCaptureWithOutputAsync(exe, args, OnLine, ct);
        if (exitCode != 0)
        {
            string detail;
            lock (tail) detail = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"yt-dlp info fetch failed (exit {exitCode}): {Tail(detail)}");
        }
        return ParseVideoJson(stdout);
    }

    // Pure metadata parser (unit-tested without a binary or network).
    internal static VideoInfoDto ParseVideoJson(string stdout)
    {
        var json = stdout.Trim();
        // Defensive: isolate the object in case a warning leaked to stdout.
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("yt-dlp returned no video metadata.");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json[start..(end + 1)]);
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString() : null;
            var id = Str("id") ?? throw new InvalidOperationException("yt-dlp returned no video id.");
            var title = Str("title");
            if (string.IsNullOrWhiteSpace(title)) title = id;
            var uploader = Str("uploader") ?? Str("channel");
            if (string.IsNullOrWhiteSpace(uploader) || uploader == "NA") uploader = null;
            return new VideoInfoDto(id, title, uploader, Str("webpage_url") ?? "");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"yt-dlp returned unparsable metadata: {ex.Message}");
        }
    }

    // Shared subprocess runner: captures stdout, feeds every line (both
    // streams) to onLine, kills the tree on cancellation. When resolveTimeout
    // is set, also kills the process if it fires while hasProgressed is
    // still false (hung pre-progress resolve); Stalled reports that kill.
    private async Task<(int ExitCode, string Stdout, bool Stalled)> RunCaptureWithOutputAsync(
        string exe, IReadOnlyList<string> args, Action<string?> onLine, CancellationToken ct,
        TimeSpan? resolveTimeout = null, Func<bool>? hasProgressed = null)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        var stdout = new StringBuilder();
        void Handle(string? line)
        {
            if (line is null) return;
            lock (stdout) stdout.AppendLine(line);
            onLine(line);
        }
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Lost a race with an uninstall: drop the cached probe so the
            // next job re-resolves.
            _probed = false;
            _exe = null;
            throw new InvalidOperationException(
                $"{Maintenance.YtDlpPaths.Missing().Message} (tried '{exe}': {ex.Message})");
        }
        var stalled = 0;
        using var stallCts = resolveTimeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (stallCts is not null && resolveTimeout.HasValue)
            stallCts.CancelAfter(resolveTimeout.Value);
        using CancellationTokenRegistration? stallReg =
            stallCts is null ? null : stallCts.Token.Register(() =>
        {
            try
            {
                if (hasProgressed is null || !hasProgressed())
                {
                    Interlocked.Exchange(ref stalled, 1);
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
        });
        var stdoutTask = ConsumeAsync(proc.StandardOutput, Handle, ct);
        var stderrTask = ConsumeAsync(proc.StandardError, Handle, ct);
        await proc.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        lock (stdout) return (proc.ExitCode, stdout.ToString(),
            Interlocked.CompareExchange(ref stalled, 0, 0) == 1);
    }

    // Bound for yt-dlp's progress-blind resolve phase
    // (Downloads:ResolveTimeoutSec, default 180s, clamped 30..600): must
    // stay under the queue's 5-minute silence watchdog.
    internal static TimeSpan ResolveTimeout(IConfiguration config) =>
        TimeSpan.FromSeconds(Math.Clamp(
            config.GetValue("Downloads:ResolveTimeoutSec", 180), 30, 600));

    private string Locate()
    {
        if (_probed) return _exe ?? throw Maintenance.YtDlpPaths.Missing();
        _probed = true;
        _exe = Maintenance.YtDlpPaths.Resolve(config);
        return _exe;
    }

    // Directory holding FFmpeg for yt-dlp's post-processing (best effort:
    // null skips the flag and yt-dlp falls back to its own PATH lookup).
    private string? FfmpegDir()
    {
        try
        {
            var configured = config["FFmpeg:Path"];
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetDirectoryName(configured);
            var installed = Maintenance.FfmpegPaths.InstalledExe(config);
            if (installed is not null) return Path.GetDirectoryName(installed);
            var onPath = Maintenance.FfmpegPaths.FindOnPath();
            if (onPath is not null) return Path.GetDirectoryName(onPath);
        }
        catch { }
        return null;
    }

    // Pure arg builder (unit-tested without a binary or network).
    internal static IReadOnlyList<string> BuildArgumentList(
        string url, string outputPath, string? ffmpegDir,
        string? cookiesFromBrowser, string? cookiesFile, string? extraArgs)
    {
        var args = new List<string>
        {
            "--no-playlist",
            "-x", "--audio-format", "mp3", "--audio-quality", "0",
            "--force-overwrites",
            "--no-progress", "--newline",
            "--progress-template", "PROG %(progress._percent_str)s",
            "--socket-timeout", "30", "--retries", "3",
            "-o", outputPath,
        };
        if (!string.IsNullOrWhiteSpace(ffmpegDir))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpegDir);
        }
        AddAuthArgs(args, cookiesFromBrowser, cookiesFile, extraArgs);
        args.Add(url);
        return args;
    }

    private static void AddAuthArgs(List<string> args,
        string? cookiesFromBrowser, string? cookiesFile, string? extraArgs)
    {
        if (!string.IsNullOrWhiteSpace(cookiesFromBrowser))
        {
            args.Add("--cookies-from-browser");
            args.Add(cookiesFromBrowser.Trim());
        }
        if (!string.IsNullOrWhiteSpace(cookiesFile))
        {
            args.Add("--cookies");
            args.Add(cookiesFile.Trim());
        }
        args.AddRange(SplitExtraArgs(extraArgs));
    }

    // Minimal shell-like split (whitespace, single/double quotes).
    internal static IReadOnlyList<string> SplitExtraArgs(string? extra)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(extra)) return result;
        var cur = new StringBuilder();
        char? quote = null;
        foreach (var c in extra.Trim())
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else cur.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    // Parses "--progress-template PROG %(progress._percent_str)s" lines
    // ("PROG  45.2%") into 0..1.
    internal static bool TryParseProgress(string? line, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var s = line.Trim();
        if (!s.StartsWith("PROG", StringComparison.Ordinal)) return false;
        s = s[4..].Trim().TrimEnd('%').Trim();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return false;
        value = pct / 100.0;
        return true;
    }

    private static async Task ConsumeAsync(StreamReader reader, Action<string?> onLine, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                onLine(line);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Tail(string s, int max = 500) =>
        s.Length <= max ? s : s[^max..];
}

// IDownloadEngine that keeps YoutubeExplode as the primary path and falls
// back to yt-dlp on any primary download failure (403/bot-checks, cipher
// changes, throttling). Search/info/preview stay on Explode.
public sealed class FallbackDownloadEngine(
    IDownloadEngine primary,
    YtDlpDownloader fallback,
    ILogger<FallbackDownloadEngine> log) : IDownloadEngine
{
    public Task<IReadOnlyList<VideoHitDto>> SearchAsync(
        string query, int limit, CancellationToken ct = default) =>
        primary.SearchAsync(query, limit, ct);

    // Info resolves through Explode first; a bot-check page there
    // misreports existing videos as unavailable, so fall back to yt-dlp
    // (which handles those checks) before failing the job.
    public async Task<VideoInfoDto> GetInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            return await primary.GetInfoAsync(url, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Primary YouTube info fetch failed for {Url}, falling back to yt-dlp", url);
            return await fallback.GetInfoAsync(url, ct);
        }
    }

    public Task<string?> GetPreviewUrlAsync(string url, CancellationToken ct = default) =>
        primary.GetPreviewUrlAsync(url, ct);

    public async Task DownloadAudioAsync(string url, string destMp3Path,
        Action<double> progress, Action<string>? onPhase = null, CancellationToken ct = default)
    {
        // Qualify inner phases with the active engine ("primary/manifest",
        // "fallback/ytdlp") so job errors name the exact stall point.
        void PrimaryPhase(string phase)
        {
            try { onPhase?.Invoke("primary/" + phase); } catch { }
        }
        void FallbackPhase(string phase)
        {
            try { onPhase?.Invoke("fallback/" + phase); } catch { }
        }
        Exception? primaryError;
        try
        {
            try { onPhase?.Invoke("primary"); } catch { }
            await primary.DownloadAudioAsync(url, destMp3Path, progress, PrimaryPhase, ct);
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // True cancellation (user cancel, silence watchdog, shutdown):
            // never mask it as a download failure, and never burn time on
            // a fallback whose token is already dead — it would fail
            // instantly with the same confusing "operation was canceled".
            // (A primary-internal timeout that leaves our token alive still
            // falls through to the fallback below.)
            throw;
        }
        catch (Exception ex)
        {
            primaryError = ex;
            log.LogWarning(ex, "Primary YouTube download failed for {Url}, falling back to yt-dlp", url);
        }
        try
        {
            try { onPhase?.Invoke("fallback"); } catch { }
            await fallback.DownloadAudioAsync(url, destMp3Path, progress, FallbackPhase, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fallbackError)
        {
            throw new InvalidOperationException(
                $"YouTube download failed (YoutubeExplode: {primaryError.Message}; " +
                $"yt-dlp: {fallbackError.Message})", fallbackError);
        }
    }
}

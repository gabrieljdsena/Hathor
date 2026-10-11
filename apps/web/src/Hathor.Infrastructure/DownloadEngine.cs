using System.Diagnostics;
using System.Globalization;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace Hathor.Infrastructure.Ingest;

// YoutubeExplode search + audio download, FFmpeg MP3 transcode.
// FFmpeg resolution: FFmpeg:Path config → app-downloaded (Settings) → PATH,
// probed explicitly so a missing binary fails jobs with the actionable
// message below instead of a raw Win32Exception. The probe result is
// cached per process but reset on launch failure, so installing FFmpeg
// (or setting FFmpeg__Path) takes effect without an app restart.
public sealed class YoutubeExplodeEngine(
    IConfiguration config,
    ILogger<YoutubeExplodeEngine> log,
    IMemoryCache cache) : IDownloadEngine
{
    private readonly YoutubeClient _youtube = new();
    private string? _ffmpeg;
    private bool _ffmpegProbed;
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<VideoHitDto>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, 25);
        var key = $"ytsearch:{query.Trim().ToLowerInvariant()}:{clamped}";
        if (cache.TryGetValue(key, out IReadOnlyList<VideoHitDto>? cached) && cached is not null)
            return cached;
        try
        {
            // YoutubeExplode yields paged batches: take only what was asked
            // for and stop (breaking disposes the enumerator, so no further
            // pages are fetched). Awaiting the whole sequence instead pulls
            // hundreds of videos (~25s) for 5 displayed hits.
            var hits = new List<VideoHitDto>(clamped);
            await foreach (var v in _youtube.Search.GetVideosAsync(query, ct).WithCancellation(ct))
            {
                hits.Add(new VideoHitDto(
                    v.Id.Value,
                    v.Title,
                    v.Author.ChannelTitle,
                    v.Duration?.TotalSeconds ?? 0,
                    v.Thumbnails.TryGetWithHighestResolution()?.Url ?? ""));
                if (hits.Count >= clamped) break;
            }
            cache.Set(key, (IReadOnlyList<VideoHitDto>)hits,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = SearchCacheTtl });
            return hits;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "YouTube search failed for {Query}", query);
            throw;
        }
    }

    public async Task<VideoInfoDto> GetInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var video = await _youtube.Videos.GetAsync(url, ct);
            return new VideoInfoDto(video.Id.Value, video.Title, video.Author.ChannelTitle, video.Url);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "YouTube info fetch failed for {Url}", url);
            throw;
        }
    }

    public async Task DownloadAudioAsync(string url, string destMp3Path,
        Action<double> progress, Action<string>? onPhase = null, CancellationToken ct = default)
    {
        string? container = null;
        void Phase(string name)
        {
            try { onPhase?.Invoke(name); } catch { }
        }
        try
        {
            Phase("manifest");
            // Manifest resolution reports no progress and can hang on
            // throttled/bot-checked videos: bound it well under the queue's
            // 5-minute silence watchdog so a stall fails fast into the
            // yt-dlp fallback instead of burning the whole watchdog window.
            using var manifestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            manifestCts.CancelAfter(ManifestTimeout(config));
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(url, manifestCts.Token);
            var audio = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            // Phase-transition heartbeat for the queue's silence watchdog:
            // manifest resolution reports no progress and can take a while
            // on throttled/bot-checked videos.
            try { progress(0.02); } catch { }
            var dir = Path.GetDirectoryName(destMp3Path)!;
            Directory.CreateDirectory(dir);
            container = Path.Combine(dir, Guid.NewGuid().ToString("N") + "." + audio.Container.Name);
            Phase("download");
            await _youtube.Videos.Streams.DownloadAsync(
                audio, container, new Progress<double>(progress), ct);
            progress(1.0);
            Phase("transcode");
            await TranscodeAsync(container, destMp3Path, progress, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "YouTube audio download failed for {Url}", url);
            throw;
        }
        finally
        {
            try { if (container is not null && File.Exists(container)) File.Delete(container); } catch { }
        }
    }

    public async Task<string?> GetPreviewUrlAsync(string url, CancellationToken ct = default)
    {
        // Previews are best-effort by contract: never throw, never Error-log
        // (Warning only — the DB logs table stays for operational failures).
        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(url, ct);
            var audio = manifest.GetAudioOnlyStreams().TryGetWithHighestBitrate();
            if (audio is null)
            {
                log.LogWarning("No preview audio stream for {Url}", url);
                return null;
            }
            return audio.Url;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Preview stream resolve failed for {Url}", url);
            return null;
        }
    }

    // Bound for the MP3 transcode (stays well under the job's 30-minute
    // budget while dwarfing any realistic encode of a song or podcast).
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(15);

    private async Task TranscodeAsync(
        string sourcePath, string destMp3Path, Action<double> progress, CancellationToken ct)
    {
        var ffmpeg = LocateFFmpeg();
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                // -nostdin: without it ffmpeg can block forever reading
                // interactive commands when stdin is left attached.
                ArgumentList = { "-y", "-nostdin", "-hide_banner", "-i", sourcePath, "-codec:a", "libmp3lame", "-b:a", "320k", destMp3Path },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TranscodeTimeout);
        using var reg = timeoutCts.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Lost a race with an uninstall (or a PATH that changed under
            // us): drop the cached probe so the next job re-resolves.
            _ffmpegProbed = false;
            _ffmpeg = null;
            throw new InvalidOperationException(
                $"{FFmpegMissing().Message} (tried '{ffmpeg}': {ex.Message})");
        }
        // Drain stderr CONCURRENTLY: ffmpeg chatters progress lines there
        // for the whole run, and a filled pipe (~4KB, seconds on long
        // files) blocks it forever with a 0-byte output. The same drain
        // feeds transcode progress (0.9..0.99 of the job) from time= lines
        // against the Duration: header, so the UI moves instead of sitting
        // at 0% — which also feeds the queue's silence watchdog.
        var tail = new List<string>();
        TimeSpan? duration = null;
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (tail)
            {
                tail.Add(line);
                if (tail.Count > 30) tail.RemoveAt(0);
            }
            duration ??= TryParseDuration(line);
            var frac = duration is not null ? TranscodeFraction(line, duration.Value) : null;
            if (frac is not null)
            {
                try { progress(0.9 + 0.09 * frac.Value); } catch { }
            }
        }
        var drain = ConsumeAsync(proc.StandardError, OnLine, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string err;
            lock (tail) err = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException(
                $"FFmpeg transcode timed out after {TranscodeTimeout.TotalMinutes:0} minutes: {Tail(err)}");
        }
        await drain;
        ct.ThrowIfCancellationRequested();
        if (proc.ExitCode != 0)
        {
            string err;
            lock (tail) err = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"FFmpeg transcode failed (exit {proc.ExitCode}): {Tail(err)}");
        }
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

    // ffmpeg stderr header: "Duration: 00:17:42.63, start: 0.000000, ...".
    internal static TimeSpan? TryParseDuration(string line)
    {
        var token = TokenAfter(line, "Duration: ");
        return token is null ? null : TryParseClock(token);
    }

    // ffmpeg stderr progress: "size= 5120KiB time=00:02:31.04 bitrate= ...".
    internal static double? TranscodeFraction(string line, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return null;
        var token = TokenAfter(line, "time=");
        if (token is null) return null;
        var elapsed = TryParseClock(token);
        if (elapsed is null) return null;
        return Math.Clamp(elapsed.Value.TotalSeconds / duration.TotalSeconds, 0, 1);
    }

    private static string? TokenAfter(string line, string marker)
    {
        var i = line.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var s = line[(i + marker.Length)..].Trim();
        var end = s.IndexOfAny([' ', ',']);
        return end < 0 ? s : s[..end];
    }

    private static TimeSpan? TryParseClock(string token)
    {
        var parts = token.Split(':');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return null;
        if (h < 0 || m < 0 || s < 0) return null;
        return new TimeSpan(0, h, m, 0) + TimeSpan.FromSeconds(s);
    }

    private string LocateFFmpeg()
    {
        if (_ffmpegProbed) return _ffmpeg ?? throw FFmpegMissing();
        _ffmpegProbed = true;
        // Explicit FFmpeg:Path → app-downloaded (Settings) → PATH probe.
        var configured = config["FFmpeg:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            _ffmpeg = configured;
            return _ffmpeg;
        }
        _ffmpeg = Maintenance.FfmpegPaths.InstalledExe(config)
            ?? Maintenance.FfmpegPaths.FindOnPath()
            ?? throw FFmpegMissing();
        return _ffmpeg;
    }

    private static Exception FFmpegMissing() => new InvalidOperationException(
        "FFmpeg not found. Download it from Settings → FFmpeg, install FFmpeg on PATH, "
        + "or set the FFmpeg__Path environment variable.");

    // Bound for the progress-blind manifest fetch (Downloads:ManifestTimeoutSec,
    // default 120s, clamped 30..600): must stay well under the queue's
    // 5-minute silence watchdog so a hung resolve fails fast into fallback.
    internal static TimeSpan ManifestTimeout(IConfiguration config) =>
        TimeSpan.FromSeconds(Math.Clamp(
            config.GetValue("Downloads:ManifestTimeoutSec", 120), 30, 600));

    private static string Tail(string s, int max = 500) =>
        s.Length <= max ? s : s[^max..];
}

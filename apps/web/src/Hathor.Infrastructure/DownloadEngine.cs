using System.Diagnostics;
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
        Action<double> progress, CancellationToken ct = default)
    {
        string? container = null;
        try
        {
            var manifest = await _youtube.Videos.Streams.GetManifestAsync(url, ct);
            var audio = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();
            var dir = Path.GetDirectoryName(destMp3Path)!;
            Directory.CreateDirectory(dir);
            container = Path.Combine(dir, Guid.NewGuid().ToString("N") + "." + audio.Container.Name);
            await _youtube.Videos.Streams.DownloadAsync(
                audio, container, new Progress<double>(progress), ct);
            progress(1.0);
            await TranscodeAsync(container, destMp3Path, ct);
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

    private async Task TranscodeAsync(string sourcePath, string destMp3Path, CancellationToken ct)
    {
        var ffmpeg = LocateFFmpeg();
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                ArgumentList = { "-y", "-i", sourcePath, "-codec:a", "libmp3lame", "-b:a", "320k", destMp3Path },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
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
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"FFmpeg transcode failed (exit {proc.ExitCode}): {Tail(err)}");
        }
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

    private static string Tail(string s, int max = 500) =>
        s.Length <= max ? s : s[^max..];
}

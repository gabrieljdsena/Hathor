using System.Diagnostics;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace Hathor.Infrastructure.Ingest;

// YoutubeExplode search + audio download, FFmpeg MP3 transcode.
// FFmpeg resolution: FFmpeg:Path config → PATH. (Auto-download ships with
// the maintenance phase; until then a missing binary fails jobs loudly.)
public sealed class YoutubeExplodeEngine(IConfiguration config, ILogger<YoutubeExplodeEngine> log) : IDownloadEngine
{
    private readonly YoutubeClient _youtube = new();
    private string? _ffmpeg;
    private bool _ffmpegProbed;

    public async Task<IReadOnlyList<VideoHitDto>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        try
        {
            var results = await _youtube.Search.GetVideosAsync(query, ct);
            return results.Take(Math.Clamp(limit, 1, 25)).Select(v => new VideoHitDto(
                v.Id.Value,
                v.Title,
                v.Author.ChannelTitle,
                v.Duration?.TotalSeconds ?? 0,
                v.Thumbnails.TryGetWithHighestResolution()?.Url ?? "")).ToList();
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
        proc.Start();
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
        // Explicit FFmpeg:Path → app-downloaded (Settings) → PATH.
        var configured = config["FFmpeg:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            _ffmpeg = configured;
            return _ffmpeg;
        }
        _ffmpeg = Maintenance.FfmpegPaths.InstalledExe(config) ?? Maintenance.FfmpegPaths.ExeName;
        return _ffmpeg; // Process.Start throws Win32Exception when absent → wrapped below
    }

    private static Exception FFmpegMissing() => new InvalidOperationException(
        "FFmpeg not found. Install FFmpeg on PATH or set the FFmpeg__Path environment variable.");

    private static string Tail(string s, int max = 500) =>
        s.Length <= max ? s : s[^max..];
}

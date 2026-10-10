using System.Diagnostics;
using System.Text.Json;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Maintenance;

// Startup probe (desktop find_ffmpeg + check_libraries_status) plus
// on-demand maintenance: FFmpeg self-install status and NuGet update
// checks for the managed libraries.
public sealed class SystemProbe(
    IConfiguration config,
    IHttpClientFactory httpFactory,
    ILogger<SystemProbe> log) : ISystemProbe
{
    public FFmpegStatusDto GetFFmpegStatus()
    {
        // Resolution order: explicit FFmpeg:Path → app-downloaded → PATH.
        var configured = config["FFmpeg:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return new FFmpegStatusDto(true, configured, Path.GetDirectoryName(configured), null);
        var installed = FfmpegPaths.InstalledExe(config);
        if (installed is not null)
            return new FFmpegStatusDto(true, installed, Path.GetDirectoryName(installed), null);
        var exe = FfmpegPaths.FindOnPath() ?? FfmpegPaths.ExeName;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    ArgumentList = { "-version" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var first = proc.StandardOutput.ReadLine() ?? "";
            if (!proc.WaitForExit(10_000)) { try { proc.Kill(); } catch { } }
            return proc.ExitCode == 0
                ? new FFmpegStatusDto(true, exe, null, first.Trim())
                : new FFmpegStatusDto(false, null, null, "ffmpeg -version failed");
        }
        catch
        {
            return new FFmpegStatusDto(false, null, null,
                "FFmpeg not found. Download it from Settings, install FFmpeg on PATH, or set the FFmpeg__Path environment variable.");
        }
    }

    public FFmpegStatusDto GetYtDlpStatus()
    {
        // Resolution order: explicit YtDlp:Path → app-downloaded → PATH.
        var configured = config["YtDlp:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return new FFmpegStatusDto(true, configured, Path.GetDirectoryName(configured), null);
        var installed = YtDlpPaths.InstalledExe(config);
        if (installed is not null)
            return new FFmpegStatusDto(true, installed, Path.GetDirectoryName(installed), null);
        var exe = YtDlpPaths.FindOnPath() ?? YtDlpPaths.ExeName;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var first = proc.StandardOutput.ReadLine() ?? "";
            if (!proc.WaitForExit(10_000)) { try { proc.Kill(); } catch { } }
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(first)
                ? new FFmpegStatusDto(true, exe, null, first.Trim())
                : new FFmpegStatusDto(false, null, null, "yt-dlp --version failed");
        }
        catch
        {
            return new FFmpegStatusDto(false, null, null,
                "yt-dlp not found. Download it from Settings, install yt-dlp on PATH, or set the YtDlp__Path environment variable.");
        }
    }

    public IReadOnlyList<LibraryStatusDto> GetLibraryStatus() =>
    [
        new("YoutubeExplode", VersionOf("YoutubeExplode"), null, "up-to-date"),
        new("TagLibSharp", VersionOf("TagLibSharp"), null, "up-to-date"),
    ];

    public MaintenanceResultDto RunChecks()
    {
        var ffmpeg = GetFFmpegStatus();
        var libs = GetLibraryStatus();
        var allOk = ffmpeg.Found && libs.All(l => l.Status is "up-to-date" or "unknown");
        return new MaintenanceResultDto(ffmpeg, libs, allOk);
    }

    // Live NuGet check (Settings → YouTube downloader): latest stable per
    // package compared against the running assemblies. A library is
    // compiled in, so an available update means "update the package and
    // redeploy" — it cannot self-install at runtime.
    public async Task<IReadOnlyList<LibraryStatusDto>> CheckLibraryUpdatesAsync(
        CancellationToken ct = default)
    {
        var result = new List<LibraryStatusDto>();
        foreach (var package in new[] { "YoutubeExplode", "TagLibSharp" })
        {
            var current = VersionOf(package);
            string? latest = null;
            var status = "unknown";
            try
            {
                latest = await LatestStableAsync(package, ct);
                status = CompareVersions(current, latest) switch
                {
                    < 0 => "update-available",
                    0 => "up-to-date",
                    _ => "up-to-date",
                };
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Library update check failed for {Package}", package);
            }
            result.Add(new LibraryStatusDto(package, current, latest, status));
        }
        return result;
    }

    private async Task<string?> LatestStableAsync(string package, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("nuget");
        using var res = await http.GetAsync(
            $"https://api.nuget.org/v3-flatcontainer/{package.ToLowerInvariant()}/index.json", ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("versions", out var versions)) return null;
        string? best = null;
        foreach (var v in versions.EnumerateArray())
        {
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s) || s.Contains('-')) continue; // stable only
            if (best is null || CompareVersions(best, s) < 0) best = s;
        }
        return best;
    }

    // -1 current older, 0 equal, 1 current newer (or unparseable → treat as current).
    internal static int CompareVersions(string? current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(latest)) return 0;
        if (!Version.TryParse(Normalize(current), out var c)) return 1;
        if (!Version.TryParse(Normalize(latest), out var l)) return 0;
        return c.CompareTo(l);
    }

    private static string Normalize(string v)
    {
        // NuGet versions can carry a 4th component; System.Version handles it.
        var parts = v.Trim().Split('.');
        while (parts.Length < 3) parts = [.. parts, "0"];
        return string.Join('.', parts.Take(4));
    }

    private static string? VersionOf(string assemblyName)
    {
        try
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName,
                    StringComparison.OrdinalIgnoreCase))
                ?.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }
}

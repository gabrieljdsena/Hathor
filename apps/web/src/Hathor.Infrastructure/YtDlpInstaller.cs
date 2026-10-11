using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Maintenance;

// Where an app-managed yt-dlp binary lives (shared by the downloader,
// the probe and the installer below). Same tools dir as FFmpeg: a sibling
// "tools" of the media storage root (Docker: /data/library → /data/tools).
internal static class YtDlpPaths
{
    public static string ExeName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    public static string? InstalledExe(IConfiguration config)
    {
        var candidate = Path.Combine(FfmpegPaths.ToolsDir(config), ExeName);
        return File.Exists(candidate) ? candidate : null;
    }

    // Explicit PATH probe (same rationale as FfmpegPaths.FindOnPath:
    // services run with a minimal PATH where a bare-name lookup silently
    // misses, so probe here and fail fast with guidance instead).
    public static string? FindOnPath(
        string? exeName = null, string? pathVariable = null)
    {
        var exe = string.IsNullOrWhiteSpace(exeName) ? ExeName : exeName;
        var path = pathVariable
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim().Trim('"'), exe);
            }
            catch
            {
                continue;
            }
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Resolution order: explicit YtDlp:Path → app-downloaded → PATH.
    public static string Resolve(IConfiguration config) =>
        (config["YtDlp:Path"] is { } configured
            && !string.IsNullOrWhiteSpace(configured)
            && File.Exists(configured) ? configured : null)
        ?? InstalledExe(config)
        ?? FindOnPath()
        ?? throw Missing();

    public static Exception Missing() => new InvalidOperationException(
        "yt-dlp not found. Download it from Settings → System, install yt-dlp on PATH, "
        + "or set the YtDlp__Path environment variable.");
}

public interface IYtDlpInstaller
{
    Hathor.Application.Dtos.FfmpegDownloadDto Status();
    // Starts the background download+install; throws InvalidOperationException
    // when one is already running. force=true deletes an installed binary
    // first and re-downloads the latest release (update path).
    Hathor.Application.Dtos.FfmpegDownloadDto StartDownload(bool force = false);
}

// Self-install for deployments without yt-dlp on PATH (native/Windows
// runs; Docker images bake it in). yt-dlp ships as a single-file binary
// (no archive), so this downloads the file, marks it executable and
// verifies with `--version`.
public sealed class YtDlpInstaller(
    IConfiguration config,
    IHttpClientFactory httpFactory,
    ILogger<YtDlpInstaller> log) : IYtDlpInstaller
{
    private readonly object _lock = new();
    private string _state = "idle";
    private double _progress;
    private string? _exe;
    private string? _error;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    public Hathor.Application.Dtos.FfmpegDownloadDto Status()
    {
        lock (_lock) return new Hathor.Application.Dtos.FfmpegDownloadDto(_state, _progress, _exe, _error);
    }

    public Hathor.Application.Dtos.FfmpegDownloadDto StartDownload(bool force = false)
    {
        lock (_lock)
        {
            if (_state is "downloading" or "extracting")
                throw new InvalidOperationException("A yt-dlp download is already running.");
            if (force)
            {
                // Update path: refresh the app-managed copy only. An
                // explicitly configured YtDlp:Path is user-managed (winget,
                // scoop, distro package) — refuse rather than overwrite it.
                var pinned = config["YtDlp:Path"];
                if (!string.IsNullOrWhiteSpace(pinned) && File.Exists(pinned))
                    throw new InvalidOperationException(
                        $"yt-dlp is pinned via YtDlp:Path ('{pinned}'): update it with your package manager instead.");
                // Drop the installed binary so the run below fetches the
                // latest release (best effort — a locked file fails loudly
                // in RunInstallAsync instead).
                var current = YtDlpPaths.InstalledExe(config);
                if (current is not null)
                {
                    try { File.Delete(current); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Could not remove the installed yt-dlp for update: {ex.Message}");
                    }
                }
            }
            else
            {
                var existing = YtDlpPaths.InstalledExe(config);
                if (existing is not null)
                    return new Hathor.Application.Dtos.FfmpegDownloadDto("ready", 1, existing, null);
            }
            _state = "downloading";
            _progress = 0;
            _exe = null;
            _error = null;
        }
        _ = Task.Run(RunInstallAsync);
        return Status();
    }

    // Platform source selection (testable without network).
    internal static string? DownloadUrl()
    {
        const string baseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
        if (OperatingSystem.IsWindows())
            return baseUrl + "yt-dlp.exe";
        if (OperatingSystem.IsMacOS()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64)
            return baseUrl + "yt-dlp_macos";
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return baseUrl + "yt-dlp";
        return null;
    }

    private async Task RunInstallAsync()
    {
        using var cts = new CancellationTokenSource(InstallTimeout);
        var ct = cts.Token;
        try
        {
            var url = DownloadUrl()
                ?? throw new InvalidOperationException("Automatic yt-dlp download is not supported on this platform.");
            var toolsDir = FfmpegPaths.ToolsDir(config);
            Directory.CreateDirectory(toolsDir);
            var dest = Path.Combine(toolsDir, YtDlpPaths.ExeName);
            await DownloadAsync(url, dest, ct);
            if (!OperatingSystem.IsWindows()) ChmodExec(dest);
            Verify(dest);
            log.LogInformation("yt-dlp installed at {Exe}", dest);
            Set("ready", 1, dest, null);
        }
        catch (OperationCanceledException ex)
        {
            Fail($"yt-dlp download timed out or was cancelled: {ex.Message}");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        log.LogInformation("Downloading yt-dlp from {Url}", url);
        var http = httpFactory.CreateClient("ytdlp");
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength;
        // No Content-Length (chunked release asset): report indeterminate
        // (-1) instead of a stuck-looking 0% — the UI renders that as a
        // bare "Downloading…" without a percentage.
        if (total is null or <= 0) Set("downloading", -1, null, null);
        await using var net = await res.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await net.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) Set("downloading", (double)done / total.Value, null, null);
        }
        log.LogInformation("Downloaded yt-dlp ({Bytes} bytes)", done);
    }

    private static void ChmodExec(string exe)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    ArgumentList = { "+x", exe },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            proc.WaitForExit(10_000);
        }
        catch
        {
            // Best effort: a noexec mount fails loudly at verify() instead.
        }
    }

    private static void Verify(string exe)
    {
        using var proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
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
        if (!proc.WaitForExit(30_000) || proc.ExitCode != 0 || string.IsNullOrWhiteSpace(first))
            throw new InvalidOperationException("Downloaded yt-dlp failed its --version check.");
    }

    private void Set(string state, double progress, string? exe, string? error)
    {
        lock (_lock)
        {
            _state = state;
            _progress = progress;
            if (exe is not null) _exe = exe;
            if (error is not null) _error = error;
        }
    }

    private void Fail(string error)
    {
        log.LogError("yt-dlp download failed: {Error}", error);
        Set("failed", 0, null, error);
    }
}

using System.IO.Compression;
using System.Runtime.InteropServices;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Maintenance;

// Where an app-managed FFmpeg binary lives (shared by the probe, the
// download engine and the installer below).
internal static class FfmpegPaths
{
    public static string ToolsDir(IConfiguration config) =>
        ToolsDir(
            config.GetValue("FFmpeg:ToolsDir", ""),
            config.GetValue("Database:StorageRoot", "data"));

    internal static string ToolsDir(string? configured, string? storageRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        // Sibling "tools" of the media storage root: same volume in Docker
        // (/data/library → /data/tools), ./tools for local runs.
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(storageRoot) ? "data" : storageRoot);
        return Path.Combine(Path.GetDirectoryName(root) ?? ".", "tools");
    }

    public static string ExeName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public static string? InstalledExe(IConfiguration config)
    {
        var candidate = Path.Combine(ToolsDir(config), ExeName);
        return File.Exists(candidate) ? candidate : null;
    }

    // Resolution order: explicit FFmpeg:Path → app-downloaded → PATH.
    public static string? Resolve(IConfiguration config)
    {
        var configured = config["FFmpeg:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        return InstalledExe(config) ?? ExeName;
    }
}

public interface IFfmpegInstaller
{
    FfmpegDownloadDto Status();
    // Starts the background download+install; throws InvalidOperationException
    // when one is already running.
    FfmpegDownloadDto StartDownload();
}

// Self-install for deployments without FFmpeg on PATH (native/Windows
// runs; Docker images bake it in). Downloads a static build with progress,
// extracts the binary into the tools dir, verifies with `-version`.
public sealed class FfmpegInstaller(
    IConfiguration config,
    IHttpClientFactory httpFactory,
    ILogger<FfmpegInstaller> log) : IFfmpegInstaller
{
    private readonly object _lock = new();
    private string _state = "idle";
    private double _progress;
    private string? _exe;
    private string? _error;
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    public FfmpegDownloadDto Status()
    {
        lock (_lock) return new FfmpegDownloadDto(_state, _progress, _exe, _error);
    }

    public FfmpegDownloadDto StartDownload()
    {
        lock (_lock)
        {
            if (_state is "downloading" or "extracting")
                throw new InvalidOperationException("An FFmpeg download is already running.");
            var existing = FfmpegPaths.InstalledExe(config);
            if (existing is not null)
                return new FfmpegDownloadDto("ready", 1, existing, null);
            _state = "downloading";
            _progress = 0;
            _exe = null;
            _error = null;
        }
        _ = Task.Run(RunInstallAsync);
        return Status();
    }

    // Platform source selection (testable without network).
    internal static (string Url, string ArchiveName, string ExeRelPath)? DownloadSource()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return ("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                "ffmpeg.zip", "bin/ffmpeg.exe");
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return ("https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz",
                "ffmpeg.tar.xz", "ffmpeg-6.1-amd64-static/ffmpeg");
        return null;
    }

    private async Task RunInstallAsync()
    {
        using var cts = new CancellationTokenSource(InstallTimeout);
        var ct = cts.Token;
        try
        {
            var source = DownloadSource();
            if (source is null)
                throw new InvalidOperationException("Automatic FFmpeg download is not supported on this platform.");
            var toolsDir = FfmpegPaths.ToolsDir(config);
            Directory.CreateDirectory(toolsDir);
            var archive = Path.Combine(toolsDir, source.Value.ArchiveName);
            await DownloadAsync(source.Value.Url, archive, ct);
            Set("extracting", -1, null, null);
            var exe = await ExtractAsync(archive, toolsDir, source.Value.ExeRelPath, ct);
            try { File.Delete(archive); } catch { }
            Verify(exe);
            log.LogInformation("FFmpeg installed at {Exe}", exe);
            Set("ready", 1, exe, null);
        }
        catch (OperationCanceledException ex)
        {
            Fail($"FFmpeg download timed out or was cancelled: {ex.Message}");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("ffmpeg");
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength;
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
    }

    private async Task<string> ExtractAsync(
        string archive, string toolsDir, string exeRelPath, CancellationToken ct)
    {
        var stage = Path.Combine(toolsDir, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ExtractZip(archive, stage), ct);
            else
                await ExtractTarXzAsync(archive, stage, ct);
            var exe = FindExe(stage, exeRelPath);
            var dest = Path.Combine(toolsDir, FfmpegPaths.ExeName);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(exe, dest);
            if (!OperatingSystem.IsWindows()) ChmodExec(dest);
            return dest;
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    // Zip-slip guarded: entries must stay inside the staging dir.
    private static void ExtractZip(string archive, string stage)
    {
        var root = Path.GetFullPath(stage);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            var dest = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsafe archive entry: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static async Task ExtractTarXzAsync(string archive, string stage, CancellationToken ct)
    {
        // .NET has no XZ decoder in-box; delegate to system tar (present on
        // modern Linux/macOS images, including the API container).
        using var proc = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList = { "-xJf", archive, "-C", stage },
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not extract the FFmpeg archive: system 'tar' is unavailable.", ex);
        }
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not extract the FFmpeg archive (tar exit {proc.ExitCode}).");
    }

    // Prefer the expected relative path, fall back to any ffmpeg binary
    // (gyan/johnvansickle version their top-level folder names).
    private static string FindExe(string stage, string exeRelPath)
    {
        var direct = Path.Combine(stage, exeRelPath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(direct)) return direct;
        var any = Directory.EnumerateFiles(stage, FfmpegPaths.ExeName, SearchOption.AllDirectories).FirstOrDefault();
        return any ?? throw new InvalidOperationException("FFmpeg binary not found in the downloaded archive.");
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
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        proc.Start();
        var first = proc.StandardOutput.ReadLine() ?? "";
        if (!proc.WaitForExit(30_000) || proc.ExitCode != 0 || !first.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded FFmpeg failed its -version check.");
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
        log.LogError("FFmpeg download failed: {Error}", error);
        Set("failed", 0, null, error);
    }
}

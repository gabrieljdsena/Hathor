using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using Hathor.Application.Dtos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Maintenance;

// YouTube bot-check protection without logins or manual cookies: a
// proof-of-origin (PO) token provider for yt-dlp
// (Brainicism/bgutil-ytdlp-pot-provider — the documented fix for
// "Sign in to confirm you're not a bot" from flagged IPs; yt-dlp's own
// YouTube OAuth was removed, so there is no Google-login flow to offer).
//
// Layout under the shared tools dir (same root as FFmpeg/yt-dlp):
//   tools/youtube-pot/bgutil-ytdlp-pot-provider-<ver>.zip  plugin archive
//   tools/youtube-pot/server/    provider sources (built via npm)
//   tools/youtube-pot/state.json { enabled, version, port, verified }
// The plugin archive is kept AS A ZIP: yt-dlp's --plugin-dirs loader only
// discovers yt_dlp_plugins inside archives (or doubly-nested dirs) — an
// extracted tree at the dir root scans as empty ("Plugin directories:
// none"). The provider is a loopback-only node server (`node
// build/main.js`); the plugin auto-discovers it at 127.0.0.1:4416, so
// yt-dlp needs no extractor args beyond --plugin-dirs (a base_url arg is
// added only for non-default ports). Explicit cookies config still takes
// precedence (AddAuthArgs runs first) — this is additive, never a
// replacement.
internal static class PotPaths
{
    public const int DefaultPort = 4416;
    public const string DefaultVersion = "2.0.2";

    public static string BaseDir(IConfiguration config) =>
        Path.Combine(FfmpegPaths.ToolsDir(config), "youtube-pot");

    public static string PluginDir(IConfiguration config) =>
        Path.Combine(BaseDir(config), "plugins");

    public static string PluginZip(IConfiguration config, string version) =>
        Path.Combine(PluginDir(config), $"bgutil-ytdlp-pot-provider-{version}.zip");

    public static string ServerDir(IConfiguration config) =>
        Path.Combine(BaseDir(config), "server");

    public static string ServerEntry(IConfiguration config) =>
        Path.Combine(ServerDir(config), "build", "main.js");

    public static string StateFile(IConfiguration config) =>
        Path.Combine(BaseDir(config), "state.json");

    public static int Port(IConfiguration config) =>
        int.TryParse(config["YtDlp:PotProviderPort"], out var p) && p is >= 1 and <= 65535
            ? p
            : DefaultPort;

    public static string Version(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["YtDlp:PotProviderVersion"])
            ? DefaultVersion
            : config["YtDlp:PotProviderVersion"]!.Trim();

    public static string PluginZipUrl(string version) =>
        $"https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/{version}/bgutil-ytdlp-pot-provider.zip";

    public static string ServerTarballUrl(string version) =>
        $"https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/{version}.tar.gz";

    // Legacy layout (v1 provisioning extracted the zip): an extracted
    // tree at the plugin-dir root is invisible to yt-dlp's loader.
    public static string LegacyExtractedDir(IConfiguration config) =>
        Path.Combine(PluginDir(config), "yt_dlp_plugins");

    public static bool PluginPresent(IConfiguration config)
    {
        try
        {
            var dir = PluginDir(config);
            return Directory.Exists(dir)
                && Directory.EnumerateFiles(dir, "bgutil-ytdlp-pot-provider-*.zip").Any();
        }
        catch
        {
            return false;
        }
    }

    public static bool BuildComplete(IConfiguration config) =>
        PluginPresent(config) && File.Exists(ServerEntry(config));
}

// Persisted opt-in (explicit Enable click — provisioning downloads ~MBs
// from GitHub and runs npm, so it never starts on its own).
internal sealed record PotState(bool Enabled, string Version, int Port, bool Verified = false)
{
    public PotState WithEnabled(bool enabled) => this with { Enabled = enabled };

    public static PotState? TryLoad(IConfiguration config)
    {
        try
        {
            var file = PotPaths.StateFile(config);
            if (!File.Exists(file)) return null;
            var parsed = JsonSerializer.Deserialize<PotState>(File.ReadAllText(file));
            return parsed is null ? null : parsed with { Port = parsed.Port is < 1 or > 65535 ? PotPaths.DefaultPort : parsed.Port };
        }
        catch
        {
            return null;
        }
    }

    public void Save(IConfiguration config)
    {
        Directory.CreateDirectory(PotPaths.BaseDir(config));
        File.WriteAllText(PotPaths.StateFile(config), JsonSerializer.Serialize(this));
    }
}

// yt-dlp args for the protection profile (pure — unit-tested). Empty when
// disabled or unprovisioned; the plugin no-ops gracefully if its server
// is momentarily down, so no per-download reachability probe is needed.
internal static class PotArgs
{
    public static IReadOnlyList<string> Build(string pluginDir, int port)
    {
        var args = new List<string> { "--plugin-dirs", pluginDir };
        if (port != PotPaths.DefaultPort)
        {
            args.Add("--extractor-args");
            args.Add($"youtubepot-bgutilhttp:base_url=http://127.0.0.1:{port}");
        }
        return args;
    }

    public static IReadOnlyList<string> FromState(IConfiguration config)
    {
        var state = PotState.TryLoad(config);
        if (state is null || !state.Enabled) return [];
        if (!PotPaths.PluginPresent(config)) return [];
        return Build(PotPaths.PluginDir(config), state.Port);
    }
}

public interface IPotProtection
{
    YoutubeProtectionStatusDto Status();
    // Starts background provisioning + enable (202-style: returns current
    // status immediately). Throws InvalidOperationException while busy.
    YoutubeProtectionStatusDto StartEnable(bool force = false);
    YoutubeProtectionStatusDto Disable();
}

public sealed class PotProtectionService(
    IConfiguration config,
    IHttpClientFactory httpFactory,
    ILogger<PotProtectionService> log) : IPotProtection, IHostedService, IDisposable
{
    private readonly object _lock = new();
    private string _provisionState = "idle";
    private double _provisionProgress;
    private string? _provisionError;
    private Process? _provider;
    private int _restarts;
    private bool _disposed;
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NpmTimeout = TimeSpan.FromMinutes(20);
    private const int MaxRestarts = 5;

    public YoutubeProtectionStatusDto Status()
    {
        PotState? state;
        string provisionState;
        double provisionProgress;
        string? provisionError;
        lock (_lock)
        {
            state = PotState.TryLoad(config);
            provisionState = _provisionState;
            provisionProgress = _provisionProgress;
            provisionError = _provisionError;
        }
        var (nodeFound, nodeVersion) = ProbeNode();
        var port = state?.Port ?? PotPaths.Port(config);
        return new YoutubeProtectionStatusDto(
            state?.Enabled ?? false,
            provisionState,
            provisionProgress,
            nodeFound, nodeVersion,
            PotPaths.PluginPresent(config),
            ProbePort(port),
            port,
            state?.Version, state?.Verified ?? false, provisionError);
    }

    public YoutubeProtectionStatusDto StartEnable(bool force = false)
    {
        lock (_lock)
        {
            if (_provisionState is "downloading" or "extracting" or "building" or "starting")
                throw new InvalidOperationException("YouTube protection setup is already running.");
            var existing = PotState.TryLoad(config);
            if (!force && existing?.Enabled == true && PotPaths.BuildComplete(config))
            {
                EnsureProviderRunning(existing);
                _provisionState = "ready";
                return Status();
            }
            _provisionState = "downloading";
            _provisionProgress = 0;
            _provisionError = null;
        }
        _ = Task.Run(() => RunProvisionAsync(force));
        return Status();
    }

    public YoutubeProtectionStatusDto Disable()
    {
        PotState? state;
        lock (_lock) state = PotState.TryLoad(config);
        StopProvider();
        state?.WithEnabled(false).Save(config);
        lock (_lock)
        {
            if (_provisionState is not ("failed" or "ready")) _provisionState = "idle";
        }
        log.LogInformation("YouTube bot-check protection disabled.");
        return Status();
    }

    // Hosted startup: resume an enabled protection across restarts (node
    // child process dies with the host — respawn it, don't re-provision).
    public Task StartAsync(CancellationToken ct)
    {
        var state = PotState.TryLoad(config);
        if (state?.Enabled != true) return Task.CompletedTask;
        if (PotPaths.BuildComplete(config))
        {
            try
            {
                EnsureProviderRunning(state);
                lock (_lock) _provisionState = "ready";
                log.LogInformation("YouTube bot-check protection resumed (provider on 127.0.0.1:{Port}).", state.Port);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _provisionState = "failed";
                    _provisionError = ex.Message;
                }
                log.LogWarning(ex, "YouTube protection provider failed to start at boot.");
            }
            return Task.CompletedTask;
        }
        // Enabled but incomplete (e.g. an older build gutted the install):
        // repair automatically instead of waiting for a manual Update.
        log.LogWarning("YouTube protection is enabled but incomplete — repairing automatically.");
        lock (_lock)
        {
            if (_provisionState is "downloading" or "extracting" or "building" or "starting")
                return Task.CompletedTask;
            _provisionState = "starting";
        }
        _ = Task.Run(() => RunProvisionAsync(false));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopProvider();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopProvider();
    }

    private async Task RunProvisionAsync(bool force)
    {
        var version = PotPaths.Version(config);
        var port = PotPaths.Port(config);
        try
        {
            var (nodeFound, nodeVersion) = ProbeNode();
            if (!nodeFound)
                throw new InvalidOperationException(
                    "Node.js was not found on PATH. Install Node.js 22+ (it ships npm), then retry. " +
                    "Manual alternative: set YtDlp:CookiesFile to a cookies.txt.");
            log.LogInformation("Provisioning YouTube protection v{Version} (node {Node}).", version, nodeVersion);
            Set("downloading", 0.05, null);

            // The running provider locks its own files on Windows (notably
            // canvas.node): stop it BEFORE touching anything, or a rebuild
            // dies with UnauthorizedAccessException and leaves a gutted
            // server dir behind.
            StopProvider();

            var baseDir = PotPaths.BaseDir(config);
            Directory.CreateDirectory(baseDir);
            var http = httpFactory.CreateClient("ytdlp");

            // 1. Plugin archive (kept as a zip — see PotPaths note).
            var pluginDir = PotPaths.PluginDir(config);
            Directory.CreateDirectory(pluginDir);
            RemoveLegacyExtractedPlugin(config);
            foreach (var stale in Directory.EnumerateFiles(pluginDir, "bgutil-ytdlp-pot-provider-*.zip"))
            {
                if (!stale.EndsWith($"{version}.zip", StringComparison.OrdinalIgnoreCase))
                    TryDeleteFile(stale);
            }
            using var dlCts = new CancellationTokenSource(NetworkTimeout);
            using (var dl = await http.GetAsync(PotPaths.PluginZipUrl(version),
                HttpCompletionOption.ResponseHeadersRead, dlCts.Token))
            {
                dl.EnsureSuccessStatusCode();
                await using var net = await dl.Content.ReadAsStreamAsync(dlCts.Token);
                await using var file = File.Create(PotPaths.PluginZip(config, version));
                await net.CopyToAsync(file, dlCts.Token);
            }
            Set("downloading", 0.3, null);

            // 2. Provider server sources (release tarball, server/ subtree).
            var serverDir = PotPaths.ServerDir(config);
            if (force) DeleteDirResilient(serverDir);
            Directory.CreateDirectory(serverDir);
            using var dlCts2 = new CancellationTokenSource(NetworkTimeout);
            using (var dl = await http.GetAsync(PotPaths.ServerTarballUrl(version),
                HttpCompletionOption.ResponseHeadersRead, dlCts2.Token))
            {
                dl.EnsureSuccessStatusCode();
                await using var net = await dl.Content.ReadAsStreamAsync(dlCts2.Token);
                await using var gz = new GZipStream(net, CompressionMode.Decompress);
                ExtractServerSubtree(gz, serverDir);
            }
            Set("extracting", 0.5, null);

            // 3. Build the server (npm ci + tsc, per upstream README).
            Set("building", 0.6, null);
            var buildTail = await RunNpm(serverDir, ["ci", "--no-audit", "--no-fund"], NpmTimeout);
            if (!File.Exists(Path.Combine(serverDir, "node_modules", ".package-lock.json"))
                && !Directory.Exists(Path.Combine(serverDir, "node_modules")))
                throw new InvalidOperationException($"npm ci produced no node_modules. {buildTail}");
            Set("building", 0.8, null);
            buildTail = await RunNpm(serverDir, ["exec", "--no", "--", "tsc"], TimeSpan.FromMinutes(10));
            if (!File.Exists(PotPaths.ServerEntry(config)))
                throw new InvalidOperationException($"Provider build finished without build/main.js. {buildTail}");

            // 4. Persist + start + verify.
            var state = new PotState(true, version, port);
            state.Save(config);
            Set("starting", 0.9, null);
            EnsureProviderRunning(state);

            // 5. End-to-end pickup check: the exact failure mode seen in the
            // wild is "ready but yt-dlp never loads the plugin". Best
            // effort (needs youtube.com): a miss only clears Verified and
            // logs — provisioning still succeeds.
            var verified = await VerifyPluginPickup(port);
            state = state with { Verified = verified };
            state.Save(config);
            Set("ready", 1, null);
            log.LogInformation(
                "YouTube bot-check protection ready (v{Version}, provider on 127.0.0.1:{Port}, plugin pickup {Pickup}).",
                version, port, verified ? "verified" : "unverified");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "YouTube protection provisioning failed.");
            Set("failed", 0, ex.Message);
        }
    }

    private void Set(string state, double progress, string? error)
    {
        lock (_lock)
        {
            _provisionState = state;
            _provisionProgress = progress;
            if (error is not null) _provisionError = error;
        }
    }

    // Deletes a directory with retries (Windows holds native-module locks
    // briefly after a process dies; AV scanners too). Missing dir is a
    // no-op; persistent failure throws with the locked path named.
    internal static void DeleteDirResilient(string dir, int attempts = 3)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == attempts - 1)
                    throw new InvalidOperationException(
                        $"Could not clear '{dir}' (a file is locked — stop the provider first): {ex.Message}", ex);
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    internal static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    internal static void RemoveLegacyExtractedPlugin(IConfiguration cfg)
    {
        var legacy = PotPaths.LegacyExtractedDir(cfg);
        if (Directory.Exists(legacy)) DeleteDirResilient(legacy);
    }

    internal static string PotProbeVideo(IConfiguration cfg) =>
        string.IsNullOrWhiteSpace(cfg["YtDlp:PotProbeVideo"])
            ? "https://www.youtube.com/watch?v=BaW_jenozKc"
            : cfg["YtDlp:PotProbeVideo"]!.Trim();

    // Runs the provisioned yt-dlp against a probe video and checks the
    // verbose output for the bgutil provider line. Pure parsing is
    // unit-tested (StdoutShowsPotProviders); this wrapper is best-effort.
    internal static bool StdoutShowsPotProviders(string stdout) =>
        stdout.Contains("PO Token Providers: bgutil", StringComparison.Ordinal);

    private async Task<bool> VerifyPluginPickup(int port)
    {
        string exe;
        try
        {
            exe = YtDlpPaths.Resolve(config);
        }
        catch (Exception ex)
        {
            log.LogWarning("Skipping plugin pickup check (yt-dlp not found: {Error}).", ex.Message);
            return false;
        }
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--plugin-dirs");
        psi.ArgumentList.Add(PotPaths.PluginDir(config));
        if (port != PotPaths.DefaultPort)
        {
            psi.ArgumentList.Add("--extractor-args");
            psi.ArgumentList.Add($"youtubepot-bgutilhttp:base_url=http://127.0.0.1:{port}");
        }
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--skip-download");
        psi.ArgumentList.Add("--socket-timeout");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("%(id)s");
        psi.ArgumentList.Add(PotProbeVideo(config));
        using var proc = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();
        void Keep(string? line)
        {
            if (line is null) return;
            lock (output)
            {
                if (output.Length < 16_000) output.AppendLine(line);
            }
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            proc.Start();
            var stdout = ConsumeAsync(proc.StandardOutput, Keep, cts.Token);
            var stderr = ConsumeAsync(proc.StandardError, Keep, cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Plugin pickup check did not complete.");
            return false;
        }
        var ok = StdoutShowsPotProviders(output.ToString());
        if (!ok) log.LogWarning("Plugin pickup check: yt-dlp did not report the bgutil provider.");
        return ok;
    }

    // Extracts only <tag>/server/** from the release tarball, rebased onto
    // dest. Skips absolute/parent-traversing entries defensively.
    internal static void ExtractServerSubtree(Stream tarGz, string dest)
    {
        using var reader = new TarReader(tarGz, leaveOpen: true);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.Directory)) continue;
            var name = (entry.Name ?? "").Replace('\\', '/').Trim('/');
            var slash = name.IndexOf('/');
            if (slash < 0) continue; // tag root itself
            var rel = name[(slash + 1)..];
            if (!rel.StartsWith("server/", StringComparison.Ordinal)) continue;
            rel = rel["server/".Length..];
            if (string.IsNullOrEmpty(rel)) continue;
            var target = Path.GetFullPath(Path.Combine(dest, rel));
            if (!target.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var outFile = File.Create(target);
            entry.DataStream!.CopyTo(outFile);
        }
    }

    // node --version / npm --version via explicit PATH probe (services run
    // with a minimal PATH where bare names silently miss — same rationale
    // as YtDlpPaths.FindOnPath).
    internal static (bool Found, string? Version) ProbeNode()
    {
        var node = YtDlpPaths.FindOnPath("node", pathVariable: null)
            ?? (OperatingSystem.IsWindows() ? YtDlpPaths.FindOnPath("node.exe") : null);
        if (node is null) return (false, null);
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = node,
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            proc.Start();
            var version = proc.StandardOutput.ReadLine()?.Trim();
            if (!proc.WaitForExit(15_000) || proc.ExitCode != 0) return (false, null);
            return (true, version);
        }
        catch
        {
            return (false, null);
        }
    }

    private static string? FindNpm()
    {
        if (OperatingSystem.IsWindows())
        {
            // npm ships as npm.cmd — CreateProcess needs cmd /c.
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), "npm.cmd");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }
        return YtDlpPaths.FindOnPath("npm");
    }

    private async Task<string> RunNpm(string workDir, string[] args, TimeSpan timeout)
    {
        var npm = FindNpm() ?? throw new InvalidOperationException("npm was not found on PATH.");
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(npm);
        }
        else psi.FileName = npm;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        var tail = new List<string>();
        void Keep(string? line)
        {
            if (line is null) return;
            lock (tail)
            {
                tail.Add(line);
                if (tail.Count > 10) tail.RemoveAt(0);
            }
        }
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            proc.Start();
            var stdout = ConsumeAsync(proc.StandardOutput, Keep, cts.Token);
            var stderr = ConsumeAsync(proc.StandardError, Keep, cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"npm {args[0]} timed out after {timeout.TotalMinutes:0} minutes.");
        }
        lock (tail)
        {
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"npm {args[0]} failed (exit {proc.ExitCode}): {string.Join(Environment.NewLine, tail)}");
            return string.Join(Environment.NewLine, tail);
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

    private static bool ProbePort(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            return false;
        }
    }

    // Starts the loopback provider unless something already answers on the
    // port (an externally managed provider is adopted, not replaced).
    private void EnsureProviderRunning(PotState state)
    {
        lock (_lock)
        {
            if (_provider is { HasExited: false }) return;
        }
        if (ProbePort(state.Port))
        {
            log.LogInformation("POT provider already answers on 127.0.0.1:{Port} — adopting it.", state.Port);
            return;
        }
        var (nodeFound, _) = ProbeNode();
        if (!nodeFound)
            throw new InvalidOperationException("Node.js was not found on PATH.");
        var entry = PotPaths.ServerEntry(config);
        if (!File.Exists(entry))
            throw new InvalidOperationException($"Provider server is not built (missing {entry}). Re-run setup.");
        var node = YtDlpPaths.FindOnPath("node") ?? YtDlpPaths.FindOnPath("node.exe")
            ?? throw new InvalidOperationException("Node.js was not found on PATH.");
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = node,
                ArgumentList = { entry, "--host", "127.0.0.1", "--port", state.Port.ToString() },
                WorkingDirectory = PotPaths.ServerDir(config),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) log.LogDebug("pot-provider: {Line}", e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) log.LogDebug("pot-provider ERR: {Line}", e.Data);
        };
        proc.Exited += (_, _) => _ = Task.Run(() => OnProviderExited(state));
        if (!proc.Start())
            throw new InvalidOperationException("Could not start the POT provider process.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        lock (_lock) _provider = proc;
        // Give it a moment, then verify it answers.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_provider?.HasExited == true) break;
            }
            if (ProbePort(state.Port)) return;
            Thread.Sleep(500);
        }
        lock (_lock)
        {
            if (_provider?.HasExited == true)
                throw new InvalidOperationException("The POT provider exited during startup — see server logs.");
        }
        if (!ProbePort(state.Port))
            throw new InvalidOperationException($"The POT provider did not answer on 127.0.0.1:{state.Port} within 20s.");
    }

    private void OnProviderExited(PotState state)
    {
        lock (_lock)
        {
            if (_provider is null) return;
            _provider.Dispose();
            _provider = null;
        }
        // Respawn loop (bounded): enabled protection survives provider
        // crashes; a poisoned setup surfaces via the status error instead
        // of spinning forever.
        var current = PotState.TryLoad(config);
        if (current is null || !current.Enabled) return;
        int attempt;
        lock (_lock) attempt = ++_restarts;
        if (attempt > MaxRestarts)
        {
            log.LogError("POT provider exited {Max} times — giving up until re-enabled.", MaxRestarts);
            lock (_lock)
            {
                _provisionState = "failed";
                _provisionError = "The provider process keeps exiting — check server logs, then Disable/Re-enable.";
            }
            return;
        }
        log.LogWarning("POT provider exited unexpectedly — restarting (attempt {Attempt}/{Max}).", attempt, MaxRestarts);
        Thread.Sleep(TimeSpan.FromSeconds(2 * attempt));
        try
        {
            EnsureProviderRunning(current with { Port = current.Port });
            lock (_lock) _restarts = 0;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "POT provider restart failed.");
        }
    }

    private void StopProvider()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _provider;
            _provider = null;
            _restarts = 0;
        }
        if (proc is null) return;
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch { }
        try { proc.Dispose(); } catch { }
    }
}

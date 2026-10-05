using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Hathor.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Enrichment;

// MeCab romanization backend (fugashi + UniDic via the bundled sidecar).
// Full morphological analysis + Hepburn — dramatically better than the
// kana table on kanji-heavy lyrics. Optional by design: no Python, no
// packages, timeout or bad output all yield null and the caller falls back
// to KanaRomaji (which is always available, including Docker images
// without Python — the Dockerfile is intentionally untouched).
public sealed class RomanizerOptions
{
    // Explicit python exe (service-safe: LocalSystem PATH may not see user
    // installs). Empty = auto-detect python -> python3 -> py -3.
    public string PythonPath { get; set; } = "";
    public int TimeoutSec { get; set; } = 60;
}

public sealed class FugashiRomanizer(
    RomanizerOptions options,
    ILogger<FugashiRomanizer> log) : IRomanizerBackend
{
    private static readonly string ScriptDir = Path.Combine(
        AppContext.BaseDirectory, "Python");

    // Probe once per process: a missing/broken interpreter must not cost
    // process spawns on every lyrics toggle.
    private static readonly ConcurrentDictionary<string, bool> ProbeCache = new();

    public async Task<string?> RomanizeAsync(string text, bool isLrc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var (exe, prefixArgs) = ResolvePython();
        if (exe is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSec, 5, 300)));
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in prefixArgs) process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.ArgumentList.Add(Path.Combine(ScriptDir, "romanize.py"));
            if (!process.Start()) return null;
            var request = JsonSerializer.Serialize(new { text, is_lrc = isLrc });
            await process.StandardInput.WriteAsync(request);
            process.StandardInput.Close();
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("error", out _)) return null;
            return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Python romanizer unavailable — kana fallback");
            return null;
        }
    }

    private (string? Exe, string[] PrefixArgs) ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(options.PythonPath) && Exists(options.PythonPath, []))
            return (options.PythonPath, []);
        // Windows: python | py -3. Linux/macOS + Docker: python3.
        if (Exists("python", [])) return ("python", []);
        if (Exists("python3", [])) return ("python3", []);
        if (Exists("py", ["-3"])) return ("py", ["-3"]);
        return (null, []);
    }

    private static bool Exists(string exe, string[] prefixArgs)
    {
        var key = exe + "|" + string.Join(" ", prefixArgs);
        if (ProbeCache.TryGetValue(key, out var cached)) return cached;
        var ok = false;
        try
        {
            using var probe = new Process();
            probe.StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in prefixArgs) probe.StartInfo.ArgumentList.Add(arg);
            probe.StartInfo.ArgumentList.Add("-c");
            probe.StartInfo.ArgumentList.Add("import fugashi");
            probe.Start();
            probe.WaitForExit(15000);
            ok = probe.ExitCode == 0;
        }
        catch
        {
            ok = false;
        }
        ProbeCache[key] = ok;
        return ok;
    }
}

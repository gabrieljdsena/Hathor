using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// Batch CLAP sidecar (laion_clap via Python). Loads the model once per batch,
// which is why callers embed 8-ish files at a time. Missing Python/model is a
// soft failure: the caller keeps metadata-only ranking.
public sealed class ClapEmbeddingProvider(
    DiscoveryOptions options,
    ILogger<ClapEmbeddingProvider> log) : IEmbeddingProvider
{
    private const string Marker = "HATHOR_EMBEDDING_RESULT:";
    private static readonly string ScriptDir =
        Path.Combine(AppContext.BaseDirectory, "Python");
    private static readonly ConcurrentDictionary<string, bool> ProbeCache = new();

    public async Task<IReadOnlyList<float[]?>> EmbedFilesAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var result = paths.Select(_ => (float[]?)null).ToList();
        if (!options.AudioEmbeddingsEnabled || paths.Count == 0) return result;

        var (exe, prefixArgs) = ResolvePython();
        if (exe is null)
        {
            log.LogWarning(
                "CLAP embeddings enabled but Python is unavailable — audio signal skipped");
            return result;
        }

        Process? process = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(
                Math.Clamp(options.AudioEmbeddingTimeoutSec, 10, 1800)));

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var arg in prefixArgs) process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.ArgumentList.Add(Path.Combine(ScriptDir, "clap_embed.py"));
            if (!process.Start()) return result;

            var request = JsonSerializer.Serialize(new
            {
                paths,
                checkpoint = options.AudioEmbeddingCheckpoint,
            });
            await process.StandardInput.WriteAsync(request.AsMemory(), timeout.Token);
            process.StandardInput.Close();

            // Drain stderr too: CLAP/torch can be chatty, and a full pipe
            // would deadlock the child process.
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            if (process.ExitCode != 0)
            {
                log.LogWarning("CLAP sidecar exited with code {Code}", process.ExitCode);
                return result;
            }

            var line = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
            if (line is null)
            {
                log.LogWarning("CLAP sidecar returned no embedding payload");
                return result;
            }

            using var doc = JsonDocument.Parse(line[Marker.Length..]);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                log.LogWarning("CLAP unavailable: {Error}", error.GetString());
                return result;
            }
            if (!doc.RootElement.TryGetProperty("vectors", out var vectors) ||
                vectors.ValueKind != JsonValueKind.Array)
                return result;

            var i = 0;
            foreach (var vector in vectors.EnumerateArray())
            {
                if (i >= result.Count) break;
                if (vector.ValueKind != JsonValueKind.Array) { i++; continue; }
                result[i] = vector.EnumerateArray()
                    .Select(v => (float)v.GetDouble())
                    .ToArray();
                i++;
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "CLAP embedding batch failed — audio signal skipped");
            return result;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    /* process cleanup must never break the request */
                }
                process.Dispose();
            }
        }
    }

    private (string? Exe, string[] PrefixArgs) ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(options.AudioEmbeddingPythonPath) &&
            File.Exists(options.AudioEmbeddingPythonPath))
            return (options.AudioEmbeddingPythonPath, []);

        // Windows: python | py -3. Linux/macOS: python3.
        if (Exists("python", [])) return ("python", []);
        if (Exists("python3", [])) return ("python3", []);
        if (Exists("py", ["-3"])) return ("py", ["-3"]);
        return (null, []);
    }

    private static bool Exists(string exe, string[] args)
    {
        var key = exe + "\u0001" + string.Join('\u0001', args);
        return ProbeCache.GetOrAdd(key, _ =>
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
                process.StartInfo.ArgumentList.Add("--version");
                if (!process.Start()) return false;
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* probe timeout */ }
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}

using System.Diagnostics;
using System.Text.Json;
using Hathor.Application.Ports;
using Hathor.Infrastructure.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Enrichment;

// Integrated-loudness analysis via ffmpeg loudnorm (single pass, no file
// changes): measures LUFS, stores per file, engine converts to gain.
// Resolution order matches SystemProbe: explicit FFmpeg:Path, then the
// app-downloaded tools dir, then PATH. Never throws (null = unmeasured).
public sealed class LoudnessAnalyzer(
    IConfiguration config,
    ILogger<LoudnessAnalyzer> log) : ILoudnessAnalyzer
{
    public async Task<double?> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var ffmpeg = config["FFmpeg:Path"];
        if (string.IsNullOrWhiteSpace(ffmpeg))
            ffmpeg = FfmpegPaths.InstalledExe(config) ?? FfmpegPaths.ExeName;
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                ArgumentList = { "-hide_banner", "-i", path, "-af", "loudnorm=print_format=json", "-f", "null", "-" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) return null;
            return ParseInputLufs(stderr);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Loudness analysis failed for {File}", Path.GetFileName(path));
            return null;
        }
    }

    // loudnorm prints its JSON summary to stderr: {"input_i": "-11.23", ...}.
    public static double? ParseInputLufs(string ffmpegStderr)
    {
        try
        {
            var start = ffmpegStderr.IndexOf('{');
            var end = ffmpegStderr.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            using var doc = JsonDocument.Parse(ffmpegStderr[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("input_i", out var lufs)) return null;
            var raw = lufs.ValueKind == JsonValueKind.String ? lufs.GetString() : lufs.GetRawText();
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                    && double.IsFinite(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

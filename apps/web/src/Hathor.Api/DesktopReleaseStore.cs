using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Hathor.Api;

// Scans the persistent desktop update folder for sideload releases. The
// publishing side copies one zip per release; the store picks the highest
// versionCode so older packages can stay around for rollback. Filename
// contract: hathor-desktop-<versionCode>-<versionName>.zip
public sealed record DesktopRelease(
    int VersionCode,
    string VersionName,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset PublishedAtUtc);

public sealed partial class DesktopReleaseStore(IConfiguration config, IMemoryCache cache)
{
    public const string ContentType = "application/zip";

    [GeneratedRegex(
        @"^hathor-desktop-(?<code>\d{1,9})-(?<name>.+)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseName();

    // DesktopApp:PackageDirectory wins; empty falls back next to the
    // persistent storage root (install-service.ps1 pins both to
    // C:\ProgramData\Hathor). Deliberately outside wwwroot/artifacts.
    public string PackageDirectory
    {
        get
        {
            var configured = config.GetValue<string>("DesktopApp:PackageDirectory");
            var dir = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(config.GetValue("Database:StorageRoot", "data") ?? "data", "desktop")
                : configured;
            return Path.GetFullPath(dir);
        }
    }

    public DesktopRelease? Latest()
    {
        var dir = PackageDirectory;
        if (!Directory.Exists(dir)) return null;

        string[] files;
        try
        {
            // Eager: EnumerateFiles can also throw mid-iteration when the
            // share goes away.
            files = Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        DesktopRelease? best = null;
        foreach (var path in files)
        {
            var match = ReleaseName().Match(Path.GetFileName(path));
            if (!match.Success || !int.TryParse(match.Groups["code"].Value, out var code))
                continue;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0) continue;
                var candidate = new DesktopRelease(
                    code,
                    match.Groups["name"].Value,
                    info.Name,
                    info.Length,
                    Sha256(info),
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
                if (best is null || candidate.VersionCode > best.VersionCode ||
                    (candidate.VersionCode == best.VersionCode &&
                     candidate.PublishedAtUtc > best.PublishedAtUtc))
                    best = candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mid-copy file or locked share: skip this candidate, keep
                // serving the previous release.
                continue;
            }
        }
        return best;
    }

    // Bare filename validation: no separators, must match the release naming
    // contract, and must exist in the drop folder. Returns null when absent.
    public string? ResolveDownload(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255) return null;
        if (Path.GetFileName(fileName) != fileName) return null;
        if (!ReleaseName().IsMatch(fileName)) return null;
        var path = Path.Combine(PackageDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    private string Sha256(FileInfo info)
    {
        var key = $"desktop-release-sha256:{info.FullName}:{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        return cache.GetOrCreate(key, entry =>
        {
            entry.SetSize(1);
            entry.SetAbsoluteExpiration(TimeSpan.FromHours(12));
            using var stream = info.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        })!;
    }
}

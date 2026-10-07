using System.Text.RegularExpressions;

namespace Hathor.Domain.Services;

// Ports Download._safe_filename + apply_metadata collision handling.
// Strips Windows-illegal chars, control chars, collapses spaces, caps at 150.
public static class FileNamingService
{
    private static readonly Regex IllegalChars = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);
    private static readonly Regex ControlChars = new(@"[\x00-\x1f]", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public static string SafeStem(string? name)
    {
        var s = IllegalChars.Replace(name ?? "", "");
        s = ControlChars.Replace(s, "");
        s = MultiSpace.Replace(s, " ").Trim().TrimEnd('.');
        if (s.Length > 150) s = s[..150].TrimEnd();
        return string.IsNullOrEmpty(s) ? "Unknown" : s;
    }

    public static string SafeMp3FileName(string? title) => SafeStem(title) + ".mp3";

    // Final name for a finished download. Pull jobs carry the exact remote
    // filename so the merged row resolves (returned verbatim — the pull
    // only submits bare filenames that already passed the traversal guard).
    // Normal jobs keep collision-safe derived names.
    public static string ResolveFinalName(
        string? targetFile, string derivedBase, Func<string, bool> exists) =>
        !string.IsNullOrWhiteSpace(targetFile)
            ? targetFile.Trim()
            : ResolveCollision(derivedBase, exists);

    // Returns a non-colliding filename: Title.mp3, Title (1).mp3, ...
    public static string ResolveCollision(string desiredFileName, Func<string, bool> exists)
    {
        if (!exists(desiredFileName)) return desiredFileName;
        var stem = desiredFileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            ? desiredFileName[..^4]
            : desiredFileName;
        var counter = 1;
        string candidate;
        do
        {
            candidate = $"{stem} ({counter}).mp3";
            counter++;
        } while (exists(candidate));
        return candidate;
    }
}

// Ports DatabaseManager._build_daily_mix_files quotas:
// 2 from top 10, 5 from 11-25, 13 from 26-70, rest prefer outside top 70.
public static class DailyMixGenerator
{
    public const int TargetSize = 50;

    public static IReadOnlyList<string> Build(
        IReadOnlyList<string> allFiles,
        IReadOnlyList<string> rankedFiles,
        Random? random = null)
    {
        random ??= Random.Shared;
        var target = Math.Min(TargetSize, allFiles.Count);
        if (target == 0) return Array.Empty<string>();

        var all = allFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ranked = rankedFiles.Where(f => all.Contains(f)).ToList();
        var top10 = ranked.Take(10).ToList();
        var top11_25 = ranked.Skip(10).Take(15).ToList();
        var top26_70 = ranked.Skip(25).Take(45).ToList();
        var top70 = ranked.Take(70).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var picked = new List<string>();
        foreach (var (pool, quota) in new[] { (top10, 2), (top11_25, 5), (top26_70, 13) })
        {
            if (pool.Count == 0 || picked.Count >= target) break;
            picked.AddRange(Sample(pool, Math.Min(quota, Math.Min(pool.Count, target - picked.Count)), random));
        }

        var remaining = target - picked.Count;
        if (remaining > 0)
        {
            var pickedSet = picked.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var discovery = allFiles.Where(f => !pickedSet.Contains(f) && !top70.Contains(f)).ToList();
            var take = Math.Min(remaining, discovery.Count);
            if (take > 0)
            {
                picked.AddRange(Sample(discovery, take, random));
                remaining -= take;
            }
            if (remaining > 0)
            {
                pickedSet = picked.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rest = allFiles.Where(f => !pickedSet.Contains(f)).ToList();
                picked.AddRange(Sample(rest, Math.Min(remaining, rest.Count), random));
            }
        }

        // Fisher-Yates shuffle of the final order.
        for (var i = picked.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (picked[i], picked[j]) = (picked[j], picked[i]);
        }
        return picked;
    }

    private static List<string> Sample(List<string> pool, int n, Random random)
    {
        if (n >= pool.Count) return new List<string>(pool);
        return pool.OrderBy(_ => random.Next()).Take(n).ToList();
    }
}

// Ports LyricsService._clean_track_artist.
public static class LyricsCleaning
{
    private static readonly Regex BracketQualifier = new(
        @"\s*[\(\[].*?(remaster|mix|version|edit|live|feat\.|ft\.).*?[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string Track, string Artist) CleanTrackArtist(string? trackName, string? artistName)
    {
        var track = (trackName ?? "").Replace('?', '?').Replace('！', '!');
        track = BracketQualifier.Replace(track, "").Trim();
        var artist = artistName ?? "";

        if (IsUnknownArtist(artist) && track.Contains(" - "))
        {
            var parts = track.Split([" - "], 2, StringSplitOptions.None);
            artist = parts[0].Trim();
            track = parts[1].Trim();
        }
        return (track, artist);
    }

    public static bool IsUnknownArtist(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ||
        artist.Trim().ToLowerInvariant() is "unknown" or "unknown artist";
    public static bool ArtistUsable(string? artist) => !IsUnknownArtist(artist);

    // Track-only fallback acceptance: exact case-insensitive track match, skips instrumentals.
    public static bool AcceptTrackOnlyCandidate(string cleanTrack, string? candidateTrack, bool instrumental) =>
        !instrumental &&
        !string.IsNullOrWhiteSpace(candidateTrack) &&
        Regex.Replace(candidateTrack, @"\s+", " ").Trim().Equals(
            Regex.Replace(cleanTrack, @"\s+", " ").Trim(),
            StringComparison.OrdinalIgnoreCase);

    // Podcast chapter names are "Title - Artist" (DJ-mix style) — the
    // reverse of the "Artist - Title" filename convention CleanTrackArtist
    // assumes, so that splitter would keep the wrong half. Take the leading
    // title part for track-only exact matching (artist stays empty).
    public static string CleanChapterTitle(string? name)
    {
        var title = BracketQualifier.Replace(name ?? "", "").Trim();
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) title = title[..dash].Trim();
        return title;
    }
}

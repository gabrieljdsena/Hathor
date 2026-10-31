using Hathor.Application.Ports;
using Hathor.Domain.Services;

namespace Hathor.Application.AiDj;

public sealed class AiDjCandidateSet
{
    public IReadOnlyList<AiDjCandidate> Candidates { get; init; } = [];
    public object Taste { get; init; } = new { };
    public IReadOnlyList<object> RecentTracks { get; init; } = [];
}

public sealed class AiDjCandidateService(
    IAiDjReadModel read,
    IDiscoverTasteReadModel taste,
    IHistoryReadModel history,
    ISongReadModel songs)
{
    public async Task<AiDjCandidateSet> BuildAsync(
        Guid userId, AiDjSettings settings, IReadOnlyCollection<string> excludeFiles,
        CancellationToken ct)
    {
        var rows = await read.GetCandidatesAsync(userId, 800, ct);
        if (rows.Count == 0) return new AiDjCandidateSet();

        var topArtists = await taste.GetTopArtistsAsync(userId, 8, ct);
        var topTags = await taste.GetTopTagsAsync(userId, 6, ct);
        var topTracks = await taste.GetTopTracksAsync(userId, 8, ct);
        var recentFiles = (await history.GetRecentPlayedFilesAsync(userId, 40, ct)).ToList();
        var recentSet = new HashSet<string>(recentFiles.Take(20), StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(excludeFiles, StringComparer.OrdinalIgnoreCase);
        var avoidArtists = SplitList(settings.AvoidArtists);
        var preferredGenres = SplitList(settings.PreferredGenres);
        var topArtistMap = topArtists.ToDictionary(
            a => MediaKeys.Normalize(a.Artist), a => a.Weight, StringComparer.OrdinalIgnoreCase);
        var topTagSet = new HashSet<string>(topTags, StringComparer.OrdinalIgnoreCase);
        var preferredSet = new HashSet<string>(preferredGenres, StringComparer.OrdinalIgnoreCase);
        var discovery = Math.Clamp(settings.DiscoveryLevel, 0, 1);
        var rng = new Random();

        var scored = new List<AiDjCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.File)) continue;
            if (recentSet.Contains(row.File) || excluded.Contains(row.File)) continue;
            var artist = row.Artist ?? "Unknown";
            if (avoidArtists.Any(a => MediaKeys.IsSameArtist(artist, a))) continue;
            var artistKey = MediaKeys.Normalize(artist);
            topArtistMap.TryGetValue(artistKey, out var artistWeight);
            var genre = row.Genre ?? "Unknown";
            var completion = row.Events > 0
                ? Math.Clamp((double)row.Completions / row.Events, 0, 1)
                : 0.55;
            var playFactor = Math.Min(1, row.PlayCount / 24.0);
            var preferredBoost = preferredSet.Contains(genre) ? 0.25 : 0;
            var topTagBoost = topTagSet.Contains(genre) ? 0.12 : 0;
            var playlistBoost = row.InPlaylists ? 0.08 : 0;
            var jitter = rng.NextDouble() * (0.15 + discovery * 0.35);
            var score = artistWeight * 0.40 + completion * 0.20 + playFactor * 0.15
                + preferredBoost + topTagBoost + playlistBoost + jitter;
            scored.Add(new AiDjCandidate
            {
                File = row.File,
                Title = row.Title,
                Artist = artist,
                Album = row.Album ?? "Unknown",
                Genre = genre,
                Year = row.Year ?? "Unknown",
                Duration = row.DurationSecs,
                PlayCount = row.PlayCount,
                CompletionRate = completion,
                LastPlayedDaysAgo = row.LastPlayedUtc is null
                    ? null
                    : Math.Max(0, (int)(DateTime.UtcNow - row.LastPlayedUtc.Value).TotalDays),
                InPlaylists = row.InPlaylists,
                Score = score,
            });
        }

        var selected = new List<AiDjCandidate>();
        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in scored.OrderByDescending(c => c.Score))
        {
            var key = MediaKeys.Normalize(candidate.Artist);
            artistCounts.TryGetValue(key, out var count);
            if (count >= 2) continue;
            selected.Add(candidate);
            artistCounts[key] = count + 1;
            if (selected.Count >= Math.Clamp(settings.BatchSize * 20, 20, 80)) break;
        }

        var recentSongs = recentFiles.Count == 0
            ? []
            : (await songs.GetManyAsync(userId, recentFiles.Take(12), includeCover: false, ct))
                .Select(s => (object)new { file = s.File, title = s.Title, artist = s.Artist })
                .ToList();
        var tasteSnapshot = new
        {
            top_artists = topArtists.Select(a => a.Artist).ToArray(),
            top_genres = topTags,
            top_tracks = topTracks.Select(t => $"{t.Title} — {t.Artist}").ToArray(),
            time_of_day = DateTime.Now.Hour switch
            {
                < 6 => "late night",
                < 12 => "morning",
                < 18 => "afternoon",
                _ => "evening",
            },
            day_of_week = DateTime.Now.DayOfWeek.ToString(),
        };
        return new AiDjCandidateSet
        {
            Candidates = selected,
            Taste = tasteSnapshot,
            RecentTracks = recentSongs,
        };
    }

    public static IReadOnlyList<string> FallbackPicks(
        IReadOnlyList<AiDjCandidate> candidates, int count,
        IReadOnlyCollection<string> excludeFiles)
    {
        var excluded = new HashSet<string>(excludeFiles, StringComparer.OrdinalIgnoreCase);
        var picks = new List<string>();
        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates.OrderByDescending(c => c.Score))
        {
            if (excluded.Contains(candidate.File)) continue;
            var key = MediaKeys.Normalize(candidate.Artist);
            artistCounts.TryGetValue(key, out var n);
            if (n >= 2) continue;
            picks.Add(candidate.File);
            artistCounts[key] = n + 1;
            if (picks.Count >= count) break;
        }
        return picks;
    }

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

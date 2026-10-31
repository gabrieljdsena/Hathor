using System.Globalization;
using System.Text.Json;
using Hathor.Domain.Entities;
using Hathor.Domain.Services;

namespace Hathor.Application.Discover;

// Joins stored ranker snapshots with feedback by attributing each event to
// the latest snapshot (<= 7 days old) that actually surfaced the item.
public static class DiscoverEvaluationData
{
    public static List<DiscoverEvaluationItem> Build(
        IReadOnlyList<DiscoverImpression> snapshots,
        IReadOnlyList<DiscoverFeedback> feedbackRows)
    {
        var parsed = new List<(DiscoverImpression Snapshot, List<DiscoverImpressionItem> Items)>();
        foreach (var snapshot in snapshots.OrderBy(s => s.Date))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<DiscoverImpressionItem>>(
                    snapshot.ItemsJson) ?? [];
                parsed.Add((snapshot, items));
            }
            catch (JsonException)
            {
                // Corrupt/old snapshot: skip it rather than failing the report.
            }
        }
        if (parsed.Count == 0) return [];

        var labels = new Dictionary<(string Date, string Key), (string? Event, DateTime At)>();
        foreach (var row in feedbackRows)
        {
            var feedbackDate = DateOnly.FromDateTime(row.CreatedAtUtc);
            var key = string.IsNullOrWhiteSpace(row.ItemKey)
                ? MediaKeys.Key(row.Title, row.Artist)
                : row.ItemKey;

            for (var i = parsed.Count - 1; i >= 0; i--)
            {
                if (!DateOnly.TryParseExact(parsed[i].Snapshot.Date, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var snapshotDate))
                    continue;
                if (snapshotDate > feedbackDate) continue;
                if (feedbackDate.DayNumber - snapshotDate.DayNumber > 7) break;

                if (!parsed[i].Items.Any(item =>
                        MediaKeys.Key(item.Title, item.Artist) == key))
                    continue;

                var labelKey = (parsed[i].Snapshot.Date, key);
                if (!labels.TryGetValue(labelKey, out var existing) ||
                    row.CreatedAtUtc > existing.At)
                    labels[labelKey] = (row.Event, row.CreatedAtUtc);
                break;
            }
        }

        var result = new List<DiscoverEvaluationItem>();
        foreach (var (snapshot, items) in parsed)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var key = MediaKeys.Key(item.Title, item.Artist);
                labels.TryGetValue((snapshot.Date, key), out var label);
                result.Add(new DiscoverEvaluationItem(
                    snapshot.Date,
                    i + 1,
                    key,
                    item.Source,
                    item.Artist,
                    item.Tags,
                    item.Features ?? FallbackFeatures(item.Score),
                    label.Event));
            }
        }
        return result;
    }

    private static DiscoverItemFeatures FallbackFeatures(double score) =>
        new(0, 0.5, 0, 0.5, 0.5, 0, 0, 0, 0, 0, score, 0, score);
}

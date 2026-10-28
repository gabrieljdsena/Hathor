namespace Hathor.Domain.Services;

// Feedback event names shared by the API, ranker and (later) learned models.
public static class DiscoverFeedbackEvents
{
    public const string Dismiss = "dismiss";
    public const string Preview = "preview";
    public const string Click = "click";
    public const string Download = "download";

    public static bool IsValid(string? value) => value is
        Dismiss or Preview or Click or Download;
}

// Normalized implicit signals derived from a user's recent feedback. All
// sets contain MediaKeys-normalized values; the ranker never sees raw rows.
public sealed record DiscoverFeedbackSignals(
    IReadOnlySet<string> DismissedKeys,
    IReadOnlySet<string> DismissedArtists,
    IReadOnlySet<string> LikedArtists,
    IReadOnlySet<string> LikedSeeds,
    IReadOnlySet<string> EngagedArtists,
    IReadOnlyDictionary<string, double> SourcePriors,
    IReadOnlyDictionary<string, double> ArtistBoosts,
    IReadOnlyDictionary<string, double> SeedBoosts)
{
    public static readonly DiscoverFeedbackSignals Empty = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, double>(StringComparer.Ordinal),
        new Dictionary<string, double>(StringComparer.Ordinal));
}

namespace Hathor.Domain.Services;

// Local-only taste profile (packages/contracts/discover.md): recency-weighted
// artists/tracks/tags built from Music_History + Songs. No external profile
// data ever flows in here.
// Parameter order matches the Dapper column order in DiscoverQueries
// (Artist, Plays, Weight) so constructor materialization needs no shims.
public sealed record TasteArtist(string Artist, long Plays, double Weight);

public sealed record TasteTrack(string Title, string Artist, long Plays, double Weight);

// Top played library files with recency/completion weights. Used for the
// CLAP taste centroid so audio similarity follows actual song history.
public sealed record TasteFile(string File, double Weight);

// User's historical global-popularity band (log10 listen counts of top
// played tracks). Null when there is not enough data.
public sealed record PopularityProfile(double MedianLog, int Samples);

public sealed record TasteProfile(
    IReadOnlyList<TasteArtist> Artists,
    IReadOnlyList<TasteTrack> Tracks,
    IReadOnlyList<string> Tags,
    IReadOnlyList<TasteFile>? Files = null)
{
    public static readonly TasteProfile Empty = new([], [], [], []);

    public bool IsEmpty => Artists.Count == 0 && Tracks.Count == 0 && Tags.Count == 0;
}

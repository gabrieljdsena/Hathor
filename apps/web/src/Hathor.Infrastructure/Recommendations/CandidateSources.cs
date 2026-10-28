using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// Primary candidate source: open MusicBrainz graph + ListenBrainz public
// similarity. MusicBrainz is the slow dependency (~1 req/s), so the options
// cap seeds and browses; a cancelled budget returns partial candidates.
public sealed class OpenDataCandidateSource(
    IMusicBrainzClient mb,
    IListenBrainzClient lb,
    DiscoveryOptions options,
    ILogger<OpenDataCandidateSource> log) : ICandidateSource
{
    public string Name => "open-data";
    public bool IsFallback => false;

    public async Task<IReadOnlyList<DiscoverCandidate>> GetCandidatesAsync(
        TasteProfile taste, int limit, CancellationToken ct = default)
    {
        limit = Math.Max(1, limit);

        var out_ = new List<DiscoverCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        bool Add(DiscoverCandidate candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.Title) ||
                string.IsNullOrWhiteSpace(candidate.Artist))
                return true;
            var key = !string.IsNullOrWhiteSpace(candidate.Mbid)
                ? "mb:" + candidate.Mbid
                : MediaKeys.Key(candidate.Title, candidate.Artist);
            if (!seen.Add(key)) return true;
            out_.Add(candidate);
            return out_.Count < limit;
        }

        try
        {
            // No listening history: use a few broad open tags so Discover is
            // never empty just because the profile is cold. The chart
            // fallback still runs when open data is unavailable.
            if (taste.IsEmpty)
            {
                foreach (var tag in options.ColdStartTags.Take(3))
                {
                    if (ct.IsCancellationRequested) return out_;
                    var recordings = await mb.GetTagRecordingsAsync(
                        tag, options.TagRecordingsLimit, ct);
                    foreach (var r in recordings)
                        if (!Add(ToCandidate(r, DiscoverSources.Tag, seedArtist: null,
                                similarity: 0.5, popularity: 0, fallbackGenre: tag)))
                            return out_;
                }
                return out_;
            }

            var seeds = new List<(TasteArtist Taste, string Mbid)>();
            foreach (var artist in taste.Artists.Take(Math.Max(1, options.SeedArtistLimit)))
            {
                if (ct.IsCancellationRequested) return out_;
                var mbArtist = await mb.SearchArtistAsync(artist.Artist, ct);
                if (mbArtist is not null) seeds.Add((artist, mbArtist.Mbid));
            }

            foreach (var (seed, seedMbid) in seeds)
            {
                if (ct.IsCancellationRequested) return out_;

                var similar = await lb.GetSimilarArtistsAsync(
                    seedMbid, options.SimilarArtistLimit, ct);
                if (ct.IsCancellationRequested) return out_;
                var related = await mb.GetRelatedArtistsAsync(
                    seedMbid,
                    options.SimilarArtistsToBrowse + options.RelatedArtistsToBrowse,
                    ct);

                foreach (var artist in similar.Take(options.SimilarArtistsToBrowse))
                {
                    if (ct.IsCancellationRequested) return out_;
                    var recordings = await mb.GetArtistRecordingsAsync(
                        artist.Mbid, options.RecordingsPerArtist, ct);
                    foreach (var r in recordings)
                        if (!Add(ToCandidate(r, DiscoverSources.Similar, seed.Artist,
                                artist.Score, popularity: 0)))
                            return out_;
                }

                foreach (var artist in related.Take(options.RelatedArtistsToBrowse))
                {
                    if (ct.IsCancellationRequested) return out_;
                    var recordings = await mb.GetArtistRecordingsAsync(
                        artist.Mbid, options.RecordingsPerArtist, ct);
                    foreach (var r in recordings)
                        if (!Add(ToCandidate(r, DiscoverSources.Graph, seed.Artist,
                                similarity: 0.7, popularity: 0)))
                            return out_;
                }
            }

            // Genre/tag exploration from local tags (no external profile).
            foreach (var tag in taste.Tags.Take(Math.Max(1, options.TopTags)))
            {
                if (ct.IsCancellationRequested) return out_;
                var recordings = await mb.GetTagRecordingsAsync(
                    tag, options.TagRecordingsLimit, ct);
                foreach (var r in recordings)
                    if (!Add(ToCandidate(r, DiscoverSources.Tag, seedArtist: null,
                            similarity: 0.5, popularity: 0, fallbackGenre: tag)))
                        return out_;
            }

            // Track-level CF: capped to protect the MusicBrainz rate limit.
            foreach (var track in taste.Tracks.Take(Math.Max(1, options.SimilarRecordingSeeds)))
            {
                if (ct.IsCancellationRequested) return out_;
                var seed = await mb.SearchRecordingAsync(track.Title, track.Artist, ct);
                if (seed is null) continue;

                var similar = await lb.GetSimilarRecordingsAsync(
                    seed.Mbid, options.SimilarRecordingsPerSeed, ct);
                foreach (var candidate in similar)
                {
                    if (ct.IsCancellationRequested) return out_;
                    var recording = await mb.GetRecordingAsync(candidate.Mbid, ct);
                    if (recording is null) continue;
                    if (!Add(ToCandidate(recording, DiscoverSources.Similar,
                            seedArtist: track.Artist, similarity: candidate.Score,
                            popularity: candidate.ListenCount)))
                        return out_;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Budget/caller cancellation: rank what is already collected.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Open-data candidate generation failed — partial pool kept");
        }

        return out_;
    }

    private static DiscoverCandidate ToCandidate(
        MbRecordingDto r, string source, string? seedArtist,
        double similarity, long popularity, string? fallbackGenre = null)
    {
        var genre = r.Tags.Count > 0 ? r.Tags[0] : fallbackGenre ?? "";
        return new DiscoverCandidate(
            r.Title, r.Artist, r.Album, r.Year, genre, "",
            source, r.Mbid, seedArtist, similarity, popularity, r.Tags);
    }
}

// Cold-start fallback only: chart picks (empty history) or same-artist
// expansion when open data returned too little. Runs after the primary
// sources and is explicitly marked as a fallback in the service.
public sealed class FallbackCandidateSource(IITunesClient itunes) : ICandidateSource
{
    public string Name => "itunes-fallback";
    public bool IsFallback => true;

    public async Task<IReadOnlyList<DiscoverCandidate>> GetCandidatesAsync(
        TasteProfile taste, int limit, CancellationToken ct = default)
    {
        limit = Math.Max(1, limit);
        var out_ = new List<DiscoverCandidate>();

        if (!taste.IsEmpty)
        {
            foreach (var artist in taste.Artists.Take(8))
            {
                if (ct.IsCancellationRequested) break;
                var hits = await itunes.SearchTermAsync(artist.Artist, 5, ct);
                foreach (var hit in hits.Where(h => DiscoverRanker.IsSameArtist(h.Artist, artist.Artist)))
                {
                    out_.Add(new DiscoverCandidate(
                        hit.Title, hit.Artist, hit.Album, hit.Year, hit.Genre,
                        hit.ArtworkUrl, DiscoverSources.Artist, null, artist.Artist,
                        Similarity: 0.85));
                    if (out_.Count >= limit) return out_;
                }
            }
        }

        if (out_.Count > 0) return out_;

        // Empty history (or no same-artist hits): chart fallback, clearly
        // labeled and never mixed into a personalized response.
        var trending = await itunes.GetTrendingAsync(10, ct);
        foreach (var term in trending)
        {
            if (ct.IsCancellationRequested) break;
            var hit = await itunes.SearchSingleAsync(term, null, ct);
            if (hit is null) continue;
            out_.Add(new DiscoverCandidate(
                hit.Title, hit.Artist, hit.Album, hit.Year, hit.Genre,
                hit.ArtworkUrl, DiscoverSources.Chart, null, null, Similarity: 0.2));
            if (out_.Count >= limit) break;
        }
        return out_;
    }
}

// iTunes is a catalog/artwork resolver here — it never decides the order.
// Metadata is only filled when it matches the candidate artist, so a fuzzy
// search hit cannot attach the wrong cover.
public sealed class ITunesCandidateResolver(
    IITunesClient itunes,
    ILogger<ITunesCandidateResolver> log) : ICandidateResolver
{
    public async Task<DiscoverCandidate> ResolveAsync(
        DiscoverCandidate candidate, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ArtworkUrl)) return candidate;

        try
        {
            var hit = await itunes.SearchSingleAsync(candidate.Title, candidate.Artist, ct);
            if (hit is null) return candidate;
            if (!string.IsNullOrWhiteSpace(hit.Artist) &&
                !string.IsNullOrWhiteSpace(candidate.Artist) &&
                !MediaKeys.IsSameArtist(hit.Artist, candidate.Artist))
                return candidate;

            return candidate with
            {
                Album = string.IsNullOrWhiteSpace(candidate.Album) ? hit.Album : candidate.Album,
                Year = string.IsNullOrWhiteSpace(candidate.Year) ? hit.Year : candidate.Year,
                Genre = string.IsNullOrWhiteSpace(candidate.Genre) ? hit.Genre : candidate.Genre,
                ArtworkUrl = hit.ArtworkUrl,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "iTunes artwork resolve failed for {Title}", candidate.Title);
            return candidate;
        }
    }
}

using System.Text.Json;
using Hathor.Application.Discover;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Recommendations;

// Adds CLAP content similarity to candidates that already have a cached
// preview embedding. The library centroid comes from per-user song vectors;
// missing vectors are neutral (the ranker treats null as 0.5).
public sealed class ClapAudioSimilarityScorer(
    ITrackEmbeddingRepository embeddings,
    DiscoveryOptions options,
    ILogger<ClapAudioSimilarityScorer> log) : IAudioSimilarityScorer
{
    public async Task<IReadOnlyList<DiscoverCandidate>> ScoreAsync(
        Guid userId, TasteProfile taste, IReadOnlyList<DiscoverCandidate> candidates,
        CancellationToken ct = default)
    {
        if (!options.AudioEmbeddingsEnabled || candidates.Count == 0)
            return candidates;

        try
        {
            var centroid = await BuildCentroidAsync(userId, taste, ct);
            if (centroid is null) return candidates;

            var mbids = candidates
                .Select(c => c.Mbid)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (mbids.Count == 0) return candidates;

            var previews = await embeddings.GetManyAsync(
                userId, "preview", mbids, options.AudioEmbeddingModel, ct);
            if (previews.Count == 0) return candidates;

            var vectors = new Dictionary<string, float[]?>(StringComparer.Ordinal);
            foreach (var preview in previews)
                vectors[preview.OwnerKey] = Parse(preview.VectorJson);

            return candidates.Select(candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate.Mbid) ||
                    !vectors.TryGetValue(candidate.Mbid, out var vector) ||
                    vector is null)
                    return candidate;

                var cosine = Cosine(centroid, vector);
                return candidate with
                {
                    AudioSimilarity = Math.Clamp((cosine + 1.0) / 2.0, 0, 1),
                };
            }).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "CLAP scoring failed — metadata-only ranking kept");
            return candidates;
        }
    }

    private async Task<float[]?> BuildCentroidAsync(
        Guid userId, TasteProfile taste, CancellationToken ct)
    {
        // Prefer the user's top played tracks (history), falling back to the
        // whole library for brand-new accounts or before backfill catches up.
        var files = taste.Files ?? [];
        if (files.Count > 0)
        {
            var keys = files.Select(f => f.File)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rows = await embeddings.GetManyAsync(
                userId, "song", keys, options.AudioEmbeddingModel, ct);
            var byFile = new Dictionary<string, float[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
                byFile[row.OwnerKey] = Parse(row.VectorJson);

            var weighted = WeightedCentroid(files, byFile);
            if (weighted is not null) return weighted;
        }

        var library = await embeddings.GetAllAsync(
            userId, "song", options.AudioEmbeddingModel, ct);
        return Centroid(library);
    }

    private static float[]? WeightedCentroid(
        IReadOnlyList<TasteFile> files,
        IReadOnlyDictionary<string, float[]?> vectors)
    {
        float[]? centroid = null;
        double totalWeight = 0;
        foreach (var file in files)
        {
            if (!vectors.TryGetValue(file.File, out var vector) || vector is null)
                continue;
            if (centroid is null)
            {
                centroid = new float[vector.Length];
            }
            else if (centroid.Length != vector.Length)
            {
                continue;
            }

            var weight = Math.Max(0.01, file.Weight);
            for (var i = 0; i < vector.Length; i++)
                centroid[i] += (float)(vector[i] * weight);
            totalWeight += weight;
        }

        if (centroid is null || totalWeight <= 0) return null;
        for (var i = 0; i < centroid.Length; i++)
            centroid[i] = (float)(centroid[i] / totalWeight);
        return centroid;
    }

    private static float[]? Centroid(IReadOnlyList<TrackEmbedding> library)
    {
        if (library.Count == 0) return null;
        float[]? centroid = null;
        var count = 0;
        foreach (var row in library)
        {
            var vector = Parse(row.VectorJson);
            if (vector is null) continue;
            if (centroid is null)
            {
                centroid = new float[vector.Length];
            }
            else if (centroid.Length != vector.Length)
            {
                continue;
            }
            for (var i = 0; i < vector.Length; i++) centroid[i] += vector[i];
            count++;
        }
        if (centroid is null || count == 0) return null;
        for (var i = 0; i < centroid.Length; i++) centroid[i] /= count;
        return centroid;
    }

    private static float[]? Parse(string json)
    {
        try
        {
            var vector = JsonSerializer.Deserialize<float[]>(json);
            return vector is { Length: > 0 } ? vector : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, ma = 0, mb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            ma += a[i] * a[i];
            mb += b[i] * b[i];
        }
        if (ma <= 0 || mb <= 0) return 0;
        return dot / Math.Sqrt(ma * mb);
    }
}

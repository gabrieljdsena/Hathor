using System.Text.Json;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using Hathor.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class ClapAudioSimilarityScorerTests
{
    private static TrackEmbedding Embedding(string key, float[] vector) => new()
    {
        UserId = Guid.Empty,
        OwnerType = key.StartsWith("song:", StringComparison.Ordinal) ? "song" : "preview",
        OwnerKey = key.Split(':', 2)[1],
        Model = "clap-v1",
        VectorJson = JsonSerializer.Serialize(vector),
        Dimension = vector.Length,
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static DiscoverCandidate Candidate(string title, string? mbid) =>
        new(title, "Artist", "Album", "2020", "Rock", "", DiscoverSources.Similar,
            Mbid: mbid);

    [Fact]
    public async Task Score_SetsCosineSimilarity_ForCachedPreviews()
    {
        var userId = Guid.NewGuid();
        var repo = Substitute.For<ITrackEmbeddingRepository>();
        repo.GetAllAsync(userId, "song", "clap-v1", Arg.Any<CancellationToken>())
            .Returns([
                Embedding("song:a.mp3", [1f, 0f]),
                Embedding("song:b.mp3", [1f, 0f]),
            ]);
        repo.GetManyAsync(userId, "preview", Arg.Any<IEnumerable<string>>(),
                "clap-v1", Arg.Any<CancellationToken>())
            .Returns([
                Embedding("preview:mb-a", [1f, 0f]),
                Embedding("preview:mb-b", [0f, 1f]),
            ]);
        var options = new DiscoveryOptions
        {
            AudioEmbeddingsEnabled = true,
            AudioEmbeddingModel = "clap-v1",
        };
        var scorer = new ClapAudioSimilarityScorer(
            repo, options, Substitute.For<ILogger<ClapAudioSimilarityScorer>>());

        var result = await scorer.ScoreAsync(userId, TasteProfile.Empty,
            [Candidate("Same", "mb-a"), Candidate("Orthogonal", "mb-b"),
             Candidate("Unknown", null)], CancellationToken.None);

        result[0].AudioSimilarity.Should().NotBeNull();
        result[0].AudioSimilarity!.Value.Should().BeApproximately(1.0, 0.001);
        result[1].AudioSimilarity.Should().NotBeNull();
        result[1].AudioSimilarity!.Value.Should().BeApproximately(0.5, 0.001);
        result[2].AudioSimilarity.Should().BeNull();
    }

    [Fact]
    public async Task Score_UsesTopPlayedTracks_BeforeWholeLibrary()
    {
        var userId = Guid.NewGuid();
        var repo = Substitute.For<ITrackEmbeddingRepository>();
        repo.GetManyAsync(userId, "song", Arg.Any<IEnumerable<string>>(),
                "clap-v1", Arg.Any<CancellationToken>())
            .Returns([Embedding("song:history.mp3", [1f, 0f])]);
        repo.GetManyAsync(userId, "preview", Arg.Any<IEnumerable<string>>(),
                "clap-v1", Arg.Any<CancellationToken>())
            .Returns([Embedding("preview:mb-a", [1f, 0f])]);
        repo.GetAllAsync(userId, "song", "clap-v1", Arg.Any<CancellationToken>())
            .Returns([Embedding("song:other.mp3", [0f, 1f])]);
        var options = new DiscoveryOptions
        {
            AudioEmbeddingsEnabled = true,
            AudioEmbeddingModel = "clap-v1",
        };
        var scorer = new ClapAudioSimilarityScorer(
            repo, options, Substitute.For<ILogger<ClapAudioSimilarityScorer>>());
        var taste = new TasteProfile([], [], [],
            [new TasteFile("history.mp3", 1.0)]);

        var result = await scorer.ScoreAsync(userId, taste,
            [Candidate("Same", "mb-a")], CancellationToken.None);

        result[0].AudioSimilarity.Should().NotBeNull();
        result[0].AudioSimilarity!.Value.Should().BeApproximately(1.0, 0.001);
        await repo.DidNotReceive().GetAllAsync(
            userId, "song", "clap-v1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Score_Disabled_LeavesCandidatesUntouched()
    {
        var repo = Substitute.For<ITrackEmbeddingRepository>();
        var options = new DiscoveryOptions { AudioEmbeddingsEnabled = false };
        var scorer = new ClapAudioSimilarityScorer(
            repo, options, Substitute.For<ILogger<ClapAudioSimilarityScorer>>());
        var candidates = new List<DiscoverCandidate> { Candidate("Song", "mb-a") };

        var result = await scorer.ScoreAsync(
            Guid.NewGuid(), TasteProfile.Empty, candidates, CancellationToken.None);

        result.Should().BeSameAs(candidates);
        await repo.DidNotReceive().GetAllAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

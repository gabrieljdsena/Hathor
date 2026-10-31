using FluentAssertions;
using Hathor.Application.AiDj;
using Hathor.Domain.Playback;

namespace Hathor.Application.Tests;

public sealed class AiDjTests
{
    [Fact]
    public void FallbackPicks_DiversifiesArtistsAndSkipsExcludedFiles()
    {
        var candidates = new List<AiDjCandidate>
        {
            new() { File = "a1.mp3", Artist = "A", Score = 1.0 },
            new() { File = "a2.mp3", Artist = "A", Score = 0.9 },
            new() { File = "a3.mp3", Artist = "A", Score = 0.8 },
            new() { File = "b1.mp3", Artist = "B", Score = 0.7 },
            new() { File = "c1.mp3", Artist = "C", Score = 0.6 },
        };

        var picks = AiDjCandidateService.FallbackPicks(candidates, 4, ["a2.mp3"]);

        picks.Should().BeEquivalentTo(["a1.mp3", "a3.mp3", "b1.mp3", "c1.mp3"]);
    }

    [Fact]
    public void AiDjIsAValidQueueSource()
    {
        QueueSourceTypes.IsValid(QueueSourceTypes.AiDj).Should().BeTrue();
        QueueSource.Parse($"{{\"Type\":\"ai_dj\",\"Id\":\"session\"}}")!.Type
            .Should().Be(QueueSourceTypes.AiDj);
    }
}

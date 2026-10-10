using FluentAssertions;
using Hathor.Application.Sync;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

// Resume-state snapshot (DB-free substitutes): the player's persisted state
// IS the spot (no remote). Push reports whether a spot exists; latest
// surfaces it fresh-only (30-day rule), null otherwise.
public sealed class PlaybackSyncTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task PushPlayback_PlayingTrack_ReportsSpot()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new PlaybackState
            {
                UserId = UserId, CurrentFile = "s.mp3", CurrentIsPodcast = false,
                IsPlaying = true, FirstPlay = false,
                PositionOffsetSec = 42, LastPlayUtc = DateTime.UtcNow,
            });

        (await new PushPlaybackStateHandler(playback)
            .Handle(new PushPlaybackStateCommand(UserId), CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task PushPlayback_NothingLoaded_ReportsNoSpot()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new PlaybackState { UserId = UserId, FirstPlay = true });

        (await new PushPlaybackStateHandler(playback)
            .Handle(new PushPlaybackStateCommand(UserId), CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task LatestPlayback_FreshSpot_SurfacesIt()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new PlaybackState
            {
                UserId = UserId, CurrentFile = "e.mp3", CurrentIsPodcast = true,
                FirstPlay = false, PausePositionSec = 95, LastPlayUtc = DateTime.UtcNow,
            });

        var spot = await new GetLatestPlaybackHandler(playback)
            .Handle(new GetLatestPlaybackQuery(UserId), CancellationToken.None);

        spot.Should().NotBeNull();
        spot!.File.Should().Be("e.mp3");
        spot.PositionSec.Should().Be(95);
        spot.IsPodcast.Should().BeTrue();
    }

    [Fact]
    public async Task LatestPlayback_StaleSpot_Hidden()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new PlaybackState
            {
                UserId = UserId, CurrentFile = "old.mp3", CurrentIsPodcast = false,
                FirstPlay = false, PausePositionSec = 10,
                LastPlayUtc = DateTime.UtcNow - TimeSpan.FromDays(31),
            });

        (await new GetLatestPlaybackHandler(playback)
            .Handle(new GetLatestPlaybackQuery(UserId), CancellationToken.None))
            .Should().BeNull();
    }
}

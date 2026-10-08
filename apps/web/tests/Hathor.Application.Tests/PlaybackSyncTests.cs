using FluentAssertions;
using Hathor.Application.Ports;
using Hathor.Application.Sync;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

// Resume-state snapshot (DB-free substitutes): pushes only when a track is
// loaded past first play; the latest-spot query delegates to the pull port.
public sealed class PlaybackSyncTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task PushPlayback_PlayingTrack_PushesPosition()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new PlaybackState
            {
                UserId = UserId, CurrentFile = "s.mp3", CurrentIsPodcast = false,
                IsPlaying = true, FirstPlay = false,
                PositionOffsetSec = 42, LastPlayUtc = DateTime.UtcNow,
            });
        var push = Substitute.For<IRemotePushService>();
        push.PushPlaybackAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<double>(),
            Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        (await new PushPlaybackStateHandler(playback, push)
            .Handle(new PushPlaybackStateCommand(UserId), CancellationToken.None))
            .Should().BeTrue();
        await push.Received(1).PushPlaybackAsync(UserId, "s.mp3",
            Arg.Is<double>(p => p >= 42 && p < 60), false, "Web",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushPlayback_NothingLoaded_SkipsWithoutPush()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new PlaybackState { UserId = UserId, FirstPlay = true });
        var push = Substitute.For<IRemotePushService>();

        (await new PushPlaybackStateHandler(playback, push)
            .Handle(new PushPlaybackStateCommand(UserId), CancellationToken.None))
            .Should().BeFalse();
        await push.DidNotReceiveWithAnyArgs().PushPlaybackAsync(
            default!, default!, default, default, default!, default);
    }

    [Fact]
    public async Task LatestPlayback_DelegatesToPullService()
    {
        var pull = Substitute.For<IRemotePullService>();
        var spot = new PlaybackSpotDto("desktop", "e.mp3", 95, true, "Desktop", DateTime.UtcNow);
        pull.GetLatestPlaybackAsync(UserId, Arg.Any<CancellationToken>()).Returns(spot);

        (await new GetLatestPlaybackHandler(pull)
            .Handle(new GetLatestPlaybackQuery(UserId), CancellationToken.None))
            .Should().Be(spot);
    }
}

using FluentAssertions;
using Hathor.Application.Dtos;
using Hathor.Application.Player;
using Hathor.Application.Ports;
using Hathor.Domain.Playback;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class PlaybackClampTests
{
    [Theory]
    [InlineData(120.0, 900.0, 120.0)] // sane positions pass through
    [InlineData(5000.0, 900.0, 900.0)] // runaway estimates clamp to the track
    [InlineData(-5.0, 900.0, 0.0)] // negative seeks clamp to zero
    [InlineData(5000.0, 0.0, 5000.0)] // unknown length keeps legacy behavior
    [InlineData(5000.0, null, 5000.0)]
    public void Clamp_BoundsPosition(double seconds, double? duration, double expected) =>
        PlaybackPositionClamp.Clamp(seconds, duration).Should().Be(expected);

    [Fact]
    public async Task ClampStoredAsync_BoundsBothOffsets()
    {
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(Arg.Any<Guid>(), "ep.mp3", false, Arg.Any<CancellationToken>())
            .Returns(new SongDto("ep.mp3", "Host", "Episode", "", "", 900, null, null, true));
        var state = new PlaybackState
        {
            CurrentFile = "ep.mp3",
            PositionOffsetSec = 5000,
            PausePositionSec = 5000,
        };

        await PlaybackPositionClamp.ClampStoredAsync(state, songs, Guid.NewGuid());

        state.PositionOffsetSec.Should().Be(900);
        state.PausePositionSec.Should().Be(900);
    }

    [Fact]
    public async Task ClampStoredAsync_LeavesSanePositionsAlone()
    {
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(Arg.Any<Guid>(), "ep.mp3", false, Arg.Any<CancellationToken>())
            .Returns(new SongDto("ep.mp3", "Host", "Episode", "", "", 900, null, null, true));
        var state = new PlaybackState
        {
            CurrentFile = "ep.mp3",
            PositionOffsetSec = 120,
            PausePositionSec = 120,
        };

        await PlaybackPositionClamp.ClampStoredAsync(state, songs, Guid.NewGuid());

        state.PositionOffsetSec.Should().Be(120);
        state.PausePositionSec.Should().Be(120);
    }

    [Fact]
    public async Task ClampStoredAsync_SkipsUnknownFiles()
    {
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((SongDto?)null);
        var state = new PlaybackState
        {
            CurrentFile = "ghost.mp3",
            PositionOffsetSec = 5000,
            PausePositionSec = 5000,
        };

        await PlaybackPositionClamp.ClampStoredAsync(state, songs, Guid.NewGuid());

        state.PositionOffsetSec.Should().Be(5000);
        state.PausePositionSec.Should().Be(5000);
    }
}

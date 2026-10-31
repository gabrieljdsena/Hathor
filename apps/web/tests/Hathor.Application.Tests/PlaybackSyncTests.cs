using FluentAssertions;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

// Device-aware resume spots (one row per user+device, last wins): push
// upserts the caller's row; latest surfaces the newest spot from a
// DIFFERENT device (own device excluded), fresh-only (30-day rule).
public sealed class PlaybackSyncTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task PushPlayback_UpsertsCallerRow_ReportsTrue()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();

        (await new PushPlaybackStateHandler(spots).Handle(
            new PushPlaybackStateCommand(UserId, "browser-1", "Chrome", "s.mp3", 42.5, false),
            CancellationToken.None))
            .Should().BeTrue();

        await spots.Received(1).UpsertAsync(
            Arg.Is<PlaybackSpot>(s =>
                s.UserId == UserId && s.DeviceId == "browser-1" &&
                s.File == "s.mp3" && s.PositionSec == 42.5 &&
                s.IsPodcast == false && s.DeviceLabel == "Chrome" &&
                s.QueueFiles.Count == 1 && s.QueueFiles[0] == "s.mp3"),
            Arg.Any<CancellationToken>());
        await spots.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushPlayback_QueueSnapshot_CurrentFirstCapped()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();
        var files = new List<string> { "b.mp3", " ", "s.mp3", "c.mp3", "b.mp3" };

        await new PushPlaybackStateHandler(spots).Handle(
            new PushPlaybackStateCommand(UserId, "browser-1", null, "s.mp3", 10, false,
                files, new Dtos.QueueSourceDto("playlist", "7")),
            CancellationToken.None);

        await spots.Received(1).UpsertAsync(
            Arg.Is<PlaybackSpot>(s =>
                s.QueueFiles.SequenceEqual(new[] { "s.mp3", "b.mp3", "c.mp3" }) &&
                s.QueueSourceJson != null && s.QueueSourceJson.Contains("playlist")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LatestPlayback_SurfacesQueueSnapshot()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();
        spots.LatestForeignAsync(
                UserId, "browser-1", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new PlaybackSpot
            {
                UserId = UserId, DeviceId = "phone-9", File = "s.mp3",
                PositionSec = 95, IsPodcast = false, DeviceLabel = "Android",
                UpdatedUtc = DateTime.UtcNow,
                QueueFiles = new List<string> { "s.mp3", "n.mp3" },
                QueueSourceJson = """{"Type":"all_songs","Id":null}""",
            });

        var spot = await new GetLatestPlaybackHandler(spots)
            .Handle(new GetLatestPlaybackQuery(UserId, "browser-1"), CancellationToken.None);

        spot.Should().NotBeNull();
        spot!.QueueFiles.Should().Equal(new[] { "s.mp3", "n.mp3" });
        spot.QueueSource.Should().NotBeNull();
        spot.QueueSource!.Type.Should().Be("all_songs");
    }

    [Fact]
    public async Task PushPlayback_BlankDeviceOrFile_WritesNothing()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();

        (await new PushPlaybackStateHandler(spots).Handle(
            new PushPlaybackStateCommand(UserId, "  ", null, "s.mp3", 1, false),
            CancellationToken.None))
            .Should().BeFalse();
        (await new PushPlaybackStateHandler(spots).Handle(
            new PushPlaybackStateCommand(UserId, "browser-1", null, "", 1, false),
            CancellationToken.None))
            .Should().BeFalse();

        await spots.DidNotReceive().UpsertAsync(
            Arg.Any<PlaybackSpot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PushPlayback_NegativePosition_ClampedToZero()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();

        await new PushPlaybackStateHandler(spots).Handle(
            new PushPlaybackStateCommand(UserId, "browser-1", null, "s.mp3", -5, false),
            CancellationToken.None);

        await spots.Received(1).UpsertAsync(
            Arg.Is<PlaybackSpot>(s => s.PositionSec == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LatestPlayback_ForeignSpot_SurfacesIt()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();
        spots.LatestForeignAsync(
                UserId, "browser-1", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new PlaybackSpot
            {
                UserId = UserId, DeviceId = "phone-9", File = "e.mp3",
                PositionSec = 95, IsPodcast = true, DeviceLabel = "Android",
                UpdatedUtc = DateTime.UtcNow,
            });

        var spot = await new GetLatestPlaybackHandler(spots)
            .Handle(new GetLatestPlaybackQuery(UserId, "browser-1"), CancellationToken.None);

        spot.Should().NotBeNull();
        spot!.File.Should().Be("e.mp3");
        spot.PositionSec.Should().Be(95);
        spot.IsPodcast.Should().BeTrue();
        spot.UserKey.Should().Be("phone-9");
        spot.Device.Should().Be("Android");
    }

    [Fact]
    public async Task LatestPlayback_OnlyOwnSpot_ReturnsNull()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();
        spots.LatestForeignAsync(
                UserId, "browser-1", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((PlaybackSpot?)null);

        (await new GetLatestPlaybackHandler(spots)
            .Handle(new GetLatestPlaybackQuery(UserId, "browser-1"), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task LatestPlayback_UsesThirtyDayFreshnessCutoff()
    {
        var spots = Substitute.For<IPlaybackSpotRepository>();

        await new GetLatestPlaybackHandler(spots)
            .Handle(new GetLatestPlaybackQuery(UserId, null), CancellationToken.None);

        await spots.Received(1).LatestForeignAsync(
            UserId, null,
            Arg.Is<DateTime>(d =>
                d <= DateTime.UtcNow - TimeSpan.FromDays(29) &&
                d >= DateTime.UtcNow - TimeSpan.FromDays(31)),
            Arg.Any<CancellationToken>());
    }
}

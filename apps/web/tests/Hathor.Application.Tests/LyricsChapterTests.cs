using FluentAssertions;
using Hathor.Application.Lyrics;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

// Chapter-scoped lyric saves/removals (all DB-free substitutes).
// An explicit chapter id that no longer resolves must fail loudly —
// silently falling back to the file row would store (or wipe!) lyrics no
// view ever reads back.
public sealed class LyricsChapterTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static IPodcastTimestampRepository Timestamps(PodcastTimestamp? chapter)
    {
        var timestamps = Substitute.For<IPodcastTimestampRepository>();
        timestamps.GetAsync(UserId, "ep.mp3", 7, Arg.Any<CancellationToken>())
            .Returns(chapter);
        return timestamps;
    }

    private static PodcastTimestamp Chapter(string name) =>
        new() { Id = 7, UserId = UserId, PodcastFile = "ep.mp3", Name = name, StartSecs = 60 };

    [Fact]
    public async Task Save_ValidChapterId_WritesChapterKey()
    {
        var cache = Substitute.For<ILyricsRepository>();

        var ok = await new SaveLyricsHandler(cache, Timestamps(Chapter("Main")))
            .Handle(new SaveLyricsCommand(UserId, "ep.mp3", "[00:01.00] hi", null, 7),
                CancellationToken.None);

        ok.Should().BeTrue();
        await cache.Received(1).UpsertAsync(UserId, "ep.mp3::chapter:7",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_UnknownChapterId_ReturnsFalse_WithoutWriting()
    {
        var cache = Substitute.For<ILyricsRepository>();

        var ok = await new SaveLyricsHandler(cache, Timestamps(null))
            .Handle(new SaveLyricsCommand(UserId, "ep.mp3", "[00:01.00] hi", null, 7),
                CancellationToken.None);

        ok.Should().BeFalse();
        await cache.DidNotReceiveWithAnyArgs().UpsertAsync(
            default, default!, default!, default);
    }

    [Fact]
    public async Task Save_BlankChapterName_ReturnsFalse_WithoutWriting()
    {
        var cache = Substitute.For<ILyricsRepository>();

        var ok = await new SaveLyricsHandler(cache, Timestamps(Chapter("   ")))
            .Handle(new SaveLyricsCommand(UserId, "ep.mp3", "[00:01.00] hi", null, 7),
                CancellationToken.None);

        ok.Should().BeFalse();
        await cache.DidNotReceiveWithAnyArgs().UpsertAsync(
            default, default!, default!, default);
    }

    [Fact]
    public async Task Delete_UnknownChapterId_ReturnsFalse_WithoutDeleting()
    {
        var cache = Substitute.For<ILyricsRepository>();

        var ok = await new DeleteLyricsHandler(cache, Timestamps(null))
            .Handle(new DeleteLyricsCommand(UserId, "ep.mp3", 7), CancellationToken.None);

        ok.Should().BeFalse();
        await cache.DidNotReceiveWithAnyArgs().DeleteAsync(default, default!, default);
    }

    [Fact]
    public async Task Delete_ValidChapterId_DeletesChapterKey()
    {
        var cache = Substitute.For<ILyricsRepository>();
        cache.DeleteAsync(UserId, "ep.mp3::chapter:7", Arg.Any<CancellationToken>())
            .Returns(true);

        var ok = await new DeleteLyricsHandler(cache, Timestamps(Chapter("Main")))
            .Handle(new DeleteLyricsCommand(UserId, "ep.mp3", 7), CancellationToken.None);

        ok.Should().BeTrue();
        await cache.Received(1).DeleteAsync(UserId, "ep.mp3::chapter:7",
            Arg.Any<CancellationToken>());
    }
}

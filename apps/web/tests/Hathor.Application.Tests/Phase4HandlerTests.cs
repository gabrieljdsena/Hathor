using FluentAssertions;
using Hathor.Application.Enrichment;
using Hathor.Application.Ingest;
using Hathor.Application.Lyrics;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class Phase4HandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Submit_DelegatesToQueue()
    {
        var queue = Substitute.For<IDownloadQueue>();
        queue.SubmitAsync(UserId, "https://x", "T", "A", false, Arg.Any<CancellationToken>())
            .Returns("qid1");
        var qid = await new SubmitDownloadHandler(queue)
            .Handle(new SubmitDownloadCommand(UserId, "https://x", "T", "A", false), CancellationToken.None);
        qid.Should().Be("qid1");
    }

    [Fact]
    public async Task Batch_SubmitsNonBlankLines()
    {
        var queue = Substitute.For<IDownloadQueue>();
        queue.SubmitAsync(UserId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                false, Arg.Any<CancellationToken>())
            .Returns("q");
        var qids = await new BatchDownloadHandler(queue).Handle(
            new BatchDownloadCommand(UserId, ["  ", "song one", "", "song two"], false),
            CancellationToken.None);
        qids.Should().HaveCount(2);
        await queue.Received(2).SubmitAsync(UserId, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLyrics_CacheHit_SkipsNetwork()
    {
        var cache = Substitute.For<ILyricsRepository>();
        cache.GetByFileAsync(UserId, "s.mp3", Arg.Any<CancellationToken>())
            .Returns(new Domain.Entities.Lyric
            {
                UserId = UserId,
                SongFile = "s.mp3",
                LyricsJson = """{"Synced":null,"Plain":"la la"}""",
            });
        var lrclib = Substitute.For<ILrclibClient>();

        var dto = await new GetLyricsHandler(cache, lrclib).Handle(
            new GetLyricsQuery(UserId, "s.mp3", "Track", "Artist", null, null),
            CancellationToken.None);

        dto!.Plain.Should().Be("la la");
        await lrclib.DidNotReceiveWithAnyArgs().GetExactAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task GetLyrics_Miss_FetchesAndCaches()
    {
        var cache = Substitute.For<ILyricsRepository>();
        var lrclib = Substitute.For<ILrclibClient>();
        lrclib.GetExactAsync("Track", "Artist", null, Arg.Any<CancellationToken>())
            .Returns(new Dtos.LyricsDto("[00:01.00] la", "la"));

        var dto = await new GetLyricsHandler(cache, lrclib).Handle(
            new GetLyricsQuery(UserId, "s.mp3", "Track", "Artist", null, null),
            CancellationToken.None);

        dto!.Plain.Should().Be("la");
        await cache.Received(1).UpsertAsync(UserId, "s.mp3", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveLyrics_Empty_ReturnsFalse()
    {
        var cache = Substitute.For<ILyricsRepository>();
        (await new SaveLyricsHandler(cache).Handle(
            new SaveLyricsCommand(UserId, "s.mp3", null, null), CancellationToken.None))
            .Should().BeFalse();
        await cache.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GetLyrics_Refresh_SkipsCacheRead()
    {
        var cache = Substitute.For<ILyricsRepository>();
        var lrclib = Substitute.For<ILrclibClient>();
        lrclib.GetExactAsync("Track", "Artist", null, Arg.Any<CancellationToken>())
            .Returns(new Dtos.LyricsDto("[00:01.00] fresh", "fresh"));

        var dto = await new GetLyricsHandler(cache, lrclib).Handle(
            new GetLyricsQuery(UserId, "s.mp3", "Track", "Artist", null, null, Refresh: true),
            CancellationToken.None);

        dto!.Plain.Should().Be("fresh");
        await cache.DidNotReceiveWithAnyArgs().GetByFileAsync(default!, default!, default);
        await cache.Received(1).UpsertAsync(UserId, "s.mp3", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteLyrics_Missing_ReturnsFalse()
    {
        var cache = Substitute.For<ILyricsRepository>();
        cache.DeleteAsync(UserId, "s.mp3", Arg.Any<CancellationToken>()).Returns(false);
        (await new DeleteLyricsHandler(cache).Handle(
            new DeleteLyricsCommand(UserId, "s.mp3"), CancellationToken.None))
            .Should().BeFalse();
        await cache.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    public async Task DeleteLyrics_Existing_DeletesAndSaves()
    {
        var cache = Substitute.For<ILyricsRepository>();
        cache.DeleteAsync(UserId, "s.mp3", Arg.Any<CancellationToken>()).Returns(true);
        (await new DeleteLyricsHandler(cache).Handle(
            new DeleteLyricsCommand(UserId, "s.mp3"), CancellationToken.None))
            .Should().BeTrue();
        await cache.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ArtistImage_PrefersExactMatch_FallsBackToTopHit()
    {
        var itunes = Substitute.For<IITunesClient>();
        itunes.SearchMultiAsync("Queen", null, 5, Arg.Any<CancellationToken>()).Returns(
        [
            new Dtos.ITunesHitDto("T1", "Not Queen", "A", "2000", "Rock", "https://x/other.jpg"),
            new Dtos.ITunesHitDto("T2", "Queen", "B", "2001", "Rock", "https://x/queen.jpg"),
        ]);
        var art = await new ArtworkHandlers(itunes).Handle(
            new ArtistImageQuery("Queen"), CancellationToken.None);
        art!.Url.Should().Be("https://x/queen.jpg");
    }

    [Fact]
    public async Task ArtistImage_NoUsableArtwork_ReturnsNull()
    {
        var itunes = Substitute.For<IITunesClient>();
        itunes.SearchMultiAsync("Nobody", null, 5, Arg.Any<CancellationToken>())
            .Returns(new List<Dtos.ITunesHitDto>());
        (await new ArtworkHandlers(itunes).Handle(
            new ArtistImageQuery("Nobody"), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task AlbumImage_RequiresAlbumMatch_RespectsArtist()
    {
        var itunes = Substitute.For<IITunesClient>();
        itunes.SearchMultiAsync("Opera", "Queen", 5, Arg.Any<CancellationToken>()).Returns(
        [
            new Dtos.ITunesHitDto("T", "Other Band", "Opera", "2000", "Rock", "https://x/wrong.jpg"),
            new Dtos.ITunesHitDto("T", "Queen", "Opera", "1975", "Rock", "https://x/right.jpg"),
        ]);
        var art = await new ArtworkHandlers(itunes).Handle(
            new AlbumImageQuery("Opera", "Queen"), CancellationToken.None);
        art!.Url.Should().Be("https://x/right.jpg");
    }

    [Fact]
    public async Task GetLyricsOffset_ReturnsStoredValue()
    {
        var cache = Substitute.For<ILyricsRepository>();
        cache.GetOffsetAsync(UserId, "s.mp3", Arg.Any<CancellationToken>()).Returns(250);
        (await new GetLyricsOffsetHandler(cache).Handle(
            new GetLyricsOffsetQuery(UserId, "s.mp3"), CancellationToken.None))
            .Should().Be(250);
    }

    [Fact]
    public async Task SetLyricsOffset_Clamps_Saves_ReturnsClamped()
    {
        var cache = Substitute.For<ILyricsRepository>();
        var result = await new SetLyricsOffsetHandler(cache).Handle(
            new SetLyricsOffsetCommand(UserId, "s.mp3", 99999), CancellationToken.None);
        result.Should().Be(10000);
        await cache.Received(1).SetOffsetAsync(UserId, "s.mp3", 10000, Arg.Any<CancellationToken>());
        await cache.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

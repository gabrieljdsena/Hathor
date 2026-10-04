using FluentAssertions;
using Hathor.Application.Dtos;
using Hathor.Application.History;
using Hathor.Application.Mix;
using Hathor.Application.Player;
using Hathor.Application.Ports;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class Phase3HandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static SongDto Song(string file) =>
        new(file, "Artist", "Title", "Album", "2020", 180, null, null);

    [Fact]
    public async Task DailyMix_ReturnsCached_WhenRowValid()
    {
        var mixes = Substitute.For<IDailyMixRepository>();
        mixes.GetAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Domain.Entities.DailyMix
            {
                UserId = UserId,
                MixDate = "2026-10-03",
                SongFilesJson = """["a.mp3","b.mp3"]""",
            });
        var songs = Substitute.For<ISongReadModel>();
        songs.GetManyAsync(UserId, Arg.Any<IEnumerable<string>>(), false, Arg.Any<CancellationToken>())
            .Returns([Song("a.mp3"), Song("b.mp3")]);
        var storage = Substitute.For<ILibraryStorage>();

        var dto = await new DailyMixService(mixes, songs, storage)
            .GetAsync(UserId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeTrue();
        dto.Songs.Should().HaveCount(2);
        await mixes.DidNotReceive().SaveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DailyMix_Regenerates_WhenStoredFilesGone()
    {
        var mixes = Substitute.For<IDailyMixRepository>();
        mixes.GetAsync(UserId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Domain.Entities.DailyMix
            {
                UserId = UserId,
                MixDate = "2026-10-03",
                SongFilesJson = """["gone.mp3"]""",
            });
        mixes.GetRankedFilesAsync(UserId, Arg.Any<CancellationToken>())
            .Returns([]);
        var songs = Substitute.For<ISongReadModel>();
        songs.GetManyAsync(UserId, Arg.Any<IEnumerable<string>>(), false, Arg.Any<CancellationToken>())
            .Returns([]);
        var storage = Substitute.For<ILibraryStorage>();
        storage.ListSongFiles(UserId).Returns(["n1.mp3", "n2.mp3"]);

        var dto = await new DailyMixService(mixes, songs, storage)
            .GetAsync(UserId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeFalse();
        await mixes.Received(1).SaveAsync(UserId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DailyMix_EmptyLibrary_ReturnsEmptyUncached()
    {
        var mixes = Substitute.For<IDailyMixRepository>();
        var songs = Substitute.For<ISongReadModel>();
        songs.GetManyAsync(UserId, Arg.Any<IEnumerable<string>>(), false, Arg.Any<CancellationToken>())
            .Returns([]);
        var storage = Substitute.For<ILibraryStorage>();
        storage.ListSongFiles(UserId).Returns([]);

        var dto = await new DailyMixService(mixes, songs, storage)
            .GetAsync(UserId, forceRegenerate: true, CancellationToken.None);

        dto.Songs.Should().BeEmpty();
        dto.Cached.Should().BeFalse();
    }

    [Fact]
    public async Task Rebuild_NoSource_ReturnsFalse()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new PlaybackState { UserId = UserId, CurrentFile = "a.mp3" });
        var songs = Substitute.For<ISongReadModel>();

        var result = await new RebuildQueueHandler(
            playback,
            Substitute.For<IPlaylistRepository>(),
            new DailyMixService(Substitute.For<IDailyMixRepository>(), songs, Substitute.For<ILibraryStorage>()),
            songs,
            Substitute.For<IHistoryReadModel>(),
            Substitute.For<ILibraryStorage>(),
            Substitute.For<IPlaybackHub>())
            .Handle(new RebuildQueueCommand(UserId), CancellationToken.None);

        result.Rebuilt.Should().BeFalse();
    }

    [Fact]
    public async Task Rebuild_PlaylistSource_RebuildsAroundCurrent()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        var state = new PlaybackState
        {
            UserId = UserId,
            CurrentFile = "b.mp3",
            Source = new QueueSource("playlist", "7"),
        };
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(state);
        var playlists = Substitute.For<IPlaylistRepository>();
        playlists.GetSongFilesAsync(UserId, 7, Arg.Any<CancellationToken>())
            .Returns(new List<(string, DateTime)>
            {
                ("a.mp3", DateTime.UtcNow), ("b.mp3", DateTime.UtcNow), ("c.mp3", DateTime.UtcNow),
            });
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(UserId, Arg.Any<string>()).Returns(true);
        var songs = Substitute.For<ISongReadModel>();
        var mix = new DailyMixService(
            Substitute.For<IDailyMixRepository>(), songs, storage);

        var result = await new RebuildQueueHandler(
            playback, playlists, mix, songs,
            Substitute.For<IHistoryReadModel>(), storage,
            Substitute.For<IPlaybackHub>())
            .Handle(new RebuildQueueCommand(UserId), CancellationToken.None);

        result.Rebuilt.Should().BeTrue();
        state.NextFiles.Should().BeEquivalentTo("c.mp3");
        state.PrevFiles.Should().BeEquivalentTo("a.mp3");
        state.CurrentPlaylistId.Should().Be(7);
    }

    [Fact]
    public async Task Rebuild_StaleSource_ReturnsFalse()
    {
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new PlaybackState
            {
                UserId = UserId,
                CurrentFile = "gone.mp3",
                Source = new QueueSource("playlist", "7"),
            });
        var playlists = Substitute.For<IPlaylistRepository>();
        playlists.GetSongFilesAsync(UserId, 7, Arg.Any<CancellationToken>())
            .Returns(new List<(string, DateTime)> { ("a.mp3", DateTime.UtcNow) });
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(UserId, Arg.Any<string>()).Returns(true);
        var songs = Substitute.For<ISongReadModel>();
        var mix = new DailyMixService(
            Substitute.For<IDailyMixRepository>(), songs, storage);

        var result = await new RebuildQueueHandler(
            playback, playlists, mix, songs,
            Substitute.For<IHistoryReadModel>(), storage,
            Substitute.For<IPlaybackHub>())
            .Handle(new RebuildQueueCommand(UserId), CancellationToken.None);

        result.Rebuilt.Should().BeFalse();
    }

    [Fact]
    public async Task History_SkipsMissingFiles()
    {
        var history = Substitute.For<IHistoryReadModel>();
        history.GetPlayedPageAsync(UserId, 1, 10, Arg.Any<CancellationToken>())
            .Returns((new List<(string, DateTime)> { ("gone.mp3", DateTime.UtcNow) }, 1));
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(UserId, "gone.mp3", true, Arg.Any<CancellationToken>())
            .Returns((SongDto?)null);

        var page = await new HistoryHandlers(
            history, songs, Substitute.For<IPlaylistReadModel>())
            .Handle(new GetPlayedHistoryQuery(UserId), CancellationToken.None);

        page.Items.Should().BeEmpty();
        page.TotalPages.Should().Be(1);
        page.CurrentPage.Should().Be(1);
    }
}

using FluentAssertions;
using Hathor.Application.Metadata;
using Hathor.Application.Podcasts;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

// A file locked by another holder (desktop player sharing this library,
// an active web stream, a scanner) must stash the payload as a pending
// edit instead of 500ing — the applier writes it on the next track change
// and keeps it across failures until the lock releases.
public sealed class MetadataLockFallbackTests
{
    private readonly Guid _userId = Guid.NewGuid();

    private static PlaybackState PlayingOther() =>
        new() { CurrentFile = "other.mp3", IsPlaying = true, FirstPlay = false };

    private static Dtos.SongDto SongRow(string file) =>
        new(file, "Artist", "Title", "Album", "2020", 180, null, null, false);

    [Fact]
    public async Task UpdateSongMetadata_LockedFile_StashesInsteadOfThrowing()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        var writer = Substitute.For<IMetadataWriter>();
        writer.WriteSongAsync(
                Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException(
                "The process cannot access the file because it is being used by another process.")));
        var songs = Substitute.For<ISongReadModel>();
        songs.GetByFileAsync(_userId, "s.mp3", true, Arg.Any<CancellationToken>())
            .Returns(SongRow("s.mp3"));
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(PlayingOther());
        var records = Substitute.For<ISongRecordRepository>();
        var pending = Substitute.For<IPendingEditRepository>();

        var handler = new UpdateSongMetadataHandler(storage, writer, records, songs, playback, pending);
        var result = await handler.Handle(new UpdateSongMetadataCommand(
            _userId, "s.mp3", "New Title", null, null, null, null, null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Pending.Should().BeTrue();
        await pending.Received(1).UpsertAsync(
            Arg.Is<PendingMetadataEdit>(p => p.File == "s.mp3" && p.Title == "New Title" && !p.IsPodcast),
            Arg.Any<CancellationToken>());
        await records.DidNotReceiveWithAnyArgs().UpsertMetadataAsync(
            default!, default!, default!, default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task UpdateSongMetadata_MissingFile_StillThrowsWithoutStashing()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        var writer = Substitute.For<IMetadataWriter>();
        writer.WriteSongAsync(
                Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<FileNotFoundException>(new FileNotFoundException()));
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(PlayingOther());
        var pending = Substitute.For<IPendingEditRepository>();

        var handler = new UpdateSongMetadataHandler(
            storage, writer,
            Substitute.For<ISongRecordRepository>(),
            Substitute.For<ISongReadModel>(),
            playback, pending);
        await Assert.ThrowsAsync<FileNotFoundException>(() => handler.Handle(
            new UpdateSongMetadataCommand(
                _userId, "gone.mp3", "T", null, null, null, null, null),
            CancellationToken.None));
        await pending.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task UpdatePodcastMetadata_LockedFile_StashesInsteadOfThrowing()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.PodcastExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
        storage.PodcastPath(Arg.Any<Guid>(), Arg.Any<string>()).Returns("e.mp3");
        var writer = Substitute.For<IMetadataWriter>();
        writer.WritePathAsync(
                Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException(
                "The process cannot access the file because it is being used by another process.")));
        var podcasts = Substitute.For<IPodcastReadModel>();
        podcasts.GetByFileAsync(_userId, "e.mp3", Arg.Any<CancellationToken>())
            .Returns(SongRow("e.mp3"));
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(PlayingOther());
        var pending = Substitute.For<IPendingEditRepository>();

        var handler = new UpdatePodcastMetadataHandler(
            storage, writer,
            Substitute.For<IPodcastRecordRepository>(),
            podcasts, playback, pending);
        var result = await handler.Handle(new UpdatePodcastMetadataCommand(
            _userId, "e.mp3", "New Title", "Host", null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Pending.Should().BeTrue();
        await pending.Received(1).UpsertAsync(
            Arg.Is<PendingMetadataEdit>(p => p.File == "e.mp3" && p.Title == "New Title" && p.IsPodcast),
            Arg.Any<CancellationToken>());
    }
}

using FluentAssertions;
using Hathor.Application.Dtos;
using Hathor.Application.Metadata;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

// Deferred metadata edits (all DB-free substitutes — unlike the Phase2/5
// suites, which need live Postgres).
public sealed class PendingEditTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed record Deps(
        IPendingEditRepository Pending,
        IMetadataWriter Writer,
        ILibraryStorage Storage,
        ISongRecordRepository SongRecords,
        ISongReadModel Songs,
        IPodcastRecordRepository PodcastRecords,
        IPodcastReadModel Podcasts,
        IPlaybackStateRepository Playback);

    private static Deps Mocks(PlaybackState state)
    {
        var d = new Deps(
            Substitute.For<IPendingEditRepository>(),
            Substitute.For<IMetadataWriter>(),
            Substitute.For<ILibraryStorage>(),
            Substitute.For<ISongRecordRepository>(),
            Substitute.For<ISongReadModel>(),
            Substitute.For<IPodcastRecordRepository>(),
            Substitute.For<IPodcastReadModel>(),
            Substitute.For<IPlaybackStateRepository>());
        d.Playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>()).Returns(state);
        return d;
    }

    private static PendingMetadataApplier Applier(Deps d) =>
        new(d.Pending, d.Writer, d.Storage, d.SongRecords, d.Songs,
            d.PodcastRecords, d.Podcasts, d.Playback,
            Substitute.For<ILogger<PendingMetadataApplier>>());

    private static SongDto Song(string file) =>
        new(file, "Artist", "Title", "Album", "2020", 180, null, null, false);

    [Fact]
    public async Task Apply_SongEdit_WritesTags_AndDropsRow()
    {
        var d = Mocks(new PlaybackState { UserId = UserId, CurrentFile = "next.mp3" });
        d.Storage.SongExists(UserId, "s.mp3").Returns(true);
        d.Pending.GetAsync(UserId, "s.mp3", Arg.Any<CancellationToken>())
            .Returns(new PendingMetadataEdit
            {
                UserId = UserId, File = "s.mp3", Title = "New Title",
                CreatedUtc = DateTime.UtcNow,
            });
        d.Songs.GetByFileAsync(UserId, "s.mp3", true, Arg.Any<CancellationToken>())
            .Returns(Song("s.mp3"));

        (await Applier(d).ApplyForFileAsync(UserId, "s.mp3", CancellationToken.None))
            .Should().BeTrue();
        await d.Writer.Received(1).WriteSongAsync(
            UserId, "s.mp3", "New Title", null, null, null, null, null,
            Arg.Any<CancellationToken>());
        await d.Pending.Received(1).DeleteAsync(UserId, "s.mp3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_StillCurrent_KeepsRow_WithoutWriting()
    {
        var d = Mocks(new PlaybackState { UserId = UserId, CurrentFile = "s.mp3" });

        (await Applier(d).ApplyForFileAsync(UserId, "s.mp3", CancellationToken.None))
            .Should().BeFalse();
        await d.Writer.DidNotReceiveWithAnyArgs().WriteSongAsync(
            default!, default!, default, default, default, default, default, default, default);
        await d.Pending.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task Apply_MissingFile_DropsRow_WithoutWriting()
    {
        var d = Mocks(new PlaybackState { UserId = UserId, CurrentFile = "next.mp3" });
        d.Storage.SongExists(UserId, "s.mp3").Returns(false);
        d.Pending.GetAsync(UserId, "s.mp3", Arg.Any<CancellationToken>())
            .Returns(new PendingMetadataEdit { UserId = UserId, File = "s.mp3" });

        (await Applier(d).ApplyForFileAsync(UserId, "s.mp3", CancellationToken.None))
            .Should().BeFalse();
        await d.Writer.DidNotReceiveWithAnyArgs().WriteSongAsync(
            default!, default!, default, default, default, default, default, default, default);
        await d.Pending.Received(1).DeleteAsync(UserId, "s.mp3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_WriterFailure_KeepsRow_ForNextTrackChange()
    {
        var d = Mocks(new PlaybackState { UserId = UserId, CurrentFile = "next.mp3" });
        d.Storage.SongExists(UserId, "s.mp3").Returns(true);
        d.Pending.GetAsync(UserId, "s.mp3", Arg.Any<CancellationToken>())
            .Returns(new PendingMetadataEdit { UserId = UserId, File = "s.mp3", Title = "T" });
        d.Writer.WriteSongAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => Task.FromException(new InvalidOperationException("locked")));

        (await Applier(d).ApplyForFileAsync(UserId, "s.mp3", CancellationToken.None))
            .Should().BeFalse();
        await d.Pending.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, default);
    }

    [Fact]
    public async Task Apply_PodcastEdit_WritesEpisodePath()
    {
        var d = Mocks(new PlaybackState { UserId = UserId, CurrentFile = "next.mp3" });
        d.Storage.PodcastExists(UserId, "e.mp3").Returns(true);
        d.Storage.PodcastPath(UserId, "e.mp3").Returns("/tmp/e.mp3");
        d.Pending.GetAsync(UserId, "e.mp3", Arg.Any<CancellationToken>())
            .Returns(new PendingMetadataEdit
            {
                UserId = UserId, File = "e.mp3", IsPodcast = true,
                Title = "New Title", Artist = "Host", CreatedUtc = DateTime.UtcNow,
            });
        d.Podcasts.GetByFileAsync(UserId, "e.mp3", Arg.Any<CancellationToken>())
            .Returns(new SongDto("e.mp3", "Host", "New Title", "", "", 60, null, null, true));

        (await Applier(d).ApplyForFileAsync(UserId, "e.mp3", CancellationToken.None))
            .Should().BeTrue();
        await d.Writer.Received(1).WritePathAsync(
            "/tmp/e.mp3", "New Title", "Host", null, null, null, null,
            Arg.Any<CancellationToken>());
        await d.Pending.Received(1).DeleteAsync(UserId, "e.mp3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PodcastPatch_CurrentEpisode_StashesInsteadOfWriting()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.PodcastExists(UserId, "e.mp3").Returns(true);
        var writer = Substitute.For<IMetadataWriter>();
        var readModel = Substitute.For<IPodcastReadModel>();
        readModel.GetByFileAsync(UserId, "e.mp3", Arg.Any<CancellationToken>())
            .Returns(new SongDto("e.mp3", "Host", "Ep", "", "", 60, null, null, true));
        var playback = Substitute.For<IPlaybackStateRepository>();
        playback.GetOrCreateAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new PlaybackState { UserId = UserId, CurrentFile = "e.mp3", IsPlaying = true });
        var pending = Substitute.For<IPendingEditRepository>();

        var result = await new Podcasts.UpdatePodcastMetadataHandler(
                storage, writer, Substitute.For<IPodcastRecordRepository>(),
                readModel, playback, pending)
            .Handle(new Podcasts.UpdatePodcastMetadataCommand(
                UserId, "e.mp3", "New Title", "Host", null), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Pending.Should().BeTrue();
        await writer.DidNotReceiveWithAnyArgs().WritePathAsync(
            default!, default, default, default, default, default, default, default);
        await pending.Received(1).UpsertAsync(
            Arg.Is<PendingMetadataEdit>(p => p.File == "e.mp3" && p.IsPodcast && p.Title == "New Title"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ReturnsDtos_InOrder()
    {
        var pending = Substitute.For<IPendingEditRepository>();
        pending.ListAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new List<PendingMetadataEdit>
            {
                new() { UserId = UserId, File = "b.mp3", Title = "B", CreatedUtc = DateTime.UtcNow },
            });

        var rows = await new ListPendingEditsHandler(pending)
            .Handle(new ListPendingEditsQuery(UserId), CancellationToken.None);

        rows.Should().ContainSingle().Which.File.Should().Be("b.mp3");
    }

    [Fact]
    public async Task Discard_RemovesRow_OrReturnsFalse()
    {
        var pending = Substitute.For<IPendingEditRepository>();
        pending.DeleteAsync(UserId, "s.mp3", Arg.Any<CancellationToken>()).Returns(true);
        pending.DeleteAsync(UserId, "gone.mp3", Arg.Any<CancellationToken>()).Returns(false);

        (await new DiscardPendingEditHandler(pending)
            .Handle(new DiscardPendingEditCommand(UserId, "s.mp3"), CancellationToken.None))
            .Should().BeTrue();
        (await new DiscardPendingEditHandler(pending)
            .Handle(new DiscardPendingEditCommand(UserId, "gone.mp3"), CancellationToken.None))
            .Should().BeFalse();
    }
}

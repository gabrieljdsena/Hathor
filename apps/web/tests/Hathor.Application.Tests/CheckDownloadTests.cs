using FluentAssertions;
using Hathor.Application.Dtos;
using Hathor.Application.Ingest;
using Hathor.Application.Ports;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class CheckDownloadTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static ISongReadModel Songs(string? ownedFile)
    {
        var songs = Substitute.For<ISongReadModel>();
        songs.FindFileByMetadataAsync(UserId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ownedFile);
        return songs;
    }

    [Fact]
    public async Task Owned_ReturnsFile()
    {
        var dto = await new CheckDownloadHandler(Songs("Hit.mp3"))
            .Handle(new CheckDownloadQuery(UserId, "Hit", "Artist"), CancellationToken.None);

        dto.Should().Be(new OwnedCheckDto(true, "Hit.mp3"));
    }

    [Fact]
    public async Task NotOwned_ReturnsFalse()
    {
        var dto = await new CheckDownloadHandler(Songs(null))
            .Handle(new CheckDownloadQuery(UserId, "New Song", "New Artist"), CancellationToken.None);

        dto.Should().Be(new OwnedCheckDto(false, null));
    }

    [Fact]
    public async Task BlankTitle_ShortCircuitsWithoutQuery()
    {
        var songs = Songs("Hit.mp3");

        var dto = await new CheckDownloadHandler(songs)
            .Handle(new CheckDownloadQuery(UserId, "  ", "Artist"), CancellationToken.None);

        dto.Should().Be(new OwnedCheckDto(false, null));
        await songs.DidNotReceive().FindFileByMetadataAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}

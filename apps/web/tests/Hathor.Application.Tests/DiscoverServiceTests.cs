using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class DiscoverServiceTests
{
    // Fresh user per test: DiscoverService caches per user id in-memory,
    // so sharing one id across tests would serve stale cached results.
    private static Guid NewUserId() => Guid.NewGuid();

    private static ITunesHitDto Hit(string title, string artist) =>
        new(title, artist, "Album", "2020", "Rock", "http://art");

    private static (IDiscoverTasteReadModel Taste, IITunesClient ITunes) Fakes(
        Guid userId,
        IReadOnlyList<(string Artist, long Plays)>? taste = null,
        IReadOnlyList<(string Title, string Artist)>? library = null)
    {
        var t = Substitute.For<IDiscoverTasteReadModel>();
        t.GetTopArtistsAsync(userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(taste ?? []);
        t.GetLibraryPairsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(library ?? []);
        t.GetActiveDownloadPairsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        return (t, Substitute.For<IITunesClient>());
    }

    [Fact]
    public async Task GetAsync_ExpandsTopArtists_FilteringOtherArtists()
    {
        var userId = NewUserId();
        var (taste, itunes) = Fakes(userId, [("Pink Floyd", 10)]);
        itunes.SearchTermAsync("Pink Floyd", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                Hit("Comfortably Numb", "Pink Floyd"),
                Hit("Not Their Song", "Someone Else"),
            ]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = await new DiscoverService(taste, itunes)
            .GetAsync(userId, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Comfortably Numb");
        dto.Items[0].Source.Should().Be(DiscoverSources.Artist);
        dto.Cached.Should().BeFalse();
    }

    [Fact]
    public async Task GetAsync_EmptyHistory_FallsBackToChart()
    {
        var userId = NewUserId();
        var (taste, itunes) = Fakes(userId);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["Chart Song Artist"]);
        itunes.SearchSingleAsync("Chart Song Artist", null, Arg.Any<CancellationToken>())
            .Returns(Hit("Chart Song", "Artist"));

        var dto = await new DiscoverService(taste, itunes)
            .GetAsync(userId, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Source.Should().Be(DiscoverSources.Chart);
    }

    [Fact]
    public async Task GetAsync_FiltersOwnedLibrary()
    {
        var userId = NewUserId();
        var (taste, itunes) = Fakes(userId,
            [("Pink Floyd", 10)],
            [("Comfortably Numb", "Pink Floyd")]);
        itunes.SearchTermAsync("Pink Floyd", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Hit("Comfortably Numb", "Pink Floyd")]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = await new DiscoverService(taste, itunes)
            .GetAsync(userId, CancellationToken.None);

        dto.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_SecondCall_ServedFromCache()
    {
        var userId = NewUserId();
        var (taste, itunes) = Fakes(userId, [("M83", 7)]);
        itunes.SearchTermAsync("M83", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Hit("Midnight City", "M83")]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var service = new DiscoverService(taste, itunes);

        var first = await service.GetAsync(userId, CancellationToken.None);
        var second = await service.GetAsync(userId, CancellationToken.None);

        first.Cached.Should().BeFalse();
        second.Cached.Should().BeTrue();
        second.Items.Should().HaveCount(1);
        await itunes.Received(1).SearchTermAsync(
            "M83", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}

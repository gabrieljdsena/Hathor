using System.Text.Json;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class DiscoverServiceTests
{
    private static Guid NewUserId() => Guid.NewGuid();

    private static ITunesHitDto Hit(string title, string artist) =>
        new(title, artist, "Album", "2020", "Rock", "http://art");

    private static DiscoverItemDto Item(string title, string artist, string source) =>
        new(title, artist, "Album", "2020", "Rock", "http://art", source, 0.9);

    private static (IDiscoverTasteReadModel Taste, IITunesClient ITunes, IDiscoverCacheRepository Cache)
        Fakes(
            Guid userId,
            IReadOnlyList<(string Artist, long Plays)>? taste = null,
            IReadOnlyList<(string Title, string Artist)>? library = null,
            string? cachedJson = null)
    {
        var t = Substitute.For<IDiscoverTasteReadModel>();
        t.GetTopArtistsAsync(userId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(taste ?? []);
        t.GetLibraryPairsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(library ?? []);
        t.GetActiveDownloadPairsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
        var c = Substitute.For<IDiscoverCacheRepository>();
        c.GetAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedJson is null ? null : new Hathor.Domain.Entities.DiscoverCache
            {
                UserId = userId,
                Date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                ItemsJson = cachedJson,
                CreatedAtUtc = DateTime.UtcNow,
            });
        return (t, Substitute.For<IITunesClient>(), c);
    }

    private static IDiscoverySuggester NoLlm()
    {
        var s = Substitute.For<IDiscoverySuggester>();
        s.SuggestAsync(Arg.Any<IReadOnlyList<(string Artist, long Plays)>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        return s;
    }

    private static IDiscoverySuggester LlmReturning(params (string Title, string Artist)[] pairs)
    {
        var s = Substitute.For<IDiscoverySuggester>();
        s.SuggestAsync(Arg.Any<IReadOnlyList<(string Artist, long Plays)>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(pairs.ToList());
        return s;
    }

    [Fact]
    public async Task GetAsync_ExpandsTopArtists_FilteringOtherArtists()
    {
        var userId = NewUserId();
        var (taste, itunes, cache) = Fakes(userId, [("Pink Floyd", 10)]);
        itunes.SearchTermAsync("Pink Floyd", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([
                Hit("Comfortably Numb", "Pink Floyd"),
                Hit("Not Their Song", "Someone Else"),
            ]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = await new DiscoverService(taste, itunes, NoLlm(), cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Comfortably Numb");
        dto.Items[0].Source.Should().Be(DiscoverSources.Artist);
        dto.Cached.Should().BeFalse();
        await cache.Received(1).SaveAsync(
            userId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_EmptyHistory_FallsBackToChart()
    {
        var userId = NewUserId();
        var (taste, itunes, cache) = Fakes(userId);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(["Chart Song Artist"]);
        itunes.SearchSingleAsync("Chart Song Artist", null, Arg.Any<CancellationToken>())
            .Returns(Hit("Chart Song", "Artist"));

        var dto = await new DiscoverService(taste, itunes, NoLlm(), cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Source.Should().Be(DiscoverSources.Chart);
    }

    [Fact]
    public async Task GetAsync_FiltersOwnedLibrary()
    {
        var userId = NewUserId();
        var (taste, itunes, cache) = Fakes(userId,
            [("Pink Floyd", 10)],
            [("Comfortably Numb", "Pink Floyd")]);
        itunes.SearchTermAsync("Pink Floyd", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Hit("Comfortably Numb", "Pink Floyd")]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = await new DiscoverService(taste, itunes, NoLlm(), cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_TableCacheHit_ReturnsCachedWithoutITunes()
    {
        var userId = NewUserId();
        var stored = JsonSerializer.Serialize(new List<DiscoverItemDto>
        {
            Item("Midnight City", "M83", DiscoverSources.Artist),
        });
        var (taste, itunes, cache) = Fakes(userId, cachedJson: stored);

        var dto = await new DiscoverService(taste, itunes, NoLlm(), cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Cached.Should().BeTrue();
        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Midnight City");
        await itunes.DidNotReceive().SearchTermAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SaveAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_ForceRegenerate_RecomputesDespiteCache()
    {
        var userId = NewUserId();
        var stored = JsonSerializer.Serialize(new List<DiscoverItemDto>
        {
            Item("Stale Song", "Stale Artist", DiscoverSources.Chart),
        });
        var (taste, itunes, cache) = Fakes(userId, [("M83", 7)], cachedJson: stored);
        itunes.SearchTermAsync("M83", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([Hit("Midnight City", "M83")]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var dto = await new DiscoverService(taste, itunes, NoLlm(), cache)
            .GetAsync(userId, forceRegenerate: true, CancellationToken.None);

        dto.Cached.Should().BeFalse();
        dto.Items.Should().HaveCount(1);
        dto.Items[0].Title.Should().Be("Midnight City");
        await cache.Received(1).SaveAsync(
            userId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_LlmSuggestionVerified_AppearsAsLlm()
    {
        var userId = NewUserId();
        var (taste, itunes, cache) = Fakes(userId, [("M83", 7)]);
        itunes.SearchTermAsync("M83", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        itunes.SearchSingleAsync("Outro", "M83", Arg.Any<CancellationToken>())
            .Returns(Hit("Outro", "M83"));

        var dto = await new DiscoverService(taste, itunes, LlmReturning(("Outro", "M83")), cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().HaveCount(1);
        dto.Items[0].Source.Should().Be(DiscoverSources.Llm);
    }

    [Fact]
    public async Task GetAsync_LlmSuggestionUnverified_IsDropped()
    {
        var userId = NewUserId();
        var (taste, itunes, cache) = Fakes(userId, [("M83", 7)]);
        itunes.SearchTermAsync("M83", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        itunes.GetTrendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        itunes.SearchSingleAsync(
                "Song That Does Not Exist", "Imaginary Band", Arg.Any<CancellationToken>())
            .Returns((ITunesHitDto?)null);

        var dto = await new DiscoverService(
                taste, itunes, LlmReturning(("Song That Does Not Exist", "Imaginary Band")), cache)
            .GetAsync(userId, forceRegenerate: false, CancellationToken.None);

        dto.Items.Should().BeEmpty();
    }
}

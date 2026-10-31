using System.Text.Json;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using Hathor.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class RankingOptionsProviderTests
{
    private static RankingModel Model(DateTime updated) => new()
    {
        UserId = Guid.NewGuid(),
        BaseVersion = DiscoverService.ModelVersion,
        PresetName = "tag-heavy",
        WeightsJson = JsonSerializer.Serialize(
            new DiscoverRankingWeights(0.25, 0.45, 0.10, 0.10, 0.10, 0.10)),
        Samples = 50,
        Snapshots = 10,
        CreatedAtUtc = updated,
        UpdatedAtUtc = updated,
    };

    [Fact]
    public async Task GetAsync_UsesFreshTunedModel_AndChangesCacheVersion()
    {
        var model = Model(DateTime.UtcNow);
        var repo = Substitute.For<IRankingModelRepository>();
        repo.GetAsync(model.UserId, Arg.Any<CancellationToken>()).Returns(model);
        var provider = new RankingOptionsProvider(
            repo, new DiscoveryOptions(), Substitute.For<ILogger<RankingOptionsProvider>>());

        var result = await provider.GetAsync(model.UserId, CancellationToken.None);

        result.PresetName.Should().Be("tag-heavy");
        result.Options.TagWeight.Should().Be(0.45);
        result.CacheVersion.Should().StartWith(DiscoverService.ModelVersion + ":tune-");
    }

    [Fact]
    public async Task GetAsync_StaleModel_FallsBackToDefaults()
    {
        var model = Model(DateTime.UtcNow.AddDays(-365));
        var repo = Substitute.For<IRankingModelRepository>();
        repo.GetAsync(model.UserId, Arg.Any<CancellationToken>()).Returns(model);
        var provider = new RankingOptionsProvider(
            repo, new DiscoveryOptions(), Substitute.For<ILogger<RankingOptionsProvider>>());

        var result = await provider.GetAsync(model.UserId, CancellationToken.None);

        result.PresetName.Should().BeNull();
        result.CacheVersion.Should().Be(DiscoverService.ModelVersion);
        result.Options.TagWeight.Should().Be(new DiscoveryOptions().ToRankingOptions().TagWeight);
    }
}

using FluentAssertions;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Recommendations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class TrackPopularityServiceTests
{
    [Fact]
    public async Task GetAsync_UsesCache_AndFetchesOnlyMissing()
    {
        var repo = Substitute.For<ITrackPopularityRepository>();
        repo.GetManyAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["cached"] = 5 });
        var lb = Substitute.For<IListenBrainzClient>();
        lb.GetRecordingPopularityAsync(Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["new"] = 7 });
        var service = new TrackPopularityService(
            repo, lb, Substitute.For<ILogger<TrackPopularityService>>());

        var result = await service.GetAsync(["cached", "new"], CancellationToken.None);

        result["cached"].Should().Be(5);
        result["new"].Should().Be(7);
        await lb.Received(1).GetRecordingPopularityAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "new" })),
            Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertAsync(
            Arg.Is<IReadOnlyDictionary<string, long>>(d => d["new"] == 7),
            Arg.Any<CancellationToken>());
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

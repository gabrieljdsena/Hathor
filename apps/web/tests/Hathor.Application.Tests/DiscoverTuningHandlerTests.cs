using System.Text.Json;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class DiscoverTuningHandlerTests
{
    private sealed class Impressions(DiscoverImpression snapshot)
        : IDiscoverImpressionRepository
    {
        public Task SaveAsync(Guid userId, string date, string modelVersion,
            string itemsJson, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DiscoverImpression>> GetSinceAsync(
            Guid userId, string sinceDate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoverImpression>>([snapshot]);

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class Models : IRankingModelRepository
    {
        public RankingModel? Stored { get; private set; }

        public Task<RankingModel?> GetAsync(
            Guid userId, CancellationToken ct = default) =>
            Task.FromResult(Stored);

        public Task UpsertAsync(RankingModel model, CancellationToken ct = default)
        {
            Stored = model;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static DiscoverItemFeatures Features(double tag, double popularity) =>
        new(0, tag, 0, popularity, 0.5, 0, 0, 0, 0, 0, 0.5, 0, 0.5, 0.5);

    private static DiscoverImpression Snapshot(Guid userId, string date)
    {
        var items = new List<DiscoverImpressionItem>
        {
            new("Wrong", "Artist A", null, DiscoverSources.Similar, null, null,
                0.5, Features(0, 1)),
            new("Right", "Artist B", null, DiscoverSources.Similar, null, null,
                0.5, Features(1, 0)),
        };
        return new DiscoverImpression
        {
            UserId = userId,
            Date = date,
            ModelVersion = DiscoverService.ModelVersion,
            ItemsJson = JsonSerializer.Serialize(items),
            CreatedAtUtc = DateTime.UtcNow,
        };
    }

    private static DiscoverFeedback Label(
        Guid userId, string title, string artist, string @event, DateTime at)
    {
        var key = MediaKeys.Key(title, artist);
        return new DiscoverFeedback
        {
            UserId = userId,
            ItemKey = key,
            Title = title,
            Artist = artist,
            Source = DiscoverSources.Similar,
            Event = @event,
            CreatedAtUtc = at,
        };
    }

    [Fact]
    public async Task Tune_AppliesAndPersists_ThenStatusReportsModel()
    {
        var userId = Guid.NewGuid();
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var snapshot = Snapshot(userId, date);
        var feedback = Substitute.For<IDiscoverFeedbackRepository>();
        feedback.GetSinceAsync(userId, Arg.Any<DateTime>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([
                Label(userId, "Wrong", "Artist A", DiscoverFeedbackEvents.Dismiss,
                    DateTime.UtcNow),
                Label(userId, "Right", "Artist B", DiscoverFeedbackEvents.Download,
                    DateTime.UtcNow),
            ]);
        var models = new Models();
        var options = new DiscoveryOptions
        {
            TuningEnabled = true,
            TuningMinSnapshots = 1,
            TuningMinLabels = 2,
            TuningMargin = 0.02,
        };

        var dto = await new TuneDiscoverHandler(
                new Impressions(snapshot), feedback, models, options)
            .Handle(new TuneDiscoverCommand(userId), CancellationToken.None);

        dto.Applied.Should().BeTrue();
        dto.Weights.Should().NotBeNull();
        models.Stored.Should().NotBeNull();
        models.Stored!.PresetName.Should().Be(dto.Preset);
        models.Stored.Samples.Should().Be(2);

        var status = await new GetDiscoverTuningHandler(models, options)
            .Handle(new GetDiscoverTuningQuery(userId), CancellationToken.None);
        status.HasTunedModel.Should().BeTrue();
        status.Preset.Should().Be(dto.Preset);
        status.Weights.Should().NotBeNull();
    }
}

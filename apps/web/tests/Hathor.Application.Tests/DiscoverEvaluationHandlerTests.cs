using System.Text.Json;
using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class DiscoverEvaluationHandlerTests
{
    private sealed class Impressions(params DiscoverImpression[] rows)
        : IDiscoverImpressionRepository
    {
        public Task SaveAsync(Guid userId, string date, string modelVersion,
            string itemsJson, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DiscoverImpression>> GetSinceAsync(
            Guid userId, string sinceDate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DiscoverImpression>>(
                rows.Where(r => r.UserId == userId).ToList());

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static DiscoverItemFeatures Features() =>
        new(0.9, 0.5, 0.8, 0.5, 0.7, 0, 0, 0, 0, 0, 0.8, 0, 0.8);

    private static DiscoverImpression Snapshot(
        Guid userId, string date, params (string Title, string Artist)[] items) =>
        new()
        {
            UserId = userId,
            Date = date,
            ModelVersion = DiscoverService.ModelVersion,
            ItemsJson = JsonSerializer.Serialize(items.Select(i =>
                new DiscoverImpressionItem(
                    i.Title, i.Artist, null, DiscoverSources.Similar, "Seed",
                    ["rock"], 0.8, Features())).ToList()),
            CreatedAtUtc = DateTime.UtcNow,
        };

    private static DiscoverFeedback Label(
        Guid userId, string title, string artist, string @event, DateTime at) =>
        new()
        {
            UserId = userId,
            ItemKey = MediaKeys.Key(title, artist),
            Title = title,
            Artist = artist,
            Source = DiscoverSources.Similar,
            Event = @event,
            CreatedAtUtc = at,
        };

    [Fact]
    public async Task Handler_JoinsSnapshotsWithFeedback_AndReportsMetrics()
    {
        var userId = Guid.NewGuid();
        var d1 = DateTime.UtcNow.AddDays(-3);
        var d2 = DateTime.UtcNow.AddDays(-2);
        var d3 = DateTime.UtcNow.AddDays(-1);
        var snapshots = new[]
        {
            Snapshot(userId, d1.ToString("yyyy-MM-dd"),
                ("A1", "Artist A"), ("A2", "Artist B"), ("A3", "Artist C"), ("A4", "Artist D")),
            Snapshot(userId, d2.ToString("yyyy-MM-dd"),
                ("B1", "Artist E"), ("B2", "Artist F"), ("B3", "Artist G"), ("B4", "Artist H")),
            Snapshot(userId, d3.ToString("yyyy-MM-dd"),
                ("C1", "Artist I"), ("C2", "Artist J"), ("C3", "Artist K"), ("C4", "Artist L")),
        };
        var labels = new[]
        {
            Label(userId, "A1", "Artist A", DiscoverFeedbackEvents.Download, d1),
            Label(userId, "A2", "Artist B", DiscoverFeedbackEvents.Dismiss, d1),
            Label(userId, "A3", "Artist C", DiscoverFeedbackEvents.Preview, d1),
            Label(userId, "B1", "Artist E", DiscoverFeedbackEvents.Download, d2),
            Label(userId, "B2", "Artist F", DiscoverFeedbackEvents.Download, d2),
            Label(userId, "B3", "Artist G", DiscoverFeedbackEvents.Dismiss, d2),
            Label(userId, "C1", "Artist I", DiscoverFeedbackEvents.Download, d3),
            Label(userId, "C2", "Artist J", DiscoverFeedbackEvents.Preview, d3),
            Label(userId, "C3", "Artist K", DiscoverFeedbackEvents.Click, d3),
            Label(userId, "C4", "Artist L", DiscoverFeedbackEvents.Dismiss, d3),
        };

        var feedback = Substitute.For<IDiscoverFeedbackRepository>();
        feedback.GetSinceAsync(userId, Arg.Any<DateTime>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(labels);
        var handler = new GetDiscoverEvaluationHandler(
            new Impressions(snapshots), feedback, new DiscoveryOptions());

        var dto = await handler.Handle(
            new GetDiscoverEvaluationQuery(userId, 30, 10), CancellationToken.None);

        dto.Snapshots.Should().Be(3);
        dto.Items.Should().Be(12);
        dto.Labeled.Should().Be(10);
        dto.Downloads.Should().Be(4);
        dto.Dismissals.Should().Be(3);
        dto.Previews.Should().Be(2);
        dto.Clicks.Should().Be(1);
        dto.InsufficientData.Should().BeFalse();
        dto.PrecisionAtK.Should().BeGreaterThan(0);
        dto.NdcgAtK.Should().BeGreaterThan(0);
        dto.Presets.Should().HaveCount(DiscoverEvaluator.Presets.Count + 1);
        dto.Sources.Should().Contain(s => s.Source == DiscoverSources.Similar);
    }
}

using FluentAssertions;
using Hathor.Domain;

namespace Hathor.Application.Tests;

// Wire instants must always be true UTC with an explicit Z: Unspecified
// Kind (Dapper reads, desktop-imported rows) is assumed UTC, Local is
// converted. A raw ToString + "Z" would mislabel local wall times.
public sealed class UtcIsoTests
{
    [Fact]
    public void Format_UnspecifiedKind_AssumedUtc()
    {
        UtcIso.Format(new DateTime(2026, 10, 11, 14, 30, 0, DateTimeKind.Unspecified))
            .Should().Be("2026-10-11T14:30:00Z");
    }

    [Fact]
    public void Format_UtcKind_Unchanged()
    {
        UtcIso.Format(new DateTime(2026, 10, 11, 14, 30, 0, DateTimeKind.Utc))
            .Should().Be("2026-10-11T14:30:00Z");
    }

    [Fact]
    public void Format_LocalKind_ConvertedToUtc()
    {
        var local = new DateTime(2026, 10, 11, 14, 30, 0, DateTimeKind.Local);
        UtcIso.Format(local)
            .Should().Be(local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss") + "Z");
    }

    [Fact]
    public void EnsureUtc_Unspecified_MarksUtcWithoutShifting()
    {
        var dt = new DateTime(2026, 10, 11, 14, 30, 0, DateTimeKind.Unspecified);
        var ensured = UtcIso.EnsureUtc(dt);
        ensured.Kind.Should().Be(DateTimeKind.Utc);
        ensured.Should().Be(new DateTime(2026, 10, 11, 14, 30, 0, DateTimeKind.Utc));
    }
}

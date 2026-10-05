using FluentAssertions;
using Hathor.Application.Dtos;
using Hathor.Application.Library;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using NSubstitute;

namespace Hathor.Application.Tests;

public sealed class BackfillLoudnessTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static (ISongReadModel Songs, ILoudnessAnalyzer Analyzer, ILibraryStorage Storage, ISongRecordRepository Records)
        Fakes(IReadOnlyList<string> missing, double? lufs)
    {
        var songs = Substitute.For<ISongReadModel>();
        // First call lists the batch, second (post-save remaining check) is empty.
        songs.GetFilesMissingLoudnessAsync(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(missing, new List<string>());
        var analyzer = Substitute.For<ILoudnessAnalyzer>();
        analyzer.AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(lufs);
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongPath(UserId, Arg.Any<string>())
            .Returns(ci => "/music/" + ci.Arg<string>());
        return (songs, analyzer, storage, Substitute.For<ISongRecordRepository>());
    }

    [Fact]
    public async Task MeasuresMissingFiles_AndReportsRemaining()
    {
        var (songs, analyzer, storage, records) = Fakes(["a.mp3", "b.mp3"], -11.5);

        var dto = await new BackfillLoudnessHandler(songs, analyzer, storage, records)
            .Handle(new BackfillLoudnessCommand(UserId, 20), CancellationToken.None);

        dto.Should().Be(new LoudnessBackfillDto(2, 0));
        await records.Received(1).SetLoudnessAsync(UserId, "a.mp3", -11.5, Arg.Any<CancellationToken>());
        await records.Received(1).SetLoudnessAsync(UserId, "b.mp3", -11.5, Arg.Any<CancellationToken>());
        await records.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SkipsUnmeasurableFiles_WithoutSaving()
    {
        var (songs, analyzer, storage, records) = Fakes(["a.mp3"], null);

        var dto = await new BackfillLoudnessHandler(songs, analyzer, storage, records)
            .Handle(new BackfillLoudnessCommand(UserId, 20), CancellationToken.None);

        dto.Should().Be(new LoudnessBackfillDto(0, 0));
        await records.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingMissing_ReturnsZero()
    {
        var (songs, analyzer, storage, records) = Fakes([], -11.5);

        var dto = await new BackfillLoudnessHandler(songs, analyzer, storage, records)
            .Handle(new BackfillLoudnessCommand(UserId, 20), CancellationToken.None);

        dto.Should().Be(new LoudnessBackfillDto(0, 0));
        await analyzer.DidNotReceive().AnalyzeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

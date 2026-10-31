using FluentAssertions;
using Hathor.Application.Player;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Application.Tests;

public sealed class PlaybackEventTests : IAsyncLifetime
{
    private HathorDbContext _db = null!;
    private string _database = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) =
            await TestPostgres.CreateDatabaseAsync("hathor_playbackevents");
        _database = database;
        _db = new HathorDbContext(TestPostgres.Options(connectionString));
        Hathor.Infrastructure.Auth.InfrastructureServiceExtensions.EnsureDatabaseCreated(_db);
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        await TestPostgres.DropDatabaseAsync(_database);
    }

    private sealed class CapturingRepo : IPlaybackEventRepository
    {
        public PlaybackEvent? Stored { get; private set; }

        public Task AddAsync(PlaybackEvent playbackEvent, CancellationToken ct = default)
        {
            Stored = playbackEvent;
            return Task.CompletedTask;
        }

        public Task<PlaybackSummary> GetSummaryAsync(
            Guid userId, DateTime sinceUtc, CancellationToken ct = default) =>
            Task.FromResult(new PlaybackSummary(0, 0, 0));

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Handler_ClampsPosition_AndNormalizesCompleted()
    {
        var repo = new CapturingRepo();
        var handler = new RecordPlaybackEventHandler(repo);
        var future = DateTime.UtcNow.AddHours(1);

        await handler.Handle(new RecordPlaybackEventCommand(
            _userId, " seg-1 ", " song.mp3 ", 500, 200, Completed: true,
            Skipped: true, StartedAtUtc: future), CancellationToken.None);

        repo.Stored.Should().NotBeNull();
        repo.Stored!.SegmentId.Should().Be("seg-1");
        repo.Stored.SongFile.Should().Be("song.mp3");
        repo.Stored.PositionSec.Should().Be(200);
        repo.Stored.DurationSec.Should().Be(200);
        repo.Stored.Completed.Should().BeTrue();
        repo.Stored.Skipped.Should().BeFalse();
        repo.Stored.StartedAtUtc.Should().BeBefore(DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Validator_RejectsPaths_AndBothFlags()
    {
        var validator = new RecordPlaybackEventValidator();

        validator.Validate(new RecordPlaybackEventCommand(
            _userId, "seg", "../song.mp3", 1, 2, false, true, null))
            .IsValid.Should().BeFalse();

        validator.Validate(new RecordPlaybackEventCommand(
            _userId, "seg", "song.mp3", 1, 2, true, true, null))
            .IsValid.Should().BeFalse();

        validator.Validate(new RecordPlaybackEventCommand(
            _userId, "seg", "song.mp3", 1, 2, false, true, null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Repository_DedupesSegmentId()
    {
        var repo = new EfPlaybackEventRepository(_db);
        var evt = new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "same-segment",
            SongFile = "song.mp3",
            PositionSec = 10,
            DurationSec = 200,
            Completed = false,
            Skipped = true,
            StartedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        };

        await repo.AddAsync(evt);
        await repo.SaveChangesAsync();
        await repo.AddAsync(new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "same-segment",
            SongFile = "song.mp3",
            PositionSec = 20,
            DurationSec = 200,
            Completed = false,
            Skipped = true,
            StartedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await repo.SaveChangesAsync();

        (await _db.PlaybackEvents.CountAsync()).Should().Be(1);
    }
}

using FluentAssertions;
using Hathor.Application.Discover;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Dapper;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Library;
using Hathor.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Hathor.Application.Tests;

// Real Postgres coverage for the recency-weighted Discover read model:
// Dapper constructor mapping + the decay SQL must stay in sync with TasteProfile.
public sealed class DiscoverQueriesTests : IAsyncLifetime
{
    private HathorDbContext _db = null!;
    private string _database = "";
    private string _connectionString = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) =
            await TestPostgres.CreateDatabaseAsync("hathor_discoverqueries");
        _connectionString = connectionString;
        _database = database;
        _db = new HathorDbContext(TestPostgres.Options(connectionString));
        Hathor.Infrastructure.Auth.InfrastructureServiceExtensions.EnsureDatabaseCreated(_db);
    }

    public async Task DisposeAsync()
    {
        _db.Dispose();
        await TestPostgres.DropDatabaseAsync(_database);
    }

    private DapperConnectionFactory Factory()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["Database:ConnectionString"] = _connectionString,
            })
            .Build();
        return new DapperConnectionFactory(config);
    }

    private DapperDiscoverTasteReadModel ReadModel() =>
        new(Factory(), new DiscoveryOptions { ArtistHalfLifeDays = 60 });

    private DapperAiDjReadModel AiDjModel() => new(Factory());

    private EfDailyMixRepository DailyMix() => new(_db, Factory());

    private async Task SeedAsync(params (string File, string Title, string Artist,
        string Genre, DateTime PlayedAt)[] rows)
    {
        foreach (var file in rows.GroupBy(r => r.File))
        {
            var first = file.First();
            _db.Songs.Add(new Song
            {
                UserId = _userId,
                File = first.File,
                Title = first.Title,
                Artist = first.Artist,
                Genre = first.Genre,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-200),
            });
        }
        foreach (var row in rows)
        {
            _db.MusicHistory.Add(new MusicHistoryEntry
            {
                UserId = _userId,
                SongFile = row.File,
                DatePlayedUtc = row.PlayedAt,
            });
        }
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task TopArtists_UsesRecencyDecay_AndMapsTasteArtist()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("recent.mp3", "Recent Song", "Recent Artist", "Electronic", now),
            ("recent.mp3", "Recent Song", "Recent Artist", "Electronic", now),
            ("old.mp3", "Old Song", "Old Artist", "Rock", now.AddDays(-120)),
            ("old.mp3", "Old Song", "Old Artist", "Rock", now.AddDays(-120)));

        var artists = await ReadModel().GetTopArtistsAsync(_userId, 10);

        artists.Should().HaveCount(2);
        artists[0].Artist.Should().Be("Recent Artist");
        artists[0].Plays.Should().Be(2);
        artists[0].Weight.Should().BeGreaterThan(artists[1].Weight);
        artists.Should().OnlyContain(a => a.Weight > 0);
    }

    [Fact]
    public async Task TopArtists_InfersCompletionFromHistoryGaps()
    {
        var start = DateTime.UtcNow.AddHours(-3);
        _db.Songs.AddRange(
            new Song
            {
                UserId = _userId,
                File = "done.mp3",
                Title = "Done",
                Artist = "Done Artist",
                Genre = "Rock",
                DurationSecs = 200,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-1),
            },
            new Song
            {
                UserId = _userId,
                File = "skip.mp3",
                Title = "Skip",
                Artist = "Skip Artist",
                Genre = "Rock",
                DurationSecs = 200,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-1),
            },
            new Song
            {
                UserId = _userId,
                File = "after.mp3",
                Title = "After",
                Artist = "After Artist",
                Genre = "Rock",
                DurationSecs = 200,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-1),
            });
        _db.MusicHistory.AddRange(
            new MusicHistoryEntry
            {
                UserId = _userId, SongFile = "done.mp3", DatePlayedUtc = start,
            },
            new MusicHistoryEntry
            {
                UserId = _userId, SongFile = "skip.mp3",
                DatePlayedUtc = start.AddSeconds(200),
            },
            new MusicHistoryEntry
            {
                UserId = _userId, SongFile = "after.mp3",
                DatePlayedUtc = start.AddSeconds(210),
            });
        await _db.SaveChangesAsync();

        var artists = await ReadModel().GetTopArtistsAsync(_userId, 10);
        var done = artists.Single(a => a.Artist == "Done Artist");
        var skip = artists.Single(a => a.Artist == "Skip Artist");

        done.Weight.Should().BeGreaterThan(skip.Weight);
    }

    [Fact]
    public async Task TopArtists_ExplicitTelemetry_OverridesInference()
    {
        var start = DateTime.UtcNow.AddHours(-3);
        _db.Songs.AddRange(
            new Song
            {
                UserId = _userId, File = "inferred.mp3", Title = "Inferred",
                Artist = "Inferred Artist", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-1),
            },
            new Song
            {
                UserId = _userId, File = "explicit.mp3", Title = "Explicit",
                Artist = "Explicit Artist", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-1),
            },
            new Song
            {
                UserId = _userId, File = "next.mp3", Title = "Next",
                Artist = "Next Artist", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = DateTime.UtcNow.AddDays(-1),
            });
        _db.MusicHistory.AddRange(
            new MusicHistoryEntry
            {
                UserId = _userId, SongFile = "inferred.mp3", DatePlayedUtc = start,
            },
            new MusicHistoryEntry
            {
                UserId = _userId, SongFile = "explicit.mp3",
                DatePlayedUtc = start.AddSeconds(200),
            },
            new MusicHistoryEntry
            {
                UserId = _userId, SongFile = "next.mp3",
                DatePlayedUtc = start.AddSeconds(400),
            });
        _db.PlaybackEvents.Add(new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "explicit-skip",
            SongFile = "explicit.mp3",
            PositionSec = 10,
            DurationSec = 200,
            Skipped = true,
            StartedAtUtc = start.AddSeconds(200),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var artists = await ReadModel().GetTopArtistsAsync(_userId, 10);
        var inferred = artists.Single(a => a.Artist == "Inferred Artist");
        var explicitSkip = artists.Single(a => a.Artist == "Explicit Artist");

        inferred.Weight.Should().BeGreaterThan(explicitSkip.Weight);
    }

    [Fact]
    public async Task TopTags_FallsBackToLibraryGenres_WithoutHistory()
    {
        _db.Songs.AddRange(
            new Song
            {
                UserId = _userId,
                File = "a.mp3",
                Title = "A",
                Artist = "Artist",
                Genre = "Shoegaze",
                DateDownloadUtc = DateTime.UtcNow,
            },
            new Song
            {
                UserId = _userId,
                File = "b.mp3",
                Title = "B",
                Artist = "Artist",
                Genre = "Shoegaze",
                DateDownloadUtc = DateTime.UtcNow,
            },
            new Song
            {
                UserId = _userId,
                File = "c.mp3",
                Title = "C",
                Artist = "Artist",
                Genre = "Jazz",
                DateDownloadUtc = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        var tags = await ReadModel().GetTopTagsAsync(_userId, 10);

        tags.Should().Equal("Shoegaze", "Jazz");
    }

    [Fact]
    public async Task TopArtists_WeightsCompletedPlaybackHigher()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("done.mp3", "Done Song", "Done Artist", "Rock", now.AddHours(-1)),
            ("skip.mp3", "Skip Song", "Skip Artist", "Rock", now.AddHours(-1)));
        _db.PlaybackEvents.AddRange(
            new PlaybackEvent
            {
                UserId = _userId,
                SegmentId = "done-1",
                SongFile = "done.mp3",
                PositionSec = 200,
                DurationSec = 200,
                Completed = true,
                StartedAtUtc = now.AddHours(-1),
                CreatedAtUtc = now,
            },
            new PlaybackEvent
            {
                UserId = _userId,
                SegmentId = "skip-1",
                SongFile = "skip.mp3",
                PositionSec = 10,
                DurationSec = 200,
                Skipped = true,
                StartedAtUtc = now.AddHours(-1),
                CreatedAtUtc = now,
            });
        await _db.SaveChangesAsync();

        var artists = await ReadModel().GetTopArtistsAsync(_userId, 10);

        artists[0].Artist.Should().Be("Done Artist");
        artists[0].Weight.Should().BeGreaterThan(artists[1].Weight);
    }

    [Fact]
    public async Task TopArtists_UsesEventOnlyPlays()
    {
        var now = DateTime.UtcNow;
        _db.Songs.Add(new Song
        {
            UserId = _userId,
            File = "event-only.mp3",
            Title = "Event Only",
            Artist = "Event Artist",
            Genre = "Electronic",
            DurationSecs = 200,
            DateDownloadUtc = now.AddDays(-1),
        });
        _db.PlaybackEvents.Add(new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "event-only-1",
            SongFile = "event-only.mp3",
            PositionSec = 200,
            DurationSec = 200,
            Completed = true,
            StartedAtUtc = now.AddHours(-1),
            CreatedAtUtc = now,
        });
        await _db.SaveChangesAsync();

        var artists = await ReadModel().GetTopArtistsAsync(_userId, 10);

        var artist = artists.Single(a => a.Artist == "Event Artist");
        artist.Plays.Should().Be(1);
        artist.Weight.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TopTracksTagsAndFiles_UseEventOnlyPlays()
    {
        var now = DateTime.UtcNow;
        _db.Songs.Add(new Song
        {
            UserId = _userId,
            File = "event-signals.mp3",
            Title = "Event Signals",
            Artist = "Event Artist",
            Genre = "Shoegaze",
            DurationSecs = 200,
            DateDownloadUtc = now.AddDays(-1),
        });
        _db.PlaybackEvents.Add(new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "event-signals-1",
            SongFile = "event-signals.mp3",
            PositionSec = 200,
            DurationSec = 200,
            Completed = true,
            StartedAtUtc = now.AddHours(-1),
            CreatedAtUtc = now,
        });
        await _db.SaveChangesAsync();

        var model = ReadModel();
        var tracks = await model.GetTopTracksAsync(_userId, 10);
        var tags = await model.GetTopTagsAsync(_userId, 10);
        var files = await model.GetTopPlayedFilesAsync(_userId, 10);

        tracks.Should().ContainSingle(t => t.Title == "Event Signals");
        tracks[0].Plays.Should().Be(1);
        tags.Should().Contain("Shoegaze");
        files.Should().ContainSingle(f => f.File == "event-signals.mp3");
    }

    [Fact]
    public async Task TopArtists_DoesNotDoubleCountMatchingHistoryAndEvent()
    {
        var at = DateTime.UtcNow.AddHours(-2);
        _db.Songs.Add(new Song
        {
            UserId = _userId,
            File = "matched.mp3",
            Title = "Matched",
            Artist = "Matched Artist",
            Genre = "Rock",
            DurationSecs = 200,
            DateDownloadUtc = at.AddDays(-1),
        });
        _db.MusicHistory.Add(new MusicHistoryEntry
        {
            UserId = _userId,
            SongFile = "matched.mp3",
            DatePlayedUtc = at,
        });
        _db.PlaybackEvents.Add(new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "matched-1",
            SongFile = "matched.mp3",
            PositionSec = 200,
            DurationSec = 200,
            Completed = true,
            StartedAtUtc = at.AddSeconds(1),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var artist = (await ReadModel().GetTopArtistsAsync(_userId, 10))
            .Single(a => a.Artist == "Matched Artist");

        artist.Plays.Should().Be(1);
    }

    [Fact]
    public async Task TopArtists_KeepsOlderHistoryBesideRecentEvent()
    {
        var now = DateTime.UtcNow;
        _db.Songs.Add(new Song
        {
            UserId = _userId,
            File = "mixed.mp3",
            Title = "Mixed",
            Artist = "Mixed Artist",
            Genre = "Rock",
            DurationSecs = 200,
            DateDownloadUtc = now.AddDays(-30),
        });
        _db.MusicHistory.Add(new MusicHistoryEntry
        {
            UserId = _userId,
            SongFile = "mixed.mp3",
            DatePlayedUtc = now.AddDays(-20),
        });
        _db.PlaybackEvents.Add(new PlaybackEvent
        {
            UserId = _userId,
            SegmentId = "mixed-1",
            SongFile = "mixed.mp3",
            PositionSec = 200,
            DurationSec = 200,
            Completed = true,
            StartedAtUtc = now.AddHours(-1),
            CreatedAtUtc = now,
        });
        await _db.SaveChangesAsync();

        var artist = (await ReadModel().GetTopArtistsAsync(_userId, 10))
            .Single(a => a.Artist == "Mixed Artist");

        artist.Plays.Should().Be(2);
    }

    [Fact]
    public async Task AiDjCandidates_UseEventOnlyPlays()
    {
        var now = DateTime.UtcNow;
        _db.Songs.AddRange(
            new Song
            {
                UserId = _userId, File = "dj-a.mp3", Title = "DJ A",
                Artist = "DJ Artist A", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = now.AddDays(-1),
            },
            new Song
            {
                UserId = _userId, File = "dj-b.mp3", Title = "DJ B",
                Artist = "DJ Artist B", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = now.AddDays(-1),
            });
        _db.PlaybackEvents.AddRange(
            new PlaybackEvent
            {
                UserId = _userId, SegmentId = "dj-a-1", SongFile = "dj-a.mp3",
                PositionSec = 200, DurationSec = 200, Completed = true,
                StartedAtUtc = now.AddHours(-3), CreatedAtUtc = now,
            },
            new PlaybackEvent
            {
                UserId = _userId, SegmentId = "dj-a-2", SongFile = "dj-a.mp3",
                PositionSec = 200, DurationSec = 200, Completed = true,
                StartedAtUtc = now.AddHours(-2), CreatedAtUtc = now,
            },
            new PlaybackEvent
            {
                UserId = _userId, SegmentId = "dj-b-1", SongFile = "dj-b.mp3",
                PositionSec = 10, DurationSec = 200, Skipped = true,
                StartedAtUtc = now.AddHours(-1), CreatedAtUtc = now,
            });
        await _db.SaveChangesAsync();

        var rows = await AiDjModel().GetCandidatesAsync(_userId, 10);
        var preferred = rows.Single(r => r.File == "dj-a.mp3");
        var skipped = rows.Single(r => r.File == "dj-b.mp3");

        preferred.PlayCount.Should().Be(2);
        preferred.Events.Should().Be(2);
        preferred.Completions.Should().Be(2);
        preferred.LastPlayedUtc.Should().NotBeNull();
        skipped.PlayCount.Should().Be(1);
        skipped.Completions.Should().Be(0);
    }

    [Fact]
    public async Task DailyMix_RanksEventOnlyPlays()
    {
        var now = DateTime.UtcNow;
        _db.Songs.AddRange(
            new Song
            {
                UserId = _userId, File = "mix-a.mp3", Title = "Mix A",
                Artist = "Mix Artist", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = now.AddDays(-1),
            },
            new Song
            {
                UserId = _userId, File = "mix-b.mp3", Title = "Mix B",
                Artist = "Mix Artist", Genre = "Rock", DurationSecs = 200,
                DateDownloadUtc = now.AddDays(-1),
            });
        _db.PlaybackEvents.AddRange(
            new PlaybackEvent
            {
                UserId = _userId, SegmentId = "mix-a-1", SongFile = "mix-a.mp3",
                PositionSec = 200, DurationSec = 200, Completed = true,
                StartedAtUtc = now.AddHours(-3), CreatedAtUtc = now,
            },
            new PlaybackEvent
            {
                UserId = _userId, SegmentId = "mix-a-2", SongFile = "mix-a.mp3",
                PositionSec = 200, DurationSec = 200, Completed = true,
                StartedAtUtc = now.AddHours(-2), CreatedAtUtc = now,
            },
            new PlaybackEvent
            {
                UserId = _userId, SegmentId = "mix-b-1", SongFile = "mix-b.mp3",
                PositionSec = 200, DurationSec = 200, Completed = true,
                StartedAtUtc = now.AddHours(-1), CreatedAtUtc = now,
            });
        await _db.SaveChangesAsync();

        var ranked = await DailyMix().GetRankedFilesAsync(_userId);

        ranked.Should().StartWith("mix-a.mp3");
    }

    [Fact]
    public async Task DailyMix_KeepsHistoryOnlyPlays()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("history-a.mp3", "History A", "History Artist", "Rock", now.AddDays(-2)),
            ("history-a.mp3", "History A", "History Artist", "Rock", now.AddHours(-1)),
            ("history-b.mp3", "History B", "History Artist", "Rock", now.AddDays(-3)));

        var ranked = await DailyMix().GetRankedFilesAsync(_userId);

        ranked.Should().StartWith("history-a.mp3");
    }

    [Fact]
    public async Task RecommendationCache_RoundTripsAndExpires()
    {
        var cache = new Hathor.Infrastructure.Recommendations.EfRecommendationCache(_db);

        await cache.SetAsync("k", new List<string> { "a", "b" }, TimeSpan.FromDays(1));
        var first = await cache.GetAsync<List<string>>("k");
        first.Should().BeEquivalentTo(["a", "b"]);

        await cache.SetAsync("k", new List<string> { "c" }, TimeSpan.FromDays(1));
        var second = await cache.GetAsync<List<string>>("k");
        second.Should().BeEquivalentTo(["c"]);

        await cache.SetAsync("expired", new List<string> { "x" },
            TimeSpan.FromMilliseconds(1));
        await Task.Delay(20);
        (await cache.GetAsync<List<string>>("expired")).Should().BeNull();
    }

    [Fact]
    public async Task TopTracks_AndTags_MapLocalSignals()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(
            ("a.mp3", "Midnight City", "M83", "Electronic", now),
            ("a.mp3", "Midnight City", "M83", "Electronic", now),
            ("b.mp3", "Alison", "Slowdive", "Shoegaze", now.AddDays(-1)));

        var model = ReadModel();
        var tracks = await model.GetTopTracksAsync(_userId, 10);
        var tags = await model.GetTopTagsAsync(_userId, 10);
        var files = await model.GetTopPlayedFilesAsync(_userId, 10);

        files.Should().HaveCount(2);
        files[0].File.Should().Be("a.mp3");
        files[0].Weight.Should().BeGreaterThan(files[1].Weight);

        tracks.Should().HaveCount(2);
        tracks[0].Title.Should().Be("Midnight City");
        tracks[0].Artist.Should().Be("M83");
        tracks[0].Plays.Should().Be(2);
        tags.Should().BeEquivalentTo(["Electronic", "Shoegaze"]);
    }
}

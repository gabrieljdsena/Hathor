using FluentAssertions;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Library;
using Hathor.Infrastructure.Repositories;
using Hathor.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Hathor.Application.Tests;

// DapperSongReadModel over materialized columns: lists/search/counts must
// not open MP3 files (storage throws if touched), except bounded backfill.
public sealed class SongReadModelTests : IAsyncLifetime
{
    private HathorDbContext _db = null!;
    private string _database = "";
    private string _connectionString = "";
    private readonly Guid _userId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var (connectionString, database) = await TestPostgres.CreateDatabaseAsync("hathor_readmodel");
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

    private sealed class StubConfig(Dictionary<string, string?> values) : IConfiguration
    {
        public string? this[string key]
        {
            get => values.TryGetValue(key, out var v) ? v : null;
            set => values[key] = value;
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(System.Threading.CancellationToken.None);
        public IConfigurationSection GetSection(string key) => new StubSection(this, key);

        private sealed class StubSection(StubConfig root, string key) : IConfigurationSection
        {
            public string? this[string k]
            {
                get => root[$"{key}:{k}"];
                set => root[$"{key}:{k}"] = value;
            }

            public string Key => key;
            public string Path => key;
            public string? Value { get => root[key]; set => root[key] = value; }
            public IEnumerable<IConfigurationSection> GetChildren() => [];
            public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() =>
                new Microsoft.Extensions.Primitives.CancellationChangeToken(System.Threading.CancellationToken.None);
            public IConfigurationSection GetSection(string k) => new StubSection(root, $"{key}:{k}");
        }
    }

    private DapperSongReadModel ReadModel(
        ILibraryStorage storage, SongMetadataReader metadata, EfSongRecordRepository? records = null) =>
        new(
            new Hathor.Infrastructure.Dapper.DapperConnectionFactory(new StubConfig(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "postgres",
                    ["Database:ConnectionString"] = _connectionString,
                })),
            storage,
            metadata,
            records ?? new EfSongRecordRepository(_db));

    private static ILibraryStorage Storage(params string[] files)
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.ListSongFiles(Arg.Any<Guid>()).Returns(files.ToList());
        storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(call => files.Contains(call.ArgAt<string>(1), StringComparer.OrdinalIgnoreCase));
        storage.SongPath(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(call => throw new InvalidOperationException("disk touched"));
        return storage;
    }

    private SongMetadataReader Metadata(ILibraryStorage storage) =>
        new(storage, new SongMetadataCache());

    private async Task Seed(params Song[] rows)
    {
        _db.Songs.AddRange(rows);
        await _db.SaveChangesAsync();
    }

    private Song Row(string file, string title, string artist = "Artist", string album = "Album",
        string year = "2020", string genre = "Rock", double duration = 180) => new()
        {
            UserId = _userId,
            File = file,
            Title = title,
            Artist = artist,
            Album = album,
            Year = year,
            Genre = genre,
            DurationSecs = duration,
            DateDownloadUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public async Task ListAsync_ServesRows_WithoutOpeningFiles()
    {
        await Seed(Row("b.mp3", "Beta"), Row("a.mp3", "Alpha"));
        var storage = Storage("b.mp3", "a.mp3");
        var model = ReadModel(storage, Metadata(storage));

        var songs = await model.ListAsync(_userId, null, "Title", "asc", 1, 50);

        songs.Select(s => s.Title).Should().Equal("Alpha", "Beta");
        songs.Should().OnlyContain(s => s.DateDownload == "2026-01-01T00:00:00Z");
    }

    [Fact]
    public async Task ListAsync_ToleratesTypos_AndSkipsGhostRows()
    {
        await Seed(Row("bohemian.mp3", "Bohemian Rhapsody"), Row("gone.mp3", "Gone"));
        var storage = Storage("bohemian.mp3"); // gone.mp3 deleted on disk
        var model = ReadModel(storage, Metadata(storage));

        var songs = await model.ListAsync(_userId, "Bohemain Rapsody", "Title", "asc", 1, 50);

        songs.Select(s => s.File).Should().Equal("bohemian.mp3");
        (await model.CountAsync(_userId, "Bohemain Rapsody")).Should().Be(1);
        (await model.CountAsync(_userId, null)).Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_SortsByDateDownload_Desc()
    {
        await Seed(
            Row("old.mp3", "Old"),
            Row("new.mp3", "New"));
        _db.Songs.First(s => s.File == "new.mp3").DateDownloadUtc =
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();
        var storage = Storage("old.mp3", "new.mp3");
        var model = ReadModel(storage, Metadata(storage));

        var songs = await model.ListAsync(_userId, null, "DateDownload", "desc", 1, 50);

        songs.Select(s => s.File).Should().Equal("new.mp3", "old.mp3");
    }

    [Fact]
    public async Task Artists_And_SongsByArtist_ComeFromColumns()
    {
        await Seed(Row("q1.mp3", "T1", artist: "Queen"), Row("q2.mp3", "T2", artist: "Queen"));
        var storage = Storage("q1.mp3", "q2.mp3");
        var model = ReadModel(storage, Metadata(storage));

        (await model.GetArtistsAsync(_userId)).Should().Equal("Queen");
        var songs = await model.GetSongsByArtistAsync(_userId, "Queen");
        songs.Select(s => s.Title).Should().BeEquivalentTo("T1", "T2");
        (await model.GetSongsByArtistAsync(_userId, "queen")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetMany_PreservesOrder_WithoutOpeningFiles()
    {
        await Seed(Row("b.mp3", "Beta"), Row("a.mp3", "Alpha"));
        var storage = Storage("b.mp3", "a.mp3");
        var model = ReadModel(storage, Metadata(storage));

        var songs = await model.GetManyAsync(_userId, ["b.mp3", "missing.mp3", "a.mp3"], false);

        songs.Select(s => s.Title).Should().Equal("Beta", "Alpha");
    }

    [Fact]
    public async Task LegacyRow_BackfillsFromFileOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), "hathor-backfill-" + Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
            new Random(42).NextBytes(frame.AsSpan(4));
            await File.WriteAllBytesAsync(path, frame);

            await Seed(new Song
            {
                UserId = _userId, File = "legacy.mp3", Title = "Legacy",
                DateDownloadUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var storage = Substitute.For<ILibraryStorage>();
            storage.ListSongFiles(Arg.Any<Guid>()).Returns(new List<string> { "legacy.mp3" });
            storage.SongExists(Arg.Any<Guid>(), Arg.Any<string>()).Returns(true);
            storage.SongPath(Arg.Any<Guid>(), Arg.Any<string>()).Returns(path);
            var model = ReadModel(storage, Metadata(storage));

            var songs = await model.ListAsync(_userId, null, "Title", "asc", 1, 50);

            // Tagless file: served with fallbacks, row healed to non-null.
            songs.Should().ContainSingle();
            (await new EfSongRecordRepository(_db).GetAsync(_userId, "legacy.mp3"))!
                .Album.Should().NotBeNull();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}

using System.Collections.Concurrent;
using Dapper;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Services;

namespace Hathor.Infrastructure.Library;

// Dapper read-model: merges files on disk (TagLibSharp metadata, like
// MetadataManager.get_song_metadata) with Songs table rows (date_download).
// Mirrors get_all_songs / get_songs_by_artist / history date_lookup behavior.
public sealed class DapperSongReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory,
    ILibraryStorage storage,
    SongMetadataReader metadata) : ISongReadModel
{
    public async Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, string? search,
        string? sort, string? dir, int page, int pageSize, CancellationToken ct = default)
    {
        var dates = await DateLookupAsync(userId, ct);
        var files = storage.ListSongFiles(userId);

        var songs = new List<SongDto>();
        foreach (var file in files)
        {
            var dto = metadata.Read(userId, file, includeCover: false);
            dto = dto with { DateDownload = dates.TryGetValue(file, out var d) ? d : null };
            if (!MatchesSearch(dto, search)) continue;
            songs.Add(dto);
        }

        return Sort(songs, sort ?? "Title", dir ?? "asc")
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    public Task<int> CountAsync(Guid userId, string? search, CancellationToken ct = default)
    {
        // Library count = files on disk (desktop get_library_count counts .mp3 files).
        var files = storage.ListSongFiles(userId);
        if (string.IsNullOrWhiteSpace(search)) return Task.FromResult(files.Count);
        var count = files.Count(f =>
        {
            if (FuzzySearch.IsMatch(f, search)) return true;
            var dto = metadata.Read(userId, f, includeCover: false);
            return FuzzySearch.IsMatch([dto.Title, dto.Artist, dto.Album], search);
        });
        return Task.FromResult(count);
    }

    public async Task<SongDto?> GetByFileAsync(Guid userId, string file,
        bool includeCover, CancellationToken ct = default)
    {
        // Queue DTOs may reference either library (mixed song+episode queues).
        if (storage.SongExists(userId, file))
        {
            var dto = metadata.Read(userId, file, includeCover);
            var dates = await DateLookupAsync(userId, ct);
            return dto with { DateDownload = dates.TryGetValue(file, out var d) ? d : null };
        }
        if (storage.PodcastExists(userId, file))
        {
            var dto = metadata.ReadPodcastFile(userId, file, includeCover);
            var dates = await PodcastDateLookupAsync(userId, ct);
            return dto with
            {
                IsPodcast = true,
                DateDownload = dates.TryGetValue(file, out var d) ? d : null,
            };
        }
        return null;
    }

    public SongDto ReadLocalSong(Guid userId, string file) =>
        metadata.Read(userId, file, includeCover: false);

    // Batch resolve preserving input order, skipping missing files
    // (single date lookup instead of one query per file).
    public async Task<IReadOnlyList<SongDto>> GetManyAsync(Guid userId, IEnumerable<string> files,
        bool includeCover, CancellationToken ct = default)
    {
        var dates = await DateLookupAsync(userId, ct);
        var podcastDates = await PodcastDateLookupAsync(userId, ct);
        var result = new List<SongDto>();
        foreach (var file in files)
        {
            if (storage.SongExists(userId, file))
            {
                var dto = metadata.Read(userId, file, includeCover);
                result.Add(dto with { DateDownload = dates.TryGetValue(file, out var d) ? d : null });
            }
            else if (storage.PodcastExists(userId, file))
            {
                var dto = metadata.ReadPodcastFile(userId, file, includeCover);
                result.Add(dto with
                {
                    IsPodcast = true,
                    DateDownload = podcastDates.TryGetValue(file, out var d) ? d : null,
                });
            }
        }
        return result;
    }

    // Artist/album browsing over file metadata (desktop get_songs_by_artist/album:
    // exact match, artist view sorted Album+Title, album view Artist+Title).
    public Task<IReadOnlyList<string>> GetArtistsAsync(Guid userId, CancellationToken ct = default)
    {
        var artists = storage.ListSongFiles(userId)
            .Select(f => metadata.Read(userId, f, includeCover: false).Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a) && a != "Unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(artists);
    }

    public Task<IReadOnlyList<SongDto>> GetSongsByArtistAsync(
        Guid userId, string artist, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SongDto>>(storage.ListSongFiles(userId)
            .Select(f => metadata.Read(userId, f, includeCover: false))
            .Where(s => string.Equals(s.Artist, artist, StringComparison.Ordinal))
            .OrderBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList());

    public Task<IReadOnlyList<string>> GetAlbumsAsync(Guid userId, CancellationToken ct = default)
    {
        var albums = storage.ListSongFiles(userId)
            .Select(f => metadata.Read(userId, f, includeCover: false).Album)
            .Where(a => !string.IsNullOrWhiteSpace(a) && a != "Unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(albums);
    }

    public Task<IReadOnlyList<SongDto>> GetSongsByAlbumAsync(
        Guid userId, string album, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SongDto>>(storage.ListSongFiles(userId)
            .Select(f => metadata.Read(userId, f, includeCover: false))
            .Where(s => string.Equals(s.Album, album, StringComparison.Ordinal))
            .OrderBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList());

    private async Task<Dictionary<string, string?>> PodcastDateLookupAsync(Guid userId, CancellationToken ct)
    {
        // Quoted identifiers: Postgres folds unquoted names to lowercase
        // while EF creates quoted PascalCase tables/columns.
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Podcasts")} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string File, DateTime DateDownloadUtc)>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            lookup[r.File] = r.DateDownloadUtc.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        return lookup;
    }

    private async Task<Dictionary<string, string?>> DateLookupAsync(Guid userId, CancellationToken ct)
    {
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string File, DateTime DateDownloadUtc)>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            lookup[r.File] = r.DateDownloadUtc.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        return lookup;
    }

    private static bool MatchesSearch(SongDto dto, string? search) =>
        // Typo-tolerant: exact substring always matches, otherwise every
        // query token must fuzz-match a title/artist/album token.
        FuzzySearch.IsMatch([dto.Title, dto.Artist, dto.Album], search);

    private static IEnumerable<SongDto> Sort(List<SongDto> songs, string sort, string dir)
    {
        var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sort switch
        {
            "Album" => desc
                ? songs.OrderByDescending(s => s.Album).ThenByDescending(s => s.Title)
                : songs.OrderBy(s => s.Album).ThenBy(s => s.Title),
            "Duration" => desc
                ? songs.OrderByDescending(s => s.Duration)
                : songs.OrderBy(s => s.Duration),
            "DateDownload" => desc
                ? songs.OrderByDescending(s => s.DateDownload)
                : songs.OrderBy(s => s.DateDownload),
            _ => desc
                ? songs.OrderByDescending(s => s.Title)
                : songs.OrderBy(s => s.Title),
        }).ToList();
    }
}

// TagLibSharp reader — ports MetadataManager.get_song_metadata defaults
// (Unknown artist/album/year, filename title, embedded APIC → data URI).
public sealed class SongMetadataReader(ILibraryStorage storage, SongMetadataCache cache)
{
    public SongDto Read(Guid userId, string file, bool includeCover)
    {
        var path = storage.SongPath(userId, file);
        if (!includeCover && cache.TryGet(path, file, out var cached)) return cached;
        var dto = ReadFile(path, file, includeCover);
        if (!includeCover) cache.Set(path, file, dto);
        return dto;
    }

    public SongDto ReadPodcastFile(Guid userId, string file, bool includeCover)
    {
        var path = storage.PodcastPath(userId, file);
        if (!includeCover && cache.TryGet(path, file, out var cached)) return cached;
        var dto = ReadFile(path, file, includeCover);
        if (!includeCover) cache.Set(path, file, dto);
        return dto;
    }

    public static SongDto ReadFile(string path, string file, bool includeCover)
    {
        var dto = new SongDto(file, "Unknown", Path.GetFileNameWithoutExtension(file),
            "Unknown", "Unknown", 0, null, null);
        if (!File.Exists(path)) return dto;
        try
        {
            using var tag = TagLib.File.Create(path);
            var title = tag.Tag.Title?.Trim();
            var artist = tag.Tag.FirstPerformer?.Trim();
            var album = tag.Tag.Album?.Trim();
            var year = tag.Tag.Year is 0 or > 2100 ? null : tag.Tag.Year.ToString();
            var genre = tag.Tag.FirstGenre?.Trim();
            string? cover = null;
            if (includeCover && tag.Tag.Pictures is { Length: > 0 } pics)
            {
                var pic = pics[0];
                var mime = string.IsNullOrWhiteSpace(pic.MimeType) ? "image/jpeg" : pic.MimeType;
                cover = $"data:{mime};base64,{Convert.ToBase64String(pic.Data.Data)}";
            }
            dto = dto with
            {
                Title = string.IsNullOrWhiteSpace(title) ? dto.Title : title!,
                Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown" : artist!,
                Album = string.IsNullOrWhiteSpace(album) ? "Unknown" : album!,
                Year = string.IsNullOrWhiteSpace(year) ? "Unknown" : year!,
                Genre = string.IsNullOrWhiteSpace(genre) ? "Unknown" : genre!,
                Duration = tag.Properties.Duration.TotalSeconds,
                CoverArt = cover,
            };
        }
        catch
        {
            // Corrupt/unreadable file → filename defaults (desktop except-pass equivalent).
        }
        return dto;
    }
}

// Process-wide tag cache. Queue DTOs re-resolve every queued file on
// every player-state mutation (setQueue + play per click, plus every
// broadcast), so without this an All-Songs click re-opens 1000+ files.
// Validated by size+mtime so tag edits and re-downloads invalidate.
public sealed class SongMetadataCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(SongDto Dto, long Size, DateTime MtimeUtc);

    public bool TryGet(string path, string file, out SongDto dto)
    {
        dto = null!;
        if (!_entries.TryGetValue(path, out var entry)) return false;
        var info = Stat(path);
        if (info is null || info.Value.Size != entry.Size || info.Value.MtimeUtc != entry.MtimeUtc)
        {
            _entries.TryRemove(path, out _);
            return false;
        }
        // File name doubles as the fallback title; a renamed path misses the
        // key anyway, but guard the shape regardless.
        if (!string.Equals(entry.Dto.File, file, StringComparison.Ordinal)) return false;
        dto = entry.Dto;
        return true;
    }

    public void Set(string path, string file, SongDto dto)
    {
        var info = Stat(path);
        if (info is null) return;
        if (_entries.Count > 20000) _entries.Clear();
        _entries[path] = new Entry(dto, info.Value.Size, info.Value.MtimeUtc);
    }

    // Tag writes must evict: small ID3 edits fit the existing padding, so
    // size AND mtime can come back identical (verified empirically) and
    // validation alone would serve stale tags forever.
    public void Remove(string path) => _entries.TryRemove(path, out _);

    private static (long Size, DateTime MtimeUtc)? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class LocalLibraryStorage : ILibraryStorage
{
    private readonly string _storageRoot; // app data (backgrounds stay per-user here)
    private readonly string _songsDir; // shared media library (desktop folder)
    private readonly string _podcastsDir;

    public LocalLibraryStorage(string storageRoot, string? songsPath = null, string? podcastsPath = null)
    {
        _storageRoot = storageRoot;
        _songsDir = ResolveDir(songsPath, DefaultSongsPath(storageRoot));
        _podcastsDir = ResolveDir(podcastsPath, DefaultPodcastsPath(storageRoot));
    }

    // Desktop defaults: the Python app reads %APPDATA%\musicPlayer and
    // %APPDATA%\musicPlayerPodcasts (see settings.py). Every backend user
    // shares these folders, exactly like every desktop profile does.
    public static string DefaultSongsPath(string storageRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "musicPlayer")
            : Path.Combine(storageRoot, "shared", "songs");

    public static string DefaultPodcastsPath(string storageRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "musicPlayerPodcasts")
            : Path.Combine(storageRoot, "shared", "podcasts");

    private static string ResolveDir(string? configured, string @default) =>
        string.IsNullOrWhiteSpace(configured) ? @default : configured;

    public string SongsDir(Guid userId)
    {
        Directory.CreateDirectory(_songsDir);
        return _songsDir;
    }

    public string PodcastsDir(Guid userId)
    {
        Directory.CreateDirectory(_podcastsDir);
        return _podcastsDir;
    }

    public IReadOnlyList<string> ListSongFiles(Guid userId)
    {
        var dir = SongsDir(userId);
        return Directory.EnumerateFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Select(f => f!)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string SongPath(Guid userId, string file) =>
        Path.Combine(SongsDir(userId), file);

    public bool SongExists(Guid userId, string file)
    {
        // Guard path traversal: file must be a bare filename.
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || file.Contains(Path.DirectorySeparatorChar)
            || file.Contains(Path.AltDirectorySeparatorChar)) return false;
        return File.Exists(SongPath(userId, file));
    }

    public IReadOnlyList<string> ListPodcastFiles(Guid userId)
    {
        var dir = PodcastsDir(userId);
        return Directory.EnumerateFiles(dir, "*.mp3", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Select(f => f!)
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string PodcastPath(Guid userId, string file) =>
        Path.Combine(PodcastsDir(userId), file);

    public bool PodcastExists(Guid userId, string file)
    {
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || file.Contains(Path.DirectorySeparatorChar)
            || file.Contains(Path.AltDirectorySeparatorChar)) return false;
        return File.Exists(PodcastPath(userId, file));
    }

    private static readonly Dictionary<string, string> BackgroundExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/bmp"] = ".bmp",
    };

    private string BackgroundsDir(Guid userId)
    {
        var dir = Path.Combine(_storageRoot, userId.ToString(), "backgrounds");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string? FindBackground(Guid userId) =>
        Directory.EnumerateFiles(BackgroundsDir(userId))
            .Select(Path.GetFileName)
            .FirstOrDefault(f => f is not null);

    public async Task<string> SaveBackgroundAsync(Guid userId, byte[] bytes,
        string contentType, CancellationToken ct = default)
    {
        var dir = BackgroundsDir(userId);
        foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
        var ext = BackgroundExtensions.TryGetValue(contentType, out var e) ? e : ".jpg";
        var name = "background" + ext;
        await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes, ct);
        return name;
    }

    public Task DeleteBackgroundAsync(Guid userId, CancellationToken ct = default)
    {
        foreach (var f in Directory.EnumerateFiles(BackgroundsDir(userId))) File.Delete(f);
        return Task.CompletedTask;
    }

    public string BackgroundPath(Guid userId, string file) =>
        Path.Combine(BackgroundsDir(userId), file);
}

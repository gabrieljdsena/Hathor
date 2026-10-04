using System.Collections.Concurrent;
using Dapper;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;

namespace Hathor.Infrastructure.Library;

// Dapper read-model over the materialized Songs columns (written at
// scan/download/edit time): lists, counts, search and artist/album views
// run as single indexed queries — no MP3 is opened. TagLib stays for
// covers, single-file details and one-time backfill of legacy rows.
public sealed class DapperSongReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory,
    ILibraryStorage storage,
    SongMetadataReader metadata,
    ISongRecordRepository records) : ISongReadModel
{
    // Narrow row: everything list/search/sort/display needs, no file I/O.
    private sealed record SongRow(
        string File, string Title, string? Artist, string? Album, string? Year,
        string? Genre, double DurationSecs, DateTime DateDownloadUtc);

    private static string Iso(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";

    private static SongDto ToDto(SongRow r) => new(
        r.File,
        string.IsNullOrWhiteSpace(r.Artist) ? "Unknown" : r.Artist,
        string.IsNullOrWhiteSpace(r.Title)
            ? Path.GetFileNameWithoutExtension(r.File) : r.Title,
        string.IsNullOrWhiteSpace(r.Album) ? "Unknown" : r.Album,
        string.IsNullOrWhiteSpace(r.Year) ? "Unknown" : r.Year,
        r.DurationSecs,
        null,
        Iso(r.DateDownloadUtc),
        false,
        string.IsNullOrWhiteSpace(r.Genre) ? "Unknown" : r.Genre);

    private async Task<IReadOnlyList<SongRow>> SongRowsAsync(Guid userId, CancellationToken ct)
    {
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")}, {factory.Quote("Album")}, {factory.Quote("Year")}, " +
            $"{factory.Quote("Genre")}, {factory.Quote("DurationSecs")}, " +
            $"{factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<SongRow>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        return rows.ToList();
    }

    private async Task<Dictionary<string, SongRow>> SongRowsByFilesAsync(
        Guid userId, IReadOnlyList<string> files, CancellationToken ct)
    {
        var map = new Dictionary<string, SongRow>(StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0) return map;
        var cols = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")}, {factory.Quote("Album")}, {factory.Quote("Year")}, " +
            $"{factory.Quote("Genre")}, {factory.Quote("DurationSecs")}, " +
            $"{factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("File")} IN ";
        using var conn = factory.Create();
        foreach (var chunk in files.Chunk(500))
        {
            // Numbered parameters (not Dapper list-expansion): identical
            // SQL shape on Postgres and MySQL, no array-typing pitfalls.
            var names = chunk.Select((_, ix) => $"@f{ix}").ToArray();
            var parameters = new DynamicParameters();
            parameters.Add("UserId",
                Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId));
            for (var ix = 0; ix < chunk.Length; ix++)
                parameters.Add($"f{ix}", chunk[ix]);
            var rows = await conn.QueryAsync<SongRow>(
                new CommandDefinition(cols + $"({string.Join(",", names)})",
                    parameters, cancellationToken: ct));
            foreach (var r in rows) map[r.File] = r;
        }
        return map;
    }

    public async Task<IReadOnlyList<SongDto>> ListAsync(Guid userId, string? search,
        string? sort, string? dir, int page, int pageSize, CancellationToken ct = default)
    {
        var byFile = (await SongRowsAsync(userId, ct))
            .ToDictionary(r => r.File, StringComparer.OrdinalIgnoreCase);
        // The library is the files on disk (desktop get_all_songs loads
        // everything); rows supply indexed metadata, TagLib fills gaps.
        var live = storage.ListSongFiles(userId);

        var songs = new List<SongDto>();
        var missing = new List<(string File, SongDto Dto)>();
        foreach (var file in live)
        {
            SongDto dto;
            if (byFile.TryGetValue(file, out var row))
            {
                dto = ToDto(row);
            }
            else
            {
                // Never scanned: read once now (rare — scan/download/edit
                // keep rows complete) and persist below.
                try { dto = metadata.Read(userId, file, includeCover: false); }
                catch { continue; }
                missing.Add((file, dto));
            }
            if (!MatchesSearch(dto, search)) continue;
            songs.Add(dto);
        }

        var paged = Sort(songs, sort ?? "Title", dir ?? "asc")
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        if (missing.Count > 0 || paged.Any(s => NeedsBackfill(byFile, s.File)))
            await BackfillAsync(userId, byFile, live, missing, paged, ct);
        return paged;
    }

    private static bool NeedsBackfill(Dictionary<string, SongRow> byFile, string file) =>
        byFile.TryGetValue(file, out var row) && row.Album is null;

    // Persist rows for unscanned files and refresh legacy rows lacking
    // materialized columns (bounded to the served page + new files).
    // Best effort — failures keep the served DTOs, a later scan heals rows.
    private async Task BackfillAsync(Guid userId, Dictionary<string, SongRow> byFile,
        IReadOnlyList<string> live, List<(string File, SongDto Dto)> missing,
        List<SongDto> paged, CancellationToken ct)
    {
        var dirty = false;
        try
        {
            foreach (var (file, dto) in missing)
            {
                if (!paged.Any(s => string.Equals(s.File, file, StringComparison.OrdinalIgnoreCase)))
                    continue;
                await records.UpsertMetadataAsync(userId, file, dto.Title, dto.Artist,
                    dto.Album, dto.Year, dto.Genre, dto.Duration, ct);
                dirty = true;
            }
            for (var i = 0; i < paged.Count; i++)
            {
                var dto = paged[i];
                if (!NeedsBackfill(byFile, dto.File)) continue;
                if (!live.Contains(dto.File, StringComparer.OrdinalIgnoreCase)) continue;
                var fresh = metadata.Read(userId, dto.File, includeCover: false);
                await records.UpsertMetadataAsync(userId, dto.File, fresh.Title, fresh.Artist,
                    fresh.Album, fresh.Year, fresh.Genre, fresh.Duration, ct);
                dirty = true;
                paged[i] = fresh with { DateDownload = dto.DateDownload };
            }
            if (dirty) await records.SaveChangesAsync(ct);
        }
        catch
        {
            // Best effort only.
        }
    }

    public async Task<int> CountAsync(Guid userId, string? search, CancellationToken ct = default)
    {
        // Library count = files on disk (desktop get_library_count counts .mp3 files).
        var live = storage.ListSongFiles(userId);
        if (string.IsNullOrWhiteSpace(search)) return live.Count;
        var byFile = (await SongRowsAsync(userId, ct))
            .ToDictionary(r => r.File, StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var file in live)
        {
            if (FuzzySearch.IsMatch(file, search)) { count++; continue; }
            SongDto? dto = byFile.TryGetValue(file, out var row)
                ? ToDto(row)
                : SafeRead(userId, file);
            if (dto is not null &&
                FuzzySearch.IsMatch([dto.Title, dto.Artist, dto.Album], search)) count++;
        }
        return count;
    }

    private SongDto? SafeRead(Guid userId, string file)
    {
        try { return metadata.Read(userId, file, includeCover: false); }
        catch { return null; }
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

    // Batch resolve preserving input order, skipping missing files.
    // Cover-less DTOs come straight from the materialized columns (no file
    // I/O); covers and unrowed files fall back to TagLib + backfill.
    public async Task<IReadOnlyList<SongDto>> GetManyAsync(Guid userId, IEnumerable<string> files,
        bool includeCover, CancellationToken ct = default)
    {
        var list = files.ToList();
        var rows = await SongRowsByFilesAsync(userId, list, ct);
        var podcastDates = await PodcastDateLookupAsync(userId, ct);
        var result = new List<SongDto>(list.Count);
        var dirty = false;
        foreach (var file in list)
        {
            if (storage.SongExists(userId, file))
            {
                if (rows.TryGetValue(file, out var row) && row.Album is not null)
                {
                    if (includeCover)
                    {
                        var dto = metadata.Read(userId, file, includeCover: true);
                        result.Add(dto with { DateDownload = Iso(row.DateDownloadUtc) });
                    }
                    else
                    {
                        result.Add(ToDto(row));
                    }
                    continue;
                }
                // Unrowed or legacy row: one TagLib read, then backfill.
                SongDto resolved;
                try
                {
                    resolved = metadata.Read(userId, file, includeCover);
                }
                catch
                {
                    continue;
                }
                var date = rows.TryGetValue(file, out var r)
                    ? Iso(r.DateDownloadUtc)
                    : resolved.DateDownload;
                result.Add(resolved with { DateDownload = date });
                try
                {
                    await records.UpsertMetadataAsync(userId, file, resolved.Title, resolved.Artist,
                        resolved.Album, resolved.Year, resolved.Genre, resolved.Duration, ct);
                    dirty = true;
                }
                catch
                {
                    // Best effort only.
                }
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
        if (dirty)
        {
            try { await records.SaveChangesAsync(ct); } catch { }
        }
        return result;
    }

    // Artist/album browsing over materialized columns (desktop
    // get_songs_by_artist/album: exact match, artist view sorted
    // Album+Title, album view Artist+Title).
    public async Task<IReadOnlyList<string>> GetArtistsAsync(Guid userId, CancellationToken ct = default)
    {
        var sql = $"SELECT DISTINCT {factory.Quote("Artist")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("Artist")} IS NOT NULL AND {factory.Quote("Artist")} <> ''";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        return rows
            .Where(a => a != "Unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SongDto>> GetSongsByArtistAsync(
        Guid userId, string artist, CancellationToken ct = default)
    {
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")}, {factory.Quote("Album")}, {factory.Quote("Year")}, " +
            $"{factory.Quote("Genre")}, {factory.Quote("DurationSecs")}, " +
            $"{factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()} " +
            $"AND UPPER({factory.Quote("Artist")}) = UPPER(@Artist)";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<SongRow>(
            new CommandDefinition(sql,
                new
                {
                    UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                    Artist = artist,
                },
                cancellationToken: ct));
        var live = storage.ListSongFiles(userId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(r => live.Contains(r.File))
            .Select(ToDto)
            .Where(s => string.Equals(s.Artist, artist, StringComparison.Ordinal))
            .OrderBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> FindFileByMetadataAsync(Guid userId, string title, string? artist,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var want = MediaKeys.Key(title, artist ?? "");
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")} FROM {factory.Quote("Songs")} " +
            $"WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string File, string Title, string? Artist)>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        var live = storage.ListSongFiles(userId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(r => live.Contains(r.File))
            .Where(r => MediaKeys.Key(r.Title, r.Artist ?? "") == want)
            .Select(r => r.File)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> GetAlbumsAsync(Guid userId, CancellationToken ct = default)
    {
        var sql = $"SELECT DISTINCT {factory.Quote("Album")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()} " +
            $"AND {factory.Quote("Album")} IS NOT NULL AND {factory.Quote("Album")} <> ''";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<string>(
            new CommandDefinition(sql,
                new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) },
                cancellationToken: ct));
        return rows
            .Where(a => a != "Unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SongDto>> GetSongsByAlbumAsync(
        Guid userId, string album, CancellationToken ct = default)
    {
        var sql = $"SELECT {factory.Quote("File")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Artist")}, {factory.Quote("Album")}, {factory.Quote("Year")}, " +
            $"{factory.Quote("Genre")}, {factory.Quote("DurationSecs")}, " +
            $"{factory.Quote("DateDownloadUtc")} " +
            $"FROM {factory.Quote("Songs")} WHERE {factory.UserIdPredicate()} " +
            $"AND UPPER({factory.Quote("Album")}) = UPPER(@Album)";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<SongRow>(
            new CommandDefinition(sql,
                new
                {
                    UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                    Album = album,
                },
                cancellationToken: ct));
        var live = storage.ListSongFiles(userId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows
            .Where(r => live.Contains(r.File))
            .Select(ToDto)
            .Where(s => string.Equals(s.Album, album, StringComparison.Ordinal))
            .OrderBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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

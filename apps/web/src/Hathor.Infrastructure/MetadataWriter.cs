using Hathor.Application.Ports;

namespace Hathor.Infrastructure.Library;

// TagLib write path — ports MetadataManager.update_song_metadata tag writes
// (TIT2/TPE1/TALB/TDRC year/TCON genre/APIC cover) plus the cover sources:
// data: URL (file picker), http(s) URL (iTunes artwork), "REMOVE" sentinel.
// Null fields are left untouched (genre preserved — plan G1).
public sealed class TagLibMetadataWriter(
    ILibraryStorage storage, HttpClient http, SongMetadataCache cache) : IMetadataWriter
{
    public const string RemoveCoverSentinel = "REMOVE";

    public Task WriteSongAsync(Guid userId, string file,
        string? title, string? artist, string? album, string? year, string? genre,
        string? coverArt, CancellationToken ct = default)
    {
        var path = storage.SongPath(userId, file);
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found.", file);
        return WritePathAsync(path, title, artist, album, year, genre, coverArt, ct);
    }

    public async Task WritePathAsync(string path,
        string? title, string? artist, string? album, string? year, string? genre,
        string? coverArt, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio file not found.", path);

        TagLib.File tag;
        try
        {
            tag = TagLib.File.Create(path);
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            throw new InvalidOperationException($"Could not read audio tags: {ex.Message}");
        }
        using (tag)
        {
            if (title is not null) tag.Tag.Title = title;
            if (artist is not null) tag.Tag.Performers = [artist];
            if (album is not null) tag.Tag.Album = album;
            if (year is not null)
            {
                // Empty clears to Unknown (read-back maps 0 → Unknown);
                // otherwise take the first 4 digits like desktop.
                if (string.IsNullOrWhiteSpace(year))
                {
                    tag.Tag.Year = 0;
                }
                else
                {
                    var digits = new string(year.Where(char.IsDigit).ToArray());
                    if (uint.TryParse(digits.Length >= 4 ? digits[..4] : digits, out var y))
                        tag.Tag.Year = y;
                }
            }
            if (genre is not null) tag.Tag.Genres = [genre];

            if (coverArt is not null)
            {
                if (coverArt == RemoveCoverSentinel)
                {
                    tag.Tag.Pictures = [];
                }
                else
                {
                    var bytes = await ResolveCoverBytesAsync(coverArt, ct);
                    if (bytes is not null)
                    {
                        var mime = SniffMime(bytes);
                        tag.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(bytes))
                        {
                            MimeType = mime,
                            Type = TagLib.PictureType.FrontCover,
                            Description = "Cover",
                        }];
                    }
                }
            }

            try
            {
                tag.Save();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not save audio tags: {ex.Message}");
            }
            // Evict the read cache: the file stat can be unchanged after a
            // save, so without this every later read serves the old tags.
            cache.Remove(path);
        }
    }

    private async Task<byte[]?> ResolveCoverBytesAsync(string coverArt, CancellationToken ct)
    {
        if (coverArt.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = coverArt.IndexOf(',');
            if (comma < 0) return null;
            try { return Convert.FromBase64String(coverArt[(comma + 1)..]); }
            catch (FormatException) { return null; }
        }
        if (coverArt.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            coverArt.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await http.GetAsync(coverArt, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch
            {
                return null; // artwork fetch must never fail the save
            }
        }
        return null;
    }

    private static string SniffMime(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50) return "image/png";
        if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49) return "image/gif";
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49) return "image/webp";
        return "image/jpeg";
    }
}

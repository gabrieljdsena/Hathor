using SkiaSharp;

namespace Hathor.Application.Playlists;

// Playlist covers travel as data: URLs through the sync contract, so the
// server normalizes them on every write path (web create/edit + device
// import). The browser already downsizes its own uploads, but mobile and
// desktop push originals; re-encoding here keeps the TEXT column bounded.
public static class PlaylistCover
{
    public const int MaxDimension = 800;
    public const int JpegQuality = 82;

    // Decoding a 100MP source would need ~400MB of RGBA pixels. Real phone
    // and camera images stay below this; anything larger is left untouched
    // (and still works) instead of risking an OOM during the request.
    private const long MaxSourcePixels = 100_000_000;

    // Normalized covers (and the web client's own 800px JPEGs) are already
    // small; skipping them avoids recompression on every device sync.
    private const int MaxStoredOriginalBytes = 500_000;

    public static string? Normalize(string? cover)
    {
        if (string.IsNullOrWhiteSpace(cover)) return null;
        // Remote/relative URLs are tiny and cannot be resized synchronously.
        if (!cover.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return cover;

        var comma = cover.IndexOf(',');
        if (comma < 0) return cover;
        var header = cover.AsSpan(5, comma - 5);
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase)) return cover;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(cover[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return cover;
        }
        if (bytes.Length == 0) return cover;

        try
        {
            var bounds = SKBitmap.DecodeBounds(bytes);
            if (bounds.Width <= 0 || bounds.Height <= 0) return cover;
            if ((long)bounds.Width * bounds.Height > MaxSourcePixels) return cover;

            using (var data = SKData.CreateCopy(bytes))
            using (var codec = SKCodec.Create(data))
            {
                var alreadyBounded = codec is not null
                    && Math.Max(bounds.Width, bounds.Height) <= MaxDimension
                    && bytes.Length <= MaxStoredOriginalBytes
                    && codec.EncodedFormat == SKEncodedImageFormat.Jpeg
                    && codec.EncodedOrigin == SKEncodedOrigin.TopLeft;
                if (alreadyBounded) return cover;
            }

            // SKImage honors EXIF orientation while SKBitmap does not, so
            // phone photos stay upright after the resize.
            using var image = SKImage.FromEncodedData(bytes);
            if (image is null) return cover;

            var scale = Math.Min(1f, (float)MaxDimension / Math.Max(image.Width, image.Height));
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));

            using var surface = SKSurface.Create(
                new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return cover;

            var canvas = surface.Canvas;
            // JPEG has no alpha; transparent pixels composite onto black,
            // matching the browser's canvas -> toDataURL('image/jpeg').
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(image, new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (encoded is null) return cover;
            return "data:image/jpeg;base64," + Convert.ToBase64String(encoded.ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unsupported/corrupt formats keep the original value rather than
            // failing the whole save (and desktop/mobile sync import).
            return cover;
        }
    }
}

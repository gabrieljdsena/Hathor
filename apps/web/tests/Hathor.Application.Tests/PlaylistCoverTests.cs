using FluentAssertions;
using Hathor.Application.Playlists;
using Hathor.Application.Sync;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using SkiaSharp;

namespace Hathor.Application.Tests;

public sealed class PlaylistCoverTests
{
    [Fact]
    public void Normalize_DownsizesLargeImageToJpeg()
    {
        var original = DataUrl(1600, 1200, SKEncodedImageFormat.Jpeg);

        var normalized = PlaylistCover.Normalize(original);

        normalized.Should().StartWith("data:image/jpeg;base64,");
        normalized!.Length.Should().BeLessThan(original.Length);
        using var image = Decode(normalized);
        image.Width.Should().Be(800);
        image.Height.Should().Be(600);
    }

    [Fact]
    public void Normalize_KeepsSmallImagesWithinBound()
    {
        var normalized = PlaylistCover.Normalize(DataUrl(320, 240, SKEncodedImageFormat.Png));

        normalized.Should().StartWith("data:image/jpeg;base64,");
        using var image = Decode(normalized!);
        image.Width.Should().Be(320);
        image.Height.Should().Be(240);
    }

    [Fact]
    public void Normalize_KeepsRemoteUrls()
    {
        const string url = "https://example.com/cover.jpg";
        PlaylistCover.Normalize(url).Should().Be(url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Normalize_EmptyBecomesNull(string? cover) =>
        PlaylistCover.Normalize(cover).Should().BeNull();

    [Fact]
    public void Normalize_AppliesExifOrientation()
    {
        var bytes = EncodeBytes(40, 20, SKEncodedImageFormat.Jpeg);
        var rotated = AddExifOrientation(bytes, 6);
        var cover = "data:image/jpeg;base64," + Convert.ToBase64String(rotated);

        var normalized = PlaylistCover.Normalize(cover);

        using var image = Decode(normalized!);
        image.Width.Should().Be(20);
        image.Height.Should().Be(40);
    }

    [Fact]
    public void Normalize_PreservesUnreadableDataUrl()
    {
        const string invalid = "data:image/png;base64,AAAA";
        PlaylistCover.Normalize(invalid).Should().Be(invalid);
    }

    [Fact]
    public async Task UpdatePlaylistHandler_NormalizesIncomingCover()
    {
        var userId = Guid.NewGuid();
        var playlists = Substitute.For<IPlaylistRepository>();
        var existing = new Playlist { Id = 7, UserId = userId, Title = "P" };
        playlists.GetAsync(userId, 7, Arg.Any<CancellationToken>()).Returns(existing);
        var original = DataUrl(1600, 1200, SKEncodedImageFormat.Jpeg);

        var dto = await new UpdatePlaylistHandler(playlists).Handle(
            new UpdatePlaylistCommand(userId, 7, null, null, original), CancellationToken.None);

        dto!.Thumbnail.Should().StartWith("data:image/jpeg;base64,");
        dto.Thumbnail!.Length.Should().BeLessThan(original.Length);
    }

    [Fact]
    public async Task SyncImport_NormalizesPlaylistCover()
    {
        await using var db = new HathorDbContext(
            new DbContextOptionsBuilder<HathorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var userId = Guid.NewGuid();
        var original = DataUrl(1600, 1200, SKEncodedImageFormat.Jpeg);

        await new EfSyncService(db).ImportAsync(userId, new SyncSnapshot(
            null, null, [new PlaylistRowDto(1, "P", null, original)], null, null,
            null, null, null, null, null, null));

        var stored = await db.Playlists.SingleAsync();
        stored.Thumbnail.Should().StartWith("data:image/jpeg;base64,");
        stored.Thumbnail!.Length.Should().BeLessThan(original.Length);
        using var image = Decode(stored.Thumbnail);
        image.Width.Should().Be(800);
    }

    private static SKImage Decode(string dataUrl) =>
        SKImage.FromEncodedData(Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]))!;

    private static byte[] EncodeBytes(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Orange);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(width, height),
                [SKColors.Red, SKColors.Blue, SKColors.Green, SKColors.Yellow],
                null, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, width, height, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    private static string DataUrl(int width, int height, SKEncodedImageFormat format)
    {
        var mime = format == SKEncodedImageFormat.Png ? "png" : "jpeg";
        return $"data:image/{mime};base64,{Convert.ToBase64String(EncodeBytes(width, height, format))}";
    }

    // Minimal EXIF APP1 block: MM byte order, one IFD0 entry (Orientation).
    private static byte[] AddExifOrientation(byte[] jpeg, ushort orientation)
    {
        var payload = new List<byte>();
        payload.AddRange("Exif\0\0"u8.ToArray());
        payload.AddRange([0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08]);
        payload.AddRange([0x00, 0x01]);
        payload.AddRange([0x01, 0x12, 0x00, 0x03]);
        payload.AddRange([0x00, 0x00, 0x00, 0x01]);
        payload.AddRange([(byte)(orientation >> 8), (byte)orientation, 0x00, 0x00]);
        payload.AddRange([0x00, 0x00, 0x00, 0x00]);

        var length = payload.Count + 2;
        var app1 = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
        app1.AddRange(payload);
        return [jpeg[0], jpeg[1], .. app1, .. jpeg[2..]];
    }
}

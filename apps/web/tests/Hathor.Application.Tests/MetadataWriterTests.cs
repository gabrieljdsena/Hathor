using FluentAssertions;
using Hathor.Application.Ports;
using Hathor.Infrastructure.Library;
using NSubstitute;

namespace Hathor.Application.Tests;

// Regression: small ID3 edits fit the existing tag padding, so file size
// AND mtime can come back identical — size/mtime cache validation alone
// served stale tags forever after an edit. The writer must evict.
public sealed class MetadataWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "hathor-tagtest-" + Guid.NewGuid().ToString("N") + ".mp3");
    private readonly Guid _userId = Guid.NewGuid();

    public MetadataWriterTests()
    {
        // Minimal valid MPEG1-Layer3 frame TagLibSharp accepts.
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        new Random(42).NextBytes(frame.AsSpan(4));
        File.WriteAllBytes(_path, frame);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    [Fact]
    public async Task WritePathAsync_EvictsStaleCacheEntry()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongPath(_userId, "t.mp3").Returns(_path);
        var cache = new SongMetadataCache();
        var reader = new SongMetadataReader(storage, cache);
        var writer = new TagLibMetadataWriter(storage, new HttpClient(), cache);

        var stale = reader.Read(_userId, "t.mp3", includeCover: false);
        stale.Title.Should().NotBe("New Title");

        await writer.WritePathAsync(_path, "New Title", null, null, null, null, null);

        cache.TryGet(_path, "t.mp3", out _).Should().BeFalse();
        reader.Read(_userId, "t.mp3", includeCover: false).Title.Should().Be("New Title");
    }

    [Fact]
    public async Task WritePathAsync_EmptyStrings_ClearFields()
    {
        var storage = Substitute.For<ILibraryStorage>();
        storage.SongPath(_userId, "t.mp3").Returns(_path);
        var cache = new SongMetadataCache();
        var reader = new SongMetadataReader(storage, cache);
        var writer = new TagLibMetadataWriter(storage, new HttpClient(), cache);

        await writer.WritePathAsync(_path, "", "", "", "", null, null);

        var dto = reader.Read(_userId, "t.mp3", includeCover: false);
        dto.Title.Should().Be("t"); // blank title falls back to filename
        dto.Artist.Should().Be("Unknown");
        dto.Album.Should().Be("Unknown");
        dto.Year.Should().Be("Unknown");
    }
}

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Hathor.Api.Tests;

// Filename/hash/selection rules for the desktop package drop folder behind
// /api/v1/desktop/latest. Pure filesystem tests: no database or web host.
public sealed class DesktopReleaseStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hathor-desktop-test-" + Guid.NewGuid().ToString("N"));
    private readonly IMemoryCache _cache =
        new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });

    public DesktopReleaseStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DesktopReleaseStore Store(string? packageDirectory = null, string? storageRoot = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DesktopApp:PackageDirectory"] = packageDirectory ?? _root,
        };
        if (storageRoot is not null) settings["Database:StorageRoot"] = storageRoot;
        return new DesktopReleaseStore(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _cache);
    }

    private string WriteZip(string name, byte[]? bytes = null, DateTime? writtenUtc = null)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3, 4]);
        if (writtenUtc is not null) File.SetLastWriteTimeUtc(path, writtenUtc.Value);
        return path;
    }

    [Fact]
    public void Latest_PicksHighestVersionCode_WithHashAndSize()
    {
        WriteZip("hathor-desktop-5-1.0.2.zip");
        var newest = WriteZip("hathor-desktop-9-1.0.5.zip", [9, 9, 9]);
        WriteZip("hathor-desktop-6-1.0.3.zip");

        var release = Store().Latest();

        release.Should().NotBeNull();
        release!.VersionCode.Should().Be(9);
        release.VersionName.Should().Be("1.0.5");
        release.FileName.Should().Be(Path.GetFileName(newest));
        release.SizeBytes.Should().Be(3);
        release.Sha256.Should().Be(
            Convert.ToHexString(SHA256.HashData([9, 9, 9])).ToLowerInvariant());
    }

    [Fact]
    public void Latest_IgnoresFilesOutsideTheContract_AndEmptyFiles()
    {
        WriteZip("hathor-desktop-2-1.0.0.zip");
        WriteZip("notes.txt");
        WriteZip("other-desktop-99-x.zip");
        WriteZip("hathor-desktop-99-9.9.9.zip.part");
        WriteZip("hathor-desktop-100-9.9.9.zip", []);
        Directory.CreateDirectory(Path.Combine(_root, "hathor-desktop-200-9.9.9.zip"));

        var release = Store().Latest();

        release!.VersionCode.Should().Be(2);
    }

    [Fact]
    public void Latest_SameVersion_PrefersNewestFile()
    {
        WriteZip("hathor-desktop-7-1.0.4-a.zip", writtenUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteZip("hathor-desktop-7-1.0.4-b.zip", writtenUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Store().Latest()!.FileName.Should().Be("hathor-desktop-7-1.0.4-b.zip");
    }

    [Fact]
    public void Latest_ReturnsNull_WhenFolderMissingOrEmpty()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Store(packageDirectory: Path.Combine(_root, "missing")).Latest().Should().BeNull();
        Store(packageDirectory: empty).Latest().Should().BeNull();
    }

    [Fact]
    public void Directory_FallsBackToStorageRootDesktop()
    {
        Store(packageDirectory: "", storageRoot: _root).PackageDirectory
            .Should().Be(Path.Combine(_root, "desktop"));
    }

    [Fact]
    public void ResolveDownload_AcceptsOnlyPublishedBareNames()
    {
        WriteZip("hathor-desktop-4-1.0.1.zip");
        var store = Store();

        store.ResolveDownload("hathor-desktop-4-1.0.1.zip").Should().NotBeNull();
        store.ResolveDownload("hathor-desktop-8-1.0.5.zip").Should().BeNull();
        store.ResolveDownload("../hathor-desktop-4-1.0.1.zip").Should().BeNull();
        store.ResolveDownload(@"..\hathor-desktop-4-1.0.1.zip").Should().BeNull();
        store.ResolveDownload("hathor-desktop-4-1.0.1.zip.exe").Should().BeNull();
        store.ResolveDownload("other.zip").Should().BeNull();
        store.ResolveDownload("").Should().BeNull();
    }
}

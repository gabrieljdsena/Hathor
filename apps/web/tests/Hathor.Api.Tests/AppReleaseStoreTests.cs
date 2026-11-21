using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Hathor.Api.Tests;

// Filename/hash/selection rules for the APK drop folder behind
// /api/v1/app/latest. Pure filesystem tests: no database or web host.
public sealed class AppReleaseStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hathor-apk-test-" + Guid.NewGuid().ToString("N"));
    private readonly IMemoryCache _cache =
        new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 });

    public AppReleaseStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private AppReleaseStore Store(string? apkDirectory = null, string? storageRoot = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["MobileApp:ApkDirectory"] = apkDirectory ?? _root,
        };
        if (storageRoot is not null) settings["Database:StorageRoot"] = storageRoot;
        return new AppReleaseStore(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            _cache);
    }

    private string WriteApk(string name, byte[]? bytes = null, DateTime? writtenUtc = null)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3, 4]);
        if (writtenUtc is not null) File.SetLastWriteTimeUtc(path, writtenUtc.Value);
        return path;
    }

    [Fact]
    public void Latest_PicksHighestVersionCode_WithHashAndSize()
    {
        WriteApk("hathor-5-0.4.2-hathor.apk");
        var newest = WriteApk("hathor-9-0.5.0-hathor.apk", [9, 9, 9]);
        WriteApk("hathor-6-0.4.3-hathor.apk");

        var release = Store().Latest();

        release.Should().NotBeNull();
        release!.VersionCode.Should().Be(9);
        release.VersionName.Should().Be("0.5.0-hathor");
        release.FileName.Should().Be(Path.GetFileName(newest));
        release.SizeBytes.Should().Be(3);
        release.Sha256.Should().Be(
            Convert.ToHexString(SHA256.HashData([9, 9, 9])).ToLowerInvariant());
    }

    [Fact]
    public void Latest_IgnoresFilesOutsideTheContract_AndEmptyFiles()
    {
        WriteApk("hathor-2-0.0.2.apk");
        WriteApk("notes.txt");
        WriteApk("other-99-x.apk");
        WriteApk("hathor-99-0.0.99.apk.part");
        WriteApk("hathor-100-0.1.0.apk", []);
        Directory.CreateDirectory(Path.Combine(_root, "hathor-200-nested.apk"));

        var release = Store().Latest();

        release!.VersionCode.Should().Be(2);
    }

    [Fact]
    public void Latest_SameVersion_PrefersNewestFile()
    {
        WriteApk("hathor-7-0.4.4-a.apk", writtenUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteApk("hathor-7-0.4.4-b.apk", writtenUtc: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Store().Latest()!.FileName.Should().Be("hathor-7-0.4.4-b.apk");
    }

    [Fact]
    public void Latest_ReturnsNull_WhenFolderMissingOrEmpty()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        Store(apkDirectory: Path.Combine(_root, "missing")).Latest().Should().BeNull();
        Store(apkDirectory: empty).Latest().Should().BeNull();
    }

    [Fact]
    public void Directory_FallsBackToStorageRootApk()
    {
        Store(apkDirectory: "", storageRoot: _root).ApkDirectory
            .Should().Be(Path.Combine(_root, "apk"));
    }

    [Fact]
    public void ResolveDownload_AcceptsOnlyPublishedBareNames()
    {
        WriteApk("hathor-4-0.4.1-hathor.apk");
        var store = Store();

        store.ResolveDownload("hathor-4-0.4.1-hathor.apk").Should().NotBeNull();
        store.ResolveDownload("hathor-8-0.4.5-hathor.apk").Should().BeNull();
        store.ResolveDownload("../hathor-4-0.4.1-hathor.apk").Should().BeNull();
        store.ResolveDownload(@"..\hathor-4-0.4.1-hathor.apk").Should().BeNull();
        store.ResolveDownload("hathor-4-0.4.1-hathor.apk.exe").Should().BeNull();
        store.ResolveDownload("other.apk").Should().BeNull();
        store.ResolveDownload("").Should().BeNull();
    }
}

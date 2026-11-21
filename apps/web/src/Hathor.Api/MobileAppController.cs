using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

// Sideload update channel for the Android app. Same auth as the rest of the
// LAN API (any authenticated hth_ key or JWT); the APK is never public.
// Metadata is separate from bytes so the app can compare versionCode before
// downloading, and the download supports ranges like the media endpoints.
[ApiController]
[Route("api/v1/app")]
public sealed class MobileAppController(AppReleaseStore releases) : ControllerBase
{
    [HttpGet("latest")]
    [Authorize]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public ActionResult<AppReleaseDto> Latest()
    {
        var release = releases.Latest();
        if (release is null)
            return NotFound(new
            {
                message = "No APK has been published yet. Drop " +
                          "hathor-<versionCode>-<versionName>.apk in the APK folder.",
            });
        return Ok(new AppReleaseDto(
            release.VersionCode,
            release.VersionName,
            release.FileName,
            release.SizeBytes,
            release.Sha256,
            release.PublishedAtUtc,
            $"/api/v1/app/download/{Uri.EscapeDataString(release.FileName)}"));
    }

    [HttpGet("download/{fileName}")]
    [Authorize]
    public ActionResult Download(string fileName)
    {
        var path = releases.ResolveDownload(fileName);
        if (path is null) return NotFound(new { message = "No such APK." });
        return PhysicalFile(
            path,
            AppReleaseStore.ContentType,
            fileName,
            enableRangeProcessing: true);
    }
}

public sealed record AppReleaseDto(
    int VersionCode,
    string VersionName,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset PublishedAtUtc,
    string DownloadPath);

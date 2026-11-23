using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

// Sideload update channel for the Windows desktop app. Same auth as the rest
// of the LAN API; packages are never public. Metadata is separate from bytes
// so the client can compare versionCode before downloading, and downloads
// support ranges like the media endpoints.
[ApiController]
[Route("api/v1/desktop")]
public sealed class DesktopAppController(DesktopReleaseStore releases) : ControllerBase
{
    [HttpGet("latest")]
    [Authorize]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public ActionResult<DesktopReleaseDto> Latest()
    {
        var release = releases.Latest();
        if (release is null)
            return NotFound(new
            {
                message = "No desktop release has been published yet. Drop " +
                          "hathor-desktop-<versionCode>-<versionName>.zip in the " +
                          "desktop package folder.",
            });
        return Ok(new DesktopReleaseDto(
            release.VersionCode,
            release.VersionName,
            release.FileName,
            release.SizeBytes,
            release.Sha256,
            release.PublishedAtUtc,
            $"/api/v1/desktop/download/{Uri.EscapeDataString(release.FileName)}"));
    }

    [HttpGet("download/{fileName}")]
    [Authorize]
    public ActionResult Download(string fileName)
    {
        var path = releases.ResolveDownload(fileName);
        if (path is null) return NotFound(new { message = "No such desktop package." });
        return PhysicalFile(
            path,
            DesktopReleaseStore.ContentType,
            fileName,
            enableRangeProcessing: true);
    }
}

public sealed record DesktopReleaseDto(
    int VersionCode,
    string VersionName,
    string FileName,
    long SizeBytes,
    string Sha256,
    DateTimeOffset PublishedAtUtc,
    string DownloadPath);

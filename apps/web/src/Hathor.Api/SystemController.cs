using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController(
    ISystemProbe probe,
    Hathor.Infrastructure.Maintenance.IFfmpegInstaller ffmpegInstaller) : ControllerBase
{
    [HttpGet("ffmpeg")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public ActionResult<FFmpegStatusDto> FFmpeg() => Ok(probe.GetFFmpegStatus());

    [HttpGet("libraries")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public ActionResult<IReadOnlyList<LibraryStatusDto>> Libraries() => Ok(probe.GetLibraryStatus());

    // Read-only check run (desktop run_startup_checks without installs:
    // server images bake FFmpeg in — this reports, with a Retry surface).
    [HttpPost("maintenance/run")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public ActionResult<MaintenanceResultDto> RunMaintenance() => Ok(probe.RunChecks());

    // Self-install progress for an in-flight (or finished) Settings download.
    [HttpGet("ffmpeg/download")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public ActionResult<FfmpegDownloadDto> FfmpegDownloadStatus() => Ok(ffmpegInstaller.Status());

    // Starts the background FFmpeg download+install (202 + current status).
    // 409 while one is already running; already-installed returns ready.
    [HttpPost("ffmpeg/download")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public ActionResult<FfmpegDownloadDto> StartFfmpegDownload()
    {
        try
        {
            return Accepted(ffmpegInstaller.StartDownload());
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // Live NuGet check for the YouTube downloader/searcher (YoutubeExplode)
    // and TagLibSharp. Libraries are compiled in, so "update-available"
    // means update the package and redeploy — nothing installs at runtime.
    [HttpPost("libraries/check")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<LibraryStatusDto>>> CheckLibraryUpdates(
        CancellationToken ct) => Ok(await probe.CheckLibraryUpdatesAsync(ct));
}

[ApiController]
[Route("api/v1/logs")]
public sealed class LogsController(ILogger<LogsController> log) : ControllerBase
{
    // Frontend error bridge (desktop js_log + onerror/unhandledrejection).
    public sealed record ClientLogRequest(string? Message, string? Stack, string? Route);

    [HttpPost("client")]
    [Authorize]
    public IActionResult Client([FromBody] ClientLogRequest body)
    {
        log.LogWarning("JS: {Message} route={Route} stack={Stack}",
            (body.Message ?? "").Length > 2000 ? body.Message![..2000] : body.Message,
            body.Route, body.Stack);
        return Ok();
    }
}

using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Ingest;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class DownloadsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.DownloadsRead)]
    public async Task<ActionResult<IReadOnlyList<DownloadJobDto>>> List(
        [FromQuery] int limit = 15, CancellationToken ct = default) =>
        Ok(await mediator.Send(new ListDownloadJobsQuery(CurrentUserId(), limit), ct));

    [HttpGet("active-count")]
    [Authorize(Policy = ScopeAuthorization.DownloadsRead)]
    public async Task<ActionResult<int>> ActiveCount(CancellationToken ct) =>
        Ok(await mediator.Send(new ActiveDownloadCountQuery(CurrentUserId()), ct));

    // Ownership pre-check for the "already in your library?" confirm
    // (normalized title+artist match, live files only).
    [HttpGet("check")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<OwnedCheckDto>> Check(
        [FromQuery] string title, [FromQuery] string? artist, CancellationToken ct) =>
        Ok(await mediator.Send(new CheckDownloadQuery(CurrentUserId(), title ?? "", artist), ct));

    [HttpGet("{qid}")]
    [Authorize(Policy = ScopeAuthorization.DownloadsRead)]
    public async Task<ActionResult<DownloadJobDto>> Get(string qid, CancellationToken ct)
    {
        var job = await mediator.Send(new GetDownloadJobQuery(CurrentUserId(), qid), ct);
        return job is null ? NotFound() : Ok(job);
    }

    // Enqueue a download (202 + qid). Accepts a YouTube URL or a search query;
    // Songs/Podcast routing via isPodcast (desktop toggle).
    [HttpPost]
    [Authorize(Policy = ScopeAuthorization.DownloadsWrite)]
    public async Task<ActionResult<SubmitDownloadResponse>> Submit(
        [FromBody] SubmitDownloadRequest body, CancellationToken ct)
    {
        try
        {
            var qid = await mediator.Send(new SubmitDownloadCommand(
                CurrentUserId(), body.Url, body.Title, body.Artist, body.IsPodcast), ct);
            return Accepted(new SubmitDownloadResponse(qid));
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // Batch import: .txt file, one query per line (desktop batch import).
    [HttpPost("batch")]
    [Authorize(Policy = ScopeAuthorization.DownloadsWrite)]
    [RequestSizeLimit(1_000_000)]
    public async Task<ActionResult<BatchDownloadResponse>> Batch(
        IFormFile file, [FromForm] bool isPodcast = false, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "A non-empty .txt file is required." });
        using var reader = new StreamReader(file.OpenReadStream());
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
        }
        if (lines.Count == 0)
            return BadRequest(new { message = "No queries found in the file." });
        var qids = await mediator.Send(
            new BatchDownloadCommand(CurrentUserId(), lines, isPodcast), ct);
        return Accepted(new BatchDownloadResponse(qids.Count, qids));
    }

    [HttpPost("{qid}/retry")]
    [Authorize(Policy = ScopeAuthorization.DownloadsWrite)]
    public async Task<IActionResult> Retry(string qid, CancellationToken ct) =>
        await mediator.Send(new RetryDownloadCommand(CurrentUserId(), qid), ct)
            ? Ok()
            : NotFound();

    // Re-download a known URL (history rows carry DownloadedLink).
    [HttpPost("redownload")]
    [Authorize(Policy = ScopeAuthorization.DownloadsWrite)]
    public async Task<ActionResult<SubmitDownloadResponse>> Redownload(
        [FromBody] RedownloadRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Url))
            return BadRequest(new { message = "URL is required." });
        var qid = await mediator.Send(new SubmitDownloadCommand(
            CurrentUserId(), body.Url, body.Title, body.Artist, body.IsPodcast), ct);
        return Accepted(new SubmitDownloadResponse(qid));
    }

    [HttpDelete("{qid}")]
    [Authorize(Policy = ScopeAuthorization.DownloadsWrite)]
    public async Task<IActionResult> Cancel(string qid, CancellationToken ct) =>
        await mediator.Send(new CancelDownloadCommand(CurrentUserId(), qid), ct)
            ? Ok()
            : NotFound();

    [HttpPost("clear-completed")]
    [Authorize(Policy = ScopeAuthorization.DownloadsWrite)]
    public async Task<ActionResult<ClearCompletedResponse>> ClearCompleted(CancellationToken ct) =>
        Ok(new ClearCompletedResponse(
            await mediator.Send(new ClearCompletedDownloadsCommand(CurrentUserId()), ct)));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record SubmitDownloadRequest(string? Url, string? Title, string? Artist, bool IsPodcast);
public sealed record SubmitDownloadResponse(string Qid);
public sealed record BatchDownloadResponse(int Accepted, IReadOnlyList<string> Qids);
public sealed record RedownloadRequest(string? Url, string? Title, string? Artist, bool IsPodcast);
public sealed record ClearCompletedResponse(int Deleted);

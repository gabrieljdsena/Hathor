using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Discover;
using Hathor.Application.Dtos;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/discover")]
public sealed class DiscoverController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<DiscoverDto>> Get(CancellationToken ct) =>
        Ok(await mediator.Send(new GetDiscoverQuery(CurrentUserId()), ct));

    [HttpPost("refresh")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<DiscoverDto>> Refresh(CancellationToken ct) =>
        Ok(await mediator.Send(new RegenerateDiscoverCommand(CurrentUserId()), ct));

    [HttpGet("insights")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<DiscoverInsightsDto>> Insights(
        [FromQuery] int? windowDays, CancellationToken ct) =>
        Ok(await mediator.Send(new GetDiscoverInsightsQuery(CurrentUserId(), windowDays), ct));

    [HttpGet("evaluation")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<DiscoverEvaluationDto>> Evaluation(
        [FromQuery] int? windowDays,
        [FromQuery] int k = 10,
        CancellationToken ct = default) =>
        Ok(await mediator.Send(
            new GetDiscoverEvaluationQuery(CurrentUserId(), windowDays, k), ct));

    [HttpGet("tuning")]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<DiscoverTuningStatusDto>> Tuning(
        CancellationToken ct) =>
        Ok(await mediator.Send(new GetDiscoverTuningQuery(CurrentUserId()), ct));

    [HttpPost("tuning")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<DiscoverTuningDto>> Tune(
        [FromQuery] int? windowDays,
        [FromQuery] int k = 10,
        CancellationToken ct = default) =>
        Ok(await mediator.Send(
            new TuneDiscoverCommand(CurrentUserId(), windowDays, k), ct));

    [HttpPost("feedback")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> Feedback(
        [FromBody] DiscoverFeedbackRequest request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { message = "Feedback body is required." });
        try
        {
            await mediator.Send(new RecordDiscoverFeedbackCommand(
                CurrentUserId(), request.Title, request.Artist, request.Mbid,
                request.Source, request.Event, request.SeedArtist, request.Score), ct);
            return NoContent();
        }
        catch (FluentValidation.ValidationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

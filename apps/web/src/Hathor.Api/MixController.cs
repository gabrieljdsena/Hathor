using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Mix;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/daily-mix")]
public sealed class MixController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<DailyMixDto>> Get(CancellationToken ct) =>
        Ok(await mediator.Send(new GetDailyMixQuery(CurrentUserId()), ct));

    [HttpPost("regenerate")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<ActionResult<DailyMixDto>> Regenerate(CancellationToken ct) =>
        Ok(await mediator.Send(new RegenerateDailyMixCommand(CurrentUserId()), ct));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

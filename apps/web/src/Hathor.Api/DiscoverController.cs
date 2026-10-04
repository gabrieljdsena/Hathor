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

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.History;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Policy = ScopeAuthorization.LibraryRead)]
public sealed class HistoryController(IMediator mediator) : ControllerBase
{
    [HttpGet("downloads")]
    public async Task<ActionResult<PagedResult<HistoryItemDto>>> Downloads(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetDownloadHistoryQuery(CurrentUserId(), page, pageSize), ct));

    [HttpGet("played-songs")]
    public async Task<ActionResult<PagedResult<HistoryItemDto>>> PlayedSongs(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetPlayedHistoryQuery(CurrentUserId(), page, pageSize), ct));

    [HttpGet("played-playlists")]
    public async Task<ActionResult<PagedResult<PlayedPlaylistItemDto>>> PlayedPlaylists(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetPlayedPlaylistHistoryQuery(CurrentUserId(), page, pageSize), ct));

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

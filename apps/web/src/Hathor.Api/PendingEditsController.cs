using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.Metadata;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

// Deferred metadata edits (PATCH on the playing file stashes instead of
// writing): list what is stashed, discard a stash. Application happens
// automatically on track change (PendingMetadataApplier).
[ApiController]
[Route("api/v1/pending-edits")]
public sealed class PendingEditsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.LibraryRead)]
    public async Task<ActionResult<IReadOnlyList<PendingEditDto>>> List(CancellationToken ct) =>
        Ok(await mediator.Send(new ListPendingEditsQuery(CurrentUserId()), ct));

    [HttpDelete("{file}")]
    [Authorize(Policy = ScopeAuthorization.LibraryWrite)]
    public async Task<IActionResult> Discard(string file, CancellationToken ct) =>
        await mediator.Send(new DiscardPendingEditCommand(CurrentUserId(), file), ct)
            ? NoContent()
            : NotFound();

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

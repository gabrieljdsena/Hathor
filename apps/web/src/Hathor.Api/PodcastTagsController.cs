using System.Security.Claims;
using Hathor.Api.Auth;
using Hathor.Application.Dtos;
using Hathor.Application.PodcastTags;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hathor.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class PodcastTagsController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<IReadOnlyList<PodcastTagDto>>> List(CancellationToken ct) =>
        Ok(await mediator.Send(new ListPodcastTagsQuery(CurrentUserId()), ct));

    [HttpGet("map")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsRead)]
    public async Task<ActionResult<Dictionary<string, List<long>>>> Map(CancellationToken ct) =>
        Ok(await mediator.Send(new GetPodcastTagMapQuery(CurrentUserId()), ct));

    [HttpPost]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<ActionResult<PodcastTagCreatedDto>> Create(
        [FromBody] CreatePodcastTagRequest body, CancellationToken ct)
    {
        var id = await mediator.Send(new CreatePodcastTagCommand(CurrentUserId(), body.Name ?? ""), ct);
        // Desktop new_podcast_tag: -1 on blank, existing id on duplicates.
        return id == -1 ? BadRequest(new { message = "Tag name can't be blank." }) : Ok(new PodcastTagCreatedDto(id));
    }

    [HttpPut("{id:long}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> Rename(long id, [FromBody] RenamePodcastTagRequest body, CancellationToken ct)
    {
        var tag = await mediator.Send(new ListPodcastTagsQuery(CurrentUserId()), ct);
        if (tag.All(t => t.Id != id)) return NotFound();
        // Desktop rename: false on blank/duplicate names.
        return await mediator.Send(new RenamePodcastTagCommand(CurrentUserId(), id, body.Name ?? ""), ct)
            ? Ok()
            : Conflict(new { message = "Tag name is blank or already used." });
    }

    [HttpDelete("{id:long}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct) =>
        await mediator.Send(new DeletePodcastTagCommand(CurrentUserId(), id), ct)
            ? NoContent()
            : NotFound();

    [HttpPost("{id:long}/episodes")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> Assign(
        long id, [FromBody] PodcastTagEpisodeRequest body, CancellationToken ct) =>
        await mediator.Send(new AssignPodcastTagCommand(CurrentUserId(), body.File, id), ct)
            ? Ok()
            : NotFound();

    [HttpDelete("{id:long}/episodes/{file}")]
    [Authorize(Policy = ScopeAuthorization.PlaylistsWrite)]
    public async Task<IActionResult> Unassign(long id, string file, CancellationToken ct)
    {
        await mediator.Send(new UnassignPodcastTagCommand(CurrentUserId(), file, id), ct);
        return NoContent();
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record CreatePodcastTagRequest(string? Name);
public sealed record RenamePodcastTagRequest(string? Name);
public sealed record PodcastTagCreatedDto(long Id);
public sealed record PodcastTagEpisodeRequest(string File);

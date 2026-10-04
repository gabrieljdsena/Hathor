using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.PodcastTags;

// Podcast tags mirror playlists (desktop Podcast_Tags + Podcast_Tag_Links).
// Create returns the existing id on duplicates and -1 on blank names;
// rename returns false on blank/duplicate names (desktop new/rename semantics).

public sealed record ListPodcastTagsQuery(Guid UserId) : IRequest<IReadOnlyList<PodcastTagDto>>;
public sealed record GetPodcastTagMapQuery(Guid UserId) : IRequest<Dictionary<string, List<long>>>;

public sealed class PodcastTagQueryHandlers(IPodcastTagReadModel reads) :
    IRequestHandler<ListPodcastTagsQuery, IReadOnlyList<PodcastTagDto>>,
    IRequestHandler<GetPodcastTagMapQuery, Dictionary<string, List<long>>>
{
    public Task<IReadOnlyList<PodcastTagDto>> Handle(ListPodcastTagsQuery q, CancellationToken ct) =>
        reads.ListWithCountsAsync(q.UserId, ct);
    public Task<Dictionary<string, List<long>>> Handle(GetPodcastTagMapQuery q, CancellationToken ct) =>
        reads.GetTagMapAsync(q.UserId, ct);
}

public sealed record CreatePodcastTagCommand(Guid UserId, string Name) : IRequest<long>;

public sealed class CreatePodcastTagHandler(IPodcastTagRepository tags)
    : IRequestHandler<CreatePodcastTagCommand, long>
{
    public async Task<long> Handle(CreatePodcastTagCommand cmd, CancellationToken ct)
    {
        var clean = (cmd.Name ?? "").Trim();
        if (clean.Length == 0) return -1;
        var existing = await tags.GetByNameAsync(cmd.UserId, clean, ct);
        if (existing is not null) return existing.Id;
        var tag = new PodcastTag { UserId = cmd.UserId, Name = clean };
        await tags.AddAsync(tag, ct);
        await tags.SaveChangesAsync(ct);
        return tag.Id;
    }
}

public sealed record RenamePodcastTagCommand(Guid UserId, long Id, string Name) : IRequest<bool>;

public sealed class RenamePodcastTagHandler(IPodcastTagRepository tags)
    : IRequestHandler<RenamePodcastTagCommand, bool>
{
    public async Task<bool> Handle(RenamePodcastTagCommand cmd, CancellationToken ct)
    {
        var clean = (cmd.Name ?? "").Trim();
        if (clean.Length == 0) return false;
        var tag = await tags.GetByIdAsync(cmd.UserId, cmd.Id, ct);
        if (tag is null) return false;
        var clash = await tags.GetByNameAsync(cmd.UserId, clean, ct);
        if (clash is not null && clash.Id != tag.Id) return false;
        tag.Name = clean;
        await tags.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record DeletePodcastTagCommand(Guid UserId, long Id) : IRequest<bool>;

public sealed class DeletePodcastTagHandler(
    IPodcastTagRepository tags, ITombstoneRepository tombstones)
    : IRequestHandler<DeletePodcastTagCommand, bool>
{
    public async Task<bool> Handle(DeletePodcastTagCommand cmd, CancellationToken ct)
    {
        var tag = await tags.GetByIdAsync(cmd.UserId, cmd.Id, ct);
        if (tag is null) return false;
        await tags.RemoveWithLinksAsync(tag, ct);
        await tombstones.RecordAsync(cmd.UserId, "podcast_tags", cmd.Id.ToString(), ct);
        await tags.SaveChangesAsync(ct);
        await tombstones.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record AssignPodcastTagCommand(Guid UserId, string File, long TagId) : IRequest<bool>;
public sealed record UnassignPodcastTagCommand(Guid UserId, string File, long TagId) : IRequest<bool>;

public sealed class AssignPodcastTagHandler(IPodcastTagRepository tags)
    : IRequestHandler<AssignPodcastTagCommand, bool>
{
    public async Task<bool> Handle(AssignPodcastTagCommand cmd, CancellationToken ct)
    {
        if (await tags.GetByIdAsync(cmd.UserId, cmd.TagId, ct) is null) return false;
        await tags.AssignAsync(cmd.UserId, cmd.File, cmd.TagId, ct);
        await tags.SaveChangesAsync(ct);
        return true;
    }
}

public sealed class UnassignPodcastTagHandler(IPodcastTagRepository tags)
    : IRequestHandler<UnassignPodcastTagCommand, bool>
{
    public async Task<bool> Handle(UnassignPodcastTagCommand cmd, CancellationToken ct)
    {
        await tags.UnassignAsync(cmd.UserId, cmd.File, cmd.TagId, ct);
        await tags.SaveChangesAsync(ct);
        return true;
    }
}

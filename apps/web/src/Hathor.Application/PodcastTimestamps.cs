using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.PodcastTimestamps;

// Chapter marks ("timestamps") inside a podcast episode: start offset in
// seconds, display name, optional end offset. Validation mirrors the
// podcast-tags style (explicit result unions, no exceptions for input
// errors — the controller maps them to 400/404).

public sealed record ListPodcastTimestampsQuery(Guid UserId, string File)
    : IRequest<IReadOnlyList<PodcastTimestampDto>?>;

public sealed class ListPodcastTimestampsHandler(
    ILibraryStorage storage,
    IPodcastTimestampRepository timestamps) : IRequestHandler<ListPodcastTimestampsQuery, IReadOnlyList<PodcastTimestampDto>?>
{
    public async Task<IReadOnlyList<PodcastTimestampDto>?> Handle(
        ListPodcastTimestampsQuery q, CancellationToken ct)
    {
        if (!storage.PodcastExists(q.UserId, q.File)) return null;
        var rows = await timestamps.ListAsync(q.UserId, q.File, ct);
        return rows.Select(ToDto).ToList();
    }

    internal static PodcastTimestampDto ToDto(PodcastTimestamp t) =>
        new(t.Id, t.PodcastFile, t.Name, t.StartSecs, t.EndSecs);
}

public abstract record PodcastTimestampWriteResult
{
    public sealed record Created(PodcastTimestampDto Timestamp) : PodcastTimestampWriteResult;
    public sealed record Updated(PodcastTimestampDto Timestamp) : PodcastTimestampWriteResult;
    public sealed record EpisodeNotFound : PodcastTimestampWriteResult;
    public sealed record TimestampNotFound : PodcastTimestampWriteResult;
    public sealed record Invalid(string Message) : PodcastTimestampWriteResult;

    private PodcastTimestampWriteResult() { }
}

public sealed record CreatePodcastTimestampCommand(
    Guid UserId, string File, string Name, double StartSecs, double? EndSecs)
    : IRequest<PodcastTimestampWriteResult>;

public sealed class CreatePodcastTimestampHandler(
    ILibraryStorage storage,
    IPodcastTimestampRepository timestamps) : IRequestHandler<CreatePodcastTimestampCommand, PodcastTimestampWriteResult>
{
    public async Task<PodcastTimestampWriteResult> Handle(
        CreatePodcastTimestampCommand cmd, CancellationToken ct)
    {
        if (!storage.PodcastExists(cmd.UserId, cmd.File))
            return new PodcastTimestampWriteResult.EpisodeNotFound();
        var error = Validate(cmd.Name, cmd.StartSecs, cmd.EndSecs);
        if (error is not null) return new PodcastTimestampWriteResult.Invalid(error);
        var row = new PodcastTimestamp
        {
            UserId = cmd.UserId,
            PodcastFile = cmd.File,
            Name = cmd.Name.Trim(),
            StartSecs = cmd.StartSecs,
            EndSecs = cmd.EndSecs,
        };
        await timestamps.AddAsync(row, ct);
        await timestamps.SaveChangesAsync(ct);
        return new PodcastTimestampWriteResult.Created(
            ListPodcastTimestampsHandler.ToDto(row));
    }

    internal static string? Validate(string name, double startSecs, double? endSecs)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Timestamp name can't be blank.";
        if (name.Trim().Length > 255) return "Timestamp name is too long (max 255).";
        if (double.IsNaN(startSecs) || double.IsInfinity(startSecs) || startSecs < 0)
            return "Start time must be zero or later.";
        if (endSecs.HasValue &&
            (double.IsNaN(endSecs.Value) || double.IsInfinity(endSecs.Value) || endSecs.Value <= startSecs))
            return "End time must be later than the start time.";
        return null;
    }
}

public sealed record UpdatePodcastTimestampCommand(
    Guid UserId, string File, long Id, string Name, double StartSecs, double? EndSecs)
    : IRequest<PodcastTimestampWriteResult>;

public sealed class UpdatePodcastTimestampHandler(
    ILibraryStorage storage,
    IPodcastTimestampRepository timestamps) : IRequestHandler<UpdatePodcastTimestampCommand, PodcastTimestampWriteResult>
{
    public async Task<PodcastTimestampWriteResult> Handle(
        UpdatePodcastTimestampCommand cmd, CancellationToken ct)
    {
        if (!storage.PodcastExists(cmd.UserId, cmd.File))
            return new PodcastTimestampWriteResult.EpisodeNotFound();
        var row = await timestamps.GetAsync(cmd.UserId, cmd.File, cmd.Id, ct);
        if (row is null) return new PodcastTimestampWriteResult.TimestampNotFound();
        var error = CreatePodcastTimestampHandler.Validate(cmd.Name, cmd.StartSecs, cmd.EndSecs);
        if (error is not null) return new PodcastTimestampWriteResult.Invalid(error);
        row.Name = cmd.Name.Trim();
        row.StartSecs = cmd.StartSecs;
        row.EndSecs = cmd.EndSecs;
        await timestamps.SaveChangesAsync(ct);
        return new PodcastTimestampWriteResult.Updated(
            ListPodcastTimestampsHandler.ToDto(row));
    }
}

public sealed record DeletePodcastTimestampCommand(Guid UserId, string File, long Id)
    : IRequest<bool>;

public sealed class DeletePodcastTimestampHandler(
    ILibraryStorage storage,
    IPodcastTimestampRepository timestamps) : IRequestHandler<DeletePodcastTimestampCommand, bool>
{
    public async Task<bool> Handle(DeletePodcastTimestampCommand cmd, CancellationToken ct)
    {
        // Missing episode and missing id are both 404 (same as tag unassign).
        if (!storage.PodcastExists(cmd.UserId, cmd.File)) return false;
        var row = await timestamps.GetAsync(cmd.UserId, cmd.File, cmd.Id, ct);
        if (row is null) return false;
        timestamps.Remove(row);
        await timestamps.SaveChangesAsync(ct);
        return true;
    }
}

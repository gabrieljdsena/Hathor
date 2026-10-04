using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Ingest;

// Download submission + management. Execution lives in IDownloadQueue
// (Infrastructure singleton pump); handlers only validate + delegate.

public sealed record SubmitDownloadCommand(
    Guid UserId, string? Url, string? Title, string? Artist, bool IsPodcast)
    : IRequest<string>;

public sealed class SubmitDownloadValidator : AbstractValidator<SubmitDownloadCommand>
{
    public SubmitDownloadValidator()
    {
        RuleFor(x => x).Must(x =>
            !string.IsNullOrWhiteSpace(x.Url) || !string.IsNullOrWhiteSpace(x.Title))
            .WithMessage("URL or title is required.");
    }
}

public sealed class SubmitDownloadHandler(IDownloadQueue queue)
    : IRequestHandler<SubmitDownloadCommand, string>
{
    public Task<string> Handle(SubmitDownloadCommand cmd, CancellationToken ct) =>
        queue.SubmitAsync(cmd.UserId,
            (cmd.Url ?? "").Trim(),
            (cmd.Title ?? "").Trim(),
            string.IsNullOrWhiteSpace(cmd.Artist) ? null : cmd.Artist.Trim(),
            cmd.IsPodcast, ct);
}

public sealed record BatchDownloadCommand(Guid UserId, IReadOnlyList<string> Lines, bool IsPodcast)
    : IRequest<IReadOnlyList<string>>;

public sealed class BatchDownloadHandler(IDownloadQueue queue)
    : IRequestHandler<BatchDownloadCommand, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> Handle(BatchDownloadCommand cmd, CancellationToken ct)
    {
        var qids = new List<string>();
        foreach (var line in cmd.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Trim())))
        {
            var qid = await queue.SubmitAsync(cmd.UserId, line.Trim(), line.Trim(), null, cmd.IsPodcast, ct);
            qids.Add(qid);
            // Desktop batch courtesy delay so searches don't get throttled.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        }
        return qids;
    }
}

public sealed record RetryDownloadCommand(Guid UserId, string Qid) : IRequest<bool>;
public sealed record CancelDownloadCommand(Guid UserId, string Qid) : IRequest<bool>;
public sealed record ClearCompletedDownloadsCommand(Guid UserId) : IRequest<int>;

// Ownership pre-check for the "already in your library?" download confirm:
// normalized title+artist match against live library files, or not-owned.
public sealed record CheckDownloadQuery(Guid UserId, string Title, string? Artist)
    : IRequest<OwnedCheckDto>;

public sealed class CheckDownloadHandler(ISongReadModel songs)
    : IRequestHandler<CheckDownloadQuery, OwnedCheckDto>
{
    public async Task<OwnedCheckDto> Handle(CheckDownloadQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Title)) return new OwnedCheckDto(false, null);
        var file = await songs.FindFileByMetadataAsync(q.UserId, q.Title, q.Artist, ct);
        return new OwnedCheckDto(file is not null, file);
    }
}

public sealed class RetryDownloadHandler(IDownloadQueue queue)
    : IRequestHandler<RetryDownloadCommand, bool>
{
    public Task<bool> Handle(RetryDownloadCommand cmd, CancellationToken ct) =>
        queue.RetryAsync(cmd.UserId, cmd.Qid, ct);
}

public sealed class CancelDownloadHandler(IDownloadQueue queue)
    : IRequestHandler<CancelDownloadCommand, bool>
{
    public Task<bool> Handle(CancelDownloadCommand cmd, CancellationToken ct) =>
        queue.CancelAsync(cmd.UserId, cmd.Qid, ct);
}

public sealed class ClearCompletedDownloadsHandler(IDownloadJobRepository jobs)
    : IRequestHandler<ClearCompletedDownloadsCommand, int>
{
    public async Task<int> Handle(ClearCompletedDownloadsCommand cmd, CancellationToken ct)
    {
        var n = await jobs.DeleteTerminalAsync(cmd.UserId, ct);
        await jobs.SaveChangesAsync(ct);
        return n;
    }
}

public sealed record ListDownloadJobsQuery(Guid UserId, int Limit = 15)
    : IRequest<IReadOnlyList<DownloadJobDto>>;
public sealed record GetDownloadJobQuery(Guid UserId, string Qid) : IRequest<DownloadJobDto?>;
public sealed record ActiveDownloadCountQuery(Guid UserId) : IRequest<int>;

public sealed class DownloadJobQueryHandlers(IDownloadJobRepository jobs) :
    IRequestHandler<ListDownloadJobsQuery, IReadOnlyList<DownloadJobDto>>,
    IRequestHandler<GetDownloadJobQuery, DownloadJobDto?>,
    IRequestHandler<ActiveDownloadCountQuery, int>
{
    public async Task<IReadOnlyList<DownloadJobDto>> Handle(ListDownloadJobsQuery q, CancellationToken ct) =>
        (await jobs.RecentAsync(q.UserId, Math.Clamp(q.Limit, 1, 100), ct))
            .Select(DownloadJobMaps.ToDto).ToList();

    public async Task<DownloadJobDto?> Handle(GetDownloadJobQuery q, CancellationToken ct)
    {
        var job = await jobs.GetByQidAsync(q.Qid, ct);
        return job is null || job.UserId != q.UserId ? null : DownloadJobMaps.ToDto(job);
    }

    public Task<int> Handle(ActiveDownloadCountQuery q, CancellationToken ct) =>
        jobs.CountActiveAsync(q.UserId, ct);
}

// Queue pump contract (Infrastructure singleton). Submit returns the qid;
// dedupe returns the in-flight qid for the same URL (Android behavior).
// targetFile pins the exact disk filename (pull: the remote `file`);
public interface IDownloadQueue
{
    Task<string> SubmitAsync(Guid userId, string url, string title, string? artist,
        bool isPodcast, CancellationToken ct = default, string? targetFile = null);
    Task<bool> RetryAsync(Guid userId, string qid, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid userId, string qid, CancellationToken ct = default);
    void Kick();
}

public static class DownloadJobMaps
{
    public static DownloadJobDto ToDto(Domain.Entities.DownloadJob job) => new(
        job.Qid, job.Url, string.IsNullOrWhiteSpace(job.Title) ? job.Url ?? "" : job.Title!,
        job.Artist, job.Status, job.Progress, job.Error, job.Filename, job.IsPodcast);
}

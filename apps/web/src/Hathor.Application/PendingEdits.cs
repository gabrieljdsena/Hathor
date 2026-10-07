using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Hathor.Application.Metadata;

// Deferred metadata edits: PATCH on the currently-playing file stashes the
// payload (playback stays gapless) and this applier writes it once the file
// stops being current. Wired into the play/next/prev handlers — never throws
// (a failed apply keeps the row for the next track change instead of
// breaking playback), and drops rows whose file vanished meanwhile.
public sealed class PendingMetadataApplier(
    IPendingEditRepository pending,
    IMetadataWriter writer,
    ILibraryStorage storage,
    ISongRecordRepository songRecords,
    ISongReadModel songs,
    IPodcastRecordRepository podcastRecords,
    IPodcastReadModel podcasts,
    IPlaybackStateRepository playback,
    ILogger<PendingMetadataApplier> log)
{
    public async Task<bool> ApplyForFileAsync(Guid userId, string? oldFile, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(oldFile)) return false;
        try
        {
            var state = await playback.GetOrCreateAsync(userId, ct);
            if (string.Equals(state.CurrentFile, oldFile, StringComparison.OrdinalIgnoreCase))
                return false;
            var edit = await pending.GetAsync(userId, oldFile, ct);
            if (edit is null) return false;
            var exists = edit.IsPodcast
                ? storage.PodcastExists(userId, edit.File)
                : storage.SongExists(userId, edit.File);
            if (exists)
            {
                if (edit.IsPodcast)
                    await ApplyPodcastAsync(userId, edit, ct);
                else
                    await ApplySongAsync(userId, edit, ct);
            }
            await pending.DeleteAsync(userId, oldFile, ct);
            await pending.SaveChangesAsync(ct);
            return exists;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Deferred metadata apply failed for {File}, keeping for next track change", oldFile);
            return false;
        }
    }

    private async Task ApplySongAsync(Guid userId, Domain.Entities.PendingMetadataEdit edit, CancellationToken ct)
    {
        await writer.WriteSongAsync(userId, edit.File,
            edit.Title, edit.Artist, edit.Album, edit.Year, edit.Genre, edit.CoverArt, ct);
        var updated = await songs.GetByFileAsync(userId, edit.File, includeCover: true, ct);
        if (updated is not null)
            await songRecords.UpsertMetadataAsync(userId, edit.File, updated.Title, updated.Artist,
                updated.Album, updated.Year, updated.Genre, updated.Duration, ct);
        await songRecords.SaveChangesAsync(ct);
    }

    private async Task ApplyPodcastAsync(Guid userId, Domain.Entities.PendingMetadataEdit edit, CancellationToken ct)
    {
        await writer.WritePathAsync(storage.PodcastPath(userId, edit.File),
            edit.Title, edit.Artist, null, null, null, edit.CoverArt, ct);
        var updated = await podcasts.GetByFileAsync(userId, edit.File, ct);
        if (updated is not null)
            await podcastRecords.EnsureAsync(userId, edit.File, updated.Title, updated.Artist, null, ct);
        await podcastRecords.SaveChangesAsync(ct);
    }
}

public sealed record ListPendingEditsQuery(Guid UserId) : IRequest<IReadOnlyList<PendingEditDto>>;

public sealed class ListPendingEditsHandler(IPendingEditRepository pending)
    : IRequestHandler<ListPendingEditsQuery, IReadOnlyList<PendingEditDto>>
{
    public async Task<IReadOnlyList<PendingEditDto>> Handle(ListPendingEditsQuery q, CancellationToken ct)
    {
        var rows = await pending.ListAsync(q.UserId, ct);
        return rows.Select(p => new PendingEditDto(p.File, p.IsPodcast, p.Title, p.Artist,
            p.Album, p.Year, p.Genre, p.CoverArt, p.CreatedUtc)).ToList();
    }
}

public sealed record DiscardPendingEditCommand(Guid UserId, string File) : IRequest<bool>;

public sealed class DiscardPendingEditHandler(IPendingEditRepository pending)
    : IRequestHandler<DiscardPendingEditCommand, bool>
{
    public async Task<bool> Handle(DiscardPendingEditCommand cmd, CancellationToken ct)
    {
        if (!await pending.DeleteAsync(cmd.UserId, cmd.File, ct)) return false;
        await pending.SaveChangesAsync(ct);
        return true;
    }
}

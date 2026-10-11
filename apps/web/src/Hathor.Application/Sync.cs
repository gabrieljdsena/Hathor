using System.Text.Json;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.Sync;

// Library snapshot exchange (desktop remote-DB pull/push, adapted to the
// multi-user server: export a snapshot, import/merge one). Sections are
// nullable so older clients lacking tables (podcasts, mix, tags) still merge
// (desktop guarded-table behavior).

public sealed record SongRowDto(
    string File, string? DownloadedLink, string Title, DateTime DateDownloadUtc, string? Artist,
    double? LoudnessDb = null);
public sealed record PodcastRowDto(
    string File, string? DownloadedLink, string Title, DateTime DateDownloadUtc, string? Artist);
public sealed record PlaylistRowDto(long Id, string Title, string? Description, string? Thumbnail);
public sealed record SongLinkRowDto(long Id, string SongFile, long PlaylistId, DateTime DateAddedUtc);
public sealed record TagRowDto(long Id, string Name);
public sealed record TagLinkRowDto(long Id, string PodcastFile, long TagId);
public sealed record LyricRowDto(long Id, string SongFile, string? LyricsJson, int OffsetMs = 0);
public sealed record MusicHistoryRowDto(long Id, string SongFile, DateTime DatePlayedUtc);
public sealed record PlaylistHistoryRowDto(long Id, long PlaylistId, DateTime DatePlayedUtc);
public sealed record MixRowDto(string MixDate, string SongFilesJson);
public sealed record PodcastChapterRowDto(
    long Id, string PodcastFile, string Name, double StartSecs, double? EndSecs);
public sealed record DeletionRowDto(string TableName, string RowKey);

// Resume-across-devices spot: latest foreign playback state. UserKey is
// the writer's device id (one row per device, last-writer-wins). Rows
// older than 30 days are ignored. QueueFiles is the writer's Up Next
// snapshot (current track first, capped) so a tap restores the foreign
// queue instead of collapsing it to a single song.
public sealed record PlaybackSpotDto(
    string UserKey, string File, double PositionSec, bool IsPodcast,
    string? Device, DateTime UpdatedUtc,
    List<string> QueueFiles,
    QueueSourceDto? QueueSource);

public static class PlaybackKeys
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
}

public sealed record SyncSnapshot(
    List<SongRowDto>? Songs,
    List<PodcastRowDto>? Podcasts,
    List<PlaylistRowDto>? Playlists,
    List<SongLinkRowDto>? SongLinks,
    List<TagRowDto>? PodcastTags,
    List<TagLinkRowDto>? PodcastTagLinks,
    List<LyricRowDto>? Lyrics,
    List<MusicHistoryRowDto>? MusicHistory,
    List<PlaylistHistoryRowDto>? PlaylistHistory,
    List<MixRowDto>? DailyMix,
    List<DeletionRowDto>? Deletions,
    List<PodcastChapterRowDto>? PodcastChapters = null);

public sealed record SyncSummary(
    int Songs, int Podcasts, int Playlists, int SongLinks,
    int PodcastTags, int PodcastTagLinks, int Lyrics,
    int MusicHistory, int PlaylistHistory, int DailyMix, int Deletions);

// Import outcome: the merge counts plus catalog files whose bytes are
// missing on disk — the pushing device uploads those via
// PUT /sync/files/{file} to complete the push.
public sealed record ImportResult(SyncSummary Summary, List<string> MissingFiles);

// Incremental delta (GET /sync/delta): the changed-sections snapshot plus
// the opaque cursor to send back next time. Sections reuse SyncSnapshot
// (null = nothing changed there); history stays id-cursored, tombstones
// ride in Deletions filtered by DeletedAtUtc.
public sealed record SyncDelta(string Cursor, SyncSnapshot Snapshot);

// Resume spot: one row per (user, device) in Playback_Spots, last write
// wins. Pushes happen fire-and-forget on pause (never blocking playback);
// latest surfaces the newest spot from a DIFFERENT device so a browser
// never echoes its own paused track back as a resume bento. Nothing
// auto-plays — the bento is an explicit tap.
public sealed record PushPlaybackStateCommand(
    Guid UserId, string DeviceId, string? DeviceLabel, string? File,
    double PositionSec, bool IsPodcast,
    List<string>? QueueFiles = null,
    QueueSourceDto? QueueSource = null) : IRequest<bool>;

public sealed class PushPlaybackStateHandler(
    Domain.Repositories.IPlaybackSpotRepository spots)
    : IRequestHandler<PushPlaybackStateCommand, bool>
{
    internal const int MaxQueueFiles = 200;

    public async Task<bool> Handle(PushPlaybackStateCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.DeviceId) || string.IsNullOrWhiteSpace(cmd.File))
            return false;
        var file = cmd.File.Trim();
        var files = (cmd.QueueFiles ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Where(f => f.Length <= 255)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxQueueFiles)
            .ToList();
        // Current track leads the snapshot so ReplaceQueue splits correctly.
        files.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        files.Insert(0, file);
        string? sourceJson = null;
        if (cmd.QueueSource is not null && !string.IsNullOrWhiteSpace(cmd.QueueSource.Type))
        {
            var type = cmd.QueueSource.Type.Trim();
            if (type.Length is > 0 and <= 64)
                sourceJson = JsonSerializer.Serialize(
                    new QueueSourceDto(type, cmd.QueueSource.Id?.Trim()));
        }
        await spots.UpsertAsync(new Domain.Entities.PlaybackSpot
        {
            UserId = cmd.UserId,
            DeviceId = cmd.DeviceId.Trim(),
            File = file,
            PositionSec = Math.Max(0, cmd.PositionSec),
            IsPodcast = cmd.IsPodcast,
            DeviceLabel = string.IsNullOrWhiteSpace(cmd.DeviceLabel)
                ? null
                : cmd.DeviceLabel.Trim(),
            UpdatedUtc = DateTime.UtcNow,
            QueueFiles = files,
            QueueSourceJson = sourceJson,
        }, ct);
        await spots.SaveChangesAsync(ct);
        return true;
    }
}

public sealed record GetLatestPlaybackQuery(Guid UserId, string? ExcludeDeviceId)
    : IRequest<PlaybackSpotDto?>;

public sealed class GetLatestPlaybackHandler(
    Domain.Repositories.IPlaybackSpotRepository spots)
    : IRequestHandler<GetLatestPlaybackQuery, PlaybackSpotDto?>
{
    public async Task<PlaybackSpotDto?> Handle(GetLatestPlaybackQuery q, CancellationToken ct)
    {
        var spot = await spots.LatestForeignAsync(
            q.UserId, q.ExcludeDeviceId, DateTime.UtcNow - PlaybackKeys.MaxAge, ct);
        if (spot is null) return null;
        QueueSourceDto? source = null;
        if (!string.IsNullOrWhiteSpace(spot.QueueSourceJson))
        {
            try
            {
                source = JsonSerializer.Deserialize<QueueSourceDto>(spot.QueueSourceJson);
            }
            catch (JsonException)
            {
                source = null;
            }
        }
        return new PlaybackSpotDto(spot.DeviceId, spot.File, spot.PositionSec,
            spot.IsPodcast, spot.DeviceLabel, Domain.UtcIso.EnsureUtc(spot.UpdatedUtc),
            spot.QueueFiles ?? [], source);
    }
}

public sealed record ExportSnapshotQuery(Guid UserId, long SinceId = 0) : IRequest<SyncSnapshot>;
public sealed record ImportSnapshotCommand(Guid UserId, SyncSnapshot Snapshot) : IRequest<ImportResult>;
public sealed record GetDeltaQuery(Guid UserId, string Cursor = "") : IRequest<SyncDelta>;

public sealed class ExportSnapshotHandler(ISyncService sync)
    : IRequestHandler<ExportSnapshotQuery, SyncSnapshot>
{
    public Task<SyncSnapshot> Handle(ExportSnapshotQuery q, CancellationToken ct) =>
        sync.ExportAsync(q.UserId, q.SinceId, ct);
}

public sealed class ImportSnapshotHandler(ISyncService sync)
    : IRequestHandler<ImportSnapshotCommand, ImportResult>
{
    public Task<ImportResult> Handle(ImportSnapshotCommand cmd, CancellationToken ct) =>
        sync.ImportAsync(cmd.UserId, cmd.Snapshot, ct);
}

public sealed class GetDeltaHandler(ISyncService sync)
    : IRequestHandler<GetDeltaQuery, SyncDelta>
{
    public Task<SyncDelta> Handle(GetDeltaQuery q, CancellationToken ct) =>
        sync.GetDeltaAsync(q.UserId, q.Cursor, ct);
}

public sealed record SaveSyncFileCommand(Guid UserId, string File, bool IsPodcast, byte[] Bytes)
    : IRequest<bool>;

public sealed class SaveSyncFileHandler(ISyncService sync)
    : IRequestHandler<SaveSyncFileCommand, bool>
{
    public async Task<bool> Handle(SaveSyncFileCommand cmd, CancellationToken ct)
    {
        await sync.SaveFileAsync(cmd.UserId, cmd.File, cmd.IsPodcast, cmd.Bytes, ct);
        return true;
    }
}

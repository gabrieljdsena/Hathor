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

// Resume-across-devices spot: latest foreign playback state. user_key is
// per writer (web:{userId} / desktop) because the remote is one global
// namespace with last-writer-wins. Rows older than 30 days are ignored.
public sealed record PlaybackSpotDto(
    string UserKey, string File, double PositionSec, bool IsPodcast,
    string? Device, DateTime UpdatedUtc);

public static class PlaybackKeys
{
    public static string ForWeb(Guid userId) => $"web:{userId:D}";
    public const string Desktop = "desktop";
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

// Resume spot: the player's persisted state IS the spot now (the shared
// remote playback_state table is gone with the remote DB). Push is a
// no-op success — the local row is always current; latest surfaces it
// (fresh only) so the UI resume pill keeps working single-user.
// Pushes happen fire-and-forget on pause (never blocking playback);
// latest only surfaces an affordance — nothing auto-plays.
public sealed record PushPlaybackStateCommand(Guid UserId) : IRequest<bool>;

public sealed class PushPlaybackStateHandler(
    Domain.Repositories.IPlaybackStateRepository playback)
    : IRequestHandler<PushPlaybackStateCommand, bool>
{
    public async Task<bool> Handle(PushPlaybackStateCommand cmd, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(cmd.UserId, ct);
        return state.CurrentFile is not null && !state.FirstPlay;
    }
}

public sealed record GetLatestPlaybackQuery(Guid UserId) : IRequest<PlaybackSpotDto?>;

public sealed class GetLatestPlaybackHandler(
    Domain.Repositories.IPlaybackStateRepository playback)
    : IRequestHandler<GetLatestPlaybackQuery, PlaybackSpotDto?>
{
    public async Task<PlaybackSpotDto?> Handle(GetLatestPlaybackQuery q, CancellationToken ct)
    {
        var state = await playback.GetOrCreateAsync(q.UserId, ct);
        if (state.CurrentFile is null || state.FirstPlay) return null;
        // Stale spots (older than the old 30-day remote rule) stay hidden.
        if (DateTime.UtcNow - state.LastPlayUtc > PlaybackKeys.MaxAge) return null;
        return new PlaybackSpotDto(PlaybackKeys.ForWeb(q.UserId), state.CurrentFile,
            state.EstimatedPositionSec(DateTime.UtcNow), state.CurrentIsPodcast,
            "Web", state.LastPlayUtc);
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

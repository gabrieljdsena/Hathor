using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.Sync;

// Library snapshot exchange (desktop remote-DB pull/push, adapted to the
// multi-user server: export a snapshot, import/merge one). Sections are
// nullable so older clients lacking tables (podcasts, mix, tags) still merge
// (desktop guarded-table behavior).

public sealed record SongRowDto(
    string File, string? DownloadedLink, string Title, DateTime DateDownloadUtc, string? Artist);
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
public sealed record DeletionRowDto(string TableName, string RowKey);

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
    List<DeletionRowDto>? Deletions);

public sealed record SyncSummary(
    int Songs, int Podcasts, int Playlists, int SongLinks,
    int PodcastTags, int PodcastTagLinks, int Lyrics,
    int MusicHistory, int PlaylistHistory, int DailyMix, int Deletions);

// Per-library remote pull (desktop sync_remote_to_local_and_download split
// in two): new rows merged + missing files queued for download.
public sealed record RemotePullResult(int Added, int DownloadsStarted, string Message);

public sealed record PullSongsCommand(Guid UserId) : IRequest<RemotePullResult>;
public sealed record PullPodcastsCommand(Guid UserId) : IRequest<RemotePullResult>;

public sealed class PullSongsHandler(Ports.IRemotePullService pull)
    : IRequestHandler<PullSongsCommand, RemotePullResult>
{
    public Task<RemotePullResult> Handle(PullSongsCommand cmd, CancellationToken ct) =>
        pull.PullSongsAsync(cmd.UserId, ct);
}

public sealed class PullPodcastsHandler(Ports.IRemotePullService pull)
    : IRequestHandler<PullPodcastsCommand, RemotePullResult>
{
    public Task<RemotePullResult> Handle(PullPodcastsCommand cmd, CancellationToken ct) =>
        pull.PullPodcastsAsync(cmd.UserId, ct);
}

// Per-library remote push (desktop DatabaseSync push, split in two).
public sealed record RemotePushResult(int Rows, string Message);

public sealed record PushSongsCommand(Guid UserId) : IRequest<RemotePushResult>;
public sealed record PushPodcastsCommand(Guid UserId) : IRequest<RemotePushResult>;

public sealed class PushSongsHandler(Ports.IRemotePushService push)
    : IRequestHandler<PushSongsCommand, RemotePushResult>
{
    public Task<RemotePushResult> Handle(PushSongsCommand cmd, CancellationToken ct) =>
        push.PushSongsAsync(cmd.UserId, ct);
}

public sealed class PushPodcastsHandler(Ports.IRemotePushService push)
    : IRequestHandler<PushPodcastsCommand, RemotePushResult>
{
    public Task<RemotePushResult> Handle(PushPodcastsCommand cmd, CancellationToken ct) =>
        push.PushPodcastsAsync(cmd.UserId, ct);
}

public sealed record ExportSnapshotQuery(Guid UserId, long SinceId = 0) : IRequest<SyncSnapshot>;
public sealed record ImportSnapshotCommand(Guid UserId, SyncSnapshot Snapshot) : IRequest<SyncSummary>;

public sealed class ExportSnapshotHandler(ISyncService sync)
    : IRequestHandler<ExportSnapshotQuery, SyncSnapshot>
{
    public Task<SyncSnapshot> Handle(ExportSnapshotQuery q, CancellationToken ct) =>
        sync.ExportAsync(q.UserId, q.SinceId, ct);
}

public sealed class ImportSnapshotHandler(ISyncService sync)
    : IRequestHandler<ImportSnapshotCommand, SyncSummary>
{
    public Task<SyncSummary> Handle(ImportSnapshotCommand cmd, CancellationToken ct) =>
        sync.ImportAsync(cmd.UserId, cmd.Snapshot, ct);
}

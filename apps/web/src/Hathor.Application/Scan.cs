using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Library;

// Folder → DB rescans (desktop sync_local_songs/podcasts_to_db):
// every local file gets a row (mtime as download date for new rows),
// changed title/artist update the row. Returns "N added, M updated."
public sealed record ScanSongsCommand(Guid UserId) : IRequest<ScanResult>;
public sealed record ScanPodcastsCommand(Guid UserId) : IRequest<ScanResult>;
public sealed record ScanResult(int Added, int Updated);

public sealed class ScanSongsHandler(
    ILibraryStorage storage,
    ISongReadModel songs,
    ISongRecordRepository records) : IRequestHandler<ScanSongsCommand, ScanResult>
{
    public async Task<ScanResult> Handle(ScanSongsCommand cmd, CancellationToken ct)
    {
        var added = 0;
        var updated = 0;
        foreach (var file in storage.ListSongFiles(cmd.UserId))
        {
            var meta = songs.ReadLocalSong(cmd.UserId, file);
            var row = await records.GetAsync(cmd.UserId, file, ct);
            if (row is null)
            {
                await records.EnsureAsync(cmd.UserId, file, meta.Title, ct);
                added++;
            }
            else if (row.Title != meta.Title || row.Artist != meta.Artist)
            {
                await records.UpdateTitleArtistAsync(cmd.UserId, file, meta.Title, meta.Artist, ct);
                updated++;
            }
        }
        await records.SaveChangesAsync(ct);
        return new ScanResult(added, updated);
    }
}

public sealed class ScanPodcastsHandler(
    ILibraryStorage storage,
    IPodcastReadModel podcasts,
    IPodcastRecordRepository records) : IRequestHandler<ScanPodcastsCommand, ScanResult>
{
    public async Task<ScanResult> Handle(ScanPodcastsCommand cmd, CancellationToken ct)
    {
        var added = 0;
        var updated = 0;
        foreach (var file in storage.ListPodcastFiles(cmd.UserId))
        {
            var meta = podcasts.ReadLocalEpisode(cmd.UserId, file);
            var artist = meta.Artist == "Unknown" ? "" : meta.Artist;
            // EnsureAsync upserts title/artist when the row exists.
            var before = await podcasts.GetByFileAsync(cmd.UserId, file, ct);
            await records.EnsureAsync(cmd.UserId, file, meta.Title, artist, null, ct);
            if (before is null) added++;
            else if (before.Title != meta.Title || before.Artist != artist) updated++;
        }
        await records.SaveChangesAsync(ct);
        return new ScanResult(added, updated);
    }
}

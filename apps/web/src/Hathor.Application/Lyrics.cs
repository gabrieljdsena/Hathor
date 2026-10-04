using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using Hathor.Domain.Services;
using MediatR;

namespace Hathor.Application.Lyrics;

// Lyrics: cache-first per song file, lrclib exact/track-only/search fetch,
// manual save (candidate pick). Romanization is a pure Domain call
// (controller invokes KanaRomaji directly).

public sealed record GetLyricsQuery(
    Guid UserId, string File, string Track, string Artist, string? Album, int? DurationSec,
    bool Refresh = false)
    : IRequest<LyricsDto?>;

public sealed class GetLyricsHandler(
    ILyricsRepository cache,
    ILrclibClient lrclib) : IRequestHandler<GetLyricsQuery, LyricsDto?>
{
    public async Task<LyricsDto?> Handle(GetLyricsQuery q, CancellationToken ct)
    {
        if (!q.Refresh)
        {
            var cached = await cache.GetByFileAsync(q.UserId, q.File, ct);
            if (cached?.LyricsJson is not null)
            {
                var parsed = Parse(cached.LyricsJson);
                if (parsed is not null) return parsed;
            }
        }

        var (track, artist) = LyricsCleaning.CleanTrackArtist(q.Track, q.Artist);
        LyricsDto? fetched = LyricsCleaning.ArtistUsable(artist)
            ? await lrclib.GetExactAsync(track, artist, q.DurationSec, ct)
            : await lrclib.SearchTrackOnlyAsync(track, q.DurationSec, ct);

        if (fetched?.Synced is null && fetched?.Plain is null) return null;
        await cache.UpsertAsync(q.UserId, q.File,
            System.Text.Json.JsonSerializer.Serialize(fetched), ct);
        await cache.SaveChangesAsync(ct);
        return fetched;
    }

    internal static LyricsDto? Parse(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<LyricsDto>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public sealed record SearchLyricsQuery(
    Guid UserId, string Track, string Artist, string? Album, int? DurationSec)
    : IRequest<IReadOnlyList<LyricsHitDto>>;

public sealed class SearchLyricsHandler(ILrclibClient lrclib)
    : IRequestHandler<SearchLyricsQuery, IReadOnlyList<LyricsHitDto>>
{
    public async Task<IReadOnlyList<LyricsHitDto>> Handle(SearchLyricsQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Track)) return [];
        var (track, artist) = LyricsCleaning.CleanTrackArtist(q.Track, q.Artist);
        return await lrclib.SearchAsync(track, artist, q.Album, q.DurationSec, ct);
    }
}

public sealed record SaveLyricsCommand(Guid UserId, string File, string? Synced, string? Plain)
    : IRequest<bool>;
public sealed class SaveLyricsHandler(ILyricsRepository cache)
    : IRequestHandler<SaveLyricsCommand, bool>
{
    public async Task<bool> Handle(SaveLyricsCommand cmd, CancellationToken ct)
    {
        if (cmd.Synced is null && cmd.Plain is null) return false;
        await cache.UpsertAsync(cmd.UserId, cmd.File,
            System.Text.Json.JsonSerializer.Serialize(new LyricsDto(cmd.Synced, cmd.Plain)), ct);
        await cache.SaveChangesAsync(ct);
        return true;
    }
}

// Per-song highlight timing correction, persisted server-side (sparse:
// zero removes the row when it holds no lyrics). Clamped to ±10 seconds.
public sealed record GetLyricsOffsetQuery(Guid UserId, string File) : IRequest<int>;

public sealed class GetLyricsOffsetHandler(ILyricsRepository cache)
    : IRequestHandler<GetLyricsOffsetQuery, int>
{
    public Task<int> Handle(GetLyricsOffsetQuery q, CancellationToken ct) =>
        cache.GetOffsetAsync(q.UserId, q.File, ct);
}

public sealed record SetLyricsOffsetCommand(Guid UserId, string File, int OffsetMs) : IRequest<int>;

public sealed class SetLyricsOffsetHandler(ILyricsRepository cache)
    : IRequestHandler<SetLyricsOffsetCommand, int>
{
    public async Task<int> Handle(SetLyricsOffsetCommand cmd, CancellationToken ct)
    {
        var offset = Math.Clamp(cmd.OffsetMs, -10000, 10000);
        await cache.SetOffsetAsync(cmd.UserId, cmd.File, offset, ct);
        await cache.SaveChangesAsync(ct);
        return offset;
    }
}

// Manual removal (wrong lyrics): drops the cached row so the next read
// re-fetches from lrclib instead of serving the stale entry forever.
public sealed record DeleteLyricsCommand(Guid UserId, string File) : IRequest<bool>;
public sealed class DeleteLyricsHandler(ILyricsRepository cache)
    : IRequestHandler<DeleteLyricsCommand, bool>
{
    public async Task<bool> Handle(DeleteLyricsCommand cmd, CancellationToken ct)
    {
        if (!await cache.DeleteAsync(cmd.UserId, cmd.File, ct)) return false;
        await cache.SaveChangesAsync(ct);
        return true;
    }
}

// Raw lrclib passthrough (no cache) for the query-style GET /lyrics endpoint.
public sealed record RawExactQuery(string Track, string Artist, int? DurationSec)
    : IRequest<LyricsDto?>;
public sealed record RawTrackOnlyQuery(string Track, int? DurationSec)
    : IRequest<LyricsDto?>;

public sealed class RawLyricsHandlers(ILrclibClient lrclib) :
    IRequestHandler<RawExactQuery, LyricsDto?>,
    IRequestHandler<RawTrackOnlyQuery, LyricsDto?>
{
    public Task<LyricsDto?> Handle(RawExactQuery q, CancellationToken ct) =>
        lrclib.GetExactAsync(q.Track, q.Artist, q.DurationSec, ct);
    public Task<LyricsDto?> Handle(RawTrackOnlyQuery q, CancellationToken ct) =>
        lrclib.SearchTrackOnlyAsync(q.Track, q.DurationSec, ct);
}

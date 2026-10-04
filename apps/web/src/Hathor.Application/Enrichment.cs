using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.Enrichment;

// iTunes metadata queries (desktop search_itunes[_multi] + trending RSS).
public sealed record ITunesSearchQuery(Guid UserId, string Title, string? Artist)
    : IRequest<ITunesHitDto?>;
public sealed record ITunesOptionsQuery(Guid UserId, string Title, string? Artist, int Limit = 5)
    : IRequest<IReadOnlyList<ITunesHitDto>>;
public sealed record TrendingQuery(int Limit = 4) : IRequest<IReadOnlyList<string>>;

// Detail-page artwork (desktop get_artist_image/get_album_image, 600x600):
// first candidate whose names match exactly (case-insensitive), else the
// top hit — null when iTunes has no usable artwork.
public sealed record ArtistImageQuery(string Artist) : IRequest<ArtworkDto?>;
public sealed record AlbumImageQuery(string Album, string? Artist) : IRequest<ArtworkDto?>;

public sealed record ArtworkDto(string Url);

public sealed class ArtworkHandlers(IITunesClient itunes) :
    IRequestHandler<ArtistImageQuery, ArtworkDto?>,
    IRequestHandler<AlbumImageQuery, ArtworkDto?>
{
    public async Task<ArtworkDto?> Handle(ArtistImageQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Artist)) return null;
        var hits = await itunes.SearchMultiAsync(q.Artist, null, 5, ct);
        var hit = hits.FirstOrDefault(h =>
                string.Equals(h.Artist?.Trim(), q.Artist.Trim(), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(h.ArtworkUrl))
            ?? hits.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.ArtworkUrl));
        return hit is null ? null : new ArtworkDto(hit.ArtworkUrl);
    }

    public async Task<ArtworkDto?> Handle(AlbumImageQuery q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q.Album)) return null;
        var hits = await itunes.SearchMultiAsync(q.Album, q.Artist, 5, ct);
        var hit = hits.FirstOrDefault(h =>
                string.Equals(h.Album?.Trim(), q.Album.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(q.Artist) ||
                    string.Equals(h.Artist?.Trim(), q.Artist.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(h.ArtworkUrl))
            ?? hits.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h.ArtworkUrl));
        return hit is null ? null : new ArtworkDto(hit.ArtworkUrl);
    }
}

public sealed class ITunesHandlers(IITunesClient itunes) :
    IRequestHandler<ITunesSearchQuery, ITunesHitDto?>,
    IRequestHandler<ITunesOptionsQuery, IReadOnlyList<ITunesHitDto>>,
    IRequestHandler<TrendingQuery, IReadOnlyList<string>>
{
    public Task<ITunesHitDto?> Handle(ITunesSearchQuery q, CancellationToken ct) =>
        itunes.SearchSingleAsync(q.Title, q.Artist, ct);
    public Task<IReadOnlyList<ITunesHitDto>> Handle(ITunesOptionsQuery q, CancellationToken ct) =>
        itunes.SearchMultiAsync(q.Title, q.Artist, Math.Clamp(q.Limit, 1, 10), ct);
    public Task<IReadOnlyList<string>> Handle(TrendingQuery q, CancellationToken ct) =>
        itunes.GetTrendingAsync(Math.Clamp(q.Limit, 1, 10), ct);
}

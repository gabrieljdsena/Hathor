using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using MediatR;

namespace Hathor.Application.Library;

// Artist/album browsing: exact metadata matches, desktop-sorted
// (artist view Album+Title, album view Artist+Title).

public sealed record ListArtistsQuery(Guid UserId) : IRequest<IReadOnlyList<string>>;
public sealed record ArtistSongsQuery(Guid UserId, string Artist) : IRequest<IReadOnlyList<SongDto>>;
public sealed record ListAlbumsQuery(Guid UserId) : IRequest<IReadOnlyList<string>>;
public sealed record AlbumSongsQuery(Guid UserId, string Album) : IRequest<IReadOnlyList<SongDto>>;

public sealed class LibraryQueryHandlers(ISongReadModel songs) :
    IRequestHandler<ListArtistsQuery, IReadOnlyList<string>>,
    IRequestHandler<ArtistSongsQuery, IReadOnlyList<SongDto>>,
    IRequestHandler<ListAlbumsQuery, IReadOnlyList<string>>,
    IRequestHandler<AlbumSongsQuery, IReadOnlyList<SongDto>>
{
    public Task<IReadOnlyList<string>> Handle(ListArtistsQuery q, CancellationToken ct) =>
        songs.GetArtistsAsync(q.UserId, ct);
    public Task<IReadOnlyList<SongDto>> Handle(ArtistSongsQuery q, CancellationToken ct) =>
        songs.GetSongsByArtistAsync(q.UserId, q.Artist, ct);
    public Task<IReadOnlyList<string>> Handle(ListAlbumsQuery q, CancellationToken ct) =>
        songs.GetAlbumsAsync(q.UserId, ct);
    public Task<IReadOnlyList<SongDto>> Handle(AlbumSongsQuery q, CancellationToken ct) =>
        songs.GetSongsByAlbumAsync(q.UserId, q.Album, ct);
}

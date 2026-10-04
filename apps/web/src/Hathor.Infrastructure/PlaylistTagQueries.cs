using Dapper;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;

namespace Hathor.Infrastructure.Library;

// Playlist lists + song counts (desktop submenu/grid counts, plan G3).
// Identifiers are quoted per provider (Postgres needs quoted PascalCase).
public sealed class DapperPlaylistReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory) : IPlaylistReadModel
{
    private static object P(Guid userId) =>
        new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) };

    public async Task<IReadOnlyList<PlaylistDto>> ListAsync(Guid userId, CancellationToken ct = default)
    {
        var t = factory.Quote("Playlists");
        var sql = $"SELECT {factory.Quote("Id")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Description")}, {factory.Quote("Thumbnail")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} ORDER BY {factory.Quote("Title")}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<PlaylistDto>(
            new CommandDefinition(sql, P(userId), cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<PlaylistWithCountDto>> ListWithCountsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var p = factory.Quote("Playlists");
        var l = factory.Quote("Song_Playlist");
        var sql = $"SELECT p.{factory.Quote("Id")}, p.{factory.Quote("Title")}, " +
            $"p.{factory.Quote("Description")}, p.{factory.Quote("Thumbnail")}, " +
            $"COUNT(l.{factory.Quote("Id")}) AS {factory.Quote("SongCount")} " +
            $"FROM {p} p LEFT JOIN {l} l " +
            $"ON l.{factory.Quote("PlaylistId")} = p.{factory.Quote("Id")} " +
            $"AND l.{factory.Quote("UserId")} = p.{factory.Quote("UserId")} " +
            $"WHERE {factory.UserIdPredicate("p")} " +
            $"GROUP BY p.{factory.Quote("Id")}, p.{factory.Quote("Title")}, " +
            $"p.{factory.Quote("Description")}, p.{factory.Quote("Thumbnail")} " +
            $"ORDER BY p.{factory.Quote("Title")}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(long Id, string Title, string? Description, string? Thumbnail, long SongCount)>(
            new CommandDefinition(sql, P(userId), cancellationToken: ct));
        return rows.Select(r => new PlaylistWithCountDto(r.Id, r.Title, r.Description, r.Thumbnail, (int)r.SongCount)).ToList();
    }

    public async Task<PlaylistDto?> GetAsync(Guid userId, long id, CancellationToken ct = default)
    {
        var t = factory.Quote("Playlists");
        var sql = $"SELECT {factory.Quote("Id")}, {factory.Quote("Title")}, " +
            $"{factory.Quote("Description")}, {factory.Quote("Thumbnail")} FROM {t} " +
            $"WHERE {factory.UserIdPredicate()} AND {factory.Quote("Id")} = @Id";
        using var conn = factory.Create();
        return await conn.QuerySingleOrDefaultAsync<PlaylistDto>(
            new CommandDefinition(sql, new
            {
                UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId),
                Id = id,
            }, cancellationToken: ct));
    }
}

// Podcast tags with live episode counts (files currently on disk — desktop
// get_podcast_tags) + file→ids map (desktop get_podcast_tag_map).
public sealed class DapperPodcastTagReadModel(
    Hathor.Infrastructure.Dapper.DapperConnectionFactory factory,
    ILibraryStorage storage) : IPodcastTagReadModel
{
    private static object P(Guid userId) =>
        new { UserId = Hathor.Infrastructure.Dapper.DapperConnectionFactory.UserKey(userId) };

    public async Task<IReadOnlyList<PodcastTagDto>> ListWithCountsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var tagsSql = $"SELECT {factory.Quote("Id")}, {factory.Quote("Name")} " +
            $"FROM {factory.Quote("Podcast_Tags")} WHERE {factory.UserIdPredicate()} " +
            $"ORDER BY {factory.Quote("Name")}";
        var linksSql = $"SELECT {factory.Quote("PodcastFile")}, {factory.Quote("TagId")} " +
            $"FROM {factory.Quote("Podcast_Tag_Links")} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var tags = (await conn.QueryAsync<(long Id, string Name)>(
            new CommandDefinition(tagsSql, P(userId), cancellationToken: ct))).ToList();
        var links = await conn.QueryAsync<(string File, long TagId)>(
            new CommandDefinition(linksSql, P(userId), cancellationToken: ct));

        var live = storage.ListPodcastFiles(userId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<long, int>();
        foreach (var (file, tagId) in links)
        {
            if (!live.Contains(file)) continue;
            counts[tagId] = counts.TryGetValue(tagId, out var n) ? n + 1 : 1;
        }
        return tags.Select(t => new PodcastTagDto(t.Id, t.Name, counts.TryGetValue(t.Id, out var n) ? n : 0)).ToList();
    }

    public async Task<Dictionary<string, List<long>>> GetTagMapAsync(
        Guid userId, CancellationToken ct = default)
    {
        var sql = $"SELECT {factory.Quote("PodcastFile")}, {factory.Quote("TagId")} " +
            $"FROM {factory.Quote("Podcast_Tag_Links")} WHERE {factory.UserIdPredicate()}";
        using var conn = factory.Create();
        var rows = await conn.QueryAsync<(string File, long TagId)>(
            new CommandDefinition(sql, P(userId), cancellationToken: ct));
        var map = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, tagId) in rows)
        {
            if (!map.TryGetValue(file, out var ids)) map[file] = ids = [];
            ids.Add(tagId);
        }
        return map;
    }
}

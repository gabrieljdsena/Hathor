using System.Security.Claims;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Hathor.Api.Hubs;

// Push channel: PlaybackStateChanged + QueueUpdated so the web UI and
// external remotes converge after every control mutation.
//
// Authorized: the connection's group derives from the token's user id —
// clients can only ever receive their own broadcasts. Sockets cannot set
// Authorization headers, so tokens arrive via ?access_token= (JWT through
// the bearer handler, hth_ PATs through the API-key handler).
public static class UserGroup
{
    public static string? For(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out _) ? $"user:{id}" : null;
    }
}

[Authorize]
public sealed class PlayerHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var group = UserGroup.For(Context.User);
        if (group is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await base.OnConnectedAsync();
    }
}

[Authorize]
public sealed class DownloadsHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var group = UserGroup.For(Context.User);
        if (group is not null)
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRPlaybackHub(
    IHubContext<PlayerHub> player,
    IHubContext<DownloadsHub> downloads) : IPlaybackHub
{
    public Task BroadcastStateAsync(Guid userId, PlayerStateDto state, CancellationToken ct = default) =>
        player.Clients.Group($"user:{userId}").SendAsync("PlaybackStateChanged", state, ct);

    public Task BroadcastQueueAsync(Guid userId, IReadOnlyList<SongDto> queue, CancellationToken ct = default) =>
        player.Clients.Group($"user:{userId}").SendAsync("QueueUpdated", queue, ct);

    // Staged progress (0→90 download, 90 finished, 95 metadata, 100 done).
    public Task BroadcastDownloadAsync(Guid userId, DownloadJobDto job, CancellationToken ct = default) =>
        downloads.Clients.Group($"user:{userId}").SendAsync("DownloadProgress", job, ct);
}

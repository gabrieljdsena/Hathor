using Hathor.Application.Player;
using Hathor.Domain.Entities;
using Hathor.Infrastructure.Ef;

namespace Hathor.Api.History;

// Subscribes the deferred history hook: every SongPlayed event (never raised
// for podcasts — desktop rule) appends Music_History + Playlist_History rows.
public static class HistoryHook
{
    public static void Subscribe(IServiceProvider services)
    {
        PlayerEvents.SongPlayed += (userId, file, playlistId) =>
        {
            try
            {
                using var scope = services
                    .GetRequiredService<IServiceScopeFactory>()
                    .CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HathorDbContext>();
                db.MusicHistory.Add(new MusicHistoryEntry
                {
                    UserId = userId,
                    SongFile = file,
                    DatePlayedUtc = DateTime.UtcNow,
                });
                if (playlistId.HasValue)
                    db.PlaylistHistory.Add(new PlaylistHistoryEntry
                    {
                        UserId = userId,
                        PlaylistId = playlistId.Value,
                        DatePlayedUtc = DateTime.UtcNow,
                    });
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("HistoryHook")
                    .LogError(ex, "Failed to record play history for {File}", file);
            }
        };
    }
}

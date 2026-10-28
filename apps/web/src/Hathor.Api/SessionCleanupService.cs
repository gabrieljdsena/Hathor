using Hathor.Domain.Repositories;

namespace Hathor.Api;

// Rotation writes one Sessions row per access-token refresh (every 15 min),
// so expired and long-revoked rows must be swept or the table grows forever.
public sealed class SessionCleanupService(
    IServiceScopeFactory scopes,
    ILogger<SessionCleanupService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
                var removed = await sessions.PurgeExpiredAsync(stoppingToken);
                if (removed > 0)
                    log.LogInformation("Purged {Count} expired/revoked sessions", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Cleanup is best effort: never crash the host over it.
                log.LogWarning(ex, "Session cleanup failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

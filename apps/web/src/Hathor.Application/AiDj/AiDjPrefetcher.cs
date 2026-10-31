using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hathor.Application.AiDj;

public sealed class AiDjPrefetcher(
    IServiceScopeFactory scopes,
    ILogger<AiDjPrefetcher> log)
{
    private readonly ConcurrentDictionary<Guid, byte> running = new();

    public void Queue(Guid userId)
    {
        if (!running.TryAdd(userId, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AiDjService>();
                await service.EnsureNextBatchAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "AI DJ prefetch failed for {UserId}", userId);
            }
            finally
            {
                running.TryRemove(userId, out _);
            }
        });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Hathor.Infrastructure.Ef;

namespace Hathor.Api.Controllers;

// Liveness + readiness for uptime monitors, Docker healthchecks and load
// balancers. Explicitly anonymous (monitors hold no token) and fail-closed
// everywhere else via the global fallback policy. Bodies stay shape-stable
// (status + per-check map) so monitors can alert on 503 OR parse details;
// no internals leak (a down database reports "unhealthy", not the error).
[ApiController]
[Route("api/[controller]")]
public sealed class HealthController(HathorDbContext db, IConfiguration config) : ControllerBase
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;

    // Liveness: the process answers. Touches no dependencies so a wedged
    // database cannot fail it (that is readiness' job below).
    [HttpGet("live")]
    [AllowAnonymous]
    public ActionResult<HealthDto> Live() => Ok(Shape(new Dictionary<string, string>()));

    // Readiness: database + storage root reachable. 200 healthy, 503 with
    // the same shape when anything is down. Never cached (a cached 200
    // would mask an outage from every monitor behind the same proxy).
    [HttpGet("ready")]
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<ActionResult<HealthDto>> Ready(CancellationToken ct)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["database"] = await DatabaseAsync(ct),
            ["storage"] = Storage(),
        };
        var dto = Shape(checks);
        return dto.Status == "healthy" ? Ok(dto) : StatusCode(503, dto);
    }

    private async Task<string> DatabaseAsync(CancellationToken ct)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct) ? "healthy" : "unhealthy";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "unhealthy";
        }
    }

    private string Storage()
    {
        // Same default resolution as infrastructure registration
        // (ServiceExtensions): configured root, "data" when unset.
        var root = config.GetValue("Database:StorageRoot", "data") ?? "data";
        try
        {
            return Directory.Exists(Path.GetFullPath(root)) ? "healthy" : "unhealthy";
        }
        catch
        {
            return "unhealthy";
        }
    }

    private HealthDto Shape(Dictionary<string, string> checks) => new(
        checks.Count == 0 || checks.Values.All(v => v == "healthy") ? "healthy" : "degraded",
        config.GetValue("ApiInfo:Version", "1.0.0") ?? "1.0.0",
        (long)(DateTimeOffset.UtcNow - StartedAtUtc).TotalSeconds,
        checks);
}

public sealed record HealthDto(
    string Status, string Version, long UptimeSec, Dictionary<string, string> Checks);

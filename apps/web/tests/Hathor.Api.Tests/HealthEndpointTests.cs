using FluentAssertions;
using Hathor.Api.Controllers;
using Hathor.Infrastructure.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hathor.Api.Tests;

// DB-free: the database is pointed at a closed loopback port so
// CanConnectAsync fails fast (refused, not timeout); storage uses temp dirs.
public sealed class HealthEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hathor-health-" + Guid.NewGuid().ToString("N"));

    private static HathorDbContext UnreachableDb() => new(
        new DbContextOptionsBuilder<HathorDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=h;Username=h;Password=h")
            .Options);

    private HealthController Controller(string? storageRoot) => new(
        UnreachableDb(),
        new StubConfig(new Dictionary<string, string?>
        {
            ["Database:StorageRoot"] = storageRoot,
            ["ApiInfo:Version"] = "9.9.9-test",
        }));

    private static (int Status, HealthDto Dto) Unwrap(ActionResult<HealthDto> result)
    {
        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.Value.Should().BeOfType<HealthDto>();
        return (obj.StatusCode ?? 0, (HealthDto)obj.Value!);
    }

    [Fact]
    public void Live_Returns200_WithoutTouchingDatabase()
    {
        // Even with an unreachable database, liveness answers (fast = no I/O).
        var result = Controller(null).Live();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<HealthDto>().Subject;
        dto.Status.Should().Be("healthy");
        dto.Version.Should().Be("9.9.9-test");
        dto.UptimeSec.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Ready_UnreachableDatabase_Returns503_WithoutInternals()
    {
        Directory.CreateDirectory(_dir);
        var (status, dto) = Unwrap(await Controller(_dir).Ready(CancellationToken.None));

        status.Should().Be(503);
        dto.Status.Should().Be("degraded");
        dto.Checks["database"].Should().Be("unhealthy");
        dto.Checks["storage"].Should().Be("healthy");
    }

    [Fact]
    public async Task Ready_MissingStorageDir_Returns503()
    {
        var missing = Path.Combine(_dir, "nope");
        var (status, dto) = Unwrap(await Controller(missing).Ready(CancellationToken.None));

        status.Should().Be(503);
        dto.Checks["storage"].Should().Be("unhealthy");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { }
    }
}

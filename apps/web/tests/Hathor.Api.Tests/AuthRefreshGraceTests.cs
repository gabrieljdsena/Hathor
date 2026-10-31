using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Hathor.Api.Tests;

// Concurrent tabs (and refresh responses lost in flight) can present the
// same rotated token seconds apart. Inside the grace window the replacement
// chain must keep working instead of revoking every session.
public sealed class AuthRefreshGraceTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new(new Dictionary<string, string?>
    {
        ["Auth:RefreshReuseGraceSeconds"] = "30",
    });

    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<Tokens> RegisterAsync(string username)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "auth-test-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!;
    }

    private async Task<Tokens> RefreshOkAsync(string refreshToken)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<Tokens>())!;
    }

    private async Task<List<SessionRow>> SessionsAsync(string accessToken)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/sessions");
        msg.Headers.Authorization = new("Bearer", accessToken);
        var res = await _client.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<List<SessionRow>>())!;
    }

    [Fact]
    public async Task DuplicateRefresh_WithinGrace_RotatesInsteadOfBurning()
    {
        var first = await RegisterAsync($"gr-{Guid.NewGuid():N}");
        var second = await RefreshOkAsync(first.RefreshToken);

        // A second tab replays the token that was just rotated.
        var duplicate = await RefreshOkAsync(first.RefreshToken);
        duplicate.RefreshToken.Should().NotBe(second.RefreshToken);

        // The chain is still alive: the duplicate's token rotates normally.
        var third = await RefreshOkAsync(duplicate.RefreshToken);
        third.AccessToken.Should().NotBeNullOrEmpty();

        // Nothing was burned: exactly the surviving session remains active.
        var sessions = await SessionsAsync(third.AccessToken);
        sessions.Should().ContainSingle();
        sessions[0].Current.Should().BeTrue();
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record SessionRow(
        Guid Id, bool RememberMe, string? DeviceLabel, string? IpAddress,
        DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime LastUsedAtUtc, bool Current);
}

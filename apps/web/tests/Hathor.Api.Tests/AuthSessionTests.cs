using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Hathor.Api.Hubs;

namespace Hathor.Api.Tests;

// Single-account sessions: 24h default / 30d remembered lifetimes,
// rotation with reuse detection, logout, multi-login management.
public sealed class AuthSessionTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    private async Task<Tokens> RegisterAsync(string username, bool rememberMe = false)
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "auth-test-123", rememberMe });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!;
    }

    private async Task<Tokens> LoginAsync(string username, bool rememberMe = false)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password = "auth-test-123", rememberMe });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<Tokens>())!;
    }

    private async Task<HttpStatusCode> RefreshStatusAsync(string refreshToken)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        msg.Content = JsonContent.Create(new { refreshToken });
        return (await _client.SendAsync(msg)).StatusCode;
    }

    private async Task<HttpStatusCode> ChangePasswordAsync(
        string accessToken, string currentPassword, string newPassword)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password");
        msg.Headers.Authorization = Bearer(accessToken);
        msg.Content = JsonContent.Create(new { currentPassword, newPassword });
        return (await _client.SendAsync(msg)).StatusCode;
    }

    private async Task<HttpStatusCode> TryLoginAsync(string username, string password)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login");
        msg.Content = JsonContent.Create(new { username, password });
        return (await _client.SendAsync(msg)).StatusCode;
    }

    private async Task<List<SessionRow>> SessionsAsync(string accessToken)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/sessions");
        msg.Headers.Authorization = Bearer(accessToken);
        var res = await _client.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<List<SessionRow>>())!;
    }

    [Fact]
    public async Task ChangePassword_RotatesCredentials()
    {
        var name = $"pw-{Guid.NewGuid():N}";
        var tokens = await RegisterAsync(name);

        (await ChangePasswordAsync(tokens.AccessToken, "wrong-current", "new-password-123"))
            .Should().Be(HttpStatusCode.Unauthorized);
        (await ChangePasswordAsync(tokens.AccessToken, "auth-test-123", "short"))
            .Should().Be(HttpStatusCode.BadRequest);

        (await ChangePasswordAsync(tokens.AccessToken, "auth-test-123", "new-password-123"))
            .Should().Be(HttpStatusCode.NoContent);
        (await TryLoginAsync(name, "auth-test-123")).Should().Be(HttpStatusCode.Unauthorized);
        (await TryLoginAsync(name, "new-password-123")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_SecondAttempt_Conflict_And_Status_Closes()
    {
        using var st0 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/status");
        (await (await _client.SendAsync(st0)).Content.ReadFromJsonAsync<Status>())!
            .RegistrationOpen.Should().BeTrue();

        await RegisterAsync($"u-{Guid.NewGuid():N}");

        using var dup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register");
        dup.Content = JsonContent.Create(new { username = $"other-{Guid.NewGuid():N}", password = "auth-test-123" });
        (await _client.SendAsync(dup)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var st1 = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/status");
        (await (await _client.SendAsync(st1)).Content.ReadFromJsonAsync<Status>())!
            .RegistrationOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Login_RememberMe_SetsThirtyDaySession()
    {
        var name = $"rm-{Guid.NewGuid():N}";
        await RegisterAsync(name);
        var plain = await LoginAsync(name, rememberMe: false);
        var remembered = await LoginAsync(name, rememberMe: true);

        var sessions = await SessionsAsync(remembered.AccessToken);
        sessions.Should().HaveCount(3); // register + 2 logins
        sessions.Should().ContainSingle(s => s.Current);
        var plainRow = sessions.First(s => !s.RememberMe);
        var rememberedRow = sessions.First(s => s.RememberMe);
        (rememberedRow.ExpiresAtUtc - rememberedRow.CreatedAtUtc).Should().BeCloseTo(
            TimeSpan.FromDays(30), TimeSpan.FromMinutes(5));
        (plainRow.ExpiresAtUtc - plainRow.CreatedAtUtc).Should().BeCloseTo(
            TimeSpan.FromHours(24), TimeSpan.FromMinutes(5));
        plain.AccessToken.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_Rotates_Chain_Then_Replay_Burns_All()
    {
        var first = await RegisterAsync($"rt-{Guid.NewGuid():N}");
        async Task<Tokens> RefreshOk(string refreshToken)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
            msg.Content = JsonContent.Create(new { refreshToken });
            var res = await _client.SendAsync(msg);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await res.Content.ReadFromJsonAsync<Tokens>())!;
        }

        var second = await RefreshOk(first.RefreshToken);
        second.RefreshToken.Should().NotBe(first.RefreshToken);
        var third = await RefreshOk(second.RefreshToken);

        // Replay the twice-rotated token: theft response, everything dies.
        (await RefreshStatusAsync(first.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshStatusAsync(second.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshStatusAsync(third.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_ReusedRotatedToken_Revokes_All_Sessions()
    {
        var first = await RegisterAsync($"rx-{Guid.NewGuid():N}");
        var second = await LoginAsync(first.Username, rememberMe: true);
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        msg.Content = JsonContent.Create(new { refreshToken = second.RefreshToken });
        var rotated = (await (await _client.SendAsync(msg)).Content.ReadFromJsonAsync<Tokens>())!;

        // Replay the rotated (replaced) token: theft response, everything dies.
        (await RefreshStatusAsync(second.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshStatusAsync(rotated.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshStatusAsync(first.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await SessionsAsync(rotated.AccessToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Logout_Revokes_Current_Session_Only()
    {
        var name = $"lo-{Guid.NewGuid():N}";
        var first = await RegisterAsync(name);
        var second = await LoginAsync(name);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        msg.Headers.Authorization = Bearer(second.AccessToken);
        msg.Content = JsonContent.Create(new { refreshToken = second.RefreshToken });
        (await _client.SendAsync(msg)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await RefreshStatusAsync(second.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshStatusAsync(first.RefreshToken)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LogoutAll_And_Session_Revoke()
    {
        var name = $"la-{Guid.NewGuid():N}";
        var first = await RegisterAsync(name);
        var second = await LoginAsync(name);

        var sessions = await SessionsAsync(second.AccessToken);
        sessions.Should().HaveCount(2);

        // Revoke the other session directly.
        var other = sessions.First(s => !s.Current);
        using var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/auth/sessions/{other.Id}");
        del.Headers.Authorization = Bearer(second.AccessToken);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RefreshStatusAsync(first.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);

        using var missing = new HttpRequestMessage(HttpMethod.Delete,
            $"/api/v1/auth/sessions/{Guid.NewGuid()}");
        missing.Headers.Authorization = Bearer(second.AccessToken);
        (await _client.SendAsync(missing)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Log out everywhere kills the survivor too.
        using var all = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout-all");
        all.Headers.Authorization = Bearer(second.AccessToken);
        (await _client.SendAsync(all)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefreshStatusAsync(second.RefreshToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
    private sealed record Status(bool RegistrationOpen);
    private sealed record SessionRow(
        Guid Id, bool RememberMe, string? DeviceLabel, string? IpAddress,
        DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime LastUsedAtUtc, bool Current);
}

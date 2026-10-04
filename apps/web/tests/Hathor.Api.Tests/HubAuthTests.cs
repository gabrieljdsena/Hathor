using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Hathor.Api.Hubs;

namespace Hathor.Api.Tests;

// Hubs are authorized and self-joining: no client-supplied user id can
// subscribe to another user's broadcasts. Sockets authenticate via
// ?access_token= (JWT or hth_ PAT).
public sealed class HubAuthTests : IAsyncLifetime
{
    private readonly TempLibraryFixture _lib = new();
    private HttpClient _client => _lib.Client;

    public Task InitializeAsync() => _lib.InitializeAsync();
    public Task DisposeAsync() => _lib.DisposeAsync();

    private async Task<string> AccessTokenAsync()
    {
        var reg = await _client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = $"hub-{Guid.NewGuid():N}", password = "hub-test-123" });
        reg.EnsureSuccessStatusCode();
        return (await reg.Content.ReadFromJsonAsync<Tokens>())!.AccessToken;
    }

    [Theory]
    [InlineData("/hubs/player/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/downloads/negotiate?negotiateVersion=1")]
    public async Task Negotiate_WithoutToken_Unauthorized(string url)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, url);
        (await _client.SendAsync(msg)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/hubs/player/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/downloads/negotiate?negotiateVersion=1")]
    public async Task Negotiate_WithQueryJwt_Ok(string url)
    {
        var token = await AccessTokenAsync();
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            $"{url}&access_token={Uri.EscapeDataString(token)}");
        (await _client.SendAsync(msg)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Negotiate_WithBadToken_Unauthorized()
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post,
            "/hubs/player/negotiate?negotiateVersion=1&access_token=nope");
        (await _client.SendAsync(msg)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void UserGroup_Derives_From_Token_Claims()
    {
        var id = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));
        UserGroup.For(principal).Should().Be($"user:{id}");
        UserGroup.For(new ClaimsPrincipal()).Should().BeNull();
        var bad = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "not-a-guid")], "test"));
        UserGroup.For(bad).Should().BeNull();
        UserGroup.For(null).Should().BeNull();
    }

    private sealed record Tokens(string AccessToken, string RefreshToken, string Username);
}

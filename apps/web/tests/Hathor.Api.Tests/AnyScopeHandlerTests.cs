using System.Security.Claims;
using FluentAssertions;
using Hathor.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Hathor.Api.Tests;

public sealed class AnyScopeHandlerTests
{
    private static AuthorizationHandlerContext Ctx(ClaimsPrincipal user, AnyScopeRequirement req)
    {
        var resource = new DefaultHttpContext();
        return new AuthorizationHandlerContext(new[] { req }, user, resource);
    }

    private static ClaimsPrincipal PatPrincipal(params string[] scopes)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        claims.AddRange(scopes.Select(s => new Claim("scope", s)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
    }

    [Fact]
    public async Task Handler_Succeeds_On_Matching_Scope()
    {
        var handler = new AnyScopeHandler();
        var req = new AnyScopeRequirement("player:control", "downloads:write");
        var ctx = Ctx(PatPrincipal("downloads:write"), req);
        await handler.HandleAsync(ctx);
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Handler_Fails_Without_Matching_Scope()
    {
        var handler = new AnyScopeHandler();
        var req = new AnyScopeRequirement("player:control", "downloads:write");
        var ctx = Ctx(PatPrincipal("player:read"), req);
        await handler.HandleAsync(ctx);
        ctx.HasSucceeded.Should().BeFalse();
    }
}

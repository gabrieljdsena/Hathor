using FluentAssertions;
using Hathor.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Hathor.Api.Tests;

public sealed class SecurityHeadersMiddlewareTests
{
    private static DefaultHttpContext Context(bool https = false)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/songs";
        context.Request.Scheme = https ? "https" : "http";
        return context;
    }

    private static async Task InvokeAsync(DefaultHttpContext context)
    {
        var middleware = new SecurityHeadersMiddleware(_ =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);
    }

    [Fact]
    public async Task Invoke_Sets_Hardening_Headers()
    {
        var context = Context();
        await InvokeAsync(context);

        context.Response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        context.Response.Headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
        context.Response.Headers["X-Frame-Options"].ToString().Should().Be("DENY");
        context.Response.Headers["Cross-Origin-Opener-Policy"].ToString().Should().Be("same-origin");
        context.Response.Headers["Permissions-Policy"].ToString().Should().Contain("camera=()");
        var csp = context.Response.Headers["Content-Security-Policy"].ToString();
        csp.Should().Contain("frame-ancestors 'none'");
        csp.Should().Contain("script-src 'self'");
        csp.Should().NotContain("unsafe-inline; object"); // no script unsafe-inline
        // YouTube preview iframes (Download + Discover) must stay frameable.
        csp.Should().Contain("frame-src 'self' https://www.youtube.com");
        // Direct-audio preview fallback streams from the Google video CDN.
        csp.Should().Contain("media-src 'self' blob: https://*.googlevideo.com");
        context.Response.Headers.Should().NotContainKey("Strict-Transport-Security");
    }

    [Fact]
    public async Task Invoke_Https_Adds_Hsts()
    {
        var context = Context(https: true);
        await InvokeAsync(context);

        context.Response.Headers["Strict-Transport-Security"].ToString()
            .Should().Be("max-age=31536000; includeSubDomains");
    }
}

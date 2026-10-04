using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hathor.Api.Middleware;
using Microsoft.AspNetCore.Http;

namespace Hathor.Api.Tests;

public sealed class ExceptionHandlingMiddlewareTests
{
    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/songs";
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Invoke_UnhandledException_ReturnsProblemJson()
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("db is down"));
        var context = Context();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be((int)HttpStatusCode.InternalServerError);
        context.Response.ContentType.Should().StartWith("application/problem+json");
        var body = await ReadBodyAsync(context);
        var doc = JsonDocument.Parse(body).RootElement;
        doc.GetProperty("status").GetInt32().Should().Be(500);
        doc.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        body.Should().NotContain("db is down"); // no internals leaked
    }

    [Fact]
    public async Task Invoke_NoException_PassesThrough()
    {
        var middleware = new ExceptionHandlingMiddleware(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });
        var context = Context();

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(200);
    }
}

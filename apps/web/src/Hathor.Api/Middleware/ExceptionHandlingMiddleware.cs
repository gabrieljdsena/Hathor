using System.Net;
using Serilog;

namespace Hathor.Api.Middleware;

// Last-resort catch for the request pipeline (database outages, auth
// store failures, downstream API blowups): the error is logged through
// Serilog — Error level also lands in the lab Postgres `logs` table with
// application='hathor' — and the client gets a stable problem payload
// without leaked internals.
public sealed class ExceptionHandlingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to report and nothing to log.
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await WriteProblemAsync(context);
        }
    }

    private static Task WriteProblemAsync(HttpContext context)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = "An unexpected error occurred.",
            status = 500,
            traceId = context.TraceIdentifier,
        });
        return context.Response.WriteAsync(json);
    }
}

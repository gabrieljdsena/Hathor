using System.Text;
using System.Threading.RateLimiting;
using Hathor.Api.Auth;
using Hathor.Api.History;
using Hathor.Api.Hubs;
using Hathor.Api.Middleware;
using Hathor.Application.Ports;
using Hathor.Infrastructure.Auth;
using Hathor.Infrastructure.Ef;
using Hathor.Infrastructure.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Windows-service aware (content root = publish dir, graceful stop).
// No-op under `dotnet run`, required for services.msc hosting.
// Services run non-interactive with System32 as CWD — without this the
// service would look for wwwroot/appsettings next to System32.
builder.Services.AddWindowsService(options => options.ServiceName = "Hathor");
if (!Environment.UserInteractive)
    builder.WebHost.UseContentRoot(AppContext.BaseDirectory);
// No Server: Kestrel version banner on any response.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Local secrets (lab-Postgres password, dev overrides): gitignored, optional.
// Inserted just above appsettings.json — NOT appended — so environment
// variables, command-line args and test overrides always win over it.
// See appsettings.Secrets.example.json.
builder.Configuration.Sources.Insert(1, new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
{
    Path = "appsettings.Secrets.json",
    Optional = true,
    ReloadOnChange = true,
    FileProvider = builder.Environment.ContentRootFileProvider,
});

// Serilog: console + rolling file from configuration, plus the shared lab
// Postgres `logs` table (application='hathor') for Error/Fatal. The sink
// swallows its own failures, so a down logs database can never break the
// path that is trying to report the error.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Sink(
        new PostgresLogSink(LogsDbConnection.Resolve(context.Configuration)),
        restrictedToMinimumLevel: LogEventLevel.Error));

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddHathorInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IPlaybackHub, SignalRPlaybackHub>();
builder.Services.AddScoped<TokenValidator>();

// Smart scheme: hth_ tokens go to the ApiKey handler, everything else to JWT.
// Sockets cannot set headers, so ?access_token= routes the same way.
builder.Services
    .AddAuthentication("Smart")
    .AddPolicyScheme("Smart", "JWT or API key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer hth_", StringComparison.OrdinalIgnoreCase))
                return ApiKeyAuthenticationHandler.SchemeName;
            var query = context.Request.Query["access_token"].ToString();
            if (query.StartsWith("hth_", StringComparison.Ordinal))
                return ApiKeyAuthenticationHandler.SchemeName;
            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        var jwtKey = JwtKey.RequireValid(builder.Configuration["Jwt:Key"]
            ?? Environment.GetEnvironmentVariable("JWT_KEY"));
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero,
        };
        // SignalR browsers/players authenticate sockets via query string.
        // hth_ PATs are handled by the ApiKey scheme (see Smart selector) —
        // never accept them as JWT bearer tokens here.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    var token = context.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrEmpty(token) &&
                        !token.StartsWith("hth_", StringComparison.Ordinal))
                        context.Token = token;
                }
                return Task.CompletedTask;
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, _ => { });

builder.Services.AddScopePolicies();

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("Auth", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    // Refresh tokens are 64-byte random (un-guessable), but still throttle
    // replay probing separately from login — 5/min would flake legitimate
    // multi-device rotation bursts.
    options.AddFixedWindowLimiter("AuthRefresh", opt =>
    {
        opt.PermitLimit = 30;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("Api", opt =>
    {
        opt.PermitLimit = 300;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddCors(options =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? ["http://localhost:5173"];
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Migrate the database (EF migrations own the schema; pre-migration
// databases are baselined automatically). A connection failure here is
// fatal but always logged first (console/file + lab Postgres logs table).
using (var scope = app.Services.CreateScope())
{
    try
    {
        InfrastructureServiceExtensions.EnsureDatabaseCreated(
            scope.ServiceProvider.GetRequiredService<HathorDbContext>());
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Local database migration failed; Hathor cannot start.");
        throw;
    }
    var storage = scope.ServiceProvider.GetRequiredService<Hathor.Application.Ports.ILibraryStorage>();
    Log.Information(
        "Media library: songs={SongsDir} podcasts={PodcastsDir}",
        storage.SongsDir(Guid.Empty), storage.PodcastsDir(Guid.Empty));
}

HistoryHook.Subscribe(app.Services);

// Every HTTP request in console/file logs (method, path, status, elapsed)
// so handled 4xx/5xx are visible too — Error/Fatal additionally land in
// the lab Postgres logs table via the sink. The template intentionally uses
// RequestPath (never Query) so ?token= / ?access_token= credentials for
// media streams and SignalR never land in logs.
app.UseSerilogRequestLogging(opts =>
{
    opts.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
});

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    await next();
});

// Security headers on every response (API, hubs, streams, wwwroot static,
// SPA fallback). Registered before static files so they are covered too.
app.UseMiddleware<SecurityHeadersMiddleware>();

// Self-hosted SPA (Windows-service deploys): the publish pipeline copies
// frontend/dist into wwwroot. Serves without auth/rate-limit overhead;
// unknown paths fall back to index.html (mapped below) for React Router.
// Absent in dev/docker (vite/nginx serve) — then these are no-ops.
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireRateLimiting("Api");
app.MapHub<PlayerHub>("/hubs/player").RequireAuthorization();
app.MapHub<DownloadsHub>("/hubs/downloads").RequireAuthorization();
// SPA fallback serves the public shell (login page) — explicit opt-out of
// the fallback authorization policy. Static files above bypass auth by design.
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

public partial class Program;

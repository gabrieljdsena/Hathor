using System.Security.Claims;
using System.Text.Encodings.Web;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Hathor.Api.Auth;

// API-key scheme for headless clients: Authorization: Bearer hth_<prefix><secret>.
// Only SHA256 hashes are stored; lookup is by the 8-char prefix, then hash compare.
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyRepository keys,
    IApiKeyService apiKeys) : AuthenticationHandler<AuthenticationSchemeOptions>(
        options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = HeaderToken() ?? QueryToken();
        if (token is null) return AuthenticateResult.NoResult();

        var body = token["hth_".Length..];
        if (body.Length < 8) return AuthenticateResult.Fail("Malformed API key.");
        var keyPrefix = body[..8];

        ApiKey? stored;
        try
        {
            stored = await keys.GetByPrefixAsync(keyPrefix);
        }
        catch (Exception ex)
        {
            // Auth store unreachable (database down): fail closed, and log
            // so the outage lands in the lab Postgres logs table.
            Logger.LogError(ex, "API-key lookup failed for prefix {Prefix}", keyPrefix);
            return AuthenticateResult.Fail("Authentication service unavailable.");
        }
        if (stored is null || stored.TokenHash != apiKeys.Hash(token))
            return AuthenticateResult.Fail("Invalid API key.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, stored.UserId.ToString()),
            new(ClaimTypes.Name, $"apikey:{stored.Name}"),
            new("key_id", stored.Id.ToString()),
        };
        foreach (var scope in stored.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new Claim("scope", scope));

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    // Sockets cannot set headers: Bearer hth_… header first, then
    // ?access_token=hth_… query (mirrors the Smart scheme selector).
    private string? HeaderToken()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return null;
        var value = header.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = value[prefix.Length..].Trim();
        return token.StartsWith("hth_", StringComparison.Ordinal) ? token : null;
    }

    private string? QueryToken()
    {
        var query = Request.Query["access_token"].ToString();
        return query.StartsWith("hth_", StringComparison.Ordinal) ? query : null;
    }
}
public static class ScopeAuthorization
{
    public const string PlayerRead = "player:read";
    public const string PlayerControl = "player:control";
    public const string LibraryRead = "library:read";
    public const string DownloadsRead = "downloads:read";
    public const string DownloadsWrite = "downloads:write";
    public const string PlaylistsRead = "playlists:read";
    public const string PlaylistsWrite = "playlists:write";
    public const string LibraryWrite = "library:write";

    public static void AddScopePolicies(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            // Reads accept writers of their own surface (a download key must
            // poll job status; a playlist key must list to verify), and
            // player:control stays the legacy superset everywhere.
            // NOTE: stacked [Authorize] attributes AND-combine, so every
            // action carries exactly one policy — never class + method.
            options.AddPolicy(PlayerRead,
                p => p.Requirements.Add(new AnyScopeRequirement(PlayerControl, PlayerRead)));
            options.AddPolicy(PlayerControl, p => p.RequireClaim("scope", PlayerControl));
            options.AddPolicy(LibraryRead,
                p => p.Requirements.Add(new AnyScopeRequirement(
                    PlayerControl, LibraryRead, LibraryWrite, PlaylistsWrite)));
            options.AddPolicy(DownloadsRead,
                p => p.Requirements.Add(new AnyScopeRequirement(
                    PlayerControl, LibraryRead, DownloadsWrite)));
            options.AddPolicy(DownloadsWrite,
                p => p.Requirements.Add(new AnyScopeRequirement(PlayerControl, DownloadsWrite)));
            options.AddPolicy(PlaylistsRead,
                p => p.Requirements.Add(new AnyScopeRequirement(
                    PlayerControl, LibraryRead, PlaylistsWrite)));
            options.AddPolicy(PlaylistsWrite,
                p => p.Requirements.Add(new AnyScopeRequirement(PlayerControl, PlaylistsWrite)));
            options.AddPolicy(LibraryWrite,
                p => p.Requirements.Add(new AnyScopeRequirement(PlayerControl, LibraryWrite)));
        });
        services.AddSingleton<
            Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AnyScopeHandler>();
    }
}

// Satisfied when the principal carries ANY of the listed scope claims.
public sealed class AnyScopeRequirement(params string[] scopes)
    : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement
{
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

public sealed class AnyScopeHandler
    : Microsoft.AspNetCore.Authorization.AuthorizationHandler<AnyScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context,
        AnyScopeRequirement requirement)
    {
        if (requirement.Scopes.Any(s => context.User.HasClaim("scope", s)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Hathor.Domain.Repositories;
using Hathor.Infrastructure.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Hathor.Api.Auth;

// Validates a raw JWT or hth_ PAT outside the auth middleware so media
// elements (<audio>, <video>) that cannot send Authorization headers can use
// ?token=<jwt|hth_...>. Only used by streaming endpoints.
public interface ITokenValidator
{
    Task<ClaimsPrincipal?> ValidateAsync(string token);
}

public sealed class TokenValidator(
    IConfiguration config,
    IApiKeyRepository keys,
    Application.Ports.IApiKeyService apiKeys) : ITokenValidator
{
    public async Task<ClaimsPrincipal?> ValidateAsync(string token)
    {
        if (token.StartsWith("hth_", StringComparison.Ordinal))
        {
            var body = token["hth_".Length..];
            if (body.Length < 8) return null;
            var stored = await keys.GetByPrefixAsync(body[..8]);
            if (stored is null || !HashCompare.FixedTimeEquals(stored.TokenHash, apiKeys.Hash(token))) return null;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, stored.UserId.ToString()),
                new(ClaimTypes.Name, $"apikey:{stored.Name}"),
            };
            foreach (var scope in stored.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim("scope", scope));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "QueryToken"));
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = config["Jwt:Issuer"],
                ValidAudience = config["Jwt:Audience"],
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(JwtKey.RequireValid(config["Jwt:Key"]
                        ?? Environment.GetEnvironmentVariable("JWT_KEY")))),
                ClockSkew = TimeSpan.Zero,
            }, out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }

    public static bool HasScope(ClaimsPrincipal principal, string scope) =>
        principal.FindAll("scope").Any(c => c.Value == scope);

    // Manual-token endpoints must check policy equivalence, not a single
    // scope (see ScopeAuthorization.LibraryReadScopes).
    public static bool SatisfiesLibraryRead(ClaimsPrincipal principal) =>
        ScopeAuthorization.SatisfiesLibraryRead(principal);
}

using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Hathor.Application.Auth;

// POST /api/v1/auth/register (single-account mode: conflicts once any user exists)
public sealed record RegisterCommand(string Username, string Password, bool RememberMe = false, string? DeviceLabel = null)
    : IRequest<AuthTokensDto>;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3).MaximumLength(64);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class RegisterHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IJwtTokenService jwt,
    IPlaybackStateRepository playback,
    IUserSettingsRepository settings,
    ILogger<RegisterHandler> log) : IRequestHandler<RegisterCommand, AuthTokensDto>
{
    // Single-account gate: serialize in-process so two concurrent first
    // registrations cannot both pass the AnyAsync check (DB Username unique
    // index + the DbUpdateException catch below cover the rest).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<AuthTokensDto> Handle(RegisterCommand cmd, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await HandleCoreAsync(cmd, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<AuthTokensDto> HandleCoreAsync(RegisterCommand cmd, CancellationToken ct)
    {
        if (await users.AnyAsync(ct))
        {
            // Audited (file logs): on a single-account server any
            // registration attempt after the first is someone probing.
            log.LogWarning("Rejected registration for {Username}: single-account server",
                cmd.Username.Trim());
            throw new InvalidOperationException("Registration is closed — this server allows a single account.");
        }
        var username = cmd.Username.Trim();
        if (await users.GetByUsernameAsync(username, ct) is not null)
            throw new InvalidOperationException("Username is already taken.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(cmd.Password),
            CreatedAtUtc = DateTime.UtcNow,
        };
        await users.AddAsync(user, ct);

        // Ensure per-user playback + settings rows exist from day one.
        await playback.GetOrCreateAsync(user.Id, ct);
        await settings.GetOrCreateAsync(user.Id, ct);
        try
        {
            await users.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex.GetType().Name == "DbUpdateException")
        {
            // Lost a registration race (duplicate username, or a second
            // account slipped past the gate): stay closed, never 500.
            // Matched by name to keep EF out of the Application layer.
            throw new InvalidOperationException("Registration is closed — this server allows a single account.");
        }

        var (refresh, sessionId) = await AuthHelpers.IssueSessionAsync(
            sessions, user.Id, cmd.RememberMe, cmd.DeviceLabel, null, ct);
        var access = jwt.CreateAccessToken(user.Id, user.Username, Scopes.Full, sessionId);
        return new AuthTokensDto(access, refresh, user.Username);
    }
}

// POST /api/v1/auth/login
public sealed record LoginCommand(string Username, string Password, bool RememberMe = false, string? DeviceLabel = null)
    : IRequest<AuthTokensDto>;

public sealed class LoginHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IJwtTokenService jwt,
    ILogger<LoginHandler> log) : IRequestHandler<LoginCommand, AuthTokensDto>
{
    public async Task<AuthTokensDto> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByUsernameAsync(cmd.Username.Trim(), ct);
        if (user is null || !BCrypt.Net.BCrypt.Verify(cmd.Password, user.PasswordHash))
        {
            // Audited (file logs): failed logins are rare on a single-user
            // box and always worth a look. Never logs the password.
            // Warning, not Error: the DB table is for operational failures.
            log.LogWarning("Failed login for {Username}", cmd.Username.Trim());
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        var (refresh, sessionId) = await AuthHelpers.IssueSessionAsync(
            sessions, user.Id, cmd.RememberMe, cmd.DeviceLabel, null, ct);
        var access = jwt.CreateAccessToken(user.Id, user.Username, Scopes.Full, sessionId);
        return new AuthTokensDto(access, refresh, user.Username);
    }
}

// POST /api/v1/auth/refresh (rotation with reuse detection: presenting an
// already-rotated token revokes every session — token theft response)
public sealed record RefreshCommand(string RefreshToken) : IRequest<AuthTokensDto>;

public sealed class RefreshHandler(
    IUserRepository users,
    ISessionRepository sessions,
    IJwtTokenService jwt) : IRequestHandler<RefreshCommand, AuthTokensDto>
{
    public async Task<AuthTokensDto> Handle(RefreshCommand cmd, CancellationToken ct)
    {
        var hash = AuthHelpers.Hash(cmd.RefreshToken);
        var stored = await sessions.GetByHashAsync(hash, ct)
            ?? throw new UnauthorizedAccessException("Invalid refresh token.");
        if (stored.RevokedAtUtc is not null || stored.ExpiresAtUtc <= DateTime.UtcNow)
        {
            if (stored.ReplacedById is not null)
            {
                // Rotated token replayed — assume theft, burn all sessions.
                await sessions.RevokeAllAsync(stored.UserId, ct);
                await sessions.SaveChangesAsync(ct);
            }
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        var user = await users.GetByIdAsync(stored.UserId, ct)
            ?? throw new UnauthorizedAccessException("User no longer exists.");

        stored.RevokedAtUtc = DateTime.UtcNow;
        var (refresh, freshId) = await AuthHelpers.IssueSessionAsync(
            sessions, user.Id, stored.RememberMe, stored.DeviceLabel, stored.IpAddress, ct);
        var fresh = await sessions.GetByHashAsync(AuthHelpers.Hash(refresh), ct);
        if (fresh is not null) stored.ReplacedById = fresh.Id;
        stored.LastUsedAtUtc = DateTime.UtcNow;
        await sessions.SaveChangesAsync(ct);
        var access = jwt.CreateAccessToken(user.Id, user.Username, Scopes.Full, freshId);
        return new AuthTokensDto(access, refresh, user.Username);
    }
}

// POST /api/v1/auth/change-password (requires the current password)
public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword)
    : IRequest;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}

public sealed class ChangePasswordHandler(IUserRepository users) : IRequestHandler<ChangePasswordCommand>
{
    public async Task Handle(ChangePasswordCommand cmd, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(cmd.UserId, ct)
            ?? throw new UnauthorizedAccessException("User no longer exists.");
        if (!BCrypt.Net.BCrypt.Verify(cmd.CurrentPassword, user.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect.");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(cmd.NewPassword);
        await users.SaveChangesAsync(ct);
    }
}

// POST /api/v1/auth/logout (revokes the current session)
public sealed record LogoutCommand(string? RefreshToken) : IRequest;

public sealed class LogoutHandler(ISessionRepository sessions) : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand cmd, CancellationToken ct)
    {
        if (cmd.RefreshToken is null) return;
        var stored = await sessions.GetByHashAsync(AuthHelpers.Hash(cmd.RefreshToken), ct);
        if (stored is not null)
        {
            stored.RevokedAtUtc ??= DateTime.UtcNow;
            await sessions.SaveChangesAsync(ct);
        }
    }
}

// POST /api/v1/auth/logout-all (revokes every session — log out everywhere)
public sealed record LogoutAllCommand(Guid UserId) : IRequest;

public sealed class LogoutAllHandler(ISessionRepository sessions) : IRequestHandler<LogoutAllCommand>
{
    public async Task Handle(LogoutAllCommand cmd, CancellationToken ct)
    {
        await sessions.RevokeAllAsync(cmd.UserId, ct);
        await sessions.SaveChangesAsync(ct);
    }
}

public sealed record SessionDto(
    Guid Id, bool RememberMe, string? DeviceLabel, string? IpAddress,
    DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime LastUsedAtUtc, bool Current);

public sealed record ListSessionsQuery(Guid UserId, Guid CurrentSessionId) : IRequest<IReadOnlyList<SessionDto>>;

public sealed class ListSessionsHandler(ISessionRepository sessions)
    : IRequestHandler<ListSessionsQuery, IReadOnlyList<SessionDto>>
{
    public async Task<IReadOnlyList<SessionDto>> Handle(ListSessionsQuery q, CancellationToken ct) =>
        (await sessions.ListActiveAsync(q.UserId, ct))
            .Select(s => new SessionDto(s.Id, s.RememberMe, s.DeviceLabel, s.IpAddress,
                s.CreatedAtUtc, s.ExpiresAtUtc, s.LastUsedAtUtc, s.Id == q.CurrentSessionId))
            .ToList();
}

public sealed record RevokeSessionCommand(Guid UserId, Guid SessionId) : IRequest<bool>;

public sealed class RevokeSessionHandler(ISessionRepository sessions)
    : IRequestHandler<RevokeSessionCommand, bool>
{
    public async Task<bool> Handle(RevokeSessionCommand cmd, CancellationToken ct)
    {
        var active = await sessions.ListActiveAsync(cmd.UserId, ct);
        if (!active.Any(s => s.Id == cmd.SessionId)) return false;
        await sessions.RevokeAsync(cmd.SessionId, cmd.UserId, ct);
        await sessions.SaveChangesAsync(ct);
        return true;
    }
}

public static class Scopes
{
    public const string PlayerRead = "player:read";
    public const string PlayerControl = "player:control";
    public const string LibraryRead = "library:read";
    public const string DownloadsWrite = "downloads:write";
    public const string PlaylistsWrite = "playlists:write";
    public const string LibraryWrite = "library:write";
    public static readonly string[] Full = [PlayerRead, PlayerControl, LibraryRead, DownloadsWrite, PlaylistsWrite, LibraryWrite];
    public static readonly string[] ReadOnly = [PlayerRead, LibraryRead];

    public static bool HasAll(string grantedSpaceSeparated, params string[] required)
    {
        var granted = grantedSpaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return required.All(granted.Contains);
    }
}

public static class AuthHelpers
{
    // Session lifetimes: 24h default, 30d with remember-me.
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan RememberedSessionLifetime = TimeSpan.FromDays(30);

    public static string Hash(string token)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static async Task<(string Token, Guid SessionId)> IssueSessionAsync(
        ISessionRepository repo, Guid userId, bool rememberMe,
        string? deviceLabel, string? ipAddress, CancellationToken ct)
    {
        var raw = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var now = DateTime.UtcNow;
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RefreshTokenHash = Hash(raw),
            RememberMe = rememberMe,
            DeviceLabel = string.IsNullOrWhiteSpace(deviceLabel) ? null
                : deviceLabel.Trim()[..Math.Min(128, deviceLabel.Trim().Length)],
            IpAddress = ipAddress,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + (rememberMe ? RememberedSessionLifetime : DefaultSessionLifetime),
            LastUsedAtUtc = now,
        };
        await repo.AddAsync(session, ct);
        await repo.SaveChangesAsync(ct);
        return (raw, session.Id);
    }
}

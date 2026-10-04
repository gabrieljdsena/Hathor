using FluentValidation;
using Hathor.Application.Dtos;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Settings;

public sealed record GetSettingsQuery(Guid UserId) : IRequest<UserSettingsDto>;
public sealed record UpdateSettingsCommand(
    Guid UserId,
    double? Volume,
    int? LimitDownloads,
    bool? CrossfadeEnabled,
    double? CrossfadeSeconds,
    string? LastRoute,
    string? Browser) : IRequest<UserSettingsDto>;

public sealed class UpdateSettingsValidator : AbstractValidator<UpdateSettingsCommand>
{
    public UpdateSettingsValidator()
    {
        RuleFor(x => x.Volume).InclusiveBetween(0, 1).When(x => x.Volume.HasValue);
        RuleFor(x => x.LimitDownloads).InclusiveBetween(1, 20).When(x => x.LimitDownloads.HasValue);
        RuleFor(x => x.CrossfadeSeconds).InclusiveBetween(1, 12).When(x => x.CrossfadeSeconds.HasValue);
    }
}

public sealed class SettingsHandlers(IUserSettingsRepository settings) :
    IRequestHandler<GetSettingsQuery, UserSettingsDto>,
    IRequestHandler<UpdateSettingsCommand, UserSettingsDto>
{
    public async Task<UserSettingsDto> Handle(GetSettingsQuery q, CancellationToken ct) =>
        ToDto(await settings.GetOrCreateAsync(q.UserId, ct));

    public async Task<UserSettingsDto> Handle(UpdateSettingsCommand cmd, CancellationToken ct)
    {
        var s = await settings.GetOrCreateAsync(cmd.UserId, ct);
        // Clamp defensively even though the validator rejects out-of-range
        // (external clients may bypass validation layers).
        if (cmd.Volume.HasValue) s.Volume = Math.Clamp(cmd.Volume.Value, 0, 1);
        if (cmd.LimitDownloads.HasValue) s.LimitDownloads = Math.Clamp(cmd.LimitDownloads.Value, 1, 20);
        if (cmd.CrossfadeEnabled.HasValue) s.CrossfadeEnabled = cmd.CrossfadeEnabled.Value;
        if (cmd.CrossfadeSeconds.HasValue)
            s.CrossfadeSeconds = Math.Clamp(cmd.CrossfadeSeconds.Value, 1, 12);
        if (cmd.LastRoute is not null) s.LastRoute = cmd.LastRoute;
        if (cmd.Browser is not null) s.Browser = cmd.Browser;
        await settings.SaveChangesAsync(ct);
        return ToDto(s);
    }

    internal static UserSettingsDto ToDto(Domain.Entities.UserSettings s) => new(
        s.Volume, s.LimitDownloads, s.BackgroundPath,
        s.CrossfadeEnabled, s.CrossfadeSeconds, s.LastRoute, s.Browser);
}

public sealed record GetPlayerSettingsQuery(Guid UserId) : IRequest<PlayerSettingsDto>;
public sealed record SetPlayerSettingsCommand(Guid UserId, bool CrossfadeEnabled, double CrossfadeSeconds)
    : IRequest<PlayerSettingsDto>;

public sealed class PlayerSettingsHandlers(IUserSettingsRepository settings) :    IRequestHandler<GetPlayerSettingsQuery, PlayerSettingsDto>,
    IRequestHandler<SetPlayerSettingsCommand, PlayerSettingsDto>
{
    public async Task<PlayerSettingsDto> Handle(GetPlayerSettingsQuery q, CancellationToken ct)
    {
        var s = await settings.GetOrCreateAsync(q.UserId, ct);
        return new PlayerSettingsDto(s.CrossfadeEnabled, s.CrossfadeSeconds);
    }

    public async Task<PlayerSettingsDto> Handle(SetPlayerSettingsCommand cmd, CancellationToken ct)
    {
        var s = await settings.GetOrCreateAsync(cmd.UserId, ct);
        s.CrossfadeEnabled = cmd.CrossfadeEnabled;
        s.CrossfadeSeconds = Math.Clamp(cmd.CrossfadeSeconds, 1, 12);
        await settings.SaveChangesAsync(ct);
        return new PlayerSettingsDto(s.CrossfadeEnabled, s.CrossfadeSeconds);
    }
}

public sealed record SetBackgroundCommand(Guid UserId, byte[] Bytes, string ContentType)
    : IRequest<string>;
public sealed record RemoveBackgroundCommand(Guid UserId) : IRequest<bool>;

public sealed class BackgroundHandlers(
    Ports.ILibraryStorage storage,
    IUserSettingsRepository settings) :
    IRequestHandler<SetBackgroundCommand, string>,
    IRequestHandler<RemoveBackgroundCommand, bool>
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp",
    };

    public async Task<string> Handle(SetBackgroundCommand cmd, CancellationToken ct)
    {
        if (!AllowedTypes.Contains(cmd.ContentType))
            throw new InvalidOperationException("Only JPEG, PNG, GIF, WebP or BMP images are allowed.");
        if (cmd.Bytes.Length == 0 || cmd.Bytes.Length > 10 * 1024 * 1024)
            throw new InvalidOperationException("Image must be non-empty and under 10 MB.");
        var filename = await storage.SaveBackgroundAsync(cmd.UserId, cmd.Bytes, cmd.ContentType, ct);
        var s = await settings.GetOrCreateAsync(cmd.UserId, ct);
        s.BackgroundPath = filename;
        await settings.SaveChangesAsync(ct);
        return filename;
    }

    public async Task<bool> Handle(RemoveBackgroundCommand cmd, CancellationToken ct)
    {
        await storage.DeleteBackgroundAsync(cmd.UserId, ct);
        var s = await settings.GetOrCreateAsync(cmd.UserId, ct);
        var had = s.BackgroundPath is not null;
        s.BackgroundPath = null;
        await settings.SaveChangesAsync(ct);
        return had;
    }
}

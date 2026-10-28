using FluentValidation;
using Hathor.Domain.Entities;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Player;

// Playback telemetry is a separate, server-only feed (not synced to desktop/
// mobile): Music_History keeps its "played" semantics, while this adds the
// completion/skip quality used by the Discover taste profile.
public sealed record RecordPlaybackEventCommand(
    Guid UserId,
    string SegmentId,
    string File,
    double PositionSec,
    double DurationSec,
    bool Completed,
    bool Skipped,
    DateTime? StartedAtUtc) : IRequest<bool>;

public sealed class RecordPlaybackEventValidator
    : AbstractValidator<RecordPlaybackEventCommand>
{
    public RecordPlaybackEventValidator()
    {
        RuleFor(x => x.SegmentId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.File)
            .NotEmpty()
            .MaximumLength(255)
            .Must(f => !f.Contains('/') && !f.Contains('\\') && Path.GetFileName(f) == f)
            .WithMessage("File must be a bare filename.");
        RuleFor(x => x.PositionSec)
            .GreaterThanOrEqualTo(0)
            .Must(double.IsFinite)
            .WithMessage("Position must be finite.");
        RuleFor(x => x.DurationSec)
            .GreaterThanOrEqualTo(0)
            .Must(double.IsFinite)
            .WithMessage("Duration must be finite.");
        RuleFor(x => x)
            .Must(x => !(x.Completed && x.Skipped))
            .WithMessage("Completed and skipped cannot both be true.");
    }
}

public sealed class RecordPlaybackEventHandler(IPlaybackEventRepository events)
    : IRequestHandler<RecordPlaybackEventCommand, bool>
{
    public async Task<bool> Handle(RecordPlaybackEventCommand cmd, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var started = cmd.StartedAtUtc?.ToUniversalTime() ?? now;
        if (started > now.AddMinutes(5)) started = now;
        if (started < now.AddDays(-30)) started = now.AddDays(-30);

        var duration = Math.Max(0, cmd.DurationSec);
        var position = Math.Max(0, cmd.PositionSec);
        if (duration > 0) position = Math.Min(position, duration);

        await events.AddAsync(new PlaybackEvent
        {
            UserId = cmd.UserId,
            SegmentId = cmd.SegmentId.Trim(),
            SongFile = cmd.File.Trim(),
            PositionSec = position,
            DurationSec = duration,
            Completed = cmd.Completed,
            Skipped = !cmd.Completed && cmd.Skipped,
            StartedAtUtc = started,
            CreatedAtUtc = now,
        }, ct);
        await events.SaveChangesAsync(ct);
        return true;
    }
}

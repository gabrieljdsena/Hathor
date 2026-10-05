using Hathor.Application.Dtos;
using Hathor.Application.Ports;
using Hathor.Domain.Repositories;
using MediatR;

namespace Hathor.Application.Library;

// Loudness backfill: measure library files missing analyzed LUFS, bounded
// per call so a big library drains over repeated clicks (resumable).
public sealed record BackfillLoudnessCommand(Guid UserId, int Limit = 20) : IRequest<LoudnessBackfillDto>;

public sealed class BackfillLoudnessHandler(
    ISongReadModel songs,
    ILoudnessAnalyzer analyzer,
    ILibraryStorage storage,
    ISongRecordRepository records) : IRequestHandler<BackfillLoudnessCommand, LoudnessBackfillDto>
{
    public async Task<LoudnessBackfillDto> Handle(BackfillLoudnessCommand cmd, CancellationToken ct)
    {
        var files = await songs.GetFilesMissingLoudnessAsync(
            cmd.UserId, Math.Clamp(cmd.Limit, 1, 100), ct);
        var scanned = 0;
        foreach (var file in files)
        {
            var path = storage.SongPath(cmd.UserId, file);
            var lufs = await analyzer.AnalyzeAsync(path, ct);
            if (lufs is null) continue;
            await records.SetLoudnessAsync(cmd.UserId, file, lufs.Value, ct);
            scanned++;
        }
        if (scanned > 0) await records.SaveChangesAsync(ct);
        var remaining = (await songs.GetFilesMissingLoudnessAsync(cmd.UserId, int.MaxValue, ct)).Count;
        return new LoudnessBackfillDto(scanned, remaining);
    }
}

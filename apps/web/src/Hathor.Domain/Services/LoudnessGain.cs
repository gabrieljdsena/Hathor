namespace Hathor.Domain.Services;

// Measured-loudness gain (Stage 2 normalization): per-track correction
// from analyzed integrated LUFS toward the target, clamped so a broken
// measurement can never blow out playback. Mirrored in the web audio
// engine (audio/engine.ts gainForDb) — keep the constants in sync.
public static class LoudnessGain
{
    // Streaming-style target for local playback (Spotify/YouTube use -14).
    public const double TargetLufs = -14.0;
    public const double MaxCorrectionDb = 12.0;

    public static double CorrectionDb(double? measuredLufs) =>
        measuredLufs is null || double.IsNaN(measuredLufs.Value) || double.IsInfinity(measuredLufs.Value)
            ? 0.0
            : Math.Clamp(TargetLufs - measuredLufs.Value, -MaxCorrectionDb, MaxCorrectionDb);

    public static double ToLinear(double gainDb) =>
        Math.Pow(10.0, Math.Clamp(gainDb, -MaxCorrectionDb, MaxCorrectionDb) / 20.0);
}

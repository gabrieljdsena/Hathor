using Hathor.Domain.Services;

namespace Hathor.Application.Discover;

// Discover pipeline configuration (packages/contracts/discover.md). Bound
// from the "Discovery" section; defaults keep the open-data path enabled and
// the local LLM strictly optional (off by default).
public sealed class DiscoveryOptions
{
    // Taste profile.
    public int TopArtists { get; set; } = 8;
    public int TopTracks { get; set; } = 8;
    public int TopTags { get; set; } = 5;
    public double ArtistHalfLifeDays { get; set; } = 60;
    // Completion/skip telemetry window used to weight the taste profile.
    public int PlaybackWindowDays { get; set; } = 90;
    // Clients that do not send Playback_Events (desktop/mobile) get quality
    // inferred from Music_History start-to-start gaps; explicit events win.
    public bool InferPlaybackQuality { get; set; } = true;

    // Candidate generation. MusicBrainz is capped at ~1 request/second, so
    // these defaults target ~35 calls per refresh (~35s) instead of hundreds.
    public int SeedArtistLimit { get; set; } = 4;
    public int SimilarArtistLimit { get; set; } = 10;
    public int SimilarArtistsToBrowse { get; set; } = 3;
    public int RelatedArtistsToBrowse { get; set; } = 1;
    public int RecordingsPerArtist { get; set; } = 5;
    public int SimilarRecordingSeeds { get; set; } = 5;
    public int SimilarRecordingsPerSeed { get; set; } = 3;
    public int TagRecordingsLimit { get; set; } = 40;
    public int CandidateLimit { get; set; } = 500;
    public int FallbackThreshold { get; set; } = 10;
    public int TimeBudgetSec { get; set; } = 90;

    // Ranking.
    public int MaxItems { get; set; } = 50;
    public int MaxPerArtist { get; set; } = 2;
    public double MmrLambda { get; set; } = 0.25;
    public double AudioWeight { get; set; } = 0.10;
    public long PopularityMin { get; set; } = 10_000;
    public long PopularityMax { get; set; } = 2_000_000;

    // Evaluation-driven tuning (applies a stored per-user model only after
    // enough labeled snapshots; stale models fall back to defaults).
    public bool TuningEnabled { get; set; } = true;
    public int TuningMinSnapshots { get; set; } = 10;
    public int TuningMinLabels { get; set; } = 50;
    public double TuningMargin { get; set; } = 0.02;
    public int TuningMaxAgeDays { get; set; } = 90;

    // Historical popularity profile (top played tracks -> global listen counts).
    public int PopularityProfileSeeds { get; set; } = 5;
    public int PopularityProfileMinSamples { get; set; } = 3;
    public int PopularityProfileCacheDays { get; set; } = 7;

    // Implicit feedback.
    public int DismissDays { get; set; } = 30;
    public int FeedbackDays { get; set; } = 90;
    public double LikedArtistBoost { get; set; } = 0.08;
    public double LikedSeedBoost { get; set; } = 0.08;
    public double EngagedArtistBoost { get; set; } = 0.04;

    // CLAP audio embeddings (optional sidecar; disabled by default).
    public bool AudioEmbeddingsEnabled { get; set; }
    public string AudioEmbeddingPythonPath { get; set; } = "";
    public string AudioEmbeddingModel { get; set; } = "clap-v1";
    public string AudioEmbeddingCheckpoint { get; set; } = "";
    public int AudioEmbeddingTimeoutSec { get; set; } = 180;
    public int AudioEmbeddingBatchSize { get; set; } = 8;
    public int AudioEmbeddingIntervalMinutes { get; set; } = 30;

    // Open-data clients.
    public bool MusicBrainzEnabled { get; set; } = true;
    public bool ListenBrainzEnabled { get; set; } = true;
    public string MusicBrainzBaseUrl { get; set; } = "https://musicbrainz.org/ws/2";
    public string MusicBrainzUserAgent { get; set; } =
        "Hathor/1.0 (https://github.com/gabrieljdsena/Hathor)";
    public string ListenBrainzBaseUrl { get; set; } = "https://labs.api.listenbrainz.org";
    public string ListenBrainzApiBaseUrl { get; set; } = "https://api.listenbrainz.org";
    public string ListenBrainzSimilarArtistsAlgorithm { get; set; } =
        "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30";
    public string ListenBrainzSimilarRecordingsAlgorithm { get; set; } =
        "session_based_days_7500_session_300_contribution_5_threshold_15_limit_50_skip_30";

    // No-history cold start: open tag picks before the chart fallback.
    public string[] ColdStartTags { get; set; } = ["pop", "rock", "electronic"];

    // Cache TTLs (public data only).
    public int HttpCacheDays { get; set; } = 7;
    public int IdentityCacheDays { get; set; } = 30;

    public DiscoverRankingOptions ToRankingOptions() => new()
    {
        MmrLambda = MmrLambda,
        MaxItems = MaxItems,
        MaxPerArtist = MaxPerArtist,
        PopularityMin = PopularityMin,
        PopularityMax = PopularityMax,
        AudioWeight = AudioWeight,
        LikedArtistBoost = LikedArtistBoost,
        LikedSeedBoost = LikedSeedBoost,
        EngagedArtistBoost = EngagedArtistBoost,
    };
}

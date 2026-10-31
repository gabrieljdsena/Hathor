using System.Text.Json.Serialization;
using Hathor.Application.Dtos;

namespace Hathor.Application.AiDj;

public sealed class AiDjSettings
{
    public int BatchSize { get; set; } = 3;
    public string CommentaryMode { get; set; } = "always";
    public string Language { get; set; } = "auto";
    public double DiscoveryLevel { get; set; } = 0.35;
    public string Brief { get; set; } = "";
    public string PreferredGenres { get; set; } = "";
    public string AvoidArtists { get; set; } = "";
    public double DuckLevel { get; set; } = 0.18;
    public int VoiceLeadSeconds { get; set; } = 10;
    public string? LastGreetingDate { get; set; }
}

public sealed record AiDjSettingsDto(
    int BatchSize,
    string CommentaryMode,
    string Language,
    double DiscoveryLevel,
    string Brief,
    string PreferredGenres,
    string AvoidArtists,
    double DuckLevel,
    int VoiceLeadSeconds);

public sealed record AiDjBatchDto(
    Guid Id,
    int Number,
    string Status,
    IReadOnlyList<SongDto> Tracks,
    string? OutroText,
    IReadOnlyList<Guid> OutroClipIds,
    double OutroDurationSec,
    string? IntroText,
    IReadOnlyList<Guid> IntroClipIds,
    double IntroDurationSec,
    bool ShouldComment,
    string? Mood,
    string? Reason);

public sealed record AiDjStateDto(
    bool Enabled,
    bool Configured,
    Guid? SessionId,
    string Status,
    AiDjSettingsDto Settings,
    AiDjBatchDto? CurrentBatch,
    AiDjBatchDto? NextBatch,
    string? Error);

public sealed record AiDjStartRequest(
    int? BatchSize,
    string? CommentaryMode,
    string? Language,
    double? DiscoveryLevel,
    string? Brief,
    string? PreferredGenres,
    string? AvoidArtists);

public sealed record AiDjFeedbackRequest(
    Guid? SessionId,
    Guid? BatchId,
    string? TrackFile,
    string Event);

public sealed class AiDjSessionState
{
    public Guid SessionId { get; set; }
    public string Status { get; set; } = "active";
    public AiDjSettings Settings { get; set; } = new();
    public List<AiDjBatchState> Batches { get; set; } = [];
    public Guid? CurrentBatchId { get; set; }
    public Guid? NextBatchId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? Error { get; set; }
}

public sealed class AiDjBatchState
{
    public Guid Id { get; set; }
    public int Number { get; set; }
    public string Status { get; set; } = "ready";
    public List<string> TrackFiles { get; set; } = [];
    public string? OutroText { get; set; }
    public List<Guid> OutroClipIds { get; set; } = [];
    public double OutroDurationSec { get; set; }
    public string? IntroText { get; set; }
    public List<Guid> IntroClipIds { get; set; } = [];
    public double IntroDurationSec { get; set; }
    public bool ShouldComment { get; set; }
    public string? Mood { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class AiDjCandidate
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("album")] public string Album { get; set; } = "";
    [JsonPropertyName("genre")] public string Genre { get; set; } = "";
    [JsonPropertyName("year")] public string Year { get; set; } = "";
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("play_count")] public int PlayCount { get; set; }
    [JsonPropertyName("completion_rate")] public double CompletionRate { get; set; }
    [JsonPropertyName("last_played_days_ago")] public int? LastPlayedDaysAgo { get; set; }
    [JsonPropertyName("in_playlists")] public bool InPlaylists { get; set; }
    public double Score { get; set; }
}

public sealed class AiDjPlanRequest
{
    [JsonPropertyName("session_id")] public Guid SessionId { get; set; }
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    [JsonPropertyName("batch_size")] public int BatchSize { get; set; } = 3;
    [JsonPropertyName("commentary_mode")] public string CommentaryMode { get; set; } = "always";
    [JsonPropertyName("brief")] public string Brief { get; set; } = "";
    [JsonPropertyName("first_today")] public bool FirstToday { get; set; }
    [JsonPropertyName("listener_name")] public string? ListenerName { get; set; }
    [JsonPropertyName("previous_batch")] public object? PreviousBatch { get; set; }
    [JsonPropertyName("recent_tracks")] public IReadOnlyList<object> RecentTracks { get; set; } = [];
    [JsonPropertyName("candidate_tracks")] public IReadOnlyList<AiDjCandidate> CandidateTracks { get; set; } = [];
    [JsonPropertyName("taste")] public object? Taste { get; set; }
}

public sealed class AiDjPlanResponse
{
    [JsonPropertyName("track_files")] public List<string> TrackFiles { get; set; } = [];
    [JsonPropertyName("should_comment")] public bool ShouldComment { get; set; }
    [JsonPropertyName("commentary")] public string? Commentary { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("mood")] public string? Mood { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("clips")] public List<AiDjPlanClip> Clips { get; set; } = [];
    [JsonPropertyName("outro")] public AiDjPlanSegment? Outro { get; set; }
    [JsonPropertyName("intro")] public AiDjPlanSegment? Intro { get; set; }
    [JsonPropertyName("voice_ready")] public bool VoiceReady { get; set; }
}

public sealed class AiDjPlanSegment
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("clips")] public List<AiDjPlanClip> Clips { get; set; } = [];
}

public sealed class AiDjTtsResponse
{
    [JsonPropertyName("clips")] public List<AiDjPlanClip> Clips { get; set; } = [];
    [JsonPropertyName("voice_ready")] public bool VoiceReady { get; set; }
}

public sealed class AiDjTranscribeResponse
{
    [JsonPropertyName("text")] public string? Text { get; set; }
}

public sealed class AiDjPlanClip
{
    [JsonPropertyName("audio")] public string Audio { get; set; } = "";
    [JsonPropertyName("mime")] public string Mime { get; set; } = "audio/mpeg";
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("gain")] public double Gain { get; set; } = 1.0;
}

public sealed record AiDjCandidateRow(
    string File,
    string Title,
    string? Artist,
    string? Album,
    string? Genre,
    string? Year,
    double DurationSecs,
    int PlayCount,
    int Events,
    int Completions,
    DateTime? LastPlayedUtc,
    bool InPlaylists);

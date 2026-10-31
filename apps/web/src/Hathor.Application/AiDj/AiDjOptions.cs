namespace Hathor.Application.AiDj;

public sealed class AiDjOptions
{
    public bool Enabled { get; set; } = true;
    public string AssistantBaseUrl { get; set; } = "http://127.0.0.1:5000";
    public string Token { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 3;
    public int CandidatePoolSize { get; set; } = 60;
    public string ClipDirectory { get; set; } = "data/ai-dj";
    public int MaxCommentarySeconds { get; set; } = 20;
    public int VoiceLeadSeconds { get; set; } = 10;
    public double DuckLevel { get; set; } = 0.18;
    public bool AllowInsecureTls { get; set; }
    public string? AssistantCaCertificatePath { get; set; }
}

using System.Net.Http.Json;
using System.Text.Json;
using Hathor.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.Enrichment;

// Local-LLM taste expansion (contract rule 5): asks an Ollama server (or any
// OpenAI-compatible /v1 endpoint, e.g. llama.cpp server) for songs a fan of
// the taste profile would like, in JSON mode. Output is raw title/artist
// pairs — DiscoverService iTunes-verifies each before display, so
// hallucinations never reach the user. Any failure (down, timeout, bad
// config, unparsable reply) yields [] and Discover falls back to iTunes-only.
public sealed class DiscoveryLlmOptions
{
    public bool LlmEnabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "";
    public int LlmTimeoutSec { get; set; } = 30;
}

public sealed class OllamaDiscoverySuggester(
    IHttpClientFactory httpFactory,
    DiscoveryLlmOptions options,
    ILogger<OllamaDiscoverySuggester> log) : IDiscoverySuggester
{
    public async Task<IReadOnlyList<(string Title, string Artist)>> SuggestAsync(
        IReadOnlyList<(string Artist, long Plays)> taste, int maxSuggestions,
        CancellationToken ct = default)
    {
        if (!options.LlmEnabled || taste.Count == 0 || maxSuggestions <= 0)
            return [];
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            log.LogWarning("Discovery LLM model not configured (Discovery:LlmModel) — skipping");
            return [];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.LlmTimeoutSec, 5, 300)));

            var artists = string.Join(", ", taste
                .Take(20)
                .Select(t => $"{t.Artist} ({t.Plays} plays)"));
            var body = new
            {
                model = options.Model,
                temperature = 0.7,
                stream = false,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You are a music recommender. Reply with ONLY a JSON object " +
                            "of the shape {\"suggestions\": [{\"title\": \"...\", \"artist\": \"...\"}]}. " +
                            "Only real, commercially released songs. No commentary, no markdown.",
                    },
                    new
                    {
                        role = "user",
                        content = $"A listener plays these artists most: {artists}. " +
                            $"Suggest up to {maxSuggestions} songs by OTHER artists they would " +
                            "likely enjoy but may not know. Prefer deep cuts and adjacent " +
                            "genres over the most famous hits.",
                    },
                },
            };

            var http = httpFactory.CreateClient("llm");
            using var res = await http.PostAsJsonAsync(
                options.Endpoint.TrimEnd('/') + "/chat/completions", body, timeout.Token);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(
                await res.Content.ReadAsStringAsync(timeout.Token));
            return Parse(doc, maxSuggestions);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Discovery LLM suggestion failed — falling back to iTunes-only");
            return [];
        }
    }

    internal static List<(string Title, string Artist)> Parse(JsonDocument doc, int max)
    {
        var out_ = new List<(string, string)>();
        try
        {
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0)
                return out_;
            var content = choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var c)
                    ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
                return out_;
            using var inner = JsonDocument.Parse(content);
            var root = inner.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root
                : root.TryGetProperty("suggestions", out var s) &&
                    s.ValueKind == JsonValueKind.Array ? s
                : (JsonElement?)null;
            if (arr is null)
                return out_;
            foreach (var el in arr.Value.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
                var artist = el.TryGetProperty("artist", out var a) ? a.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(artist))
                    out_.Add((title!.Trim(), artist!.Trim()));
                if (out_.Count >= max)
                    break;
            }
        }
        catch (JsonException)
        {
            // Unparsable LLM reply — treated as no suggestions.
        }
        return out_;
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hathor.Application.AiDj;
using Hathor.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Hathor.Infrastructure.AiDj;

public sealed class AiDjPlannerClient(
    IHttpClientFactory clients,
    AiDjOptions options,
    ILogger<AiDjPlannerClient> log) : IAiDjPlanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public bool IsConfigured =>
        options.Enabled &&
        !string.IsNullOrWhiteSpace(options.AssistantBaseUrl) &&
        !string.IsNullOrWhiteSpace(options.Token);

    public async Task<AiDjPlanResponse?> PlanAsync(
        AiDjPlanRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120)));
            var http = clients.CreateClient("ai-dj");
            using var message = new HttpRequestMessage(HttpMethod.Post, "api/dj/plan")
            {
                Content = JsonContent.Create(request, options: Json),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("AI DJ planner returned {Status}", (int)response.StatusCode);
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonSerializer.DeserializeAsync<AiDjPlanResponse>(stream, Json, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("AI DJ planner timed out after {Seconds}s", options.TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AI DJ planner request failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<AiDjPlanClip>> SynthesizeAsync(
        string text, string language, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(text)) return [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120)));
            var http = clients.CreateClient("ai-dj");
            using var message = new HttpRequestMessage(HttpMethod.Post, "api/dj/tts")
            {
                Content = JsonContent.Create(new { text, language }, options: Json),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("AI DJ voice retry returned {Status}", (int)response.StatusCode);
                return [];
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var tts = await JsonSerializer.DeserializeAsync<AiDjTtsResponse>(stream, Json, timeout.Token);
            return tts?.Clips ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("AI DJ voice retry timed out after {Seconds}s", options.TimeoutSeconds);
            return [];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AI DJ voice retry failed");
            return [];
        }
    }

    public async Task<string?> TranscribeAsync(
        string audioBase64, string language, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(audioBase64)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120)));
            var http = clients.CreateClient("ai-dj");
            using var message = new HttpRequestMessage(HttpMethod.Post, "api/dj/transcribe")
            {
                Content = JsonContent.Create(new { audio = audioBase64, language }, options: Json),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("AI DJ transcription returned {Status}", (int)response.StatusCode);
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var result = await JsonSerializer.DeserializeAsync<AiDjTranscribeResponse>(
                stream, Json, timeout.Token);
            return result is null ? null : (result.Text ?? "").Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("AI DJ transcription timed out after {Seconds}s", options.TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "AI DJ transcription failed");
            return null;
        }
    }
}

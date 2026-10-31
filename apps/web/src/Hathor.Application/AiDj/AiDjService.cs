using System.Text.Json;
using Hathor.Application.Dtos;
using Hathor.Application.Player;
using Hathor.Application.Ports;
using Hathor.Domain.Entities;
using Hathor.Domain.Playback;
using Hathor.Domain.Repositories;

namespace Hathor.Application.AiDj;

public sealed class AiDjService(
    IAiDjRepository repo,
    IUserSettingsRepository userSettings,
    IUserRepository users,
    AiDjCandidateService candidates,
    ISongReadModel songs,
    IPlaybackStateRepository playback,
    IPlaybackHub hub,
    IAiDjPlanner planner,
    AiDjOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] RandomBriefs =
    [
        "a warm evening set with a little bounce",
        "quiet focus, soft edges, no sad songs",
        "something adventurous but still cozy",
        "late-night drive music with a steady pulse",
        "bright indie and mellow grooves",
        "a gently nostalgic set for winding down",
        "slow-burn songs with warm textures",
        "surprise me with a few favorites and one wildcard",
    ];

    public async Task<AiDjStateDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var row = await repo.GetSessionAsync(userId, ct);
        var settings = await GetSettingsAsync(userId, ct);
        if (row is null)
            return Empty(settings);
        var state = ParseState(row);
        if (!string.Equals(state.Status, "active", StringComparison.OrdinalIgnoreCase))
            return await ToDtoAsync(userId, state, settings, ct);
        var changed = await SyncProgressAsync(userId, state, ct);
        if (!string.Equals(state.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            if (changed)
            {
                state.UpdatedAtUtc = DateTime.UtcNow;
                await SaveStateAsync(userId, state, ct);
                await hub.BroadcastAiDjStateAsync(userId, await ToDtoAsync(userId, state, settings, ct), ct);
            }
            return await ToDtoAsync(userId, state, settings, ct);
        }
        if (state.NextBatchId is not null)
        {
            var next = state.Batches.FirstOrDefault(b => b.Id == state.NextBatchId);
            if (next is not null)
                changed |= await AppendBatchToQueueAsync(userId, state, next, ct);
        }
        if (changed)
        {
            state.UpdatedAtUtc = DateTime.UtcNow;
            await SaveStateAsync(userId, state, ct);
            await hub.BroadcastAiDjStateAsync(userId, await ToDtoAsync(userId, state, settings, ct), ct);
        }
        return await ToDtoAsync(userId, state, settings, ct);
    }

    public async Task<AiDjStateDto> StartAsync(
        Guid userId, AiDjStartRequest request, CancellationToken ct)
    {
        if (!options.Enabled)
            throw new InvalidOperationException("AI DJ is disabled.");
        var settings = await MergeAndSaveSettingsAsync(userId, request, ct);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var firstToday = !string.Equals(settings.LastGreetingDate, today, StringComparison.Ordinal);
        var user = await users.GetByIdAsync(userId, ct);
        var listenerName = string.IsNullOrWhiteSpace(user?.Username) ? null : user.Username;
        var candidateSet = await candidates.BuildAsync(userId, settings, [], ct);
        if (candidateSet.Candidates.Count == 0)
            throw new InvalidOperationException("No playable library songs are available for the DJ.");

        var old = await repo.GetSessionAsync(userId, ct);
        if (old is not null)
            await repo.StopSessionAsync(userId, DateTime.UtcNow, ct);

        var state = new AiDjSessionState
        {
            SessionId = Guid.NewGuid(),
            Status = "active",
            Settings = settings,
            StartedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        var created = await CreateBatchAsync(
            userId, state, 1, null, null, candidateSet, [],
            firstToday, firstToday ? listenerName : null, ct);
        var first = created.Batch;
        if (first.TrackFiles.Count == 0)
            throw new InvalidOperationException("The DJ could not choose a first set.");
        first.Status = "ready";
        state.Batches.Add(first);
        state.CurrentBatchId = first.Id;
        if (firstToday)
        {
            settings.LastGreetingDate = today;
            await PersistSettingsAsync(userId, settings, ct);
        }
        var row = new AiDjSessionRecord
        {
            UserId = userId,
            SessionId = state.SessionId,
            Status = "active",
            StateJson = JsonSerializer.Serialize(state, Json),
            StartedAtUtc = state.StartedAtUtc,
            UpdatedAtUtc = state.UpdatedAtUtc,
        };
        await repo.SaveSessionAsync(row, ct);
        var dto = await ToDtoAsync(userId, state, ToDto(settings), ct);
        await hub.BroadcastAiDjStateAsync(userId, dto, ct);
        return dto;
    }

    public async Task<AiDjStateDto> StopAsync(Guid userId, CancellationToken ct)
    {
        var row = await repo.GetSessionAsync(userId, ct);
        var settings = await GetSettingsAsync(userId, ct);
        if (row is null) return Empty(settings);
        var state = ParseState(row);
        state.Status = "stopped";
        state.UpdatedAtUtc = DateTime.UtcNow;
        row.Status = "stopped";
        row.UpdatedAtUtc = state.UpdatedAtUtc;
        row.StateJson = JsonSerializer.Serialize(state, Json);
        await repo.SaveSessionAsync(row, ct);
        var dto = await ToDtoAsync(userId, state, settings, ct);
        await hub.BroadcastAiDjStateAsync(userId, dto, ct);
        return dto;
    }

    public async Task AddFeedbackAsync(
        Guid userId, AiDjFeedbackRequest request, CancellationToken ct)
    {
        var row = await repo.GetSessionAsync(userId, ct);
        var state = row is null ? null : ParseState(row);
        var eventName = string.IsNullOrWhiteSpace(request.Event) ? "unknown" : request.Event.Trim().ToLowerInvariant();
        await repo.AddFeedbackAsync(new AiDjFeedbackRecord
        {
            UserId = userId,
            SessionId = request.SessionId ?? state?.SessionId ?? Guid.Empty,
            BatchId = request.BatchId,
            TrackFile = string.IsNullOrWhiteSpace(request.TrackFile) ? null : request.TrackFile,
            Event = eventName.Length > 32 ? eventName[..32] : eventName,
            CreatedAtUtc = DateTime.UtcNow,
        }, ct);
    }

    public async Task EnsureNextBatchAsync(Guid userId, CancellationToken ct)
    {
        var row = await repo.GetSessionAsync(userId, ct);
        if (row is null) return;
        var state = ParseState(row);
        if (!string.Equals(state.Status, "active", StringComparison.OrdinalIgnoreCase)) return;
        var current = state.Batches.FirstOrDefault(b => b.Id == state.CurrentBatchId);
        if (current is null)
        {
            current = state.Batches.LastOrDefault();
            if (current is null) return;
            state.CurrentBatchId = current.Id;
        }
        var existing = state.Batches.FirstOrDefault(b => b.Id == state.NextBatchId);
        if (existing is not null)
        {
            var voiceChanged = await EnsureTransitionVoiceAsync(userId, state, current, existing, ct);
            if (voiceChanged)
            {
                state.UpdatedAtUtc = DateTime.UtcNow;
                await SaveStateAsync(userId, state, ct);
                await hub.BroadcastAiDjStateAsync(
                    userId, await ToDtoAsync(userId, state, ToDto(state.Settings), ct), ct);
            }
            await AppendBatchToQueueAsync(userId, state, existing, ct);
            return;
        }

        var introChanged = await EnsureTransitionVoiceAsync(userId, state, current, null, ct);
        if (introChanged)
        {
            state.UpdatedAtUtc = DateTime.UtcNow;
            await SaveStateAsync(userId, state, ct);
        }

        var exclude = state.Batches.SelectMany(b => b.TrackFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateSet = await candidates.BuildAsync(userId, state.Settings, exclude, ct);
        if (candidateSet.Candidates.Count == 0) return;
        var previous = await BatchPayloadAsync(userId, state.SessionId, current, ct);
        var nextNumber = state.Batches.Count == 0 ? 1 : state.Batches.Max(b => b.Number) + 1;
        var created = await CreateBatchAsync(
            userId, state, nextNumber, previous, current, candidateSet, exclude,
            false, null, ct);
        var next = created.Batch;
        if (next.TrackFiles.Count == 0)
        {
            state.Error = "The DJ could not prepare the next set.";
            await SaveStateAsync(userId, state, ct);
            return;
        }
        current.OutroText = created.OutroText;
        current.OutroClipIds = created.OutroClipIds;
        current.OutroDurationSec = created.OutroDurationSec;
        state.Batches.Add(next);
        state.NextBatchId = next.Id;
        state.Error = null;
        state.UpdatedAtUtc = DateTime.UtcNow;
        await SaveStateAsync(userId, state, ct);
        await AppendBatchToQueueAsync(userId, state, next, ct);
        var dto = await ToDtoAsync(userId, state, ToDto(state.Settings), ct);
        await hub.BroadcastAiDjStateAsync(userId, dto, ct);
    }

    private async Task<bool> EnsureTransitionVoiceAsync(
        Guid userId, AiDjSessionState state, AiDjBatchState current,
        AiDjBatchState? next, CancellationToken ct)
    {
        if (!planner.IsConfigured) return false;
        var changed = false;
        if (!string.IsNullOrWhiteSpace(current.OutroText) && current.OutroClipIds.Count == 0)
        {
            var clips = await planner.SynthesizeAsync(
                current.OutroText, state.Settings.Language, ct);
            foreach (var clip in clips)
            {
                var saved = await SaveClipAsync(userId, current.Id, clip, ct);
                if (saved is null) continue;
                current.OutroClipIds.Add(saved.Id);
                current.OutroDurationSec += saved.DurationSec;
                changed = true;
            }
        }
        if (!string.IsNullOrWhiteSpace(current.IntroText) && current.IntroClipIds.Count == 0)
        {
            var clips = await planner.SynthesizeAsync(
                current.IntroText, state.Settings.Language, ct);
            foreach (var clip in clips)
            {
                var saved = await SaveClipAsync(userId, current.Id, clip, ct);
                if (saved is null) continue;
                current.IntroClipIds.Add(saved.Id);
                current.IntroDurationSec += saved.DurationSec;
                changed = true;
            }
        }
        if (next is not null && !string.IsNullOrWhiteSpace(next.IntroText) &&
            next.IntroClipIds.Count == 0)
        {
            var clips = await planner.SynthesizeAsync(
                next.IntroText, state.Settings.Language, ct);
            foreach (var clip in clips)
            {
                var saved = await SaveClipAsync(userId, next.Id, clip, ct);
                if (saved is null) continue;
                next.IntroClipIds.Add(saved.Id);
                next.IntroDurationSec += saved.DurationSec;
                changed = true;
            }
        }
        return changed;
    }

    public async Task<AiDjSettingsDto> GetSettingsAsync(Guid userId, CancellationToken ct)
    {
        var stored = await userSettings.GetOrCreateAsync(userId, ct);
        var settings = ParseSettings(stored.AiDjSettingsJson);
        return ToDto(settings);
    }

    public async Task<AiDjSettingsDto> UpdateSettingsAsync(
        Guid userId, AiDjStartRequest request, CancellationToken ct) =>
        ToDto(await MergeAndSaveSettingsAsync(userId, request, ct));

    public async Task<IReadOnlyList<string>?> GetSessionFilesAsync(
        Guid userId, CancellationToken ct)
    {
        var row = await repo.GetSessionAsync(userId, ct);
        if (row is null) return null;
        var state = ParseState(row);
        if (!string.Equals(state.Status, "active", StringComparison.OrdinalIgnoreCase))
            return null;
        var current = state.Batches.FirstOrDefault(b => b.Id == state.CurrentBatchId);
        var next = state.Batches.FirstOrDefault(b => b.Id == state.NextBatchId);
        var files = new List<string>();
        if (current is not null) files.AddRange(current.TrackFiles);
        if (next is not null)
            foreach (var file in next.TrackFiles)
                if (!files.Contains(file, StringComparer.OrdinalIgnoreCase)) files.Add(file);
        return files.Count == 0 ? null : files;
    }

    public Task<string?> TranscribeAsync(
        string audioBase64, string? language, CancellationToken ct)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "pt-BR" : language;
        return planner.TranscribeAsync(audioBase64, lang, ct);
    }

    public async Task<(string Path, string Mime)?> GetClipAsync(
        Guid userId, Guid clipId, CancellationToken ct)
    {
        var clip = await repo.GetClipAsync(userId, clipId, ct);
        if (clip is null || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
            return null;
        return (clip.FilePath, clip.Mime);
    }

    private async Task<AiDjStateDto> ToDtoAsync(
        Guid userId, AiDjSessionState state, AiDjSettingsDto settings, CancellationToken ct)
    {
        var current = state.Batches.FirstOrDefault(b => b.Id == state.CurrentBatchId);
        var next = state.Batches.FirstOrDefault(b => b.Id == state.NextBatchId);
        var currentDto = await ToBatchDtoAsync(userId, current, ct);
        var nextDto = await ToBatchDtoAsync(userId, next, ct);
        return new AiDjStateDto(
            options.Enabled,
            planner.IsConfigured,
            state.SessionId,
            state.Status,
            settings,
            currentDto,
            nextDto,
            state.Error);
    }

    private async Task<AiDjBatchDto?> ToBatchDtoAsync(
        Guid userId, AiDjBatchState? batch, CancellationToken ct)
    {
        if (batch is null) return null;
        var tracks = await songs.GetManyAsync(userId, batch.TrackFiles, includeCover: false, ct);
        return new AiDjBatchDto(
            batch.Id, batch.Number, batch.Status, tracks,
            batch.OutroText, batch.OutroClipIds, batch.OutroDurationSec,
            batch.IntroText, batch.IntroClipIds, batch.IntroDurationSec,
            batch.ShouldComment, batch.Mood, batch.Reason);
    }

    private async Task<(AiDjBatchState Batch, string? OutroText,
        List<Guid> OutroClipIds, double OutroDurationSec)> CreateBatchAsync(
        Guid userId, AiDjSessionState state, int number, object? previous,
        AiDjBatchState? previousBatch, AiDjCandidateSet candidateSet,
        IReadOnlyCollection<string> exclude, bool firstToday, string? listenerName,
        CancellationToken ct)
    {
        var batch = new AiDjBatchState
        {
            Id = Guid.NewGuid(),
            Number = number,
            Status = "planning",
            CreatedAtUtc = DateTime.UtcNow,
        };
        var wanted = Random.Shared.Next(3, 5);
        AiDjPlanResponse? plan = null;
        if (planner.IsConfigured)
        {
            plan = await planner.PlanAsync(new AiDjPlanRequest
            {
                SessionId = state.SessionId,
                Language = state.Settings.Language,
                BatchSize = wanted,
                CommentaryMode = "always",
                Brief = state.Settings.Brief,
                FirstToday = firstToday,
                ListenerName = listenerName,
                PreviousBatch = previous,
                RecentTracks = candidateSet.RecentTracks,
                CandidateTracks = candidateSet.Candidates,
                Taste = candidateSet.Taste,
            }, ct);
        }
        var allowed = new HashSet<string>(candidateSet.Candidates.Select(c => c.File), StringComparer.OrdinalIgnoreCase);
        var picks = new List<string>();
        if (plan is not null)
        {
            foreach (var file in plan.TrackFiles ?? [])
            {
                if (picks.Count >= wanted) break;
                if (string.IsNullOrWhiteSpace(file) || !allowed.Contains(file)) continue;
                if (exclude.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
                if (picks.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
                picks.Add(file);
            }
        }
        if (picks.Count < wanted)
        {
            foreach (var file in AiDjCandidateService.FallbackPicks(
                candidateSet.Candidates, wanted, [.. exclude, .. picks]))
            {
                if (picks.Count >= wanted) break;
                if (!picks.Contains(file, StringComparer.OrdinalIgnoreCase)) picks.Add(file);
            }
        }
        batch.TrackFiles = picks;
        var fallbackTexts = await FallbackSegmentTextsAsync(
            userId, previousBatch, firstToday, listenerName, ct);
        var outroText = StringOrFallback(plan?.Outro?.Text, fallbackTexts.Outro);
        var introText = StringOrFallback(
            plan?.Intro?.Text ?? plan?.Commentary, fallbackTexts.Intro);
        batch.IntroText = introText;
        batch.ShouldComment = !string.IsNullOrWhiteSpace(introText);
        batch.Mood = plan?.Mood;
        batch.Reason = plan?.Reason;

        var outroClips = plan?.Outro?.Clips ?? [];
        var introClips = plan?.Intro?.Clips ?? plan?.Clips ?? [];
        if (planner.IsConfigured)
        {
            if (outroText is not null && outroClips.Count == 0)
                outroClips = (await planner.SynthesizeAsync(outroText, state.Settings.Language, ct)).ToList();
            if (introText is not null && introClips.Count == 0)
                introClips = (await planner.SynthesizeAsync(introText, state.Settings.Language, ct)).ToList();
        }

        var outroIds = new List<Guid>();
        var outroDuration = 0.0;
        foreach (var clip in outroClips)
        {
            var saved = await SaveClipAsync(userId, batch.Id, clip, ct);
            if (saved is null) continue;
            outroIds.Add(saved.Id);
            outroDuration += saved.DurationSec;
        }
        foreach (var clip in introClips)
        {
            var saved = await SaveClipAsync(userId, batch.Id, clip, ct);
            if (saved is null) continue;
            batch.IntroClipIds.Add(saved.Id);
            batch.IntroDurationSec += saved.DurationSec;
        }
        batch.Status = "ready";
        return (batch, outroText, outroIds, outroDuration);
    }

    private static string? StringOrFallback(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private async Task<(string? Outro, string? Intro)> FallbackSegmentTextsAsync(
        Guid userId, AiDjBatchState? previousBatch,
        bool firstToday, string? listenerName, CancellationToken ct)
    {
        string? outro = null;
        string? intro = null;
        if (previousBatch is not null && previousBatch.TrackFiles.Count > 0)
        {
            var previous = await songs.GetManyAsync(
                userId, previousBatch.TrackFiles.TakeLast(1), includeCover: false, ct);
            var last = previous.FirstOrDefault();
            outro = last is null
                ? "That was the last set."
                : $"That was {last.Title} by {last.Artist}.";
        }
        intro = "Here comes the next set.";
        if (firstToday && previousBatch is null)
        {
            intro = listenerName is not null && Random.Shared.Next(2) == 0
                ? $"Welcome back, {listenerName}. {intro}"
                : $"Welcome back. {intro}";
        }
        return (outro, intro);
    }

    private async Task<AiDjClipRecord?> SaveClipAsync(
        Guid userId, Guid batchId, AiDjPlanClip clip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clip.Audio)) return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(clip.Audio);
        }
        catch (FormatException)
        {
            return null;
        }
        if (bytes.Length == 0 || bytes.Length > 12 * 1024 * 1024) return null;
        var mime = string.IsNullOrWhiteSpace(clip.Mime) ? "audio/mpeg" : clip.Mime.Trim();
        var ext = mime.Contains("wav", StringComparison.OrdinalIgnoreCase) ? ".wav" : ".mp3";
        var id = Guid.NewGuid();
        var dir = Path.GetFullPath(Path.Combine(options.ClipDirectory, userId.ToString("N")));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{batchId:N}-{id:N}{ext}");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        catch
        {
            return null;
        }
        var record = new AiDjClipRecord
        {
            Id = id,
            UserId = userId,
            FilePath = path,
            Mime = mime,
            DurationSec = Math.Max(0, clip.Duration),
            Text = string.IsNullOrWhiteSpace(clip.Text) ? (clip.Text ?? "") : clip.Text.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
        };
        await repo.AddClipAsync(record, ct);
        return record;
    }

    private async Task<bool> SyncProgressAsync(
        Guid userId, AiDjSessionState state, CancellationToken ct)
    {
        var player = await playback.GetOrCreateAsync(userId, ct);
        var currentFile = player.CurrentFile;
        if (string.IsNullOrWhiteSpace(currentFile)) return false;
        var sourceIsDj = player.Source?.Type == QueueSourceTypes.AiDj;
        var sourceMatches = sourceIsDj &&
            string.Equals(player.Source?.Id, state.SessionId.ToString(), StringComparison.OrdinalIgnoreCase);
        var hadPlayback = state.Batches.Any(b => b.Status == "playing");
        if (hadPlayback && !sourceIsDj)
        {
            state.Status = "stopped";
            state.Error = "Playback switched to another source.";
            return true;
        }
        var match = state.Batches.FirstOrDefault(b =>
            b.TrackFiles.Contains(currentFile, StringComparer.OrdinalIgnoreCase));
        var changed = false;
        if (match is null)
        {
            if (hadPlayback && sourceIsDj && !sourceMatches &&
                !string.Equals(state.Status, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                state.Status = "stopped";
                state.Error = "Playback switched to another source.";
                changed = true;
            }
            else if (hadPlayback && sourceMatches &&
                !string.Equals(state.Status, "stopped", StringComparison.OrdinalIgnoreCase))
            {
                state.Status = "stopped";
                state.Error = "Playback left the DJ queue.";
                changed = true;
            }
            return changed;
        }
        if (match.Id != state.CurrentBatchId)
        {
            foreach (var batch in state.Batches.Where(b => b.Number < match.Number))
                batch.Status = "completed";
            state.CurrentBatchId = match.Id;
            match.Status = "playing";
            var following = state.Batches
                .Where(b => b.Number > match.Number)
                .OrderBy(b => b.Number)
                .FirstOrDefault();
            state.NextBatchId = following?.Id;
            changed = true;
        }
        else if (match.Status != "playing")
        {
            match.Status = "playing";
            changed = true;
        }
        return changed;
    }

    private async Task<bool> AppendBatchToQueueAsync(
        Guid userId, AiDjSessionState state, AiDjBatchState batch, CancellationToken ct)
    {
        var player = await playback.GetOrCreateAsync(userId, ct);
        if (player.Source?.Type != QueueSourceTypes.AiDj) return false;
        if (!string.Equals(player.Source.Id, state.SessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;
        var existing = new HashSet<string>(player.NextFiles, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(player.CurrentFile)) existing.Add(player.CurrentFile);
        var added = false;
        foreach (var file in batch.TrackFiles)
        {
            if (!existing.Add(file)) continue;
            player.NextFiles.Add(file);
            added = true;
        }
        if (!added) return false;
        player.IsCustomQueue = true;
        await playback.SaveChangesAsync(ct);
        QueueVersions.Bump(userId);
        var dto = await PlayerHelpers.ToDtoAsync(player, songs, userId, ct);
        await hub.BroadcastStateAsync(userId, dto, ct);
        await hub.BroadcastQueueAsync(userId, dto.Queue, dto.QueueVersion, ct);
        return true;
    }

    private async Task<object> BatchPayloadAsync(
        Guid userId, Guid sessionId, AiDjBatchState batch, CancellationToken ct)
    {
        var tracks = await songs.GetManyAsync(
            userId, batch.TrackFiles.TakeLast(1), includeCover: false, ct);
        var feedback = await repo.GetFeedbackAsync(userId, sessionId, 20, ct);
        return new
        {
            track_count = batch.TrackFiles.Count,
            tracks = tracks.Select(s => new
            {
                file = s.File,
                title = s.Title,
                artist = s.Artist,
                album = s.Album,
                genre = s.Genre,
            }).ToArray(),
            feedback = feedback.Select(f => new
            {
                track_file = f.TrackFile,
                batch_id = f.BatchId,
                event_name = f.Event,
            }).ToArray(),
        };
    }

    private async Task<AiDjSettings> MergeAndSaveSettingsAsync(
        Guid userId, AiDjStartRequest request, CancellationToken ct)
    {
        var stored = await userSettings.GetOrCreateAsync(userId, ct);
        var settings = ParseSettings(stored.AiDjSettingsJson);
        settings.BatchSize = Random.Shared.Next(3, 5);
        settings.CommentaryMode = "always";
        var requestedLanguage = NormalizeLanguage(request.Language);
        settings.Language = requestedLanguage == "auto"
            ? (Random.Shared.Next(2) == 0 ? "pt-BR" : "en-US")
            : requestedLanguage;
        settings.DiscoveryLevel = request.DiscoveryLevel.HasValue
            ? Math.Clamp(request.DiscoveryLevel.Value, 0, 1)
            : 0.2 + Random.Shared.NextDouble() * 0.4;
        settings.Brief = string.IsNullOrWhiteSpace(request.Brief)
            ? RandomBriefs[Random.Shared.Next(RandomBriefs.Length)]
            : request.Brief.Trim();
        settings.PreferredGenres = request.PreferredGenres?.Trim() ?? "";
        settings.AvoidArtists = request.AvoidArtists?.Trim() ?? "";
        settings.DuckLevel = Math.Clamp(settings.DuckLevel, 0.05, 0.6);
        settings.VoiceLeadSeconds = Math.Clamp(settings.VoiceLeadSeconds, 5, 20);
        await PersistSettingsAsync(userId, settings, ct);
        return settings;
    }

    private async Task PersistSettingsAsync(
        Guid userId, AiDjSettings settings, CancellationToken ct)
    {
        var stored = await userSettings.GetOrCreateAsync(userId, ct);
        stored.AiDjSettingsJson = JsonSerializer.Serialize(settings, Json);
        await userSettings.SaveChangesAsync(ct);
    }

    private AiDjSettings ParseSettings(string? json)
    {
        var fallback = new AiDjSettings
        {
            BatchSize = Math.Clamp(options.BatchSize, 3, 4),
            CommentaryMode = "always",
            DuckLevel = Math.Clamp(options.DuckLevel, 0.05, 0.6),
            VoiceLeadSeconds = Math.Clamp(options.VoiceLeadSeconds, 5, 20),
        };
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try
        {
            var parsed = JsonSerializer.Deserialize<AiDjSettings>(json, Json);
            if (parsed is null) return fallback;
            parsed.BatchSize = Math.Clamp(parsed.BatchSize, 3, 4);
            parsed.CommentaryMode = "always";
            parsed.Language = NormalizeLanguage(parsed.Language);
            parsed.DiscoveryLevel = Math.Clamp(parsed.DiscoveryLevel, 0, 1);
            parsed.DuckLevel = Math.Clamp(parsed.DuckLevel > 0 ? parsed.DuckLevel : fallback.DuckLevel, 0.05, 0.6);
            parsed.VoiceLeadSeconds = Math.Clamp(
                parsed.VoiceLeadSeconds > 0 ? parsed.VoiceLeadSeconds : fallback.VoiceLeadSeconds, 5, 20);
            return parsed;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string NormalizeLanguage(string? value)
    {
        var language = (value ?? "auto").Trim().ToLowerInvariant().Replace('_', '-');
        if (language.StartsWith("pt")) return "pt-BR";
        if (language.StartsWith("en")) return "en-US";
        return "auto";
    }

    private static AiDjSettingsDto ToDto(AiDjSettings s) => new(
        s.BatchSize, s.CommentaryMode, s.Language, s.DiscoveryLevel, s.Brief,
        s.PreferredGenres, s.AvoidArtists, s.DuckLevel, s.VoiceLeadSeconds);

    private AiDjStateDto Empty(AiDjSettingsDto settings) => new(
        options.Enabled, planner.IsConfigured, null, "inactive", settings, null, null, null);

    private static AiDjSessionState ParseState(AiDjSessionRecord row)
    {
        try
        {
            return JsonSerializer.Deserialize<AiDjSessionState>(row.StateJson, Json)
                ?? new AiDjSessionState { SessionId = row.SessionId, Status = row.Status };
        }
        catch (JsonException)
        {
            return new AiDjSessionState { SessionId = row.SessionId, Status = row.Status };
        }
    }

    private async Task SaveStateAsync(
        Guid userId, AiDjSessionState state, CancellationToken ct)
    {
        await repo.SaveSessionAsync(new AiDjSessionRecord
        {
            UserId = userId,
            SessionId = state.SessionId,
            Status = state.Status,
            StateJson = JsonSerializer.Serialize(state, Json),
            StartedAtUtc = state.StartedAtUtc,
            UpdatedAtUtc = state.UpdatedAtUtc,
        }, ct);
    }
}

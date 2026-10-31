# AI DJ (Vohithe) — web contract

The AI DJ picks 3-4 local-library tracks per batch and always plays two
Vohithe segments over the transition: an outro recap of the finished batch,
then an intro for the next batch.

## Scope

- Web only for the full voice-over experience.
- Tracks come from `Songs`; Discover’s out-of-library items are not playable.
- The server remains the source of truth for the playback queue.
- Commentary clips are not songs: they never enter `Music_History`,
  `Playback_Events`, the queue, or the library.

## Endpoints

| Route | Policy | Purpose |
| --- | --- | --- |
| `POST /api/v1/ai-dj/start` | `player:control` | create a session, plan the first batch, return state |
| `GET /api/v1/ai-dj/state` | `player:read` | current/next batch, settings, clip ids, status |
| `POST /api/v1/ai-dj/stop` | `player:control` | end the session; leave the current queue |
| `POST /api/v1/ai-dj/feedback` | `player:control` | like/dislike/new-set signals |
| `GET /api/v1/ai-dj/settings` | `player:read` | DJ preferences |
| `PUT /api/v1/ai-dj/settings` | `player:control` | update DJ preferences |
| `GET /api/v1/ai-dj/clips/{id}/audio` | media token | range-stream a commentary clip |

The client starts playback after `start` with:
`PUT /player/queue` + `POST /player/play`, source `{ type: "ai_dj", id: sessionId }`.

## Batch flow

1. Start plans batch 1 synchronously and generates its intro.
2. A background prefetch plans batch 2 while batch 1 plays, then appends its
   files to `Playback_State.NextFiles`.
3. The same prefetch generates batch 1’s outro recap and batch 2’s intro, plus
   both TTS clip sets. This happens while the current batch is still playing so
   there is no delay at the boundary.
4. Near the end of the last track of batch N, the web player starts batch N’s
   outro followed by batch N+1’s intro through a separate voice bus. Music stays
   ducked; the normal crossfade still advances the server queue.
5. Commentary is always on while the DJ is active. Auto/Off are not exposed.
6. If TTS is not ready at the boundary, the player holds the transition and
   retries voice synthesis. It starts the outro → intro sequence and advances
   only after both clip sets exist.
7. Each batch randomly selects 3 or 4 tracks. A new session randomly picks its
   language, discovery level, and vibe brief unless the user supplied a brief.
8. The first session of the local day asks Vohithe to greet the player once in
   the first intro. The name is optional and chosen naturally by the model.
9. A new source or a queue edit outside DJ mode ends the session.

## Data sources

Hathor builds the candidate pool and taste snapshot from:

- `Music_History` (recency/frequency),
- `Playback_Events` (completion/skip quality),
- `Song_Playlist` + `Playlists` (curation),
- `Rec_Feedback` (discover signals),
- `Daily_Mix` and library metadata,
- current playback context.

Vohithe may also use her own `user_prefs` and `user_profile`, but she may choose
only files supplied in `candidate_tracks`.

## Assistant contract

`POST /api/dj/plan` on `ai-waifu-assistant`, Bearer `DJ_API_TOKEN`.

Request: `session_id`, `language`, `batch_size`, `brief`, `first_today`,
`listener_name`, `previous_batch`, `recent_tracks`, `candidate_tracks`, `taste`.

Response: `track_files`, `language`, `mood`, `reason`, `voice_ready`, plus
`outro` and `intro` objects. Each segment has `text` and `clips`
(`audio` base64, `mime`, `duration`, `text`, `gain`). The outro is null/empty
for the first batch; every batch has an intro.

`POST /api/dj/tts` accepts `{text, language}` and returns clips for one segment.
Hathor uses it to retry failed voice synthesis without replanning tracks.

`POST /api/dj/transcribe` accepts `{audio, language}` with a short base64 WAV
and returns `{text}`. The web DJ page uses it for spoken vibe input.

## Configuration

```json
{
  "AiDj": {
    "Enabled": true,
    "AssistantBaseUrl": "https://127.0.0.1:5000",
    "Token": "<DJ_API_TOKEN>",
    "TimeoutSeconds": 30,
    "BatchSize": 3,
    "ClipDirectory": "data/ai-dj",
    "VoiceLeadSeconds": 10,
    "DuckLevel": 0.18,
    "AllowInsecureTls": false
  }
}
```

`Token` belongs in `appsettings.Secrets.json` or `AiDj__Token`.
`AllowInsecureTls` is for local self-signed certificates only.

## Fallbacks

- Planner unavailable: local deterministic selection, template outro/intro text.
- Invalid assistant picks: validate against the candidate pool and repair from
  the fallback list.
- TTS unavailable: keep the selected tracks, store the segment text, retry
  `/api/dj/tts`, and hold the boundary until both clip sets are ready.
- Next batch tracks not ready: rebuild/prefetch before the boundary; if still
  unavailable, hold and show the waiting state.
- Manual skip/stop cancels the pending transition and clears the hold.

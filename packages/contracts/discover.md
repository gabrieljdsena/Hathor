# Discover contract (recommendations for songs NOT in the library)

Out-of-library recommendations. Daily Mix covers the known library; Discover
covers everything else. All three apps (web first, then desktop + mobile)
share this wire shape and these rules.

## Endpoints (web; others map 1:1)

- `GET /discover` → `{date, items[], cached}`
  - Auth: `library:read`. Lazy once-per-day semantics (same as Daily Mix):
    today's `Discover_Cache` row is served when present, otherwise recomputed
    on read. No scheduler — there is no Hangfire in this codebase yet, so no
    pre-warm job; a daily worker can be added later without changing this shape.
- `POST /discover/refresh` → 200 + fresh `{date, items[], cached:false}`
  - Auth: `library:write` (same policy as Daily Mix regenerate).
- `POST /discover/download {title, artist}` → existing `POST /downloads`
  payload (Phase 2 wires the button; the shape is already compatible).

## Item shape

```json
{
  "title": "Midnight City",
  "artist": "M83",
  "album": "Hurry Up, We're Dreaming",
  "year": "2011",
  "genre": "Electronic",
  "artworkUrl": "https://…/600x600bb.jpg",
  "source": "artist | chart | llm",
  "score": 0.93
}
```

- `source`: which candidate generator produced the item.
  - `artist` — other tracks by a top artist from the listener's history
    (iTunes `search?term={artist}&entity=song`, exact-artist filter).
  - `chart` — iTunes RSS `topsongs` editorial picks, resolved via search.
  - `llm` — local-LLM taste expansion (llama.cpp server, or any
    OpenAI-compatible `/v1` endpoint), iTunes-verified; unverified
    suggestions are dropped, never shown.
- `score` 0..1, descending. Formula (Phase 1):
  `0.7 * artistAffinity + 0.3 * sourceWeight`
  (`artist`=1.0, `chart`=0.5; chart items have 0 affinity).

## Rules every app follows

1. **Taste profile** = top artists by `Music_History` play counts joined to
   `Songs` for artist names (`Unknown`/blank excluded). Same ranking query
   family as the Daily Mix (`GROUP BY … ORDER BY cnt DESC`).
2. **Never recommend the owned library**: drop candidates fuzzy-matching any
   `Songs` title+artist pair, and anything in-flight in the download queue
   (`queued`/`downloading`/`processing`). Normalization: lowercase, trim,
   strip `(…)`/`[…]` segments, collapse whitespace.
3. **Diversity cap**: max 2 items per artist, max 30 items total.
4. **Empty history** still returns chart picks (never 500, never empty-shaped:
   `{date, items, cached}` always).
5. **LLM suggestions are candidates, not results**: each must resolve via
   iTunes Search before display (hallucination filter).
6. Downloading a recommendation uses the normal ingest pipeline
   (320k MP3 + iTunes enrichment) — Discover never writes files itself.

## Configuration (web)

`Discovery` section in `appsettings.json` (all optional, shown with defaults):

```json
"Discovery": {
  "LlmEnabled": true,
  "Endpoint": "http://localhost:1234/v1",
  "Model": "qwen3.5-4b-uncensored",
  "LlmTimeoutSec": 30,
  "LlmMaxTokens": 1000
}
```

- `Endpoint` is the llama.cpp server base URL (`/chat/completions` is
  appended). Any OpenAI-compatible endpoint works (`response_format:
  json_object`).
- Requests carry `chat_template_kwargs: {enable_thinking: false}` plus a token
  cap: reasoning models otherwise burn the whole budget on thinking traces
  and return empty content (verified live against llama-server).
- `Model` is the server model alias. Empty disables LLM suggestions (with a
  log warning) — Discover runs iTunes-only.
- Any LLM failure (down, timeout, bad reply) silently yields zero suggestions;
  the endpoint still returns iTunes candidates.

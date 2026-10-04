# Discover contract (recommendations for songs NOT in the library)

Out-of-library recommendations. Daily Mix covers the known library; Discover
covers everything else. All three apps (web first, then desktop + mobile)
share this wire shape and these rules.

## Endpoints (web; others map 1:1)

- `GET /discover` → `{date, items[], cached}`
  - Auth: `library:read`. Computed on demand in Phase 1 (6h in-memory cache);
    Phase 3 moves to a `Discover_Cache` table + Hangfire daily job.
- `POST /discover/refresh` → 202 + fresh `{date, items[], cached:false}`
  (Phase 3; Phase 1 has no refresh — `GET` always recomputes past cache TTL).
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
  - `llm` — local-LLM taste expansion, iTunes-verified (Phase 2; unverified
    suggestions are dropped, never shown).
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

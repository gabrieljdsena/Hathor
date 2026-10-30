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
  - The cache row carries the ranker `ModelVersion`. A mismatch (deploy/new
    pipeline) forces an immediate recompute, so stale-algorithm cards are
    never served for the rest of the day. An empty/all-filtered cached list
    also recomputes instead of stranding the page.
  - With no listening history, Discover uses configurable `ColdStartTags`
    (open MusicBrainz tag picks) and then the chart fallback — the endpoint
    always tries to return recommendations at any stage.
- `POST /discover/refresh` → 200 + fresh `{date, items[], cached:false}`
  - Auth: `library:write` (same policy as Daily Mix regenerate).
- `POST /discover/download {title, artist}` → existing `POST /downloads`
  payload (Phase 2 wires the button; the shape is already compatible).
- `GET /discover/insights?windowDays=90` → local quality report
  - Auth: `library:read`.
  - Returns feedback counts/conversion by source, learned source priors,
    playback completion/skip counts, unique artists, and the window length.
  - This is the same data the ranker uses for its learned priors, so the
    report and behavior cannot drift.
- `GET /discover/evaluation?windowDays=30&k=10` → offline ranking report
  - Auth: `library:read`.
  - Joins daily ranker snapshots with feedback attributed to the snapshot
    that surfaced the item (within 7 days).
  - Returns `precision@K`, `nDCG@K`, `conversion@K`, per-source labels,
    artist coverage, and re-ranked weight presets (`current` +
    similarity/tag/affinity/popularity/audio/history-heavy). `insufficientData`
    is true until there are enough snapshots/labels to trust the numbers.
- `POST /discover/tuning?windowDays=30&k=10` → evaluate presets and persist a
  tuned per-user model only when the blended score clears `TuningMargin` and
  the sample thresholds. Auth: `library:write`.
- `GET /discover/tuning` → current tuned model status (`library:read`).
- `POST /discover/feedback` → `204`
  - Auth: `library:write`.
  - Body:
    ```json
    {
      "title": "Midnight City",
      "artist": "M83",
      "mbid": "…",
      "source": "similar",
      "event": "dismiss | preview | click | download",
      "seedArtist": "Slowdive",
      "score": 0.82
    }
    ```
  - Events are user-scoped and used only by the local ranker. `dismiss`
    hides the item and its artist for `DismissDays`; `download` boosts the
    item's artist and seed; `preview`/`click` add a weak engagement boost.

## Item shape

```json
{
  "title": "Midnight City",
  "artist": "M83",
  "album": "Hurry Up, We're Dreaming",
  "year": "2011",
  "genre": "Electronic",
  "artworkUrl": "https://…/600x600bb.jpg",
  "source": "similar | graph | tag | popular | artist | chart | llm | cf | audio",
  "score": 0.93,
  "mbid": "…",
  "reason": "Because you play Slowdive",
  "seedArtist": "Slowdive"
}
```

- `source`: which candidate generator produced the item.
  - `similar` — ListenBrainz public similar-artists/recordings from a seed
    MBID (open data, no user profile).
  - `graph` — MusicBrainz artist relationship graph (collaborations, members,
    related artists).
  - `tag` — MusicBrainz tag/genre exploration from the local profile.
  - `popular` — open listen-count fallback (cold start only).
  - `artist` — other tracks by a top artist from the listener's history
    (iTunes fallback, exact-artist filter).
  - `chart` — iTunes RSS `topsongs` fallback, used only when there is no
    personalization signal.
  - `llm` — legacy local-LLM taste expansion. The current pipeline does not
    call it (the 4B model's catalog is too small/stale); the port is reserved
    for future intent parsing/explanations.
  - `audio` — CLAP content-similarity feature from locally embedded library
    tracks and cached 30s previews; missing embeddings are neutral.
  - `cf` — reserved for future collaborative-filtering sources.
- `score` 0..1, descending. The local ranker blends similarity, tag overlap,
  recency-weighted artist affinity, popularity, a source prior and CLAP audio
  similarity, then applies MMR diversity (max 2 per artist, max 50 items):
  `0.35*similarity + 0.25*tagCosine + 0.15*affinity + 0.15*popularity + 0.10*sourcePrior + 0.10*audioSimilarity`
  plus the small implicit-feedback boosts from rule 9. `popularity` is the
  user's historical band (median log10 listen count of their top played
  tracks) when available; otherwise the absolute long-tail band is used.
  Unknown candidate counts are neutral.
- `mbid` (optional): canonical MusicBrainz recording id.
- `reason` (optional): human-readable explanation tied to a local seed
  (e.g. `Because you play Slowdive`).
- `seedArtist` (optional): the local seed that produced the candidate; sent
  back with feedback so downloads/previews can boost the right seed later.

## Rules every app follows

1. **Taste profile** = recency-weighted top artists, tracks, genres and played
   files from `Music_History` joined to `Songs` (`Unknown`/blank artist
   excluded). Half-life defaults to 60 days; older plays still contribute but
   decay. Recent `Playback_Events` completion/skip telemetry multiplies each
   file's weight (`0.2 + 0.8 * completionRate`; no events = 1.0, so
   desktop/mobile clients keep the legacy behavior). Top 5 tracks seed
   similar-recording expansion; top played files weight the CLAP audio
   centroid, so audio similarity follows actual song history. Genre matching
   is fuzzy, not exact: containment (`Rock` ↔ `Alternative Rock`), shared
   tokens, and a small family map (`rock` ↔ `shoegaze`, `electronic` ↔
   `trip hop`, `R&B` ↔ `soul`) cover local broad genres vs MusicBrainz tags.
2. **Never recommend the owned library**: drop candidates matching any
   `Songs` title+artist pair, and anything in-flight in the download queue
   (`queued`/`downloading`/`processing`). Normalization: lowercase, strip
   `(…)`/`[…]` segments, strip `feat.`/`featuring` tails and ` - <edition>`
   suffixes (`Single`, `Remastered…`, `Live…`), drop punctuation, collapse
   whitespace. The same key also dedupes **within one response**, so a song
   suggested by two sources appears once — first occurrence wins.
3. **Diversity cap**: max 2 items per artist, max 50 items total, with MMR
   penalty for tag/genre/artist similarity to already-selected items.
4. **Empty history** still returns open-data/chart picks (never 500, never
   empty-shaped: `{date, items, cached}` always). Charts are a cold-start
   fallback only.
5. **The LLM is not part of the candidate path.** `Discovery:LlmEnabled` stays
   `false`; the local model is reserved for future intent parsing, and any
   suggestion it ever produces must resolve via iTunes before display
   (hallucination filter).
6. Downloading a recommendation uses the normal ingest pipeline
   (320k MP3 + iTunes enrichment) — Discover never writes files itself.
7. **No paid placement**: editorial/promoted charts are never mixed into a
   personalized response. Sources are independent open-data graphs and local
   signals; the local ranker owns the order, enforcing per-source quotas and a
   long-tail popularity band.
8. **Privacy**: only seed artist/recording MBIDs (or names) leave the server.
   No user id, listening history, playlists or audio are sent to
   MusicBrainz/ListenBrainz. MusicBrainz core and ListenBrainz data are CC0.
9. **Feedback is local and user-scoped**: `dismiss` hides the item + artist for
   `DismissDays` (default 30); `download` boosts the item's artist and seed;
   `preview`/`click` add a weak engagement boost over `FeedbackDays`
   (default 90). Feedback never affects another user, and cached responses
   are filtered on read so dismissals/downloads disappear immediately.
10. **Playback telemetry** (`POST /player/playback-events`, `player:control`):
    the web player reports one segment per music track play with
    `{segmentId, file, positionSec, durationSec, completed, skipped,
    startedAtUtc}`. `SegmentId` is client-generated and unique per user, so
    unload/retry reports are idempotent. Events are server-only (not synced)
    and feed the taste weighting; podcasts are not reported.
11. **Learned priors** (local, per user): download/dismiss outcomes produce
    smoothed conversion rates per source and per artist/seed. The ranker
    blends the static source prior 50/50 with the learned one and adds capped
    empirical boosts (`-0.10..+0.10`) for artists/seeds with at least two
    outcomes. No global/cross-user model is trained; a future LTR model can
    replace this layer without changing the wire shape.
12. **CLAP audio embeddings** (`Rec_Embeddings`, optional): a Python
    `laion_clap` sidecar embeds local library files in batches, plus 30s
    iTunes previews for recent candidates. The ranker compares candidate
    preview vectors to the centroid of the user's top played files (falling
    back to the whole library for cold start); missing vectors are neutral
    (`0.5`), so the feature is additive when the sidecar/model is installed
    and never blocks Discover. Vectors are JSON float arrays and cosine is
    computed in-process (no pgvector requirement).
13. **Ranker snapshots** (`Rec_Impressions`): every fresh daily ranking stores
    the top items with their exact feature values (`similarity`, `tagScore`,
    `affinity`, `popularity`, `sourcePrior`, explicit/learned boosts, MMR
    penalty) and a model version. Evaluation attributes feedback to the latest
    snapshot (<= 7 days) that surfaced the item, computes rank-aware metrics,
    and compares weight presets offline. These are ranking snapshots, not
    per-view exposure counts; treat low-volume conversion numbers as
    directional until enough events accumulate.
14. **Historical popularity**: top played tracks are resolved to
    MusicBrainz recording MBIDs and looked up in ListenBrainz popularity
    (cached globally in `Rec_TrackPopularity`). Candidate counts are
    populated for every source the same way. The ranker compares log10
    counts to the user's median historical band; unknown values stay
    neutral. The top-track popularity profile is cached per user/track set.
15. **Cross-client playback quality**: explicit `Playback_Events` (web) take
    precedence. For files with no events (desktop/mobile), completion/skip is
    inferred from `Music_History` start-to-start gaps versus
    `Songs.DurationSecs`: a gap of at least 90% of duration counts as
    completed; gaps over `duration + 60s` are unknown and skipped. Set
    `InferPlaybackQuality=false` to disable.
16. **Evaluation-driven tuning**: `POST /discover/tuning` compares the current
    order and preset weight vectors on stored snapshot features, using
    `0.6*nDCG@K + 0.3*precision@K + 0.1*conversion@K`. It persists a model
    only above the sample/margin thresholds; tuned weights change the
    Discover cache version immediately. Stale models
    (`TuningMaxAgeDays`) fall back to defaults. No cross-user model is built.
17. **Popularity floor**: open-data candidates below the effective floor
    (default 10,000 global listens; lowered for niche profiles via
    `PopularityFloorProfileMargin`) are dropped. If the filtered pool is small,
    Discover runs the same-artist/chart fallback sources, which are exempt.
    Known low-popularity tracks that survive in a fallback-only pool get a
    `0.1` popularity score; unknown counts stay neutral.

## Configuration (web)

`Discovery` section in `appsettings.json` (all optional, shown with defaults):

```json
"Discovery": {
  "LlmEnabled": false,
  "Endpoint": "http://localhost:1234/v1",
  "Model": "qwen3.5-4b-uncensored",
  "LlmTimeoutSec": 90,
  "LlmMaxTokens": 1000,

  "TopArtists": 8,
  "TopTracks": 8,
  "TopTags": 5,
  "ArtistHalfLifeDays": 60,
  "PlaybackWindowDays": 90,
  "InferPlaybackQuality": true,
  "SeedArtistLimit": 4,
  "SimilarArtistLimit": 10,
  "SimilarArtistsToBrowse": 3,
  "RelatedArtistsToBrowse": 1,
  "RecordingsPerArtist": 5,
  "SimilarRecordingSeeds": 5,
  "SimilarRecordingsPerSeed": 3,
  "TagRecordingsLimit": 40,
  "CandidateLimit": 500,
  "FallbackThreshold": 10,
  "TimeBudgetSec": 90,
  "MaxItems": 50,
  "MaxPerArtist": 2,
  "MmrLambda": 0.25,
  "AudioWeight": 0.10,
  "PopularityMin": 10000,
  "PopularityMax": 2000000,
  "PopularityFilterEnabled": true,
  "PopularityFloor": 10000,
  "PopularityFloorProfileMargin": 1.0,
  "PopularityProfileSeeds": 5,
  "PopularityProfileMinSamples": 3,
  "PopularityProfileCacheDays": 7,
  "TuningEnabled": true,
  "TuningMinSnapshots": 10,
  "TuningMinLabels": 50,
  "TuningMargin": 0.02,
  "TuningMaxAgeDays": 90,
  "DismissDays": 30,
  "FeedbackDays": 90,
  "LikedArtistBoost": 0.08,
  "LikedSeedBoost": 0.08,
  "EngagedArtistBoost": 0.04,

  "AudioEmbeddingsEnabled": false,
  "AudioEmbeddingPythonPath": "",
  "AudioEmbeddingModel": "clap-v1",
  "AudioEmbeddingCheckpoint": "",
  "AudioEmbeddingTimeoutSec": 180,
  "AudioEmbeddingBatchSize": 8,
  "AudioEmbeddingIntervalMinutes": 30,

  "MusicBrainzEnabled": true,
  "MusicBrainzBaseUrl": "https://musicbrainz.org/ws/2",
  "MusicBrainzUserAgent": "Hathor/1.0 (https://github.com/gabrieljdsena/Hathor)",
  "ListenBrainzEnabled": true,
  "ListenBrainzBaseUrl": "https://labs.api.listenbrainz.org",
  "ListenBrainzApiBaseUrl": "https://api.listenbrainz.org",
  "ListenBrainzSimilarArtistsAlgorithm": "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30",
  "ListenBrainzSimilarRecordingsAlgorithm": "session_based_days_7500_session_300_contribution_5_threshold_15_limit_50_skip_30",
  "ColdStartTags": [ "pop", "rock", "electronic" ],
  "HttpCacheDays": 7,
  "IdentityCacheDays": 30
}
```

- MusicBrainz requires a meaningful `User-Agent` and <=1 request/second; the
  server enforces both process-wide and caches identities/relations in
  `Rec_HttpCache`.
- ListenBrainz Labs endpoints are public and experimental. A failure degrades
  to MusicBrainz-only candidates; it never fails the endpoint.
- `LlmEnabled` is opt-in only. When enabled, the local LLM is used for taste
  expansion/intent parsing, and every suggestion must resolve via iTunes.
- CLAP audio embeddings are opt-in and require a Python 3 environment with
  `laion-clap` + `torch` (the worker loads the model once per batch). If the
  sidecar/model is missing, audio similarity stays neutral and Discover keeps
  working. Local tracks embed first; 30s iTunes preview embeddings are
  backfilled from recent snapshots, so the audio signal appears on a later
  fresh ranking.
- Any provider failure (down, timeout, bad reply) yields partial results; the
  endpoint still returns `{date, items, cached}`.

## Open-data attribution

MusicBrainz core data is CC0; ListenBrainz listen data is CC0 (MetaBrainz
Foundation). iTunes/Deezer provide catalog metadata/artwork only and are not
used for ranking. Recommendation ordering is computed locally; provider
"recommended" endpoints and editorial charts are not used for personalization.

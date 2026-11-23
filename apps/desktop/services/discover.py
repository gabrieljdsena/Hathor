"""Discover feed, previews, owned-guard and local downloads.

The server owns recommendations; the desktop app only reads them through the
Hathor Web API and downloads through its own DownloadManager. Keep that split
explicit so Discover never creates a second media pipeline.
"""

from __future__ import annotations

import os
import re
import sqlite3
import unicodedata

from .api_config import get_api_config
from .auth_session import get_auth_session
from .hathor_api import HathorApi, HathorApiError


class DiscoverError(Exception):
    """User-facing Discover failure."""


DEFAULT_WINDOW_DAYS = 90
PREVIEW_CANDIDATES = 5

_BRACKET_QUALIFIER = re.compile(
    r"\s*[\(\[].*?(remaster|remastered|mix|version|edit|live|feat\.?|ft\.?|"
    r"deluxe|bonus|explicit).*?[\)\]]",
    re.IGNORECASE,
)


def normalize_key(value):
    """Stable title/artist comparison form (mirrors the web ingest guard).

    Lowercase, strip bracket qualifiers, featured-artist tails and
    `` - <edition>`` suffixes, fold diacritics, drop punctuation and collapse
    whitespace. The same key drives owned checks and card progress matching.
    """
    text = unicodedata.normalize("NFKD", str(value or "").lower())
    text = "".join(ch for ch in text if not unicodedata.combining(ch))
    text = _BRACKET_QUALIFIER.sub("", text)
    text = re.sub(r"\b(feat\.?|featuring|ft\.?)\b.*$", "", text)
    text = re.sub(
        r"\s+-\s+(single|deluxe|bonus|remaster(ed)?|live|radio edit|"
        r"album version|explicit).*$",
        "",
        text,
    )
    text = re.sub(r"[^a-z0-9]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def _pick(data, *names, default=None):
    if not isinstance(data, dict):
        return default
    for name in names:
        if name in data and data[name] is not None:
            return data[name]
    return default


class DiscoverService:
    def __init__(self, api):
        self.api = api

    # -- HTTP ----------------------------------------------------------
    def _client(self, timeout=20):
        try:
            session = get_auth_session(self.api.db_path)
            if session.configured():
                return session.api_client(timeout=timeout)
        except Exception:
            pass
        url, key = get_api_config(self.api.db_path)
        if not url or not key:
            raise DiscoverError(
                "Web API is not configured. Sign in in Settings -> Web API."
            )
        return HathorApi(url, key, timeout=timeout)

    def feed(self):
        return self._client().get("/api/v1/discover")

    def refresh(self):
        return self._client(timeout=60).post("/api/v1/discover/refresh")

    def feedback(self, payload):
        if not isinstance(payload, dict):
            raise DiscoverError("Feedback payload is required.")
        # Best-effort by design: a failed signal must never block a download
        # or playback. The caller can still surface the returned error.
        return self._client().post("/api/v1/discover/feedback", payload=payload) or {}

    def insights(self, window_days=DEFAULT_WINDOW_DAYS):
        try:
            days = max(1, min(int(window_days or DEFAULT_WINDOW_DAYS), 365))
        except (TypeError, ValueError):
            days = DEFAULT_WINDOW_DAYS
        return self._client().get(
            "/api/v1/discover/insights", query={"windowDays": days}
        )

    def tune(self, window_days=DEFAULT_WINDOW_DAYS):
        try:
            days = max(1, min(int(window_days or DEFAULT_WINDOW_DAYS), 365))
        except (TypeError, ValueError):
            days = DEFAULT_WINDOW_DAYS
        return self._client(timeout=90).post(
            "/api/v1/discover/tuning",
            query={"windowDays": days, "k": 10},
        )

    # -- preview -------------------------------------------------------
    def _server_preview(self, title, artist):
        query = f"{title or ''} {artist or ''}".strip()
        if not query:
            return None
        client = self._client(timeout=20)
        hits = client.get(
            "/api/v1/youtube/search", query={"q": query, "limit": PREVIEW_CANDIDATES}
        )
        if not isinstance(hits, list):
            return None
        for hit in hits:
            if not isinstance(hit, dict):
                continue
            video_id = _pick(hit, "id", "Id")
            if not video_id:
                continue
            try:
                preview = client.get(
                    "/api/v1/youtube/preview",
                    query={"id": video_id, "resolveAudio": "true"},
                )
            except HathorApiError:
                continue
            audio_url = _pick(preview, "audioUrl", "AudioUrl")
            if audio_url:
                return {
                    "audioUrl": audio_url,
                    "videoId": video_id,
                    "embeddable": bool(_pick(preview, "embeddable", "Embeddable", default=False)),
                    "source": "server",
                }
        return None

    def resolve_preview(self, title, artist=""):
        """Resolve an audio preview, trying the server first, then yt-dlp."""
        query = f"{title or ''} {artist or ''}".strip()
        if not query:
            raise DiscoverError("A title is required for a preview.")
        server_error = None
        try:
            resolved = self._server_preview(title, artist)
            if resolved:
                return resolved
        except Exception as exc:
            server_error = exc

        try:
            local = self.api.downloader.resolve_preview_url(query)
        except Exception as exc:
            local = None
            server_error = server_error or exc
        if local and local.get("audioUrl"):
            return {
                "audioUrl": local["audioUrl"],
                "videoId": local.get("id"),
                "embeddable": False,
                "source": "local",
            }
        if server_error:
            raise DiscoverError(
                f"Preview unavailable: {server_error}"
            ) from server_error
        raise DiscoverError(f"Preview unavailable for {title}.")

    def resolve_video(self, title, artist=""):
        """Return the first YouTube hit for the card's video-preview modal."""
        query = f"{title or ''} {artist or ''}".strip()
        if not query:
            raise DiscoverError("A title is required for a video preview.")
        try:
            client = self._client(timeout=20)
            hits = client.get(
                "/api/v1/youtube/search",
                query={"q": query, "limit": PREVIEW_CANDIDATES},
            )
            if isinstance(hits, list) and hits:
                hit = hits[0] if isinstance(hits[0], dict) else {}
                video_id = _pick(hit, "id", "Id")
                if video_id:
                    embeddable = True
                    try:
                        preview = client.get(
                            "/api/v1/youtube/preview", query={"id": video_id}
                        )
                        embeddable = bool(
                            _pick(preview, "embeddable", "Embeddable", default=True)
                        )
                    except HathorApiError:
                        pass
                    return {
                        "videoId": video_id,
                        "title": _pick(hit, "title", "Title", default=title),
                        "embeddable": embeddable,
                        "source": "server",
                    }
        except Exception:
            pass

        try:
            hits = self.api.downloader.search_yt(query, 1) or []
        except Exception:
            hits = []
        if hits:
            hit = hits[0]
            return {
                "videoId": hit.get("id"),
                "title": hit.get("title") or title,
                "embeddable": False,
                "source": "local",
            }
        raise DiscoverError(f"No YouTube video found for {title}.")

    # -- owned guard / download ---------------------------------------
    def check_owned(self, title, artist="", library="songs"):
        title_key = normalize_key(title)
        artist_key = normalize_key(artist)
        if not title_key:
            return {"owned": False, "file": None}
        if not self.api.db_path or not os.path.exists(self.api.db_path):
            return {"owned": False, "file": None}
        table = "Podcasts" if str(library).lower() in ("podcast", "podcasts") else "Songs"
        try:
            conn = sqlite3.connect(self.api.db_path)
            try:
                rows = conn.execute(
                    f"SELECT file, title, artist FROM {table}"
                ).fetchall()
            finally:
                conn.close()
        except sqlite3.Error:
            return {"owned": False, "file": None}
        for file, row_title, row_artist in rows:
            if normalize_key(row_title) != title_key:
                continue
            if artist_key and normalize_key(row_artist) != artist_key:
                continue
            return {"owned": True, "file": file}
        return {"owned": False, "file": None}

    def check_owned_query(self, query, library="songs"):
        """Owned check for a free-text download query (e.g. ``Title Artist``)."""
        query_key = normalize_key(query)
        if not query_key:
            return {"owned": False, "file": None}
        if not self.api.db_path or not os.path.exists(self.api.db_path):
            return {"owned": False, "file": None}
        table = "Podcasts" if str(library).lower() in ("podcast", "podcasts") else "Songs"
        try:
            conn = sqlite3.connect(self.api.db_path)
            try:
                rows = conn.execute(
                    f"SELECT file, title, artist FROM {table}"
                ).fetchall()
            finally:
                conn.close()
        except sqlite3.Error:
            return {"owned": False, "file": None}
        for file, row_title, row_artist in rows:
            title_key = normalize_key(row_title)
            joined = normalize_key(f"{row_title} {row_artist or ''}")
            if not title_key:
                continue
            # Query is usually "title artist"; accept an exact title/joined
            # match or a longer query with the library title as prefix.
            if (query_key == joined or query_key == title_key
                    or query_key.startswith(title_key + " ")):
                return {"owned": True, "file": file}
        return {"owned": False, "file": None}

    def submit_download(self, title, artist=""):
        title = (title or "").strip()
        artist = (artist or "").strip()
        if not title:
            raise DiscoverError("A title is required for a download.")
        query = f"{title} {artist} audio".strip()
        ok = self.api.download_manager.submit(
            {
                "url": query,
                "title": title,
                "artist": artist,
                "is_podcast": False,
            },
            block=False,
        )
        if not ok:
            raise DiscoverError("Could not queue the download.")
        return {"queued": True}

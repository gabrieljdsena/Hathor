"""Server-side player controls shared by AI DJ and the live player hub.

Desktop owns local playback, but the server queue/state still drives DJ
prefetch, cross-device convergence and web/other clients. These calls are
tiny and best-effort; callers never let a failed sync stop local music.
"""

from __future__ import annotations

import uuid

from .api_config import get_api_config
from .hathor_api import HathorApi


class PlayerSyncError(Exception):
    """User-facing player-sync failure."""


class PlayerSync:
    def __init__(self, api, timeout=15):
        self.api = api
        self.timeout = timeout

    def _client(self):
        url, key = get_api_config(self.api.db_path)
        if not url or not key:
            raise PlayerSyncError("Web API is not configured.")
        return HathorApi(url, key, timeout=self.timeout)

    def replace_queue(self, current_file, files, session_id=None, source=None):
        files = [f for f in (files or []) if f]
        if not current_file or not files:
            raise PlayerSyncError("The queue is empty.")
        if source is None and session_id:
            source = {"type": "ai_dj", "id": session_id}
        payload = {
            "CurrentFile": current_file,
            "Files": files,
            "Source": None if source is None else {
                "Type": source.get("type") or source.get("Type"),
                "Id": source.get("id") if "id" in source else source.get("Id"),
            },
        }
        return self._client().put("/api/v1/player/queue", payload=payload)

    def play(self, file, is_podcast=False):
        if not file:
            raise PlayerSyncError("No track to play.")
        return self._client().post(
            "/api/v1/player/play",
            payload={
                "File": file,
                "IsPodcast": bool(is_podcast),
                "InstanceId": uuid.uuid4().hex,
            },
        )

    def pause(self, file=None, position=None):
        payload = {}
        if file:
            payload["File"] = file
        if position is not None:
            payload["PositionSec"] = max(0.0, float(position))
        return self._client().post("/api/v1/player/pause", payload=payload)

    def resume(self):
        return self._client().post("/api/v1/player/resume", payload={})

    def prev(self):
        return self._client().post("/api/v1/player/prev", payload={})

    def seek(self, seconds):
        return self._client().post(
            "/api/v1/player/seek",
            payload={"Seconds": max(0.0, float(seconds or 0))},
        )

    def volume(self, volume):
        return self._client().post(
            "/api/v1/player/volume",
            payload={"Volume": max(0.0, min(1.0, float(volume or 0)))},
        )

    def next(self, auto=True, expected_file=None):
        payload = {"Auto": bool(auto)}
        if expected_file:
            payload["ExpectedFile"] = expected_file
        return self._client().post("/api/v1/player/next", payload=payload)

    def state(self):
        return self._client().get("/api/v1/player/state")

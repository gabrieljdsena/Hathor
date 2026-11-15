"""Server-side player controls used by the AI DJ session.

Desktop owns local playback, but the server queue/source still drives DJ
batch prefetch and keeps web/other clients coherent. These calls are tiny and
best-effort; the controller never lets a failed sync stop local music.
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

    def replace_queue(self, current_file, files, session_id):
        files = [f for f in (files or []) if f]
        if not current_file or not files:
            raise PlayerSyncError("The DJ queue is empty.")
        return self._client().put(
            "/api/v1/player/queue",
            payload={
                "CurrentFile": current_file,
                "Files": files,
                "Source": {"Type": "ai_dj", "Id": session_id},
            },
        )

    def play(self, file):
        if not file:
            raise PlayerSyncError("No DJ track to play.")
        return self._client().post(
            "/api/v1/player/play",
            payload={"File": file, "InstanceId": uuid.uuid4().hex},
        )

    def next(self, auto=True, expected_file=None):
        payload = {"Auto": bool(auto)}
        if expected_file:
            payload["ExpectedFile"] = expected_file
        return self._client().post("/api/v1/player/next", payload=payload)

    def state(self):
        return self._client().get("/api/v1/player/state")

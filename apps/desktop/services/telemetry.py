"""Playback segment telemetry for Discover quality signals.

One report per music track play. The player is authoritative; this service only
observes track changes/positions and POSTs a client-generated segment id. Any
network/configuration failure is swallowed so telemetry can never disturb
local playback.
"""

from __future__ import annotations

import threading
import uuid
from datetime import datetime, timezone

from .api_config import get_api_config
from .hathor_api import HathorApi


class PlaybackTelemetry:
    def __init__(self, api, start_poll=True):
        self.api = api
        self._lock = threading.RLock()
        self._active = None
        self._stop = threading.Event()
        self._thread = None
        playback = getattr(api, 'playback', None)
        if playback is not None:
            playback.add_track_changed_listener(self._on_track_changed)
            playback.add_queue_exhausted_listener(self._on_queue_exhausted)
        if start_poll:
            self._thread = threading.Thread(
                target=self._poll_loop, daemon=True, name="playback-telemetry"
            )
            self._thread.start()

    # -- observation ---------------------------------------------------
    def _current_file(self):
        return str(getattr(self.api, 'current_filename', '') or '')

    def _position(self):
        try:
            return max(0.0, float(self.api.playback.get_current_pos() or 0))
        except Exception:
            return 0.0

    def _duration(self, fallback=0):
        try:
            song = getattr(self.api, 'last_song', None) or {}
            return max(0.0, float(song.get('Duration') or fallback or 0))
        except (TypeError, ValueError):
            try:
                return max(0.0, float(fallback or 0))
            except (TypeError, ValueError):
                return 0.0

    def _on_track_changed(self, song):
        try:
            file = str((song or {}).get('File') or '')
            is_podcast = bool((song or {}).get('IsPodcast'))
            self._close_active()
            if not file or is_podcast:
                return
            if not getattr(self.api, 'playing', False):
                return
            with self._lock:
                self._active = {
                    'id': uuid.uuid4().hex,
                    'file': file,
                    'started_at': datetime.now(timezone.utc).isoformat(),
                    'last_position': self._position(),
                    'duration': self._duration((song or {}).get('Duration')),
                }
        except Exception:
            pass

    def _on_queue_exhausted(self):
        self._close_active()

    def _poll_loop(self):
        while not self._stop.wait(1.0):
            try:
                with self._lock:
                    active = dict(self._active) if self._active else None
                if not active:
                    continue
                current = self._current_file()
                song = getattr(self.api, 'last_song', None) or {}
                if (not current or current != active['file']
                        or bool(song.get('IsPodcast'))):
                    self._close_active()
                    continue
                if getattr(self.api, 'playing', False):
                    position = self._position()
                    duration = self._duration(active['duration'])
                    with self._lock:
                        if self._active and self._active['id'] == active['id']:
                            self._active['last_position'] = position
                            if duration > 0:
                                self._active['duration'] = duration
            except Exception:
                continue

    # -- reporting -----------------------------------------------------
    def _close_active(self, wait=False):
        with self._lock:
            segment = self._active
            self._active = None
        if not segment:
            return None
        playing = bool(getattr(self.api, 'playing', False))
        same_file = self._current_file() == segment['file']
        position = self._position() if (playing and same_file) else segment['last_position']
        duration = self._duration(segment['duration'])
        if duration > 0:
            position = min(max(0.0, position), duration)
        completed = bool(duration > 5 and position >= duration - 5)
        skipped = bool(not completed and duration > 0 and position < duration * 0.5)
        payload = {
            "SegmentId": segment['id'],
            "File": segment['file'],
            "PositionSec": round(max(0.0, position), 3),
            "DurationSec": round(max(0.0, duration), 3),
            "Completed": completed,
            "Skipped": skipped,
            "StartedAtUtc": segment['started_at'],
        }
        self._deliver(payload, wait)
        return payload

    def _deliver(self, payload, wait=False):
        if wait:
            self._send(payload)
            return
        threading.Thread(target=self._send, args=(payload,), daemon=True).start()

    def _send(self, payload):
        try:
            url, key = get_api_config(self.api.db_path)
            if not url or not key:
                return
            HathorApi(url, key, timeout=6).post(
                "/api/v1/player/playback-events", payload=payload)
        except Exception:
            # Telemetry is best-effort and must never surface as a play error.
            pass

    def flush(self, wait=True):
        return self._close_active(wait=wait)

    def close(self):
        self._stop.set()
        self.flush(wait=True)

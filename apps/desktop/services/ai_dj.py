"""AI DJ (Vohithe) session and local playback bridge.

The server plans batches, commentary and voice clips. Desktop keeps its own
synced files and playback queue, mirroring the server queue one forward track
at a time so prefetch and batch rotation keep working.
"""

from __future__ import annotations

import json
import os
import threading
import time
import urllib.parse

import settings

from .api_config import get_api_config
from .hathor_api import HathorApi
from .player_sync import PlayerSync


class AiDjError(Exception):
    """User-facing AI DJ failure."""


def _pick(data, *names, default=None):
    if not isinstance(data, dict):
        return default
    for name in names:
        if name in data and data[name] is not None:
            return data[name]
    return default


def _text(value):
    if value is None:
        return ""
    text = str(value).strip()
    return "" if text.lower() == "null" else text


def _text_or_none(value):
    text = _text(value)
    return text or None


def _clip_ids(value):
    if not isinstance(value, list):
        return []
    return [str(item) for item in value if _text(item)]


def parse_track(data):
    data = data if isinstance(data, dict) else {}
    return {
        "file": _text(_pick(data, "file", "File")),
        "title": _text(_pick(data, "title", "Title")),
        "artist": _text(_pick(data, "artist", "Artist")),
        "album": _text(_pick(data, "album", "Album")),
        "year": _text(_pick(data, "year", "Year")),
        "durationSec": float(_pick(data, "duration", "Duration", "durationSec", default=0) or 0),
        "coverArt": _text_or_none(_pick(data, "coverArt", "CoverArt")),
        "isPodcast": bool(_pick(data, "isPodcast", "IsPodcast", default=False)),
    }


def parse_batch(data):
    if not isinstance(data, dict):
        return None
    tracks = []
    for item in _pick(data, "tracks", "Tracks", default=[]) or []:
        track = parse_track(item)
        if track["file"]:
            tracks.append(track)
    return {
        "id": _text(_pick(data, "id", "Id")),
        "number": int(_pick(data, "number", "Number", default=0) or 0),
        "status": _text(_pick(data, "status", "Status")),
        "tracks": tracks,
        "introText": _text_or_none(_pick(data, "introText", "IntroText")),
        "introClipIds": _clip_ids(_pick(data, "introClipIds", "IntroClipIds")),
        "introDurationSec": float(_pick(data, "introDurationSec", "IntroDurationSec", default=0) or 0),
        "outroText": _text_or_none(_pick(data, "outroText", "OutroText")),
        "outroClipIds": _clip_ids(_pick(data, "outroClipIds", "OutroClipIds")),
        "outroDurationSec": float(_pick(data, "outroDurationSec", "OutroDurationSec", default=0) or 0),
        "shouldComment": bool(_pick(data, "shouldComment", "ShouldComment", default=False)),
        "mood": _text_or_none(_pick(data, "mood", "Mood")),
        "reason": _text_or_none(_pick(data, "reason", "Reason")),
    }


def parse_settings(data):
    data = data if isinstance(data, dict) else {}
    return {
        "batchSize": int(_pick(data, "batchSize", "BatchSize", default=3) or 3),
        "commentaryMode": _text(_pick(data, "commentaryMode", "CommentaryMode", default="auto")),
        "language": _text(_pick(data, "language", "Language", default="auto")),
        "discoveryLevel": float(_pick(data, "discoveryLevel", "DiscoveryLevel", default=0.35) or 0.35),
        "brief": _text(_pick(data, "brief", "Brief")),
        "preferredGenres": _text(_pick(data, "preferredGenres", "PreferredGenres")),
        "avoidArtists": _text(_pick(data, "avoidArtists", "AvoidArtists")),
        "duckLevel": max(0.02, min(0.8, float(_pick(data, "duckLevel", "DuckLevel", default=0.18) or 0.18))),
        "voiceLeadSeconds": max(1, min(30, int(_pick(data, "voiceLeadSeconds", "VoiceLeadSeconds", default=10) or 10))),
    }


def parse_state(data):
    data = data if isinstance(data, dict) else {}
    return {
        "enabled": bool(_pick(data, "enabled", "Enabled", default=False)),
        "configured": bool(_pick(data, "configured", "Configured", default=False)),
        "sessionId": _text_or_none(_pick(data, "sessionId", "SessionId")),
        "status": _text(_pick(data, "status", "Status", default="idle")),
        "settings": parse_settings(_pick(data, "settings", "Settings", default={})),
        "currentBatch": parse_batch(_pick(data, "currentBatch", "CurrentBatch")),
        "nextBatch": parse_batch(_pick(data, "nextBatch", "NextBatch")),
        "error": _text_or_none(_pick(data, "error", "Error")),
    }


class AiDjApi:
    def __init__(self, api, timeout=45):
        self.api = api
        self.timeout = timeout

    def _client(self, timeout=None):
        url, key = get_api_config(self.api.db_path)
        if not url or not key:
            raise AiDjError(
                "Web API is not configured. Open Settings -> Web API and add "
                "a key with player:read + player:control."
            )
        return HathorApi(url, key, timeout=timeout or self.timeout)

    def state(self):
        return parse_state(self._client(timeout=20).get("/api/v1/ai-dj/state"))

    def start(self, brief=""):
        payload = {}
        brief = (brief or "").strip()
        if brief:
            payload["Brief"] = brief
        return parse_state(
            self._client(timeout=75).post("/api/v1/ai-dj/start", payload=payload)
        )

    def stop(self):
        return parse_state(self._client(timeout=20).post("/api/v1/ai-dj/stop"))

    def feedback(self, session_id, batch_id, track_file, event):
        if not session_id:
            return False
        self._client(timeout=15).post(
            "/api/v1/ai-dj/feedback",
            payload={
                "SessionId": session_id,
                "BatchId": batch_id,
                "TrackFile": track_file,
                "Event": event,
            },
        )
        return True

    def transcribe(self, audio, language=None):
        if not audio:
            raise AiDjError("No audio was recorded.")
        payload = {"Audio": audio}
        if language:
            payload["Language"] = language
        response = self._client(timeout=60).post(
            "/api/v1/ai-dj/transcribe", payload=payload
        )
        if isinstance(response, dict):
            return _text(response.get("text") or response.get("Text"))
        return ""

    def clip_url(self, clip_id):
        if not clip_id:
            raise AiDjError("A clip id is required.")
        url, key = get_api_config(self.api.db_path)
        if not url or not key:
            raise AiDjError("Web API is not configured.")
        client = HathorApi(url, key)
        safe = urllib.parse.quote(str(clip_id))
        return client.media_url(f"/api/v1/ai-dj/clips/{safe}/audio")


class AiDjController:
    """App-scoped DJ session: server state + local queue playback."""

    def __init__(self, api):
        self.api = api
        self.api_client = AiDjApi(api)
        self.player_sync = PlayerSync(api)
        self._lock = threading.RLock()
        self._state = None
        self._active = False
        self._busy = False
        self._loading = True
        self._error = None
        self._voice = "idle"  # idle | speaking | waiting
        self._waiting = False
        self._session_id = None
        self._local_batch = None
        self._next_batch = None
        self._local_files = []
        self._expected_files = []
        self._last_server_file = None
        self._server_queue_ready = False
        self._boundary_started = False
        self._boundary_intro_played = False
        self._waiting_for_voice = False
        self._waiting_at_end = False
        self._wait_deadline = 0.0
        self._advancing = False
        self._voice_generation = 0
        self._poll_stop = threading.Event()
        self._poll_thread = None

        # Playback callbacks are how the local queue and the server queue stay
        # aligned without polling every track.
        self.api.playback.on_track_changed = self._on_track_changed
        self.api.playback.on_queue_exhausted = self._on_queue_exhausted
        self.api.playback.on_queue_changed = self._on_queue_changed

        threading.Thread(target=self._initial_refresh, daemon=True).start()

    # -- view / publish ------------------------------------------------
    def _song_view(self, song):
        if not isinstance(song, dict):
            return None
        return {
            "file": _text(song.get("File")),
            "title": _text(song.get("Title")) or "Unknown",
            "artist": _text(song.get("Artist")) or "",
            "album": _text(song.get("Album")) or "",
            "coverArt": _text_or_none(song.get("CoverArt")),
            "isPodcast": bool(song.get("IsPodcast")),
        }

    def view(self):
        with self._lock:
            state = self._state or {}
            url, key = get_api_config(self.api.db_path)
            settings = state.get("settings") or {}
            current = self._local_batch if self._active else state.get("currentBatch")
            nxt = self._next_batch if self._active else state.get("nextBatch")
            current_song = self._song_view(self.api.last_song) if self._active else None
            return {
                "apiConfigured": bool(url and key),
                "enabled": bool(state.get("enabled", False)),
                "configured": bool(state.get("configured", False)),
                "status": state.get("status", "idle"),
                "sessionId": self._session_id or state.get("sessionId"),
                "settings": settings,
                "currentBatch": current,
                "nextBatch": nxt,
                "currentSong": current_song,
                "active": bool(self._active),
                "busy": bool(self._busy),
                "loading": bool(self._loading),
                "voice": self._voice,
                "waiting": bool(self._waiting),
                "error": self._error or state.get("error"),
            }

    def _publish(self):
        window = getattr(self.api, "_window", None)
        if not window:
            return
        try:
            window.evaluate_js(
                "if (typeof window.onAiDjState === 'function') "
                f"window.onAiDjState({json.dumps(self.view())});"
            )
        except Exception:
            pass

    # -- HTTP state ----------------------------------------------------
    def _initial_refresh(self):
        time.sleep(1.0)
        self.refresh()

    def refresh(self):
        try:
            state = self.api_client.state()
            self._apply_server_state(state)
        except Exception as exc:
            with self._lock:
                self._loading = False
                if not self._active:
                    self._error = str(exc)
        self._publish()
        return self.view()

    def _apply_server_state(self, state):
        if not isinstance(state, dict):
            return
        with self._lock:
            self._state = state
            self._loading = False
            if state.get("error"):
                self._error = state["error"]
            if not self._active:
                return
            if state.get("status") != "active":
                # The server ended the session: leave the current local queue
                # playing, but leave DJ mode.
                self._active = False
                self._session_id = None
                self._local_batch = None
                self._next_batch = None
                self._local_files = []
                self._expected_files = []
                self._voice = "idle"
                self._waiting = False
                self._boundary_started = False
                self._boundary_intro_played = False
                self._waiting_for_voice = False
                self._waiting_at_end = False
                ended = True
            else:
                ended = False
                candidate = state.get("nextBatch")
                local_id = (self._local_batch or {}).get("id")
                if candidate and candidate.get("id") != local_id:
                    self._next_batch = candidate
        if ended:
            self._stop_voice()
            self._stop_polling()

    # -- start / stop --------------------------------------------------
    def start(self, brief=""):
        with self._lock:
            if self._busy:
                return self.view()
            self._busy = True
            self._loading = False
            self._error = None
        self._publish()

        started_session = None
        try:
            # Replace any previous DJ session before planning the new one.
            if self._active or self._session_id:
                try:
                    self.api_client.stop()
                except Exception:
                    pass
            self._reset_local(stop_voice=True)

            state = self.api_client.start(brief)
            started_session = state.get("sessionId")
            if not state.get("enabled"):
                raise AiDjError("AI DJ is disabled on the server.")
            if not state.get("configured"):
                raise AiDjError(
                    "AI DJ is enabled but not configured on the server "
                    "(assistant URL/token)."
                )
            batch = state.get("currentBatch")
            session = state.get("sessionId")
            if not batch or not session:
                raise AiDjError("The DJ did not return a playable first set.")

            # DJ sets are linear; repeat-one would trap the boundary.
            self.api.repeat = False
            files = self._resolve_batch(batch, download_missing=True)
            if not files:
                raise AiDjError(
                    "The DJ selected tracks that are not available locally. "
                    "Pull the library first."
                )

            with self._lock:
                self._state = state
                self._active = True
                self._busy = False
                self._loading = False
                self._error = None
                self._session_id = session
                self._local_batch = batch
                self._next_batch = state.get("nextBatch")
                self._local_files = files
                self._expected_files = [str(song.get("File")) for song in files]
                self._last_server_file = str(files[0].get("File"))
                self._server_queue_ready = False
                self._boundary_started = False
                self._boundary_intro_played = False
                self._waiting_for_voice = False
                self._waiting_at_end = False
                self._wait_deadline = 0.0
                self._advancing = False
                self._voice = "idle"
                self._waiting = False

            if not self._install_local_queue(files, session):
                raise AiDjError("Could not start the first DJ set.")
            self._start_polling()
            self._publish()
            threading.Thread(
                target=self._sync_server_queue,
                args=(files, session),
                daemon=True,
            ).start()

            if batch.get("shouldComment") and batch.get("introClipIds"):
                threading.Timer(0.35, self._play_intro).start()
            return self.view()
        except Exception as exc:
            if started_session:
                try:
                    self.api_client.stop()
                except Exception:
                    pass
            self._reset_local(stop_voice=True)
            with self._lock:
                self._busy = False
                self._loading = False
                self._active = False
                self._error = str(exc)
            self._publish()
            return self.view()

    def stop(self):
        with self._lock:
            session = self._session_id
            self._busy = True
            self._active = False
            self._error = None
            self._session_id = None
            self._local_batch = None
            self._next_batch = None
            self._local_files = []
            self._expected_files = []
            self._boundary_started = False
            self._boundary_intro_played = False
            self._waiting_for_voice = False
            self._waiting_at_end = False
            self._advancing = False
            self._server_queue_ready = False
            self._voice = "idle"
            self._waiting = False
        self._stop_voice()
        self._stop_polling()
        try:
            if session:
                state = self.api_client.stop()
                with self._lock:
                    self._state = state
        except Exception as exc:
            with self._lock:
                self._error = str(exc)
        with self._lock:
            self._busy = False
            self._loading = False
        self._publish()
        return self.view()

    def feedback(self, event, track_file=None):
        with self._lock:
            session = self._session_id
            batch = self._local_batch or {}
            batch_id = batch.get("id")
        if not session:
            return False
        try:
            return self.api_client.feedback(session, batch_id, track_file, event)
        except Exception:
            return False

    def transcribe(self, audio, language=None):
        return self.api_client.transcribe(audio, language)

    def clip_url(self, clip_id):
        return self.api_client.clip_url(clip_id)

    def voice_finished(self, generation):
        return self._on_voice_finished(int(generation or 0))

    def voice_error(self, generation, message):
        return self._on_voice_error(int(generation or 0), message)

    def set_duck(self, level):
        try:
            return self.api.playback.set_duck(float(level))
        except Exception:
            return 1.0

    # -- local queue / server queue -----------------------------------
    def _resolve_batch(self, batch, download_missing=True):
        files = []
        for track in (batch or {}).get("tracks") or []:
            file = os.path.basename(str(track.get("file") or ""))
            if not file:
                continue
            path = os.path.join(settings.path, file)
            if not os.path.exists(path) and download_missing:
                try:
                    from .api_sync import ApiSyncClient
                    ApiSyncClient(self.api.db_path).download_file(
                        settings.path, file, "songs"
                    )
                except Exception as exc:
                    print(f" [AI DJ] Could not fetch {file}: {exc}")
            if not os.path.exists(path):
                continue
            try:
                song = self.api.metadata.get_song_metadata(path, file, include_cover=True)
            except Exception:
                song = {"File": file}
            song["File"] = file
            if not song.get("Title"):
                song["Title"] = track.get("title") or file
            if not song.get("Artist"):
                song["Artist"] = track.get("artist") or ""
            if not song.get("Album"):
                song["Album"] = track.get("album") or ""
            if not song.get("Duration"):
                song["Duration"] = track.get("durationSec") or 0
            files.append(song)
        return files

    def _install_local_queue(self, files, session):
        if not files:
            return False
        source = {"type": "ai_dj", "id": session}
        try:
            self.api.playback.populate_queue_from_list(
                files[0], files, None, source
            )
            self.api.playback.play_button(files[0])
            return True
        except Exception as exc:
            print(f" [AI DJ] Local queue start failed: {exc}")
            return False

    def _sync_server_queue(self, files, session):
        names = [str(song.get("File")) for song in files if song.get("File")]
        if not names:
            return
        with self._lock:
            if not self._active or self._session_id != session:
                return
        try:
            self.player_sync.replace_queue(names[0], names, session)
            self.player_sync.play(names[0])
            with self._lock:
                if not self._active or self._session_id != session:
                    return
                self._server_queue_ready = True
        except Exception as exc:
            with self._lock:
                if self._session_id != session:
                    return
                self._server_queue_ready = False
                if self._active:
                    self._error = f"Server queue sync failed: {exc}"
            print(f" [AI DJ] Server queue sync failed: {exc}")
            self._publish()

    def _on_track_changed(self, song):
        if not isinstance(song, dict):
            return
        file = str(song.get("File") or "")
        if not file:
            return
        with self._lock:
            if not self._active:
                return
            previous = self._last_server_file
            self._last_server_file = file
            if not previous or previous == file:
                return
            local_names = [str(s.get("File")) for s in self._local_files]
            try:
                old_index = local_names.index(previous)
            except ValueError:
                old_index = -1
            try:
                new_index = local_names.index(file)
            except ValueError:
                return
            # Backwards moves must not advance the server queue.
            if old_index >= 0 and new_index <= old_index:
                return
            if not self._server_queue_ready:
                return
        threading.Thread(
            target=self._server_next,
            args=(previous,),
            daemon=True,
        ).start()

    def _server_next(self, expected):
        try:
            self.player_sync.next(auto=True, expected_file=expected)
        except Exception as exc:
            print(f" [AI DJ] Server next failed: {exc}")

    def _on_queue_changed(self, source):
        with self._lock:
            if not self._active:
                return
            if isinstance(source, dict) and source.get("type") == "ai_dj":
                return
        self._end_session(stop_server=True)

    def _on_queue_exhausted(self):
        with self._lock:
            if not self._active or self._advancing:
                return
            if self._voice == "speaking":
                # Voice is already running over the transition: start the next
                # batch now, keeping music ducked until the clip set ends.
                self._waiting_at_end = False
                should_advance = True
            elif self._boundary_started:
                if self._waiting_for_voice:
                    self._waiting_at_end = True
                    self._wait_deadline = time.time() + 30.0
                    should_advance = False
                    self._try_start_boundary()
                else:
                    should_advance = True
            else:
                self._boundary_started = True
                if self._try_start_boundary():
                    self._waiting_at_end = self._voice == "speaking"
                    self._wait_deadline = 0.0
                    should_advance = False
                else:
                    self._waiting_at_end = True
                    self._wait_deadline = time.time() + 30.0
                    should_advance = False
        if should_advance:
            self._begin_next_batch()
        else:
            self._publish()

    def _play_intro(self):
        with self._lock:
            batch = self._local_batch
            if not self._active or not batch:
                return
            ids = batch.get("introClipIds") or []
            should = batch.get("shouldComment")
        if should and ids:
            self._play_voice(ids)

    # -- boundary / voice ---------------------------------------------
    def _check_boundary(self):
        with self._lock:
            if not self._active or self._advancing or not self._local_files:
                return
            current = str(self.api.current_filename or "")
            last = str(self._local_files[-1].get("File") or "")
            if not current or current != last:
                return
            if self._boundary_started:
                if self._waiting_for_voice and self._voice != "speaking":
                    self._try_start_boundary()
                return
            duration = float((self.api.last_song or {}).get("Duration") or 0)
            if duration <= 0:
                return
            try:
                position = float(self.api.playback.get_current_pos() or 0)
            except Exception:
                return
            lead = max(3, int((self._state or {}).get("settings", {}).get("voiceLeadSeconds", 10) or 10))
            if position < duration - lead:
                return
            self._boundary_started = True
            self._try_start_boundary()

    def _try_start_boundary(self):
        with self._lock:
            if self._voice == "speaking":
                return True
            next_batch = self._next_batch
            current = self._local_batch or {}
            ids = list(current.get("outroClipIds") or [])
            if next_batch:
                ids.extend(next_batch.get("introClipIds") or [])
            if not next_batch or not ids:
                self._waiting_for_voice = True
                self._voice = "waiting"
                self._waiting = True
                return False
            self._boundary_intro_played = True
            self._waiting_for_voice = False
            self._waiting = False
            self._wait_deadline = 0.0
            return self._play_voice(ids)

    def _play_voice(self, ids):
        ids = [str(i) for i in ids if _text(i)]
        if not ids:
            return False
        with self._lock:
            if not self._active:
                return False
            self._voice_generation += 1
            generation = self._voice_generation
            self._voice = "speaking"
            self._waiting = False
            duck = float((self._state or {}).get("settings", {}).get("duckLevel", 0.18) or 0.18)
        try:
            self.api.playback.set_duck(duck)
        except Exception:
            pass
        self._publish()
        window = getattr(self.api, "_window", None)
        if not window:
            return False
        try:
            window.evaluate_js(
                "if (window.AiDjVoice) window.AiDjVoice.play("
                f"{json.dumps(ids)}, {duck}, {generation});"
            )
            return True
        except Exception as exc:
            print(f" [AI DJ] Voice dispatch failed: {exc}")
            self._on_voice_error(generation, "Voice playback unavailable.")
            return False

    def _on_voice_finished(self, generation):
        with self._lock:
            if generation != self._voice_generation:
                return False
            self._voice = "idle"
            self._waiting = False
            advance = self._waiting_at_end
            self._waiting_at_end = False
            self._wait_deadline = 0.0
        try:
            self.api.playback.set_duck(1.0)
        except Exception:
            pass
        self._publish()
        if advance:
            self._begin_next_batch()
        return True

    def _on_voice_error(self, generation, message):
        with self._lock:
            if generation != self._voice_generation:
                return False
            self._voice = "idle"
            self._waiting = False
            advance = self._waiting_at_end
            self._waiting_at_end = False
            self._wait_deadline = 0.0
            self._error = message or "Voice playback failed."
        try:
            self.api.playback.set_duck(1.0)
        except Exception:
            pass
        self._publish()
        if advance:
            self._begin_next_batch()
        return True

    def _stop_voice(self):
        with self._lock:
            self._voice_generation += 1
            self._voice = "idle"
            self._waiting = False
        window = getattr(self.api, "_window", None)
        if window:
            try:
                window.evaluate_js("if (window.AiDjVoice) window.AiDjVoice.stop();")
            except Exception:
                pass
        try:
            self.api.playback.set_duck(1.0)
        except Exception:
            pass

    # -- next batch ----------------------------------------------------
    def _begin_next_batch(self):
        with self._lock:
            if not self._active or self._advancing:
                return False
            next_batch = self._next_batch
            if not next_batch:
                self._waiting_for_voice = True
                self._waiting_at_end = True
                self._wait_deadline = time.time() + 30.0
                self._voice = "waiting"
                self._waiting = True
                return False
            self._advancing = True
        files = self._resolve_batch(next_batch, download_missing=True)
        if not files:
            with self._lock:
                self._advancing = False
                self._error = "The next set's tracks are not available locally."
                self._waiting = True
                self._voice = "waiting"
            self._publish()
            return False

        with self._lock:
            previous = self._local_batch
            session = self._session_id
            should_intro = not self._boundary_intro_played
            self._local_batch = next_batch
            self._next_batch = None
            self._local_files = files
            self._expected_files = [str(song.get("File")) for song in files]
            # The new set gets its own server queue below; do not let the
            # first local track change look like a forward step in the old
            # batch.
            self._last_server_file = str(files[0].get("File"))
            self._server_queue_ready = False
            self._boundary_started = False
            self._boundary_intro_played = False
            self._waiting_for_voice = False
            self._waiting_at_end = False
            self._wait_deadline = 0.0
            self._voice = self._voice if self._voice == "speaking" else "idle"
            self._waiting = False
            self._error = None
        if not self._install_local_queue(files, session):
            with self._lock:
                self._advancing = False
                self._error = "Could not start the next DJ set."
            self._publish()
            return False
        threading.Thread(
            target=self._sync_server_queue, args=(files, session), daemon=True
        ).start()
        if self._voice != "speaking":
            try:
                self.api.playback.set_duck(1.0)
            except Exception:
                pass
        with self._lock:
            self._advancing = False
        self._publish()
        if should_intro and next_batch.get("shouldComment") and next_batch.get("introClipIds"):
            threading.Timer(0.35, self._play_intro).start()
        if previous:
            print(f" [AI DJ] batch {previous.get('number')} -> {next_batch.get('number')}")
        return True

    # -- polling -------------------------------------------------------
    def _start_polling(self):
        self._poll_stop.clear()
        if self._poll_thread and self._poll_thread.is_alive():
            return
        self._poll_thread = threading.Thread(target=self._poll_loop, daemon=True)
        self._poll_thread.start()

    def _stop_polling(self):
        self._poll_stop.set()
        self._poll_thread = None

    def _poll_loop(self):
        last_state = 0.0
        while not self._poll_stop.is_set():
            with self._lock:
                active = self._active
            if not active:
                break
            now = time.time()
            if now - last_state >= 2.0 or self._waiting_for_voice:
                last_state = now
                try:
                    self._apply_server_state(self.api_client.state())
                except Exception as exc:
                    print(f" [AI DJ] State refresh failed: {exc}")
                self._publish()
            self._check_boundary()
            with self._lock:
                waiting_at_end = self._waiting_at_end
                deadline = self._wait_deadline
                speaking = self._voice == "speaking"
            if waiting_at_end and not speaking and deadline and time.time() > deadline:
                with self._lock:
                    self._waiting_at_end = False
                    self._waiting = False
                    self._voice = "idle"
                    self._wait_deadline = 0.0
                    self._error = "Voice was not ready; continuing without commentary."
                self._publish()
                self._begin_next_batch()
            self._poll_stop.wait(0.45)
        self._poll_stop.clear()

    # -- reset ---------------------------------------------------------
    def _reset_local(self, stop_voice=True):
        with self._lock:
            self._active = False
            self._session_id = None
            self._local_batch = None
            self._next_batch = None
            self._local_files = []
            self._expected_files = []
            self._last_server_file = None
            self._server_queue_ready = False
            self._boundary_started = False
            self._boundary_intro_played = False
            self._waiting_for_voice = False
            self._waiting_at_end = False
            self._wait_deadline = 0.0
            self._advancing = False
        self._stop_polling()
        if stop_voice:
            self._stop_voice()

    def _end_session(self, stop_server=True):
        with self._lock:
            session = self._session_id
            self._active = False
            self._session_id = None
            self._local_batch = None
            self._next_batch = None
            self._local_files = []
            self._expected_files = []
            self._boundary_started = False
            self._boundary_intro_played = False
            self._waiting_for_voice = False
            self._waiting_at_end = False
            self._wait_deadline = 0.0
            self._advancing = False
            self._server_queue_ready = False
            self._voice = "idle"
            self._waiting = False
        self._stop_polling()
        self._stop_voice()
        if stop_server and session:
            threading.Thread(target=self._stop_server_session, daemon=True).start()
        self._publish()

    def _stop_server_session(self):
        try:
            state = self.api_client.stop()
            with self._lock:
                self._state = state
        except Exception:
            pass

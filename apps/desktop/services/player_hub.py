"""Polling live-player hub for cross-device convergence.

Local playback stays authoritative. This service publishes desktop transport
mutations to the Web API and applies remote pause/resume/next/seek/volume when
the referenced track exists locally. Polling is intentional: no SignalR
dependency, and every network failure is swallowed so playback never stops.
"""

from __future__ import annotations

import os
import threading
import time

import settings

from .api_config import get_api_config
from .auth_session import get_auth_session
from .player_sync import PlayerSync


POLL_SECONDS = 3.0
POSITION_DRIFT_SECONDS = 5.0
IGNORE_AFTER_PUBLISH = 4.0


class PlayerHub:
    def __init__(self, api, start_poll=True, sync=None):
        self.api = api
        self.sync = sync or PlayerSync(api, timeout=8)
        self._lock = threading.RLock()
        self._applying = False
        self._expected = None
        self._ignore_until = 0.0
        self._missing_notified = set()
        self._poll_stop = threading.Event()
        self._thread = None
        playback = getattr(api, 'playback', None)
        if playback is not None:
            playback.add_track_changed_listener(self._on_track_changed)
            playback.add_queue_changed_listener(self._on_queue_changed)
            playback.add_queue_exhausted_listener(self._on_queue_exhausted)
        if start_poll:
            self._thread = threading.Thread(
                target=self._poll_loop, daemon=True, name="player-hub"
            )
            self._thread.start()

    # -- state helpers -------------------------------------------------
    def _configured(self):
        try:
            if get_auth_session(self.api.db_path).configured():
                return True
        except Exception:
            pass
        try:
            url, key = get_api_config(self.api.db_path)
            return bool(url and key)
        except Exception:
            return False

    def _ai_dj_active(self):
        try:
            return bool(self.api.ai_dj.view().get('active'))
        except Exception:
            return False

    def _current_file(self):
        return str(getattr(self.api, 'current_filename', '') or '')

    def _local_volume(self):
        try:
            return max(0.0, min(1.0, float(settings.volume)))
        except (TypeError, ValueError):
            return 1.0

    def _next_files(self):
        playback = getattr(self.api, 'playback', None)
        if playback is None:
            return []
        return [
            str(song.get('File'))
            for song in getattr(playback, 'next_songs', []) or []
            if song.get('File')
        ]

    def _queue_files(self):
        current = self._current_file()
        if not current:
            return []
        return [current] + self._next_files()

    def _source(self):
        try:
            source = self.api.playback._read_source()
            if isinstance(source, dict) and source.get('type'):
                return {'type': source.get('type'), 'id': source.get('id')}
        except Exception:
            pass
        return None

    def _local_position(self):
        try:
            return max(0.0, float(self.api.playback.get_current_pos() or 0))
        except Exception:
            return 0.0

    def _song_payload(self):
        song = getattr(self.api, 'last_song', None) or {}
        return song if isinstance(song, dict) else {}

    def _remember(self, state):
        if not isinstance(state, dict):
            return
        song = state.get('currentSong') or {}
        file = str(song.get('File') or '')
        if not file:
            return
        try:
            volume = float(state.get('volume') or state.get('Volume') or self._local_volume())
        except (TypeError, ValueError):
            volume = self._local_volume()
        with self._lock:
            self._expected = {
                'file': file,
                'is_playing': bool(state.get('isPlaying', state.get('IsPlaying'))),
                'volume': max(0.0, min(1.0, volume)),
                'version': int(state.get('queueVersion', state.get('QueueVersion', 0)) or 0),
                'position': max(0.0, float(state.get('positionSec', state.get('PositionSec', 0)) or 0)),
                'at': time.time(),
            }

    def _remember_queue(self, state):
        if not isinstance(state, dict):
            return
        with self._lock:
            if self._expected is None:
                return
            self._expected['version'] = int(
                state.get('queueVersion', state.get('QueueVersion', 0)) or 0
            )

    def _note_local(self):
        with self._lock:
            self._ignore_until = time.time() + IGNORE_AFTER_PUBLISH
            if self._expected is not None:
                # Keep the old version until the server confirms the mutation;
                # the ignore window covers the in-flight round trip.
                self._expected['at'] = time.time()

    # -- publishing ----------------------------------------------------
    def _on_track_changed(self, song):
        if not isinstance(song, dict) or not song.get('File'):
            return
        if self._applying or self._ai_dj_active():
            return
        self._note_local()
        threading.Thread(
            target=self._publish_track, args=(song,), daemon=True
        ).start()

    def _publish_track(self, song):
        if not self._configured() or self._ai_dj_active():
            return
        try:
            queue_state = self._publish_queue()
            del queue_state
            state = self.sync.play(
                str(song.get('File')), bool(song.get('IsPodcast'))
            )
            self._remember(state)
        except Exception as exc:
            print(f" [PlayerHub] publish play failed: {exc}")

    def _on_queue_changed(self, source):
        if self._applying or self._ai_dj_active():
            return
        self._note_local()
        timer = threading.Timer(0.35, self._publish_queue)
        timer.daemon = True
        timer.start()

    def _on_queue_exhausted(self):
        if self._applying or self._ai_dj_active():
            return
        current = self._current_file()
        if not current:
            return
        self._note_local()
        threading.Thread(
            target=self._publish_pause, args=(current,), daemon=True
        ).start()

    def _publish_queue(self):
        if not self._configured() or self._ai_dj_active():
            return None
        files = self._queue_files()
        if not files:
            return None
        try:
            state = self.sync.replace_queue(
                files[0], files[:200], None, self._source()
            )
            self._remember_queue(state)
            return state
        except Exception as exc:
            print(f" [PlayerHub] publish queue failed: {exc}")
            return None

    def _publish_pause(self, file=None):
        if not self._configured() or self._ai_dj_active():
            return None
        try:
            state = self.sync.pause(file or self._current_file(), self._local_position())
            self._remember(state)
            return state
        except Exception as exc:
            print(f" [PlayerHub] publish pause failed: {exc}")
            return None

    def publish_transport(self):
        """Publish pause/resume after a local play-button toggle."""
        if self._applying or self._ai_dj_active() or not self._current_file():
            return None
        self._note_local()
        try:
            if getattr(self.api, 'playing', False):
                state = self.sync.resume()
            else:
                state = self.sync.pause(
                    self._current_file(), self._local_position()
                )
            self._remember(state)
            return state
        except Exception as exc:
            print(f" [PlayerHub] publish transport failed: {exc}")
            return None

    def publish_seek(self, seconds):
        if self._applying or self._ai_dj_active() or not self._current_file():
            return None
        self._note_local()
        try:
            state = self.sync.seek(seconds)
            self._remember(state)
            return state
        except Exception as exc:
            print(f" [PlayerHub] publish seek failed: {exc}")
            return None

    def publish_volume(self, volume):
        if (self._applying or self._ai_dj_active()
                or not self._current_file()):
            return None
        self._note_local()
        try:
            state = self.sync.volume(volume)
            self._remember(state)
            return state
        except Exception as exc:
            print(f" [PlayerHub] publish volume failed: {exc}")
            return None

    # -- polling / applying -------------------------------------------
    def _poll_loop(self):
        while not self._poll_stop.wait(POLL_SECONDS):
            try:
                self.poll_once()
            except Exception:
                continue

    def poll_once(self):
        if self._applying or self._ai_dj_active() or not self._configured():
            return None
        try:
            state = self.sync.state()
        except Exception:
            return None
        if not isinstance(state, dict):
            return None
        song = state.get('currentSong') or {}
        remote_file = str(song.get('File') or '')
        if not remote_file:
            return state
        remote_playing = bool(state.get('isPlaying'))
        try:
            remote_volume = max(0.0, min(1.0, float(state.get('volume') or 0)))
        except (TypeError, ValueError):
            remote_volume = self._local_volume()
        try:
            remote_position = max(0.0, float(state.get('positionSec') or 0))
        except (TypeError, ValueError):
            remote_position = 0.0
        remote_version = int(state.get('queueVersion') or 0)

        with self._lock:
            expected = dict(self._expected) if self._expected else None
            ignore_until = self._ignore_until
        now = time.time()
        if now < ignore_until:
            return state

        local_file = self._current_file()
        local_playing = bool(getattr(self.api, 'playing', False))
        local_volume = self._local_volume()
        local_position = self._local_position()

        if (expected and remote_file == expected['file']
                and remote_playing == expected['is_playing']
                and abs(remote_volume - expected['volume']) < 0.02
                and (not expected['version'] or remote_version == expected['version'])):
            projected = expected['position']
            if expected['is_playing']:
                projected += max(0.0, now - expected['at'])
            if abs(remote_position - projected) > POSITION_DRIFT_SECONDS:
                self._apply_position(remote_position)
            with self._lock:
                if self._expected:
                    self._expected['position'] = remote_position
                    self._expected['at'] = now
            return state

        if not local_file and not local_playing:
            return state  # never auto-start playback at launch

        need_file = remote_file != local_file
        need_play = remote_playing != local_playing
        need_volume = abs(remote_volume - local_volume) > 0.02
        need_seek = (not need_file and abs(remote_position - local_position)
                     > POSITION_DRIFT_SECONDS)
        if not (need_file or need_play or need_volume or need_seek):
            return state
        self._apply_state(state)
        return state

    def _apply_position(self, position):
        with self._lock:
            self._applying = True
        try:
            self.api.progress_slider_click(position)
        finally:
            with self._lock:
                self._applying = False

    def _resolve_song(self, song, include_cover=True):
        if not isinstance(song, dict):
            return None
        file = os.path.basename(str(song.get('File') or ''))
        if not file:
            return None
        is_podcast = bool(song.get('IsPodcast'))
        base = settings.podcasts_path if is_podcast else settings.path
        path = os.path.join(base, file)
        if not os.path.isfile(path):
            return None
        try:
            data = self.api.metadata.get_song_metadata(
                path, file, include_cover=include_cover)
        except Exception:
            data = {'File': file, 'Title': file, 'Artist': '', 'Duration': 0}
        data['File'] = file
        data['IsPodcast'] = is_podcast
        return data

    def _resolve_queue(self, current, queue):
        songs = []
        seen = set()
        current_file = str((current or {}).get('File') or '').lower()
        for item in [current] + list(queue or [])[:50]:
            if not isinstance(item, dict):
                continue
            file = os.path.basename(str(item.get('File') or ''))
            if not file or file.lower() in seen:
                continue
            seen.add(file.lower())
            song = self._resolve_song(
                item, include_cover=file.lower() == current_file)
            if song:
                songs.append(song)
        return songs

    def _notify_missing(self, file):
        if not file or file in self._missing_notified:
            return
        self._missing_notified.add(file)
        window = getattr(self.api, '_window', None)
        if not window:
            return
        try:
            import json
            window.evaluate_js(
                "if (window.showPlayerHubNotice) "
                f"window.showPlayerHubNotice({json.dumps('Not on this device: ' + file)});"
            )
        except Exception:
            pass

    def _apply_state(self, state):
        song_raw = state.get('currentSong') or {}
        remote_file = str(song_raw.get('File') or '')
        local_file = self._current_file()
        remote_playing = bool(state.get('isPlaying'))
        try:
            remote_volume = max(0.0, min(1.0, float(state.get('volume') or 0)))
        except (TypeError, ValueError):
            remote_volume = self._local_volume()
        try:
            remote_position = max(0.0, float(state.get('positionSec') or 0))
        except (TypeError, ValueError):
            remote_position = 0.0

        with self._lock:
            self._applying = True
        try:
            if remote_file != local_file:
                current = self._resolve_song(song_raw)
                if current is None:
                    self._notify_missing(remote_file)
                    return
                queue = self._resolve_queue(song_raw, state.get('queue') or [])
                if not queue:
                    queue = [current]
                source = state.get('source') or state.get('Source')
                if not isinstance(source, dict) or not source.get('type'):
                    source = {
                        'type': 'podcast' if current.get('IsPodcast') else 'all_songs',
                        'id': None,
                    }
                else:
                    source = {
                        'type': source.get('type') or source.get('Type'),
                        'id': source.get('id') if 'id' in source else source.get('Id'),
                    }
                self.api.playback.populate_queue_from_list(
                    current, queue, None, source
                )
                self.api.playback.play_button(current)
                if not remote_playing:
                    self.api.playback.play_button(None)
                if remote_position > 1.0:
                    self.api.progress_slider_click(remote_position, publish=False)
            else:
                if remote_playing != bool(getattr(self.api, 'playing', False)):
                    self.api.playback.play_button(None)
                if abs(remote_position - self._local_position()) > POSITION_DRIFT_SECONDS:
                    self.api.progress_slider_click(remote_position, publish=False)
            if abs(remote_volume - self._local_volume()) > 0.02:
                self.api.volume_slider(remote_volume, publish=False)
            self._remember(state)
        finally:
            with self._lock:
                self._applying = False

    def stop(self):
        self._poll_stop.set()

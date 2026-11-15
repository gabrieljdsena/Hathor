import json
import os
import tempfile
import unittest
from unittest.mock import patch

from services.ai_dj import AiDjApi, AiDjController, parse_batch, parse_state
from services.player_sync import PlayerSync
from tests.support import FakeHathorServer


class AiDjParsingTests(unittest.TestCase):
    def test_parse_state_and_batch(self):
        state = parse_state({
            "enabled": True,
            "configured": True,
            "sessionId": "sess",
            "status": "active",
            "settings": {
                "batchSize": 4,
                "language": "pt-BR",
                "duckLevel": 0.2,
                "voiceLeadSeconds": 8,
            },
            "currentBatch": {
                "id": "b1",
                "number": 1,
                "status": "playing",
                "tracks": [{
                    "file": "track.mp3",
                    "title": "Track",
                    "artist": "Artist",
                    "album": "Album",
                    "duration": 123,
                }],
                "introClipIds": ["i1", "i2"],
                "outroClipIds": [],
                "shouldComment": True,
            },
            "nextBatch": None,
            "error": None,
        })
        self.assertTrue(state["enabled"])
        self.assertEqual(state["sessionId"], "sess")
        self.assertEqual(state["settings"]["language"], "pt-BR")
        self.assertEqual(state["currentBatch"]["tracks"][0]["file"], "track.mp3")
        self.assertEqual(state["currentBatch"]["introClipIds"], ["i1", "i2"])

    def test_parse_batch_tolerates_missing_fields(self):
        batch = parse_batch({"id": "b", "tracks": [{"file": "x.mp3"}]})
        self.assertEqual(batch["tracks"][0]["title"], "")
        self.assertEqual(batch["outroClipIds"], [])
        self.assertFalse(batch["shouldComment"])


class PlayerSyncTests(unittest.TestCase):
    def test_queue_play_and_next_payloads(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            server.add("PUT", "/api/v1/player/queue", json_body={})
            server.add("POST", "/api/v1/player/play", json_body={})
            server.add("POST", "/api/v1/player/next", json_body={})
            with patch("services.player_sync.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                sync = PlayerSync(type("A", (), {"db_path": os.path.join(tmp, "x.db")})())
                sync.replace_queue("a.mp3", ["a.mp3", "b.mp3"], "session")
                sync.play("a.mp3")
                sync.next(auto=True, expected_file="a.mp3")

            self.assertEqual(server.requests[0]["route"], "/api/v1/player/queue")
            queue_body = json.loads(server.requests[0]["body"])
            self.assertEqual(queue_body["Source"], {"Type": "ai_dj", "Id": "session"})
            self.assertEqual(queue_body["Files"], ["a.mp3", "b.mp3"])
            self.assertEqual(server.requests[1]["route"], "/api/v1/player/play")
            self.assertEqual(json.loads(server.requests[1]["body"])["File"], "a.mp3")
            next_body = json.loads(server.requests[2]["body"])
            self.assertTrue(next_body["Auto"])
            self.assertEqual(next_body["ExpectedFile"], "a.mp3")


class _DummyThread:
    def __init__(self, *args, **kwargs):
        pass

    def start(self):
        pass


class _ImmediateThread:
    def __init__(self, target=None, args=(), kwargs=None, **unused):
        self.target = target
        self.args = args
        self.kwargs = kwargs or {}

    def start(self):
        if self.target:
            self.target(*self.args, **self.kwargs)


class _FakeSync:
    def __init__(self):
        self.calls = []

    def next(self, auto=True, expected_file=None):
        self.calls.append((auto, expected_file))
        return {}


class _FakePlayback:
    def __init__(self):
        self.on_track_changed = None
        self.on_queue_exhausted = None
        self.on_queue_changed = None
        self.duck = 1.0
        self.queued = None
        self.played = []

    def set_duck(self, value):
        self.duck = value
        return value

    def populate_queue_from_list(self, current, songs, playlist_id=None, source=None):
        self.queued = (current, songs, playlist_id, source)

    def play_button(self, song, opening=False):
        self.played.append(song)
        return True


class _FakeMetadata:
    def get_song_metadata(self, path, file, include_cover=False):
        return {"File": file, "Title": "Track", "Artist": "Artist", "Duration": 120}


class AiDjControllerViewTests(unittest.TestCase):
    def test_view_exposes_local_batch_and_song(self):
        with tempfile.TemporaryDirectory() as tmp, \
                patch("services.ai_dj.threading.Thread", _DummyThread), \
                patch("services.ai_dj.get_api_config",
                      return_value=("http://host:5051", "hth_secret")):
            playback = _FakePlayback()
            fake = type("A", (), {
                "db_path": os.path.join(tmp, "x.db"),
                "playback": playback,
                "metadata": object(),
                "last_song": {"File": "track.mp3", "Title": "Track", "Artist": "Artist"},
                "current_filename": "track.mp3",
            })()
            controller = AiDjController(fake)
            controller._state = parse_state({
                "enabled": True, "configured": True, "status": "active",
                "sessionId": "sess", "settings": {"duckLevel": 0.18},
            })
            controller._active = True
            controller._session_id = "sess"
            controller._local_batch = {
                "id": "b1", "number": 2, "tracks": [{"file": "track.mp3"}],
                "introClipIds": [], "outroClipIds": [],
            }
            controller._next_batch = {"id": "b2", "number": 3, "tracks": []}
            view = controller.view()

            self.assertTrue(view["active"])
            self.assertTrue(view["apiConfigured"])
            self.assertEqual(view["currentBatch"]["number"], 2)
            self.assertEqual(view["nextBatch"]["number"], 3)
            self.assertEqual(view["currentSong"]["title"], "Track")
            self.assertIsNotNone(playback.on_track_changed)

    def test_forward_track_change_advances_server_queue(self):
        with tempfile.TemporaryDirectory() as tmp, \
                patch("services.ai_dj.threading.Thread", _DummyThread), \
                patch("services.ai_dj.get_api_config",
                      return_value=("http://host:5051", "hth_secret")):
            fake = type("A", (), {
                "db_path": os.path.join(tmp, "x.db"),
                "playback": _FakePlayback(),
                "metadata": object(),
                "last_song": {},
                "current_filename": "b.mp3",
            })()
            controller = AiDjController(fake)
            sync = _FakeSync()
            controller.player_sync = sync
            controller._active = True
            controller._server_queue_ready = True
            controller._local_files = [{"File": "a.mp3"}, {"File": "b.mp3"}]
            controller._last_server_file = "a.mp3"

            with patch("services.ai_dj.threading.Thread", _ImmediateThread):
                controller._on_track_changed({"File": "b.mp3"})
                controller._on_track_changed({"File": "a.mp3"})

            self.assertEqual(sync.calls, [(True, "a.mp3")])

    def test_next_batch_replaces_server_queue(self):
        with tempfile.TemporaryDirectory() as tmp, \
                patch("services.ai_dj.threading.Thread", _DummyThread), \
                patch("services.ai_dj.get_api_config",
                      return_value=("http://host:5051", "hth_secret")):
            fake = type("A", (), {
                "db_path": os.path.join(tmp, "x.db"),
                "playback": _FakePlayback(),
                "metadata": object(),
                "last_song": {},
                "current_filename": "b.mp3",
            })()
            controller = AiDjController(fake)
            next_batch = {
                "id": "b2", "number": 2, "tracks": [{"file": "c.mp3"}],
                "introClipIds": [], "outroClipIds": [], "shouldComment": False,
            }
            controller._active = True
            controller._session_id = "sess"
            controller._local_batch = {"id": "b1", "number": 1, "tracks": []}
            controller._next_batch = next_batch
            controller._local_files = [{"File": "a.mp3"}]
            controller._last_server_file = "a.mp3"
            controller._server_queue_ready = True
            controller._resolve_batch = lambda batch, download_missing=True: [
                {"File": "c.mp3", "Title": "C"}
            ]
            installed = []
            controller._install_local_queue = lambda files, session: installed.append((files, session)) or True
            synced = []
            controller._sync_server_queue = lambda files, session: synced.append((files, session))

            with patch("services.ai_dj.threading.Thread", _ImmediateThread):
                self.assertTrue(controller._begin_next_batch())

            self.assertEqual(installed[0][1], "sess")
            self.assertEqual(synced[0][1], "sess")
            self.assertEqual(synced[0][0][0]["File"], "c.mp3")
            self.assertEqual(controller._local_batch["id"], "b2")
            self.assertEqual(controller._last_server_file, "c.mp3")
            self.assertFalse(controller._server_queue_ready)

    def test_start_installs_first_batch_locally(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            open(os.path.join(tmp, "track.mp3"), "wb").close()
            server.add("POST", "/api/v1/ai-dj/start", json_body={
                "enabled": True,
                "configured": True,
                "sessionId": "sess",
                "status": "active",
                "settings": {"duckLevel": 0.18, "voiceLeadSeconds": 10},
                "currentBatch": {
                    "id": "b1",
                    "number": 1,
                    "tracks": [{"file": "track.mp3", "title": "Track", "artist": "Artist"}],
                    "introClipIds": [],
                    "outroClipIds": [],
                },
                "nextBatch": None,
            })
            with patch.object(AiDjController, "_initial_refresh", lambda self: None), \
                    patch.object(AiDjController, "_start_polling", lambda self: None), \
                    patch.object(AiDjController, "_sync_server_queue", lambda self, files, session: None), \
                    patch("services.ai_dj.settings.path", tmp), \
                    patch("services.ai_dj.get_api_config",
                          return_value=(server.base_url, "hth_secret")):
                playback = _FakePlayback()
                fake = type("A", (), {
                    "db_path": os.path.join(tmp, "x.db"),
                    "playback": playback,
                    "metadata": _FakeMetadata(),
                    "last_song": {},
                    "current_filename": None,
                    "repeat": False,
                })()
                controller = AiDjController(fake)
                view = controller.start("warm")

            self.assertTrue(view["active"], view.get("error"))
            self.assertEqual(view["sessionId"], "sess")
            self.assertEqual(playback.queued[3], {"type": "ai_dj", "id": "sess"})
            self.assertEqual(playback.played[0]["File"], "track.mp3")


class AiDjApiTests(unittest.TestCase):
    def test_start_and_clip_url(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            server.add("POST", "/api/v1/ai-dj/start", json_body={
                "enabled": True,
                "configured": True,
                "sessionId": "sess",
                "status": "active",
                "settings": {"duckLevel": 0.18, "voiceLeadSeconds": 10},
                "currentBatch": {"id": "b", "number": 1, "tracks": []},
            })
            fake_api = type("A", (), {"db_path": os.path.join(tmp, "x.db")})()
            with patch("services.ai_dj.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                api = AiDjApi(fake_api)
                state = api.start("warm evening")
                url = api.clip_url("clip-id")

            self.assertEqual(state["status"], "active")
            self.assertTrue(url.startswith(server.base_url + "/api/v1/ai-dj/clips/clip-id/audio"))
            self.assertIn("token=hth_secret", url)


if __name__ == "__main__":
    unittest.main()

import json
import os
import tempfile
import unittest
from unittest.mock import patch

from services.telemetry import PlaybackTelemetry
from tests.support import FakeHathorServer


class _FakePlayback:
    def __init__(self):
        self.track_listeners = []
        self.exhausted_listeners = []
        self.position = 0.0

    def add_track_changed_listener(self, callback):
        self.track_listeners.append(callback)

    def add_queue_exhausted_listener(self, callback):
        self.exhausted_listeners.append(callback)

    def get_current_pos(self):
        return self.position


class _FakeApi:
    def __init__(self, db_path):
        self.db_path = db_path
        self.playback = _FakePlayback()
        self.current_filename = "a.mp3"
        self.last_song = {"File": "a.mp3", "Duration": 100, "IsPodcast": False}
        self.playing = True


class PlaybackTelemetryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.api = _FakeApi(os.path.join(self.tmp.name, "library.db"))
        self.telemetry = PlaybackTelemetry(self.api, start_poll=False)
        self.sent = []
        self.telemetry._deliver = lambda payload, wait=False: self.sent.append(payload)

    def tearDown(self):
        self.tmp.cleanup()

    def test_completed_segment_uses_track_duration(self):
        self.api.playback.position = 99.0
        self.telemetry._on_track_changed(self.api.last_song)
        self.api.playing = False
        payload = self.telemetry.flush(wait=True)

        self.assertEqual(payload["File"], "a.mp3")
        self.assertTrue(payload["Completed"])
        self.assertFalse(payload["Skipped"])
        self.assertEqual(payload["DurationSec"], 100.0)
        self.assertEqual(self.sent[0]["SegmentId"], payload["SegmentId"])

    def test_manual_skip_closes_the_previous_segment(self):
        self.api.playback.position = 10.0
        self.telemetry._on_track_changed(self.api.last_song)
        self.api.current_filename = "b.mp3"
        self.api.playback.position = 0.0
        self.telemetry._on_track_changed({
            "File": "b.mp3", "Duration": 120, "IsPodcast": False,
        })

        closed = [p for p in self.sent if p["File"] == "a.mp3"][0]
        self.assertFalse(closed["Completed"])
        self.assertTrue(closed["Skipped"])
        self.assertEqual(closed["PositionSec"], 10.0)

    def test_podcasts_are_never_reported(self):
        self.api.last_song = {"File": "ep.mp3", "Duration": 900, "IsPodcast": True}
        self.api.current_filename = "ep.mp3"
        self.telemetry._on_track_changed(self.api.last_song)
        self.telemetry.flush(wait=True)
        self.assertEqual(self.sent, [])

    def test_http_failure_is_swallowed(self):
        payload = {
            "SegmentId": "seg", "File": "a.mp3", "PositionSec": 1,
            "DurationSec": 2, "Completed": False, "Skipped": False,
            "StartedAtUtc": None,
        }
        with patch("services.telemetry.get_api_config", return_value=("http://127.0.0.1:1", "k")):
            self.telemetry._send(payload)  # must not raise
        self.assertEqual(self.sent, [])

    def test_posts_the_server_contract(self):
        with FakeHathorServer() as server:
            server.add("POST", "/api/v1/player/playback-events", status=204)
            self.api.playback.position = 80.0
            self.telemetry._on_track_changed(self.api.last_song)
            self.api.playing = False
            self.telemetry._deliver = (
                lambda payload, wait=False: self.telemetry._send(payload)
            )
            with patch("services.telemetry.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                self.telemetry.flush(wait=True)

            self.assertEqual(server.requests[0]["route"], "/api/v1/player/playback-events")
            body = json.loads(server.requests[0]["body"])
            self.assertEqual(body["File"], "a.mp3")
            self.assertEqual(body["DurationSec"], 100.0)
            self.assertEqual(body["Completed"], False)
            self.assertEqual(body["Skipped"], False)
            self.assertTrue(body["SegmentId"])


if __name__ == "__main__":
    unittest.main()

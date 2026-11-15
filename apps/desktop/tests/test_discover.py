import os
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from services.discover import DiscoverService, normalize_key
from tests.support import FakeHathorServer


class _FakeDownloader:
    def __init__(self, preview=None, hits=None):
        self.preview = preview
        self.hits = hits or []
        self.last_query = None

    def resolve_preview_url(self, query):
        self.last_query = query
        return self.preview

    def search_yt(self, query, limit=1):
        self.last_query = query
        return self.hits[:limit]


class _FakeDownloads:
    def __init__(self):
        self.calls = []

    def submit(self, data, block=True):
        self.calls.append((data, block))
        return True


class _FakeApi:
    def __init__(self, db_path, downloader=None, downloads=None):
        self.db_path = db_path
        self.downloader = downloader or _FakeDownloader()
        self.download_manager = downloads or _FakeDownloads()


class DiscoverServiceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = os.path.join(self.tmp.name, "music_player.db")
        conn = sqlite3.connect(self.db_path)
        try:
            conn.execute("CREATE TABLE Songs (file TEXT, title TEXT, artist TEXT)")
            conn.execute("CREATE TABLE Podcasts (file TEXT, title TEXT, artist TEXT)")
            conn.execute(
                "INSERT INTO Songs (file, title, artist) VALUES (?, ?, ?)",
                ("Paranoid Android.mp3", "Paranoid Android", "Radiohead"),
            )
            conn.execute(
                "INSERT INTO Podcasts (file, title, artist) VALUES (?, ?, ?)",
                ("episode.mp3", "Deep Dive", "Host"),
            )
            conn.commit()
        finally:
            conn.close()

    def tearDown(self):
        self.tmp.cleanup()

    def _service(self, downloader=None, downloads=None):
        return DiscoverService(_FakeApi(self.db_path, downloader, downloads))

    def test_normalize_key_handles_editions_and_features(self):
        self.assertEqual(normalize_key("Song (Remastered 2011)"), "song")
        self.assertEqual(normalize_key("Song feat. Someone"), "song")
        self.assertEqual(normalize_key("Song - Live at Wembley"), "song")
        self.assertEqual(normalize_key("Beyoncé - Halo"), "beyonce halo")

    def test_feed_and_feedback_use_web_api(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/discover", json_body={
                "date": "2026-11-15",
                "cached": False,
                "items": [{"title": "Midnight City", "artist": "M83"}],
            })
            server.add("POST", "/api/v1/discover/feedback", status=204)
            with patch("services.discover.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                service = self._service()
                feed = service.feed()
                service.feedback({
                    "title": "Midnight City", "artist": "M83", "source": "similar",
                    "event": "preview",
                })

            self.assertEqual(feed["items"][0]["title"], "Midnight City")
            request = server.requests[-1]
            self.assertEqual(request["route"], "/api/v1/discover/feedback")
            self.assertIn(b'"event": "preview"', request["body"])

    def test_insights_and_tuning_use_expected_endpoints(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/discover/insights", json_body={"downloads": 0})
            server.add("POST", "/api/v1/discover/tuning", json_body={"applied": False})
            with patch("services.discover.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                service = self._service()
                insights = service.insights(30)
                tuning = service.tune(30)

            self.assertEqual(insights["downloads"], 0)
            self.assertFalse(tuning["applied"])
            self.assertEqual(server.requests[0]["route"], "/api/v1/discover/insights")
            self.assertEqual(server.requests[0]["query"], "windowDays=30")
            self.assertEqual(server.requests[1]["route"], "/api/v1/discover/tuning")
            self.assertIn("windowDays=30", server.requests[1]["query"])
            self.assertIn("k=10", server.requests[1]["query"])

    def test_preview_uses_first_available_server_candidate(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/youtube/search", json_body=[
                {"id": "abc"}, {"id": "def"}
            ])
            server.add("GET", "/api/v1/youtube/preview", json_body={
                "id": "abc", "embeddable": False, "audioUrl": None,
            })
            # The fake server routes by path, so a second preview route would
            # collide; one response is enough to prove the loop skips a
            # candidate without audio and falls through to local resolution.
            with patch("services.discover.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                local = _FakeDownloader(preview={"id": "local", "audioUrl": "http://audio.test/file.mp3"})
                service = self._service(downloader=local)
                resolved = service.resolve_preview("Midnight City", "M83")

            self.assertEqual(resolved["source"], "local")
            self.assertEqual(resolved["audioUrl"], "http://audio.test/file.mp3")
            self.assertEqual(server.requests[0]["route"], "/api/v1/youtube/search")

    def test_local_preview_fallback_without_server(self):
        local = _FakeDownloader(preview={"id": "local", "audioUrl": "http://audio.test/file.mp3"})
        service = self._service(downloader=local)
        with patch("services.discover.get_api_config", return_value=("", "")):
            resolved = service.resolve_preview("Midnight City", "M83")
        self.assertEqual(resolved["source"], "local")

    def test_owned_guard_matches_normalized_title_and_artist(self):
        service = self._service()
        owned = service.check_owned("Paranoid Android (Remastered)", "Radiohead")
        not_owned = service.check_owned("Paranoid Android", "Someone Else")
        self.assertTrue(owned["owned"])
        self.assertEqual(owned["file"], "Paranoid Android.mp3")
        self.assertFalse(not_owned["owned"])

    def test_owned_guard_checks_the_selected_library(self):
        service = self._service()
        songs = service.check_owned("Deep Dive", "Host", "songs")
        podcasts = service.check_owned("Deep Dive", "Host", "podcasts")
        self.assertFalse(songs["owned"])
        self.assertTrue(podcasts["owned"])
        self.assertEqual(podcasts["file"], "episode.mp3")

    def test_owned_query_matches_title_artist_and_prefixes(self):
        service = self._service()
        self.assertTrue(service.check_owned_query("Paranoid Android Radiohead")["owned"])
        self.assertTrue(service.check_owned_query("Paranoid Android Radiohead official audio")["owned"])
        self.assertFalse(service.check_owned_query("Paranoid Android", "podcasts")["owned"])
        self.assertTrue(service.check_owned_query("Deep Dive Host", "podcasts")["owned"])
        self.assertFalse(service.check_owned_query("Different Song")["owned"])

    def test_submit_download_uses_local_manager(self):
        downloads = _FakeDownloads()
        service = self._service(downloads=downloads)
        result = service.submit_download("Midnight City", "M83")
        self.assertTrue(result["queued"])
        self.assertEqual(downloads.calls[0][1], False)
        self.assertEqual(downloads.calls[0][0]["url"], "Midnight City M83 audio")
        self.assertEqual(downloads.calls[0][0]["title"], "Midnight City")

    def test_resolve_video_falls_back_to_local_search(self):
        local = _FakeDownloader(hits=[{"id": "vid1", "title": "Midnight City"}])
        service = self._service(downloader=local)
        with patch("services.discover.get_api_config", return_value=("", "")):
            video = service.resolve_video("Midnight City", "M83")
        self.assertEqual(video["videoId"], "vid1")
        self.assertEqual(video["source"], "local")


if __name__ == "__main__":
    unittest.main()

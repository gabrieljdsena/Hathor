import os
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from services.lyrics import LyricsService
from tests.support import FakeHathorServer


class _FakeApi:
    def __init__(self, db_path):
        self.db_path = db_path
        self.current_filename = "song.mp3"
        self.last_song = {"File": "song.mp3", "IsPodcast": False}
        self.playback = None
        self.db = _FakeDb()


class _FakeDb:
    def __init__(self, chapters=None):
        self.chapters = chapters or []

    def get_podcast_chapters(self, file):
        return list(self.chapters)


class LyricsServiceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.api = _FakeApi(os.path.join(self.tmp.name, "library.db"))
        self.service = LyricsService(self.api)

    def tearDown(self):
        self.tmp.cleanup()

    def test_payload_keeps_raw_lyrics_for_the_romaji_toggle(self):
        raw = "[00:01.00] 日本語の歌詞"
        self.service.romanize_text = lambda *args, **kwargs: self.fail(
            "payload must not romanize unconditionally")
        payload = self.service._lyrics_payload(raw, "歌詞")
        self.assertEqual(payload["synced"], raw)
        self.assertEqual(payload["plain"], "歌詞")

    def test_candidate_search_uses_web_api_first(self):
        with FakeHathorServer() as server:
            server.add("POST", "/api/v1/lyrics/search", json_body=[
                {
                    "id": 7,
                    "trackName": "Song",
                    "artistName": "Artist",
                    "albumName": "Album",
                    "duration": 201,
                    "syncedLyrics": "[00:01.00] hi",
                    "plainLyrics": "hi",
                }
            ])
            with patch("services.lyrics.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                hits = self.service.search_lyrics_candidates(
                    "Song (Remastered 2011)", "Artist", "Album", 201)

            self.assertEqual(hits[0]["trackName"], "Song")
            self.assertEqual(hits[0]["syncedLyrics"], "[00:01.00] hi")
            self.assertEqual(server.requests[0]["route"], "/api/v1/lyrics/search")
            self.assertEqual(server.requests[0]["method"], "POST")

    def test_candidate_search_falls_back_to_lrclib(self):
        self.service._lrclib_search = lambda *args, **kwargs: [
            {"id": 1, "trackName": "Song", "artistName": "Artist",
             "syncedLyrics": "[00:01.00] hi", "plainLyrics": "hi"}
        ]
        with patch("services.lyrics.get_api_config", return_value=("", "")):
            hits = self.service.search_lyrics_candidates("Song", "Artist")
        self.assertEqual(hits[0]["id"], 1)

    def test_normalizes_track_artist_and_filters_candidates(self):
        self.assertEqual(
            self.service._clean_track_artist("Song (Remastered 2011)", "Artist"),
            ("Song", "Artist"),
        )
        hits = self.service._normalize_candidates([
            {"trackName": "Song", "artistName": "Artist", "syncedLyrics": "[00:01.00] hi"},
            {"trackName": "Song", "artistName": "Artist", "plainLyrics": "duplicate"},
            {"trackName": "Empty", "artistName": "Artist"},
            {"trackName": "Instrumental", "artistName": "Artist", "instrumental": True,
             "plainLyrics": "x"},
        ])
        self.assertEqual(len(hits), 1)
        self.assertEqual(hits[0]["trackName"], "Song")

    def test_chapter_key_and_split(self):
        self.assertEqual(self.service.chapter_key("ep.mp3", 2), "ep.mp3::chapter:2")
        self.assertEqual(
            self.service._split_chapter_name("Beggin - Frankie Valli"),
            ("Beggin", "Frankie Valli"),
        )
        self.assertEqual(self.service._split_chapter_name("Intro"), ("Intro", ""))

    def test_save_and_delete_lyrics_for_a_chapter(self):
        self.api.db = _FakeDb([
            {"id": 2, "name": "Main", "startSecs": 60, "endSecs": None},
        ])
        with sqlite3.connect(self.api.db_path) as conn:
            conn.execute(
                "CREATE TABLE Lyrics (id INTEGER PRIMARY KEY AUTOINCREMENT, "
                "song_file TEXT, lyrics TEXT, offset_ms INTEGER DEFAULT 0)"
            )
            conn.execute(
                "CREATE TABLE Sync_Deletions (id INTEGER PRIMARY KEY AUTOINCREMENT, "
                "table_name TEXT, row_key TEXT)"
            )
            conn.commit()
        conn.close()
        self.assertTrue(
            self.service.save_lyrics_for_current("[00:01.00] hi", "hi", 2))
        with sqlite3.connect(self.api.db_path) as conn:
            row = conn.execute(
                "SELECT song_file FROM Lyrics WHERE song_file = ?",
                ("song.mp3::chapter:2",),
            ).fetchone()
        conn.close()
        self.assertIsNotNone(row)
        self.assertTrue(self.service.delete_lyrics_for_current(2))
        with sqlite3.connect(self.api.db_path) as conn:
            self.assertIsNone(conn.execute("SELECT 1 FROM Lyrics").fetchone())
            tombstone = conn.execute(
                "SELECT table_name, row_key FROM Sync_Deletions"
            ).fetchone()
        conn.close()
        self.assertEqual(tombstone, ("lyrics", "song.mp3::chapter:2"))

        self.assertTrue(
            self.service.save_lyrics_for_current("[00:02.00] again", "again", 2))
        with sqlite3.connect(self.api.db_path) as conn:
            self.assertIsNone(
                conn.execute("SELECT 1 FROM Sync_Deletions").fetchone())
        conn.close()

    def test_missing_chapter_target_is_rejected(self):
        self.api.db = _FakeDb([])
        with sqlite3.connect(self.api.db_path) as conn:
            conn.execute(
                "CREATE TABLE Lyrics (id INTEGER PRIMARY KEY AUTOINCREMENT, "
                "song_file TEXT, lyrics TEXT, offset_ms INTEGER DEFAULT 0)"
            )
            conn.execute(
                "CREATE TABLE Sync_Deletions (id INTEGER PRIMARY KEY AUTOINCREMENT, "
                "table_name TEXT, row_key TEXT)"
            )
            conn.commit()
        conn.close()
        self.assertFalse(
            self.service.save_lyrics_for_current("[00:01.00] hi", "hi", 99))
        self.assertFalse(self.service.delete_lyrics_for_current(99))

    def test_romanize_prefers_the_web_api_then_falls_back(self):
        with FakeHathorServer() as server:
            server.add("POST", "/api/v1/lyrics/romanize", json_body={"text": "romaji"})
            with patch("services.lyrics.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                self.assertEqual(
                    self.service.romanize_with_server("日本語", False), "romaji")
            self.assertEqual(server.requests[0]["route"], "/api/v1/lyrics/romanize")

        self.service.romanize_text = lambda text, is_lrc=False: "local"
        with patch("services.lyrics.get_api_config", return_value=("", "")):
            self.assertEqual(
                self.service.romanize_with_server("日本語", False), "local")


if __name__ == "__main__":
    unittest.main()

import os
import sqlite3
import sys
import tempfile
import types
import unittest
from unittest.mock import patch


# metadata.py imports pygame/mutagen at module import. The real app installs
# them; this stdlib-compatible test needs only the tiny surface the manager
# touches before it reaches a real audio file. Install fakes only while the
# module is being loaded, then restore sys.modules so the lazy-import tests
# still see a pygame-free environment.
pygame = types.ModuleType("pygame")
mixer = types.ModuleType("pygame.mixer")
music = types.SimpleNamespace(stop=lambda: None, unload=lambda: None)
mixer.music = music
pygame.mixer = mixer

mutagen = types.ModuleType("mutagen")
mp3 = types.ModuleType("mutagen.mp3")
id3 = types.ModuleType("mutagen.id3")


class _MP3:
    def __init__(self, *args, **kwargs):
        self.tags = None
        self.info = types.SimpleNamespace(length=0)


class _Tag:
    def __init__(self, *args, **kwargs):
        pass


class _ID3(dict):
    pass


mp3.MP3 = _MP3
id3.ID3 = _ID3
for name in ("TIT2", "TPE1", "TALB", "TDRC", "APIC"):
    setattr(id3, name, _Tag)
mutagen.mp3 = mp3
mutagen.id3 = id3

_fakes = {
    "pygame": pygame,
    "pygame.mixer": mixer,
    "mutagen": mutagen,
    "mutagen.mp3": mp3,
    "mutagen.id3": id3,
}
_saved = {}
for _name, _module in _fakes.items():
    _saved[_name] = sys.modules.get(_name)
    if _name not in sys.modules:
        sys.modules[_name] = _module

from services.metadata import MetadataManager  # noqa: E402

for _name, _original in _saved.items():
    if _original is None:
        sys.modules.pop(_name, None)
    else:
        sys.modules[_name] = _original


class _FakePlayback:
    def __init__(self):
        self.track_listeners = []
        self.exhausted_listeners = []

    def add_track_changed_listener(self, callback):
        self.track_listeners.append(callback)

    def add_queue_exhausted_listener(self, callback):
        self.exhausted_listeners.append(callback)


class _FakeDb:
    def __init__(self):
        self.song_list = []

    def get_all_songs(self):
        return list(self.song_list)


class _FakeApi:
    def __init__(self):
        self.playback = _FakePlayback()
        self.last_song = {"File": "current.mp3"}
        self.current_filename = "current.mp3"
        self.db_path = "unused.db"
        self.db = _FakeDb()
        self.playing = True
        self.first_play = False
        self._window = None


class PendingMetadataTests(unittest.TestCase):
    def setUp(self):
        self.api = _FakeApi()
        self.manager = MetadataManager(self.api)
        self.manager._write_song_metadata = lambda data: True

    def test_current_file_is_held_until_the_track_changes(self):
        result = self.manager.update_song_metadata(
            {"File": "current.mp3", "Title": "Updated"}
        )
        self.assertTrue(result["pending"])
        self.assertEqual(result["file"], "current.mp3")
        self.assertIn("current.mp3", self.manager._pending_edits)
        self.assertEqual(self.manager.apply_pending_edits(exclude_file="current.mp3"), [])
        self.assertIn("current.mp3", self.manager._pending_edits)
        self.assertEqual(self.manager.apply_pending_edits(), ["current.mp3"])
        self.assertEqual(self.manager._pending_edits, {})

    def test_non_current_file_writes_immediately(self):
        calls = []
        self.manager._write_song_metadata = lambda data: calls.append(data) or True
        result = self.manager.update_song_metadata(
            {"File": "other.mp3", "Title": "Updated"}
        )
        self.assertTrue(result)
        self.assertEqual(calls[0]["File"], "other.mp3")

    def test_cancel_and_listener_registration(self):
        self.manager.update_song_metadata({"File": "current.mp3", "Title": "Updated"})
        self.assertEqual(len(self.api.playback.track_listeners), 1)
        self.assertEqual(len(self.api.playback.exhausted_listeners), 1)
        self.assertTrue(self.manager.cancel_pending_edit("current.mp3"))
        self.assertEqual(self.manager._pending_edits, {})

    def test_queue_end_flushes_without_reloading_the_track(self):
        calls = []
        self.manager._write_song_metadata = lambda data: calls.append(data) or True
        self.manager.update_song_metadata({"File": "current.mp3", "Title": "Updated"})
        self.api.playing = False
        applied = self.manager._on_queue_exhausted()
        self.assertEqual(applied, ["current.mp3"])
        self.assertIsNone(self.api.current_filename)
        self.assertEqual(self.api.last_song, {})
        self.assertEqual(calls[0]["File"], "current.mp3")

    def test_moving_the_current_track_stops_it_and_moves_the_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            songs = os.path.join(tmp, "songs")
            podcasts = os.path.join(tmp, "podcasts")
            os.makedirs(songs)
            os.makedirs(podcasts)
            with open(os.path.join(songs, "current.mp3"), "wb") as handle:
                handle.write(b"audio")
            db_path = os.path.join(tmp, "library.db")
            conn = sqlite3.connect(db_path)
            try:
                conn.executescript(
                    "CREATE TABLE Songs (file TEXT PRIMARY KEY, downloaded_link TEXT, "
                    "title TEXT, date_download DATETIME, artist TEXT, loudness_db REAL);"
                    "CREATE TABLE Podcasts (file TEXT PRIMARY KEY, downloaded_link TEXT, "
                    "title TEXT, date_download DATETIME, artist TEXT);"
                    "CREATE TABLE Song_Playlist (song_file TEXT, playlist_id INTEGER);"
                    "CREATE TABLE Lyrics (song_file TEXT);"
                    "CREATE TABLE Music_History (song_file TEXT);"
                    "CREATE TABLE Settings (id INTEGER PRIMARY KEY, current_song TEXT);"
                    "CREATE TABLE Sync_Deletions (id INTEGER PRIMARY KEY AUTOINCREMENT, "
                    "table_name TEXT, row_key TEXT);"
                )
                conn.execute(
                    "INSERT INTO Songs (file, title, artist) VALUES (?, ?, ?)",
                    ("current.mp3", "Current", "Artist"),
                )
                conn.execute("INSERT INTO Settings (id, current_song) VALUES (1, 'current.mp3')")
                conn.commit()
            finally:
                conn.close()

            self.api.db_path = db_path
            with patch("services.metadata.settings.path", songs), \
                    patch("services.metadata.settings.podcasts_path", podcasts):
                result = self.manager.move_song_to_podcast({"File": "current.mp3"})

            self.assertTrue(result["ok"])
            self.assertFalse(os.path.exists(os.path.join(songs, "current.mp3")))
            self.assertTrue(os.path.exists(os.path.join(podcasts, "current.mp3")))
            self.assertIsNone(self.api.current_filename)
            self.assertEqual(self.api.last_song, {})
            with sqlite3.connect(db_path) as conn:
                row = conn.execute(
                    "SELECT title, artist FROM Podcasts WHERE file = 'current.mp3'"
                ).fetchone()
            conn.close()
            self.assertEqual(row, ("Current", "Artist"))


if __name__ == "__main__":
    unittest.main()

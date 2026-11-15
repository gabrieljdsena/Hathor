import contextlib
import sqlite3
import tempfile
import unittest
from pathlib import Path

from services.library_moves import LibraryMoveError, move_between_libraries


class LibraryMoveTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.songs = self.root / "songs"
        self.podcasts = self.root / "podcasts"
        self.songs.mkdir()
        self.podcasts.mkdir()
        self.db_path = self.root / "library.db"
        schema = (Path(__file__).resolve().parents[1] / "database.sql").read_text(
            encoding="utf-8"
        )
        with self._db() as conn:
            conn.executescript(schema)

    def tearDown(self):
        self.tmp.cleanup()

    def _write(self, directory, name, data=b"audio"):
        path = directory / name
        path.write_bytes(data)
        return path

    @contextlib.contextmanager
    def _db(self):
        conn = sqlite3.connect(self.db_path)
        try:
            yield conn
            conn.commit()
        finally:
            conn.close()

    def _seed_song(self, name="track.mp3"):
        path = self._write(self.songs, name)
        with self._db() as conn:
            conn.execute(
                "INSERT INTO Songs (file, downloaded_link, title, artist) VALUES (?, ?, ?, ?)",
                (name, "https://example.test/source", "Old Title", "Old Artist"),
            )
            conn.execute("INSERT INTO Playlists (id, title) VALUES (1, 'Mix')")
            conn.execute(
                "INSERT INTO Song_Playlist (song_file, playlist_id) VALUES (?, 1)",
                (name,),
            )
            conn.execute(
                "INSERT INTO Lyrics (song_file, lyrics) VALUES (?, ?)",
                (name, "[]"),
            )
            conn.execute("INSERT INTO Music_History (song_file) VALUES (?)", (name,))
        return path

    def test_song_to_podcast_moves_file_and_cascades(self):
        self._seed_song()
        result = move_between_libraries(
            self.db_path,
            self.songs,
            self.podcasts,
            "track.mp3",
            to_podcast=True,
            read_metadata=lambda path, name: {"Title": "New Title", "Artist": "New Artist"},
        )

        self.assertTrue(result["ok"])
        self.assertEqual(result["newFile"], "track.mp3")
        self.assertFalse((self.songs / "track.mp3").exists())
        self.assertEqual((self.podcasts / "track.mp3").read_bytes(), b"audio")

        with self._db() as conn:
            self.assertIsNone(
                conn.execute("SELECT 1 FROM Songs WHERE file = 'track.mp3'").fetchone()
            )
            row = conn.execute(
                "SELECT title, artist, downloaded_link FROM Podcasts WHERE file = 'track.mp3'"
            ).fetchone()
            self.assertEqual(row, ("New Title", "New Artist", "https://example.test/source"))
            self.assertIsNone(conn.execute("SELECT 1 FROM Song_Playlist").fetchone())
            self.assertIsNone(conn.execute("SELECT 1 FROM Lyrics").fetchone())
            self.assertIsNone(conn.execute("SELECT 1 FROM Music_History").fetchone())
            tombstones = {
                table for (table,) in conn.execute(
                    "SELECT table_name FROM Sync_Deletions"
                )
            }
            self.assertEqual(tombstones, {"songs", "lyrics", "music_history"})

    def test_podcast_to_song_drops_tags_and_chapters(self):
        self._write(self.podcasts, "episode.mp3", b"episode")
        with self._db() as conn:
            conn.execute(
                "INSERT INTO Podcasts (file, title, artist) VALUES (?, ?, ?)",
                ("episode.mp3", "Episode", "Host"),
            )
            conn.execute("INSERT INTO Podcast_Tags (id, name) VALUES (1, 'News')")
            conn.execute(
                "INSERT INTO Podcast_Tag_Links (podcast_file, tag_id) VALUES (?, 1)",
                ("episode.mp3",),
            )
            conn.execute(
                "INSERT INTO Podcast_Chapters (podcast_file, name, start_secs) VALUES (?, ?, ?)",
                ("episode.mp3", "Intro", 0),
            )
            conn.execute(
                "INSERT INTO Lyrics (song_file, lyrics) VALUES (?, ?)",
                ("episode.mp3::chapter:1", "[]"),
            )

        result = move_between_libraries(
            self.db_path,
            self.podcasts,
            self.songs,
            "episode.mp3",
            to_podcast=False,
            read_metadata=lambda path, name: {"Title": "Episode", "Artist": "Host"},
        )

        self.assertTrue(result["ok"])
        self.assertTrue((self.songs / "episode.mp3").exists())
        with self._db() as conn:
            self.assertIsNone(
                conn.execute("SELECT 1 FROM Podcasts WHERE file = 'episode.mp3'").fetchone()
            )
            row = conn.execute(
                "SELECT title, artist FROM Songs WHERE file = 'episode.mp3'"
            ).fetchone()
            self.assertEqual(row, ("Episode", "Host"))
            self.assertIsNone(conn.execute("SELECT 1 FROM Podcast_Tag_Links").fetchone())
            self.assertIsNone(conn.execute("SELECT 1 FROM Podcast_Chapters").fetchone())
            self.assertIsNone(
                conn.execute(
                    "SELECT 1 FROM Lyrics WHERE song_file LIKE 'episode.mp3::chapter:%'"
                ).fetchone()
            )
            tombstones = {
                (table, key) for (table, key) in conn.execute(
                    "SELECT table_name, row_key FROM Sync_Deletions"
                )
            }
            self.assertEqual(tombstones, {
                ("podcasts", "episode.mp3"),
                ("podcast_chapters", "episode.mp3"),
                ("lyrics", "episode.mp3::chapter:1"),
            })

    def test_collision_gets_a_moved_suffix_and_reverse_still_works(self):
        self._seed_song("same.mp3")
        self._write(self.podcasts, "same.mp3", b"existing")

        result = move_between_libraries(
            self.db_path, self.songs, self.podcasts, "same.mp3", to_podcast=True
        )

        self.assertEqual(result["newFile"], "same (moved 2).mp3")
        self.assertEqual((self.podcasts / "same.mp3").read_bytes(), b"existing")
        self.assertEqual((self.podcasts / "same (moved 2).mp3").read_bytes(), b"audio")

        reverse = move_between_libraries(
            self.db_path,
            self.podcasts,
            self.songs,
            result["newFile"],
            to_podcast=False,
        )
        self.assertEqual(reverse["newFile"], "same (moved 2).mp3")
        self.assertTrue((self.songs / "same (moved 2).mp3").exists())

    def test_same_folder_is_rejected(self):
        self._write(self.songs, "x.mp3")
        with self.assertRaises(LibraryMoveError):
            move_between_libraries(
                self.db_path, self.songs, self.songs, "x.mp3", to_podcast=True
            )

    def test_missing_source_is_rejected(self):
        with self.assertRaises(LibraryMoveError):
            move_between_libraries(
                self.db_path, self.songs, self.podcasts, "missing.mp3", to_podcast=True
            )


if __name__ == "__main__":
    unittest.main()

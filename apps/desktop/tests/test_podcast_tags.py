import gc
import sqlite3
import tempfile
import unittest
import warnings
from contextlib import closing
from pathlib import Path

from services.api_sync import (
    ApiSyncClient,
    _deletion_tables,
    _filter_payload,
)
from services.database import DatabaseManager


def _make_db(path):
    with closing(sqlite3.connect(path)) as conn:
        conn.executescript(
            """
            CREATE TABLE Podcast_Tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name varchar(255) NOT NULL UNIQUE
            );
            CREATE TABLE Podcast_Tag_Links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                podcast_file varchar(255) NOT NULL,
                tag_id bigint NOT NULL
            );
            CREATE UNIQUE INDEX idx_podcast_tag_links_pair
                ON Podcast_Tag_Links(podcast_file, tag_id);
            CREATE TABLE Sync_Deletions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                table_name varchar(255) NOT NULL,
                row_key varchar(255) NOT NULL,
                deleted_at datetime DEFAULT CURRENT_TIMESTAMP
            );
            """
        )


def _seed_tag(db_path, tag_id=7, file="ep.mp3"):
    with closing(sqlite3.connect(db_path)) as conn:
        conn.execute("INSERT INTO Podcast_Tags (id, name) VALUES (?, ?)", (tag_id, "Tech"))
        conn.execute(
            "INSERT INTO Podcast_Tag_Links (podcast_file, tag_id) VALUES (?, ?)",
            (file, tag_id),
        )


class PodcastTagSyncTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = str(Path(self.tmp.name) / "library.db")
        _make_db(self.db_path)
        self.db = DatabaseManager(None, self.db_path)

    def tearDown(self):
        # DatabaseManager's `with sqlite3.connect(...)` commits but does not
        # close; collect before Windows tries to remove the temp directory.
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", ResourceWarning)
            gc.collect()
        self.tmp.cleanup()

    def test_unassign_records_link_tombstone_and_assign_clears_it(self):
        _seed_tag(self.db_path)

        self.assertTrue(self.db.unassign_podcast_tag("ep.mp3", 7))
        with closing(sqlite3.connect(self.db_path)) as conn:
            self.assertEqual(
                conn.execute("SELECT COUNT(*) FROM Podcast_Tag_Links").fetchone()[0], 0
            )
            self.assertEqual(
                conn.execute(
                    "SELECT table_name, row_key FROM Sync_Deletions"
                ).fetchall(),
                [("podcast_tag_links", "7:ep.mp3")],
            )

        self.assertTrue(self.db.assign_podcast_tag("ep.mp3", 7))
        with closing(sqlite3.connect(self.db_path)) as conn:
            self.assertEqual(
                conn.execute("SELECT COUNT(*) FROM Podcast_Tag_Links").fetchone()[0], 1
            )
            self.assertEqual(
                conn.execute(
                    "SELECT COUNT(*) FROM Sync_Deletions "
                    "WHERE table_name = 'podcast_tag_links'"
                ).fetchone()[0],
                0,
            )

    def test_pull_applies_link_deletion_with_colon_in_filename(self):
        _seed_tag(self.db_path, file="ep:1.mp3")

        with closing(sqlite3.connect(self.db_path)) as conn:
            ApiSyncClient._apply_deletions(
                conn,
                [{"table_name": "podcast_tag_links", "row_key": "7:ep:1.mp3"}],
                "podcasts",
            )
            self.assertEqual(
                conn.execute("SELECT COUNT(*) FROM Podcast_Tag_Links").fetchone()[0], 0
            )

    def test_pull_assignment_clears_local_unassign_tombstone(self):
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute(
                "INSERT INTO Sync_Deletions (table_name, row_key) VALUES (?, ?)",
                ("podcast_tag_links", "7:ep.mp3"),
            )
            ApiSyncClient._merge_rest(
                conn,
                {
                    "podcast_tag_links": [
                        {"id": 9, "podcast_file": "ep.mp3", "tag_id": 7}
                    ]
                },
            )
            self.assertEqual(
                conn.execute(
                    "SELECT podcast_file, tag_id FROM Podcast_Tag_Links"
                ).fetchall(),
                [("ep.mp3", 7)],
            )
            self.assertEqual(
                conn.execute(
                    "SELECT COUNT(*) FROM Sync_Deletions "
                    "WHERE table_name = 'podcast_tag_links'"
                ).fetchone()[0],
                0,
            )

    def test_podcast_push_keeps_only_podcast_link_tombstones(self):
        self.assertIn("podcast_tag_links", _deletion_tables("podcasts"))
        payload = {
            "PodcastTags": [],
            "PodcastTagLinks": [],
            "Deletions": [
                {"TableName": "podcast_tag_links", "RowKey": "7:ep.mp3"},
                {"TableName": "songs", "RowKey": "s.mp3"},
            ],
        }
        filtered = _filter_payload(payload, "podcasts")
        self.assertEqual(
            filtered["Deletions"],
            [{"TableName": "podcast_tag_links", "RowKey": "7:ep.mp3"}],
        )


if __name__ == "__main__":
    unittest.main()

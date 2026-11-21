import json
import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path
from unittest.mock import patch

from services.api_sync import ApiSyncClient, ApiSyncError
from services.history_sync import HistorySync
from tests.support import FakeHathorServer


def _make_db(path):
    conn = sqlite3.connect(path)
    try:
        conn.execute(
            "CREATE TABLE Music_History ("
            "id INTEGER PRIMARY KEY AUTOINCREMENT, "
            "song_file varchar(255) not null, "
            "date_played datetime default CURRENT_TIMESTAMP)"
        )
        conn.execute(
            "CREATE TABLE History_Sync_Queue (history_id INTEGER PRIMARY KEY)"
        )
        conn.commit()
    finally:
        conn.close()


def _add_play(path, song_file, stamp):
    conn = sqlite3.connect(path)
    try:
        cur = conn.execute(
            "INSERT INTO Music_History (song_file, date_played) VALUES (?, ?)",
            (song_file, stamp),
        )
        conn.execute(
            "INSERT OR IGNORE INTO History_Sync_Queue (history_id) VALUES (?)",
            (cur.lastrowid,),
        )
        conn.commit()
        return cur.lastrowid
    finally:
        conn.close()


class HistorySyncTests(unittest.TestCase):
    def test_pending_history_pushes_only_queued_rows_and_clears_them(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            db_path = str(Path(tmp) / "music.db")
            _make_db(db_path)
            first = _add_play(db_path, "a.mp3", "2026-11-15 12:00:00")
            _add_play(db_path, "b.mp3", "2026-11-15 12:00:01")

            server.add("POST", "/api/v1/sync/import", json_body={"summary": {}})
            with patch(
                "services.api_sync.get_api_config",
                return_value=(server.base_url, "hth_secret"),
            ):
                pushed = ApiSyncClient(db_path).push_pending_history()

            self.assertEqual(pushed, 2)
            body = json.loads(server.requests[-1]["body"])
            self.assertEqual(
                [row["SongFile"] for row in body["MusicHistory"]],
                ["a.mp3", "b.mp3"],
            )
            self.assertEqual(body["MusicHistory"][0]["Id"], first)
            self.assertEqual(
                body["MusicHistory"][0]["DatePlayedUtc"],
                "2026-11-15T12:00:00Z",
            )
            with closing(sqlite3.connect(db_path)) as conn:
                self.assertIsNone(
                    conn.execute("SELECT 1 FROM History_Sync_Queue").fetchone()
                )

    def test_failed_push_keeps_the_outbox(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            db_path = str(Path(tmp) / "music.db")
            _make_db(db_path)
            _add_play(db_path, "a.mp3", "2026-11-15 12:00:00")

            server.add("POST", "/api/v1/sync/import", status=500,
                       json_body={"message": "nope"})
            with patch(
                "services.api_sync.get_api_config",
                return_value=(server.base_url, "hth_secret"),
            ):
                with self.assertRaises(ApiSyncError):
                    ApiSyncClient(db_path).push_pending_history()

            with closing(sqlite3.connect(db_path)) as conn:
                count = conn.execute(
                    "SELECT COUNT(*) FROM History_Sync_Queue"
                ).fetchone()[0]
            self.assertEqual(count, 1)

    def test_worker_flush_uses_the_configured_batch(self):
        with tempfile.TemporaryDirectory() as tmp:
            calls = []

            class FakeClient:
                def push_pending_history(self, limit):
                    calls.append(limit)
                    return 2

            sync = HistorySync(
                str(Path(tmp) / "music.db"),
                start=False,
                client_factory=lambda: FakeClient(),
                batch_limit=7,
            )
            self.assertEqual(sync._flush_once(), 2)
            self.assertEqual(calls, [7])

    def test_merge_dedupes_natural_key_and_rekeys_id_collisions(self):
        with tempfile.TemporaryDirectory() as tmp:
            db_path = str(Path(tmp) / "music.db")
            _make_db(db_path)
            _add_play(db_path, "a.mp3", "2026-11-15 12:00:00")

            snapshot = {
                "music_history": [
                    {"id": 1, "song_file": "a.mp3",
                     "date_played": "2026-11-15T12:00:00Z"},
                    {"id": 1, "song_file": "b.mp3",
                     "date_played": "2026-11-15T12:00:01Z"},
                    {"id": 2, "song_file": "a.mp3",
                     "date_played": "2026-11-15T13:00:00Z"},
                ]
            }
            with closing(sqlite3.connect(db_path)) as conn:
                ApiSyncClient._merge_rest(conn, snapshot)
                rows = conn.execute(
                    "SELECT id, song_file, date_played FROM Music_History "
                    "ORDER BY id"
                ).fetchall()

            self.assertEqual(len(rows), 3)
            self.assertEqual(rows[0][1], "a.mp3")
            self.assertEqual(rows[0][2], "2026-11-15 12:00:00")
            self.assertEqual(
                {(row[1], row[2]) for row in rows},
                {
                    ("a.mp3", "2026-11-15 12:00:00"),
                    ("b.mp3", "2026-11-15 12:00:01"),
                    ("a.mp3", "2026-11-15 13:00:00"),
                },
            )


if __name__ == "__main__":
    unittest.main()

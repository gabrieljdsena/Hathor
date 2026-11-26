import sqlite3
import tempfile
import unittest
from pathlib import Path


class DatabaseSchemaTests(unittest.TestCase):
    def test_database_sql_has_runtime_api_columns(self):
        sql_path = Path(__file__).resolve().parents[1] / "database.sql"
        self.assertTrue(sql_path.exists())

        with tempfile.TemporaryDirectory() as tmp:
            db_path = Path(tmp) / "schema.db"
            conn = sqlite3.connect(db_path)
            try:
                conn.executescript(sql_path.read_text(encoding="utf-8"))
                conn.commit()
                columns = {
                    row[1] for row in conn.execute("PRAGMA table_info(Settings)")
                }
                tables = {
                    row[0] for row in conn.execute(
                        "SELECT name FROM sqlite_master WHERE type = 'table'"
                    )
                }
            finally:
                conn.close()

        self.assertIn("hathor_api_url", columns)
        self.assertIn("hathor_api_key", columns)
        self.assertIn("hathor_auth_mode", columns)
        self.assertIn("hathor_auth_username", columns)
        self.assertIn("device_id", columns)
        self.assertIn("device_label", columns)
        self.assertIn("History_Sync_Queue", tables)


if __name__ == "__main__":
    unittest.main()

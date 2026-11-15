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
            finally:
                conn.close()

        self.assertIn("hathor_api_url", columns)
        self.assertIn("hathor_api_key", columns)
        self.assertIn("device_id", columns)
        self.assertIn("device_label", columns)


if __name__ == "__main__":
    unittest.main()

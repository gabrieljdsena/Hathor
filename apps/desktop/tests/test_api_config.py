import os
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from services import api_config
from tests.support import FakeHathorServer


def _make_db(path):
    conn = sqlite3.connect(path)
    try:
        conn.execute(
            "CREATE TABLE Settings (id INTEGER PRIMARY KEY, hathor_api_url TEXT, hathor_api_key TEXT)"
        )
        conn.execute("INSERT INTO Settings (id) VALUES (1)")
        conn.commit()
    finally:
        conn.close()


class ApiConfigTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db_path = os.path.join(self.tmp.name, "music_player.db")
        _make_db(self.db_path)

    def tearDown(self):
        self.tmp.cleanup()

    def test_save_get_mask_and_clear(self):
        saved = api_config.save_api_config(
            "192.168.1.10:5051/", "hth_abcdefgh1234", self.db_path
        )
        self.assertTrue(saved["configured"])
        self.assertEqual(saved["url"], "http://192.168.1.10:5051")
        self.assertEqual(saved["keyHint"], "hth_abcd...1234")
        self.assertEqual(saved["source"], "settings")
        self.assertNotIn("hth_abcdefgh1234", str(saved))

        info = api_config.get_api_config_info(self.db_path)
        self.assertEqual(info.url, "http://192.168.1.10:5051")
        self.assertEqual(info.key, "hth_abcdefgh1234")
        self.assertEqual(api_config.get_api_config(self.db_path),
                         ("http://192.168.1.10:5051", "hth_abcdefgh1234"))

        cleared = api_config.clear_api_config(self.db_path)
        self.assertFalse(cleared["configured"])
        self.assertEqual(cleared["source"], "none")

    def test_blank_key_keeps_saved_key(self):
        api_config.save_api_config("http://host:5051", "hth_abcdefgh1234", self.db_path)
        saved = api_config.save_api_config("http://host:5051", "", self.db_path)
        self.assertTrue(saved["configured"])
        self.assertEqual(api_config.get_api_config_info(self.db_path).key,
                         "hth_abcdefgh1234")

    def test_env_is_the_fallback(self):
        with patch.dict(os.environ, {
            "HATHOR_API_URL": "http://env-host:5051/",
            "HATHOR_API_KEY": "hth_envkey1234",
        }, clear=False):
            info = api_config.get_api_config_info(self.db_path)
            self.assertEqual(info.source, "env")
            self.assertEqual(info.url, "http://env-host:5051")
            self.assertEqual(info.key, "hth_envkey1234")

        api_config.save_api_config("http://runtime:5051", "hth_runtime1234", self.db_path)
        with patch.dict(os.environ, {
            "HATHOR_API_URL": "http://env-host:5051",
            "HATHOR_API_KEY": "hth_envkey1234",
        }, clear=False):
            info = api_config.get_api_config_info(self.db_path)
            self.assertEqual(info.source, "settings")
            self.assertEqual(info.url, "http://runtime:5051")

    def test_web_api_env_alias_and_url_aliases(self):
        with patch.dict(os.environ, {
            "WEB_API": "hth_webapikey1234",
            "WEB_API_URL": "http://web-env:5051/",
        }, clear=True):
            info = api_config.get_api_config_info(self.db_path)
            self.assertEqual(info.source, "env")
            self.assertEqual(info.key, "hth_webapikey1234")
            self.assertEqual(info.url, "http://web-env:5051")

        with patch.dict(os.environ, {
            "HATHOR_API_KEY": "hth_canonical1234",
            "WEB_API": "http://web-env:5051",
        }, clear=True):
            info = api_config.get_api_config_info(self.db_path)
            self.assertEqual(info.key, "hth_canonical1234")
            self.assertEqual(info.url, "http://web-env:5051")

    def test_validation(self):
        with self.assertRaises(ValueError):
            api_config.normalize_url("ftp://host")
        with self.assertRaises(ValueError):
            api_config.save_api_config("", "hth_abcdefgh1234", self.db_path)
        with self.assertRaises(ValueError):
            api_config.save_api_config("http://host:5051", "not-a-key", self.db_path)

    def test_normalize_url_strips_api_suffix_and_slash(self):
        self.assertEqual(api_config.normalize_url("host:5051/api/v1/"),
                         "http://host:5051")
        self.assertEqual(api_config.normalize_url("http://host:5051/"),
                         "http://host:5051")
        self.assertEqual(api_config.normalize_url(""), "")

    def test_test_connection_checks_read_scopes(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/health/live",
                       json_body={"status": "healthy", "version": "9.9.9"})
            server.add("GET", "/api/v1/auth/me",
                       json_body={"username": "apikey:desktop"})
            server.add("GET", "/api/v1/songs/count", json_body=12)
            server.add("GET", "/api/v1/player/state", json_body={})

            result = api_config.test_connection(
                server.base_url, "hth_abcdefgh1234", self.db_path
            )

            self.assertTrue(result["ok"])
            self.assertEqual(result["version"], "9.9.9")
            self.assertEqual(result["missing"], [])
            self.assertEqual(result["scopes"]["library:read"], "ok")
            self.assertEqual(result["scopes"]["player:read"], "ok")
            self.assertIn("apikey:desktop", result["message"])

    def test_test_connection_reports_missing_scope(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/health/live", json_body={"status": "healthy"})
            server.add("GET", "/api/v1/auth/me", json_body={"username": "apikey:desktop"})
            server.add("GET", "/api/v1/songs/count", status=403,
                       json_body={"message": "missing scope"})
            server.add("GET", "/api/v1/player/state", json_body={})

            result = api_config.test_connection(
                server.base_url, "hth_abcdefgh1234", self.db_path
            )

            self.assertTrue(result["ok"])
            self.assertEqual(result["missing"], ["library:read"])
            self.assertIn("library:read", result["message"])

    def test_test_connection_rejects_bad_key(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/health/live", json_body={"status": "healthy"})
            server.add("GET", "/api/v1/auth/me", status=401,
                       json_body={"message": "Invalid API key."})

            result = api_config.test_connection(
                server.base_url, "hth_badkey1234", self.db_path
            )

            self.assertFalse(result["ok"])
            self.assertIn("rejected the api key", result["message"].lower())

    def test_ensure_settings_columns_upgrades_old_schema(self):
        old_db = os.path.join(self.tmp.name, "old.db")
        conn = sqlite3.connect(old_db)
        try:
            conn.execute("CREATE TABLE Settings (id INTEGER PRIMARY KEY)")
            api_config.ensure_settings_columns(conn)
            conn.commit()
            columns = {row[1] for row in conn.execute("PRAGMA table_info(Settings)")}
        finally:
            conn.close()
        self.assertIn("hathor_api_url", columns)
        self.assertIn("hathor_api_key", columns)


if __name__ == "__main__":
    unittest.main()

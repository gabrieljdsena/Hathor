import os
import sqlite3
import tempfile
import time
import unittest

from services import api_config
from services.auth_session import AuthSession, TokenStore
from tests.support import FakeHathorServer


class MemoryStore(TokenStore):
    def __init__(self):
        self.tokens = {}

    @property
    def available(self):
        return True

    def load(self, url, username):
        return self.tokens.get((url, username), "")

    def save(self, url, username, refresh_token):
        self.tokens[(url, username)] = refresh_token
        return True

    def delete(self, url, username):
        self.tokens.pop((url, username), None)


def _make_db(path):
    conn = sqlite3.connect(path)
    try:
        conn.execute("CREATE TABLE Settings (id INTEGER PRIMARY KEY)")
        conn.execute("INSERT INTO Settings (id) VALUES (1)")
        api_config.ensure_settings_columns(conn)
        conn.commit()
    finally:
        conn.close()


class AuthSessionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.db = os.path.join(self.tmp.name, "music_player.db")
        _make_db(self.db)
        self.store = MemoryStore()

    def tearDown(self):
        self.tmp.cleanup()

    def session(self):
        return AuthSession(self.db, token_store=self.store)

    def _login_server(self, server):
        server.add("POST", "/api/v1/auth/login", json_body={
            "accessToken": "access-1",
            "refreshToken": "refresh-1",
            "username": "gabriel",
        })
        server.add("POST", "/api/v1/auth/refresh", json_body={
            "accessToken": "access-2",
            "refreshToken": "refresh-2",
            "username": "gabriel",
        })
        server.add("POST", "/api/v1/auth/logout", json_body={"message": "ok"})
        return server

    def test_login_persists_only_refresh_token(self):
        with FakeHathorServer() as server:
            self._login_server(server)
            auth = self.session()
            result = auth.login(server.base_url, "gabriel", "secret", remember_me=True)

        self.assertTrue(result["ok"])
        self.assertTrue(auth.configured())
        self.assertEqual(auth.view()["username"], "gabriel")
        self.assertEqual(self.store.tokens[(server.base_url, "gabriel")],
                         "refresh-1")
        self.assertNotIn("secret", str(result))
        self.assertNotIn("refresh-1", str(result))

    def test_login_rejects_bad_password(self):
        with FakeHathorServer() as server:
            server.add("POST", "/api/v1/auth/login", status=401,
                       json_body={"message": "Invalid credentials"})
            auth = self.session()
            result = auth.login(server.base_url, "gabriel", "wrong")
        self.assertFalse(result["ok"])
        self.assertIn("Invalid username or password", result["message"])
        self.assertFalse(auth.configured())

    def test_restart_reloads_refresh_token_and_rotates(self):
        with FakeHathorServer() as server:
            self._login_server(server)
            first = self.session()
            first.login(server.base_url, "gabriel", "secret")
            second = self.session()
            second._access_expires = time.time() - 1
            token = second.access_token()

        self.assertEqual(token, "access-2")
        self.assertEqual(self.store.tokens[(server.base_url, "gabriel")],
                         "refresh-2")

    def test_api_client_retries_once_on_401(self):
        with FakeHathorServer() as server:
            self._login_server(server)
            server.add("GET", "/api/v1/songs/count", status=401,
                       json_body={"message": "expired"})
            auth = self.session()
            auth.login(server.base_url, "gabriel", "secret")
            client = auth.api_client(timeout=5)
            with self.assertRaises(Exception):
                client.get("/api/v1/songs/count")

            routes = [r["route"] for r in server.requests]
        self.assertEqual(routes.count("/api/v1/songs/count"), 2)
        self.assertEqual(routes.count("/api/v1/auth/refresh"), 1)

    def test_logout_clears_refresh_token_but_keeps_username(self):
        with FakeHathorServer() as server:
            self._login_server(server)
            auth = self.session()
            auth.login(server.base_url, "gabriel", "secret")
            result = auth.logout()

        self.assertTrue(result["ok"])
        self.assertFalse(auth.configured())
        self.assertEqual(auth.view()["username"], "gabriel")
        self.assertEqual(self.store.tokens, {})

    def test_api_key_mode_remains_supported(self):
        auth = self.session()
        result = auth.save_api_key("http://host:5051", "hth_abcdefgh1234")
        self.assertTrue(result["ok"])
        self.assertTrue(auth.configured())
        self.assertEqual(auth.view()["mode"], "key")
        self.assertEqual(auth.access_token(), "hth_abcdefgh1234")


if __name__ == "__main__":
    unittest.main()

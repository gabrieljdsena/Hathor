"""Desktop login sessions and token lifecycle.

Access JWTs live in memory only. The rotating refresh token is stored in the
OS credential store (Windows Credential Manager / Secret Service on Linux).
If no secure store is available, the session still works for the current run
but the user has to sign in again after restarting Hathor.
"""

from __future__ import annotations

import base64
import json
import os
import threading
import time

from . import api_config, platform_support
from .hathor_api import HathorApi, HathorApiError

_KEYRING_SERVICE = "Hathor"
_ACCESS_SKEW_SECONDS = 60
_LOGIN_TIMEOUT = 20
_REFRESH_TIMEOUT = 20
_USER_AGENT = "hathor-desktop/1"


class TokenStore:
    """Small interface so tests never touch the OS keyring."""

    @property
    def available(self) -> bool:
        return False

    def load(self, url: str, username: str) -> str:
        return ""

    def save(self, url: str, username: str, refresh_token: str) -> bool:
        return False

    def delete(self, url: str, username: str) -> None:
        pass


class KeyringTokenStore(TokenStore):
    def __init__(self):
        self._checked = False
        self._available = False
        self._keyring = None

    def _check(self):
        if self._checked:
            return
        self._checked = True
        try:
            import keyring
            backend = keyring.get_keyring()
            name = backend.__class__.__name__.lower()
            if "fail" in name or "null" in name:
                return
            self._keyring = keyring
            self._available = True
        except Exception:
            self._available = False

    @property
    def available(self) -> bool:
        self._check()
        return self._available

    @staticmethod
    def _account(url: str, username: str) -> str:
        return f"{url}|{username}"

    def load(self, url: str, username: str) -> str:
        if not self.available:
            return ""
        try:
            return self._keyring.get_password(
                _KEYRING_SERVICE, self._account(url, username)
            ) or ""
        except Exception:
            return ""

    def save(self, url: str, username: str, refresh_token: str) -> bool:
        if not self.available:
            return False
        try:
            self._keyring.set_password(
                _KEYRING_SERVICE, self._account(url, username), refresh_token
            )
            return True
        except Exception:
            return False

    def delete(self, url: str, username: str) -> None:
        if not self.available:
            return
        try:
            self._keyring.delete_password(
                _KEYRING_SERVICE, self._account(url, username)
            )
        except Exception:
            pass


class AuthSession:
    def __init__(self, db_path=None, token_store=None):
        self.db_path = db_path or api_config._default_db_path()
        self._store = token_store or KeyringTokenStore()
        self._lock = threading.RLock()
        self._refresh_lock = threading.Lock()
        self._url = ""
        self._mode = ""
        self._username = ""
        self._api_key = ""
        self._refresh_token = ""
        self._access_token = ""
        self._access_expires = 0.0
        self._source = "none"
        self._message = ""
        self._last_error = None
        self.reload()

    # -- state ----------------------------------------------------------
    def reload(self):
        data = api_config.read_auth_settings(self.db_path)
        url = data.get("url") or ""
        key = data.get("key") or ""
        mode = data.get("mode") or ""
        username = data.get("username") or ""
        source = "settings" if (url or key or mode or username) else "none"

        # Legacy .env fallback when the local Settings row has no URL/key.
        if not url and not mode:
            info = api_config.get_api_config_info(self.db_path)
            if info.url:
                url = info.url
                source = info.source
            if not key and info.key:
                key = info.key
        try:
            url = api_config.normalize_url(url) if url else ""
        except ValueError:
            url = ""

        if not mode:
            mode = "key" if key else ("login" if username else "")
        if mode == "login" and not username:
            mode = ""
        if mode == "key" and not key:
            mode = ""

        refresh = ""
        if mode == "login" and url and username:
            refresh = self._store.load(url, username)

        with self._lock:
            self._url = url
            self._mode = mode
            self._username = username
            self._api_key = key if mode == "key" else ""
            self._refresh_token = refresh
            self._access_token = ""
            self._access_expires = 0.0
            self._source = source
            self._last_error = None
            self._message = ""
            if mode == "login" and not refresh:
                self._message = "Sign in to connect."

    def _signed_in(self):
        return bool(self._mode == "login" and self._url and self._username
                    and self._refresh_token)

    def configured(self):
        with self._lock:
            if self._mode == "key":
                return bool(self._url and self._api_key)
            if self._mode == "login":
                return self._signed_in()
            return False

    def access_token(self, force_refresh=False):
        with self._lock:
            if self._mode == "key":
                return self._api_key
            if not self._signed_in():
                return ""
            if (not force_refresh and self._access_token
                    and self._access_expires > time.time() + _ACCESS_SKEW_SECONDS):
                return self._access_token
        if self.refresh():
            with self._lock:
                return self._access_token
        return ""

    def refresh(self, force=False, failed_token=None):
        """Refresh once; concurrent callers share the same refresh lock."""
        with self._refresh_lock:
            with self._lock:
                if self._mode == "key":
                    return False
                if (failed_token and self._access_token
                        and self._access_token != failed_token):
                    # Another request already rotated while this one waited.
                    return True
                if (not force and self._access_token
                        and self._access_expires > time.time() + _ACCESS_SKEW_SECONDS):
                    return True
                url = self._url
                username = self._username
                token = self._refresh_token
            if not url or not username or not token:
                with self._lock:
                    self._last_error = "Sign in to the Web API."
                return False

            client = HathorApi(url, None, timeout=_REFRESH_TIMEOUT,
                               user_agent=_USER_AGENT)
            try:
                data = client.post(
                    "/api/v1/auth/refresh", payload={"refreshToken": token}
                )
            except HathorApiError as exc:
                if exc.status in (400, 401, 403):
                    self._drop_session(
                        "Your session expired. Sign in again in Settings -> Web API."
                    )
                else:
                    with self._lock:
                        self._last_error = (
                            "Could not refresh the Web API session. "
                            "Check the server connection."
                        )
                return False
            except Exception:
                with self._lock:
                    self._last_error = (
                        "Could not refresh the Web API session. "
                        "Check the server connection."
                    )
                return False

            try:
                self._apply_tokens(url, username, data)
            except Exception:
                with self._lock:
                    self._last_error = "The Web API returned an invalid session."
                return False
            return True

    def _decode_expiry(self, token):
        try:
            payload = token.split(".")[1]
            payload += "=" * (-len(payload) % 4)
            data = json.loads(base64.urlsafe_b64decode(payload).decode("utf-8"))
            exp = float(data["exp"])
            if exp > 0:
                return exp
        except Exception:
            pass
        return time.time() + 12 * 60

    def _apply_tokens(self, url, username, data, persisted_note=True):
        if not isinstance(data, dict):
            raise ValueError("invalid token response")
        access = str(data.get("accessToken") or data.get("AccessToken") or "").strip()
        refresh = str(data.get("refreshToken") or data.get("RefreshToken") or "").strip()
        if not access or not refresh:
            raise ValueError("missing tokens")

        with self._lock:
            self._url = url
            self._mode = "login"
            self._username = username
            self._api_key = ""
            self._refresh_token = refresh
            self._access_token = access
            self._access_expires = self._decode_expiry(access)
            self._last_error = None
            self._source = "settings"

        saved = self._store.save(url, username, refresh)
        api_config.write_auth_settings(url, "", "login", username, self.db_path)
        with self._lock:
            self._message = (
                "Signed in as " + username +
                ("" if saved else " (secure storage unavailable; session lasts this run)")
            )
        return saved

    def _drop_session(self, message):
        with self._lock:
            url = self._url
            username = self._username
            self._refresh_token = ""
            self._access_token = ""
            self._access_expires = 0.0
            self._last_error = message
            self._message = ""
        self._store.delete(url, username)
        try:
            api_config.write_auth_settings(url, "", "login" if username else "",
                                           username, self.db_path)
        except Exception:
            pass

    def login(self, url, username, password, remember_me=True):
        username = (username or "").strip()
        password = password or ""
        try:
            url = api_config.normalize_url(url)
        except ValueError as exc:
            return {"ok": False, "message": str(exc)}
        if not url:
            return {"ok": False, "message": "Server URL is required."}
        if not username:
            return {"ok": False, "message": "Username is required."}
        if not password:
            return {"ok": False, "message": "Password is required."}

        client = HathorApi(url, None, timeout=_LOGIN_TIMEOUT,
                           user_agent=_USER_AGENT)
        try:
            data = client.post(
                "/api/v1/auth/login",
                payload={
                    "username": username,
                    "password": password,
                    "rememberMe": bool(remember_me),
                    "deviceLabel": platform_support.device_label(),
                },
            )
        except HathorApiError as exc:
            if exc.status == 401:
                return {"ok": False, "message": "Invalid username or password."}
            return {"ok": False, "message": str(exc)}
        except Exception as exc:
            return {"ok": False, "message": f"Login failed: {exc}"}

        try:
            self._apply_tokens(url, username, data)
        except Exception:
            return {"ok": False, "message": "The Web API returned an invalid session."}
        return {
            "ok": True,
            "message": self._message,
            "config": self.view(),
        }

    def save_api_key(self, url, key=""):
        try:
            url = api_config.normalize_url(url)
        except ValueError as exc:
            return {"ok": False, "message": str(exc)}
        key = (key or "").strip() or self._api_key
        if not url:
            return {"ok": False, "message": "Server URL is required."}
        if not key:
            return {"ok": False, "message": "API key is required."}
        if not key.startswith("hth_"):
            return {"ok": False, "message": "The API key must start with hth_."}

        with self._lock:
            old_url, old_username = self._url, self._username
            self._url = url
            self._mode = "key"
            self._username = ""
            self._api_key = key
            self._refresh_token = ""
            self._access_token = ""
            self._access_expires = 0.0
            self._last_error = None
            self._message = "API key configured."
            self._source = "settings"
        if old_url and old_username:
            self._store.delete(old_url, old_username)
        api_config.write_auth_settings(url, key, "key", "", self.db_path)
        return {"ok": True, "message": "API key saved.", "config": self.view()}

    def logout(self):
        with self._lock:
            url, access, refresh = self._url, self._access_token, self._refresh_token
            username = self._username
        if url and refresh and (
                not access or self._access_expires <= time.time() + 5):
            access = self.access_token()
            with self._lock:
                refresh = self._refresh_token
        if url and (access or refresh):
            try:
                HathorApi(url, access or None, timeout=10,
                          user_agent=_USER_AGENT).post(
                    "/api/v1/auth/logout", payload={"refreshToken": refresh or None}
                )
            except Exception:
                pass
        self._store.delete(url, username)
        with self._lock:
            self._access_token = ""
            self._refresh_token = ""
            self._access_expires = 0.0
            self._api_key = ""
            self._mode = "login" if username else ""
            self._last_error = None
            self._message = "Signed out."
            self._source = "settings"
        api_config.write_auth_settings(url, "", self._mode, username, self.db_path)
        return {"ok": True, "message": "Signed out.", "config": self.view()}

    def clear(self):
        with self._lock:
            url, username = self._url, self._username
        self._store.delete(url, username)
        api_config.write_auth_settings("", "", "", "", self.db_path)
        with self._lock:
            self._url = ""
            self._mode = ""
            self._username = ""
            self._api_key = ""
            self._refresh_token = ""
            self._access_token = ""
            self._access_expires = 0.0
            self._message = ""
            self._last_error = None
            self._source = "none"
        return {"ok": True, "message": "Web API settings cleared.", "config": self.view()}

    def view(self):
        with self._lock:
            signed_in = self._signed_in()
            if self._last_error:
                message = self._last_error
            elif self._message:
                message = self._message
            elif signed_in:
                message = f"Signed in as {self._username}."
            elif self._mode == "key" and self._api_key:
                message = "API key configured."
            elif self._url:
                message = "Not signed in."
            else:
                message = "No Web API configured."
            return {
                "ok": not bool(self._last_error),
                "url": self._url,
                "mode": self._mode or "none",
                "username": self._username,
                "signedIn": signed_in,
                "configured": self.configured(),
                "keySet": bool(self._api_key),
                "keyHint": api_config.mask_key(self._api_key),
                "source": self._source,
                "secureStorage": self._store.available,
                "message": message,
            }

    def api_client(self, timeout=30, user_agent=_USER_AGENT):
        token = self.access_token()
        with self._lock:
            url = self._url
        if not url:
            raise HathorApiError(
                "Web API is not configured. Open Settings -> Web API."
            )
        if not token:
            raise HathorApiError(
                "Sign in to the Web API in Settings -> Web API."
            )
        return HathorApi(url, token, timeout=timeout, user_agent=user_agent,
                         auth_session=self)

    def test_connection(self):
        try:
            client = self.api_client(timeout=8)
            health = client.get("/api/health/live", timeout=8)
            me = client.get("/api/v1/auth/me", timeout=8)
            count = client.get("/api/v1/songs/count", timeout=8)
        except Exception as exc:
            return {"ok": False, "message": str(exc), "url": self._url}
        username = ""
        if isinstance(me, dict):
            username = str(me.get("username") or me.get("userName") or "").strip()
        version = str(health.get("version") or "") if isinstance(health, dict) else ""
        label = "Connected"
        if username:
            label += f" as {username}"
        if version:
            label += f" (Hathor {version})"
        label += f". {int(count) if isinstance(count, (int, float)) else count} songs visible."
        return {"ok": True, "message": label, "url": self._url}


_SESSIONS = {}
_SESSIONS_LOCK = threading.Lock()


def get_auth_session(db_path=None):
    path = os.path.abspath(str(db_path or api_config._default_db_path()))
    with _SESSIONS_LOCK:
        session = _SESSIONS.get(path)
        if session is None:
            session = AuthSession(path)
            _SESSIONS[path] = session
        return session


def reset_auth_sessions():
    """Test helper: drop cached sessions."""
    with _SESSIONS_LOCK:
        _SESSIONS.clear()

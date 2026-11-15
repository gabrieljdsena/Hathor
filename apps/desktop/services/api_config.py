"""Runtime Hathor Web API configuration.

The desktop app used to read HATHOR_API_URL/HATHOR_API_KEY from .env only,
which meant restarting the app to point it at another server. Settings ->
Web API now stores the same pair in SQLite and this module reads it before
falling back to the environment.

The API key is a scoped hth_ token, never a login password. It stays in the
local database and is masked before it reaches the webview.
"""

from __future__ import annotations

import os
import sqlite3
from dataclasses import dataclass
from urllib.parse import urlsplit, urlunsplit

API_URL_ENV = "HATHOR_API_URL"
API_KEY_ENV = "HATHOR_API_KEY"

REQUIRED_SCOPES = (
    "library:read",
    "library:write",
    "player:read",
    "player:control",
)

SETTINGS_COLUMNS = (
    ("hathor_api_url", "TEXT"),
    ("hathor_api_key", "TEXT"),
)


def ensure_settings_columns(conn):
    """Upgrade an existing Settings table (fresh installs get the SQL schema)."""
    for column, ddl in SETTINGS_COLUMNS:
        try:
            conn.execute(f"ALTER TABLE Settings ADD COLUMN {column} {ddl}")
        except sqlite3.OperationalError:
            pass


@dataclass(frozen=True)
class ApiConfig:
    url: str = ""
    key: str = ""
    source: str = "none"  # settings | env | none

    @property
    def configured(self):
        return bool(self.url and self.key)


def _default_db_path():
    try:
        import settings as app_settings
        path = getattr(app_settings, "db_path", None)
        if path:
            return path
    except Exception:
        pass
    return os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        "music_player.db",
    )


def normalize_url(value):
    """Accept a server root, add http:// when omitted, strip a trailing slash."""
    value = (value or "").strip()
    if not value:
        return ""
    if "://" not in value:
        value = "http://" + value
    parts = urlsplit(value)
    if parts.scheme not in ("http", "https"):
        raise ValueError("Server URL must start with http:// or https://.")
    if not parts.netloc:
        raise ValueError("Server URL is missing a host (for example http://192.168.1.10:5051).")
    path = (parts.path or "").rstrip("/")
    # Be forgiving if someone pastes the API root instead of the server root.
    for suffix in ("/api/v1", "/api"):
        if path.endswith(suffix):
            path = path[: -len(suffix)]
            break
    return urlunsplit((parts.scheme, parts.netloc, path, "", ""))


def mask_key(key):
    key = (key or "").strip()
    if not key:
        return ""
    if len(key) <= 12:
        return key[:4] + "*" * max(4, len(key) - 4)
    return key[:8] + "..." + key[-4:]


def _read_db_config(db_path):
    if not db_path or not os.path.exists(db_path):
        return "", ""
    conn = None
    try:
        conn = sqlite3.connect(db_path)
        row = conn.execute(
            "SELECT hathor_api_url, hathor_api_key FROM Settings LIMIT 1"
        ).fetchone()
    except sqlite3.Error:
        return "", ""
    finally:
        if conn is not None:
            conn.close()
    if not row:
        return "", ""
    return (row[0] or "").strip(), (row[1] or "").strip()


def _write_db_config(db_path, url, key):
    if not db_path:
        raise RuntimeError("Database path is unavailable.")
    if not os.path.exists(db_path):
        raise RuntimeError("The desktop database does not exist yet.")
    conn = None
    try:
        conn = sqlite3.connect(db_path)
        cur = conn.execute(
            "UPDATE Settings SET hathor_api_url = ?, hathor_api_key = ?",
            (url, key),
        )
        if cur.rowcount == 0:
            conn.execute(
                "INSERT INTO Settings (id, hathor_api_url, hathor_api_key) "
                "VALUES (1, ?, ?)",
                (url, key),
            )
        conn.commit()
    finally:
        if conn is not None:
            conn.close()


def get_api_config_info(db_path=None):
    """Return the effective config, preferring runtime settings over .env."""
    db_path = db_path or _default_db_path()
    db_url, db_key = _read_db_config(db_path)
    if db_url or db_key:
        return ApiConfig(db_url, db_key, "settings")

    env_url = ""
    try:
        env_url = normalize_url(os.getenv(API_URL_ENV) or "")
    except ValueError:
        # A malformed .env value must not prevent the app from starting.
        env_url = ""
    env_key = (os.getenv(API_KEY_ENV) or "").strip()
    if env_url or env_key:
        return ApiConfig(env_url, env_key, "env")
    return ApiConfig()


def get_api_config(db_path=None):
    """Backward-compatible tuple used by services/api_sync.py."""
    info = get_api_config_info(db_path)
    return info.url, info.key


def get_api_config_view(db_path=None):
    """Safe payload for the Settings screen; the full key never leaves Python."""
    info = get_api_config_info(db_path)
    return {
        "url": info.url,
        "keySet": bool(info.key),
        "keyHint": mask_key(info.key),
        "source": info.source,
        "configured": info.configured,
        "requiredScopes": list(REQUIRED_SCOPES),
    }


def save_api_config(url, key="", db_path=None):
    """Persist the server URL/key. A blank key keeps the current effective key."""
    db_path = db_path or _default_db_path()
    current = get_api_config_info(db_path)
    clean_url = normalize_url(url)
    clean_key = (key or "").strip() or current.key
    if not clean_url:
        raise ValueError("Server URL is required.")
    if not clean_key:
        raise ValueError("API key is required.")
    if not clean_key.startswith("hth_"):
        raise ValueError("The API key must start with hth_ (create one in the web app's API Keys page).")
    _write_db_config(db_path, clean_url, clean_key)
    return get_api_config_view(db_path)


def clear_api_config(db_path=None):
    """Clear stored values; .env remains the fallback if it is configured."""
    db_path = db_path or _default_db_path()
    _write_db_config(db_path, "", "")
    return get_api_config_view(db_path)


def _probe_scope(client, path):
    try:
        client.get(path, timeout=8)
        return "ok"
    except Exception as exc:
        status = getattr(exc, "status", None)
        if status in (401, 403):
            return "missing"
        return "unknown"


def test_connection(url=None, key="", db_path=None):
    """Check reachability, auth and the read scopes used by the desktop UI.

    Write scopes cannot be probed without mutating data; the Settings hint
    still lists them so the user knows what Discover/AI DJ need.
    """
    from .hathor_api import HathorApi

    current = get_api_config_info(db_path)
    try:
        test_url = normalize_url(url) if (url or "").strip() else current.url
    except ValueError as exc:
        return {"ok": False, "message": str(exc), "url": ""}
    test_key = (key or "").strip() or current.key
    if not test_url or not test_key:
        return {
            "ok": False,
            "message": "Enter a server URL and API key first.",
            "url": test_url or "",
        }

    client = HathorApi(test_url, test_key, timeout=8)
    try:
        health = client.get("/api/health/live", timeout=8)
    except Exception as exc:
        return {
            "ok": False,
            "message": str(exc),
            "url": test_url,
        }

    try:
        me = client.get("/api/v1/auth/me", timeout=8)
    except Exception as exc:
        return {
            "ok": False,
            "message": str(exc),
            "url": test_url,
        }

    checks = {
        "library:read": _probe_scope(client, "/api/v1/songs/count"),
        "player:read": _probe_scope(client, "/api/v1/player/state"),
        "library:write": "not-checked",
        "player:control": "not-checked",
    }
    username = ""
    if isinstance(me, dict):
        username = str(me.get("username") or me.get("userName") or "").strip()
    missing = [name for name, state in checks.items() if state == "missing"]
    version = str(health.get("version") or "") if isinstance(health, dict) else ""

    base = "Connected"
    if username:
        base += f" as {username}"
    if version:
        base += f" (Hathor {version})"
    if missing:
        message = base + ". Missing read scope(s): " + ", ".join(missing) + "."
        return {
            "ok": True,
            "message": message,
            "url": test_url,
            "version": version,
            "scopes": checks,
            "missing": missing,
        }
    return {
        "ok": True,
        "message": base + ". Library and player read access verified.",
        "url": test_url,
        "version": version,
        "scopes": checks,
        "missing": [],
    }

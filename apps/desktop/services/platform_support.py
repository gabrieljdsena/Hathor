"""Platform paths and capabilities for the desktop app.

The Windows build keeps its portable layout: the database and logs live next
to the executable. Linux installs are read-only system packages, so user data
goes to XDG directories instead. This module is deliberately stdlib-only so it
can be imported before pygame/pywebview.
"""

from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path

APP_NAME = "hathor"
DB_NAME = "music_player.db"
LOG_NAME = "hathor.log"
PACKAGE_MARKER = ".package-managed"
INSTALLER_MARKER = ".installer-managed"

IS_WINDOWS = sys.platform.startswith("win")
IS_LINUX = sys.platform.startswith("linux")
IS_MACOS = sys.platform == "darwin"

_TRUE = {"1", "true", "yes", "on"}


def _enabled(name: str) -> bool:
    return (os.getenv(name) or "").strip().lower() in _TRUE


def is_windows() -> bool:
    return IS_WINDOWS


def is_linux() -> bool:
    return IS_LINUX


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def platform_key() -> str:
    if IS_WINDOWS:
        return "windows"
    if IS_LINUX:
        return "linux"
    if IS_MACOS:
        return "macos"
    return sys.platform or "unknown"


def app_root() -> Path:
    """Read-only application files (bundle directory when frozen)."""
    bundle = getattr(sys, "_MEIPASS", None)
    if bundle:
        return Path(bundle).resolve()
    return Path(__file__).resolve().parent.parent


def install_dir() -> Path:
    """Directory containing the executable / project entry point."""
    if is_frozen():
        return Path(sys.executable).resolve().parent
    return app_root()


def portable_mode() -> bool:
    return _enabled("HATHOR_PORTABLE")


def package_managed() -> bool:
    if _enabled("HATHOR_PACKAGE_MANAGED"):
        return True
    try:
        return (app_root() / PACKAGE_MARKER).is_file()
    except OSError:
        return False


def installer_managed() -> bool:
    """Installed by scripts/install-linux.sh (per-user bundle, not a package).

    Lives under ``~/.local/opt/hathor`` and is updated by re-running the
    installer with ``--update``. The launcher also exports
    ``HATHOR_PACKAGE_MANAGED`` for releases that predate this mode, so this
    check must win over :func:`package_managed` when both are set.
    """
    if _enabled("HATHOR_INSTALLER_MANAGED"):
        return True
    try:
        return (app_root() / INSTALLER_MARKER).is_file()
    except OSError:
        return False


def externally_managed() -> bool:
    """True when the app must not use the Windows self-updater."""
    return installer_managed() or package_managed()


def _home() -> Path:
    return Path.home()


def _xdg_path(variable: str, fallback: str) -> Path:
    raw = (os.getenv(variable) or "").strip()
    if not raw:
        raw = fallback
    return Path(os.path.expandvars(os.path.expanduser(raw)))


def _linux_data_root() -> Path:
    return _xdg_path("XDG_DATA_HOME", str(_home() / ".local" / "share"))


def _linux_config_root() -> Path:
    return _xdg_path("XDG_CONFIG_HOME", str(_home() / ".config"))


def _linux_cache_root() -> Path:
    return _xdg_path("XDG_CACHE_HOME", str(_home() / ".cache"))


def _linux_state_root() -> Path:
    return _xdg_path("XDG_STATE_HOME", str(_home() / ".local" / "state"))


def _override(name: str) -> Path | None:
    raw = (os.getenv(name) or "").strip()
    if not raw:
        return None
    return Path(os.path.expandvars(os.path.expanduser(raw)))


def data_dir() -> Path:
    override = _override("HATHOR_DATA_DIR")
    if override is not None:
        return override
    if is_windows() or portable_mode():
        return install_dir()
    return _linux_data_root() / APP_NAME


def config_dir() -> Path:
    override = _override("HATHOR_CONFIG_DIR")
    if override is not None:
        return override
    if is_windows() or portable_mode():
        return install_dir()
    return _linux_config_root() / APP_NAME


def cache_dir() -> Path:
    override = _override("HATHOR_CACHE_DIR")
    if override is not None:
        return override
    if is_windows():
        root = (os.getenv("LOCALAPPDATA") or "").strip()
        if root:
            return Path(root) / "Hathor"
        return Path(os.getenv("TEMP") or ".") / "Hathor"
    return _linux_cache_root() / APP_NAME


def state_dir() -> Path:
    override = _override("HATHOR_STATE_DIR")
    if override is not None:
        return override
    if is_windows() or portable_mode():
        return install_dir()
    return _linux_state_root() / APP_NAME


def updates_dir() -> Path:
    return cache_dir() / "updates"


def db_path() -> Path:
    return data_dir() / DB_NAME


def log_path() -> Path:
    return state_dir() / LOG_NAME


def _linux_music_base() -> Path:
    """Honour XDG_MUSIC_DIR from user-dirs.dirs, fall back to ~/Music."""
    user_dirs = _linux_config_root() / "user-dirs.dirs"
    try:
        for line in user_dirs.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line.startswith("XDG_MUSIC_DIR="):
                continue
            raw = line.split("=", 1)[1].strip().strip('"')
            raw = raw.replace("$HOME", str(_home()))
            raw = os.path.expandvars(os.path.expanduser(raw))
            if raw:
                return Path(raw)
    except OSError:
        pass
    return _home() / "Music"


def default_music_dir() -> Path:
    if is_windows():
        raw = os.path.expandvars(r"%appdata%\musicPlayer")
        return Path(raw)
    return _linux_music_base() / "Hathor"


def default_podcasts_dir() -> Path:
    if is_windows():
        raw = os.path.expandvars(r"%appdata%\musicPlayerPodcasts")
        return Path(raw)
    return _linux_music_base() / "Hathor Podcasts"


def ensure_runtime_dirs() -> dict:
    """Create writable application directories. Never raises."""
    created = {}
    for name, path in (
        ("data", data_dir()),
        ("config", config_dir()),
        ("cache", cache_dir()),
        ("state", state_dir()),
    ):
        try:
            path.mkdir(parents=True, exist_ok=True)
            created[name] = str(path)
        except OSError as exc:
            created[name] = f"error: {exc}"
    return created


def migrate_legacy_data() -> dict:
    """Copy a portable DB from an old Linux tarball into XDG folders."""
    result = {"migrated_db": None}
    if is_windows():
        return result
    target_db = db_path()
    if not target_db.exists():
        candidates = [
            install_dir() / DB_NAME,
            app_root() / DB_NAME,
        ]
        for source in candidates:
            try:
                if source.is_file() and source.resolve() != target_db.resolve():
                    target_db.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(source, target_db)
                    result["migrated_db"] = str(source)
                    break
            except OSError:
                continue
    return result


def python_self_update_allowed() -> bool:
    """pip self-updates are safe on Windows/dev venvs, not system packages.

    Installer-managed builds may ship a private venv (Debian/Fedora path) or
    use the distro Python (Arch path). Only the venv case may pip-update.
    """
    if is_frozen():
        return False
    if installer_managed():
        return sys.prefix != getattr(sys, "base_prefix", sys.prefix)
    if package_managed():
        return False
    if is_windows():
        return True
    return sys.prefix != getattr(sys, "base_prefix", sys.prefix)


def device_label() -> str:
    """Human label stored on Web API login sessions."""
    if is_windows():
        return "Windows desktop"
    if is_linux():
        return "Linux desktop"
    if IS_MACOS:
        return "macOS desktop"
    return "Desktop"


def update_mode() -> str:
    if installer_managed():
        return "installer"
    if package_managed():
        return "package"
    if is_windows():
        return "self"
    return "manual"


def webview_gui() -> str | None:
    override = (os.getenv("HATHOR_WEBVIEW_GUI") or "").strip().lower()
    if override:
        return override
    return "gtk" if is_linux() else None


def window_capabilities() -> dict:
    # Both desktop targets use the same HTML titlebar; Linux goes frameless
    # too and hands drag/resize gestures to the compositor via GTK.
    custom = is_windows() or is_linux()
    return {
        "platform": platform_key(),
        "customChrome": custom,
        "nativeDecorations": not custom,
        "frameless": custom,
        "customResize": custom,
        "packageManaged": package_managed(),
        "installerManaged": installer_managed(),
        "updateMode": update_mode(),
    }

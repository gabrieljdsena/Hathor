"""Desktop self-updater for the Hathor sideload channel.

The desktop app ships as a PyInstaller onedir folder. The server publishes a
zip of that folder; this module checks ``version.json``, downloads and
verifies the package, stages it next to the cached download, and hands the
file swap to a detached PowerShell helper after Hathor exits.

Stdlib only: tests run without pygame/pywebview.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import uuid
import zipfile
from pathlib import Path, PurePosixPath
from urllib.parse import quote, unquote, urlsplit

from . import platform_support
from .api_config import get_api_config_info
from .hathor_api import HathorApi, HathorApiError

RELEASE_PATH = "/api/v1/desktop/latest"
DOWNLOAD_PREFIX = "/api/v1/desktop/download/"
PACKAGE_RE = re.compile(
    r"^hathor-desktop-(?P<code>\d{1,9})-(?P<name>.+)\.zip$",
    re.IGNORECASE,
)
EXE_NAME = "Hathor.exe"
VERSION_FILE = "version.json"
INTERNAL_DIR = "_internal"
STAGE_PREFIX = "stage-"
MAX_PACKAGE_BYTES = 2 * 1024 * 1024 * 1024
MAX_STAGE_BYTES = 4 * 1024 * 1024 * 1024
USER_AGENT = "hathor-desktop/1"


def _default_install_dir() -> Path:
    return platform_support.install_dir()


def _default_updates_dir() -> Path:
    override = (os.getenv("HATHOR_UPDATE_DIR") or "").strip()
    if override:
        return Path(override).expanduser()
    return platform_support.updates_dir()


def _read_version_file(path: Path) -> dict:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError("version.json must contain an object.")
    code = data.get("versionCode")
    if isinstance(code, bool) or not isinstance(code, int) or code < 0:
        raise ValueError("version.json has an invalid versionCode.")
    name = str(data.get("versionName") or "").strip()
    if not name:
        raise ValueError("version.json has an invalid versionName.")
    return {"versionCode": code, "versionName": name}


def _normalize_current(version) -> dict:
    if isinstance(version, dict):
        code = version.get("versionCode")
        name = str(version.get("versionName") or "").strip()
        if isinstance(code, int) and not isinstance(code, bool) and code >= 0 and name:
            return {"versionCode": code, "versionName": name}
    return {"versionCode": 0, "versionName": "dev"}


def _format_mb(value: int) -> str:
    return f"{value / 1048576:.1f} MB"


def _safe_member(name: str) -> PurePosixPath:
    raw = (name or "").replace("\\", "/")
    if not raw or raw.startswith("/"):
        raise ValueError("Update package contains an absolute path.")
    parts = PurePosixPath(raw).parts
    if not parts or ".." in parts or "." in parts:
        raise ValueError("Update package contains an unsafe path.")
    if ":" in parts[0]:
        raise ValueError("Update package contains a drive path.")
    return PurePosixPath(*parts)


def _normalize_release(data) -> dict:
    if not isinstance(data, dict):
        raise ValueError("The update server returned an invalid release.")
    code = data.get("versionCode")
    if isinstance(code, bool) or not isinstance(code, int) or code <= 0:
        raise ValueError("The update server returned an invalid versionCode.")
    name = str(data.get("versionName") or "").strip()
    if not name:
        raise ValueError("The update server returned an invalid versionName.")
    file_name = str(data.get("fileName") or "").strip()
    if (
        not file_name
        or Path(file_name).name != file_name
        or any(ch in file_name for ch in '\\/:*?"<>|')
    ):
        raise ValueError("The update server returned an invalid package name.")
    match = PACKAGE_RE.match(file_name)
    if (
        not match
        or int(match.group("code")) != code
        or match.group("name") != name
    ):
        raise ValueError("The update package name does not match its version.")
    size = data.get("sizeBytes")
    if (
        isinstance(size, bool)
        or not isinstance(size, int)
        or size <= 0
        or size > MAX_PACKAGE_BYTES
    ):
        raise ValueError("The update server returned an invalid package size.")
    sha = str(data.get("sha256") or "").strip().lower()
    if not re.fullmatch(r"[0-9a-f]{64}", sha):
        raise ValueError("The update server returned an invalid SHA-256.")
    download_path = str(data.get("downloadPath") or "").strip()
    if not download_path:
        download_path = DOWNLOAD_PREFIX + quote(file_name, safe="")
    parsed = urlsplit(download_path)
    if parsed.scheme or parsed.netloc or parsed.query or parsed.fragment:
        raise ValueError("The update server returned an invalid download path.")
    if not parsed.path.startswith(DOWNLOAD_PREFIX):
        raise ValueError("The update server returned an invalid download path.")
    if unquote(parsed.path[len(DOWNLOAD_PREFIX):]) != file_name:
        raise ValueError("The update download path does not match the package.")
    return {
        "versionCode": code,
        "versionName": name,
        "fileName": file_name,
        "sizeBytes": size,
        "sha256": sha,
        "downloadPath": parsed.path,
        "publishedAtUtc": str(data.get("publishedAtUtc") or ""),
    }


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


UPDATER_SCRIPT = r"""param(
    [Parameter(Mandatory = $true)][int]$WaitPid,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$StageDir,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [Parameter(Mandatory = $true)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$script:Started = $false
$script:CanRestart = $true
$script:BackupDir = Join-Path $InstallDir ('.hathor-backup-' + [Guid]::NewGuid().ToString('N'))
$script:AppItems = @('Hathor.exe', '_internal', 'version.json')

function Write-Log {
    param([string]$Message)
    try {
        Add-Content -LiteralPath $LogPath -Value (
            '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
        ) -Encoding UTF8
    } catch { }
}

function Write-Result {
    param([bool]$Ok, [string]$Message)
    try {
        $payload = @{ ok = $Ok; message = $Message; at = (Get-Date).ToString('o') }
        Set-Content -LiteralPath $ResultPath -Value ($payload | ConvertTo-Json -Compress) `
            -Encoding UTF8
    } catch { }
}

try {
    Write-Log "Waiting for Hathor (PID $WaitPid) to exit."
    $process = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        if (-not $process.WaitForExit(120000)) {
            $script:CanRestart = $false
            throw 'Hathor is still running; the update was not applied.'
        }
    }
    Start-Sleep -Milliseconds 400

    $exeSource = Join-Path $StageDir 'Hathor.exe'
    if (-not (Test-Path -LiteralPath $exeSource -PathType Leaf)) {
        throw 'The staged update does not contain Hathor.exe.'
    }

    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    foreach ($name in $AppItems) {
        $target = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $target) {
            Move-Item -LiteralPath $target -Destination (Join-Path $BackupDir $name) -Force
        }
    }

    Get-ChildItem -LiteralPath $StageDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $InstallDir -Recurse -Force
    }

    $exe = Join-Path $InstallDir 'Hathor.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'The update did not produce a runnable Hathor.exe.'
    }

    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
    $script:Started = $true
    Write-Log 'Update applied and Hathor restarted.'
    Write-Result $true 'Update installed.'

    Remove-Item -LiteralPath $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $StageDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
} catch {
    Write-Log ('ERROR: ' + $_.Exception.Message)
    if (-not $script:Started) {
        foreach ($name in $AppItems) {
            $target = Join-Path $InstallDir $name
            if (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
            }
            $backup = Join-Path $script:BackupDir $name
            if (Test-Path -LiteralPath $backup) {
                Move-Item -LiteralPath $backup -Destination $target -Force -ErrorAction SilentlyContinue
            }
        }
        if ($script:CanRestart) {
            $exe = Join-Path $InstallDir 'Hathor.exe'
            if (Test-Path -LiteralPath $exe -PathType Leaf) {
                try { Start-Process -FilePath $exe -WorkingDirectory $InstallDir } catch { }
            }
        }
    }
    Write-Result $false $_.Exception.Message
    exit 1
} finally {
    if (Test-Path -LiteralPath $script:BackupDir) {
        Remove-Item -LiteralPath $script:BackupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
"""


class AppUpdateService:
    def __init__(
        self,
        db_path=None,
        *,
        install_dir=None,
        updates_dir=None,
        current=None,
        frozen=None,
    ):
        self.db_path = db_path
        self.install_dir = Path(install_dir) if install_dir else _default_install_dir()
        self.updates_dir = Path(updates_dir) if updates_dir else _default_updates_dir()
        self.frozen = (
            bool(getattr(sys, "frozen", False)) if frozen is None else bool(frozen)
        )
        self.current = _normalize_current(current) if current is not None else self._detect_current()
        self._lock = threading.RLock()
        self._download_lock = threading.Lock()
        self._client = None
        self._latest = None
        self._message = ""
        self._last_error = None
        self._last_result = self._read_last_result()

    # -- version --------------------------------------------------------
    def _detect_current(self) -> dict:
        candidates = [
            self.install_dir / VERSION_FILE,
            self.install_dir / INTERNAL_DIR / VERSION_FILE,
        ]
        meipass = getattr(sys, "_MEIPASS", None)
        if meipass:
            candidates.append(Path(meipass) / VERSION_FILE)
        candidates.append(Path(__file__).resolve().parent.parent / VERSION_FILE)
        for path in candidates:
            try:
                if path.is_file():
                    return _read_version_file(path)
            except Exception:
                continue
        return {"versionCode": 0, "versionName": "dev"}

    # -- settings/status ------------------------------------------------
    def _config(self):
        try:
            return get_api_config_info(self.db_path)
        except Exception:
            class _Empty:
                url = ""
                key = ""
                configured = False

            return _Empty()

    def _read_last_result(self):
        path = self.updates_dir / "last-result.json"
        try:
            if not path.is_file():
                return None
            # PowerShell's Set-Content -Encoding UTF8 writes a BOM.
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            return data if isinstance(data, dict) else None
        except Exception:
            return None

    def _install_dir_writable(self) -> bool:
        try:
            self.install_dir.mkdir(parents=True, exist_ok=True)
            probe = self.install_dir / f".hathor-write-{uuid.uuid4().hex}.tmp"
            probe.write_text("probe", encoding="utf-8")
            probe.unlink()
            return True
        except Exception:
            return False

    def _find_package(self):
        if not self.updates_dir.is_dir():
            return None
        best = None
        for path in self.updates_dir.iterdir():
            if not path.is_file():
                continue
            match = PACKAGE_RE.match(path.name)
            if not match:
                continue
            try:
                code = int(match.group("code"))
                size = path.stat().st_size
            except (OSError, ValueError):
                continue
            if size <= 0:
                continue
            stage = self.updates_dir / f"{STAGE_PREFIX}{code}"
            item = {
                "versionCode": code,
                "versionName": match.group("name"),
                "fileName": path.name,
                "path": str(path),
                "sizeBytes": size,
                "stageDir": str(stage),
                "stageReady": self._stage_ready(stage, code),
            }
            if best is None or code > best["versionCode"]:
                best = item
        return best

    def _stage_ready(self, stage: Path, code: int) -> bool:
        try:
            if not (stage / EXE_NAME).is_file():
                return False
            if not (stage / INTERNAL_DIR).is_dir():
                return False
            version = _read_version_file(stage / VERSION_FILE)
            return version["versionCode"] == code
        except Exception:
            return False

    def status(self) -> dict:
        with self._lock:
            managed = platform_support.package_managed()
            package = None if managed else self._find_package()
            latest = dict(self._latest) if self._latest else None
            if latest is None and package:
                latest = {
                    "versionCode": package["versionCode"],
                    "versionName": package["versionName"],
                    "fileName": package["fileName"],
                    "sizeBytes": package["sizeBytes"],
                    "sha256": "",
                    "downloadPath": DOWNLOAD_PREFIX + quote(package["fileName"], safe=""),
                    "publishedAtUtc": "",
                }
            update_available = bool(
                latest and latest["versionCode"] > self.current["versionCode"]
            )
            downloaded = bool(package and package["stageReady"])
            config = self._config()
            mode = platform_support.update_mode()
            installable = (
                mode == "self"
                and self.frozen
                and os.name == "nt"
                and self._install_dir_writable()
            )
            result = self._last_result
            failed_result = bool(result and not result.get("ok", False))
            if managed:
                message = "Updates are managed by your system package manager."
            elif failed_result:
                message = "Last update attempt failed: " + str(
                    result.get("message") or "unknown error"
                )
            elif self._last_error:
                message = self._last_error
            elif self._message:
                message = self._message
            elif downloaded:
                message = "Update downloaded and verified."
            elif update_available:
                message = f"Update available: {latest['versionName']}"
            elif latest:
                message = "Up to date."
            elif not config.configured:
                message = "Configure the Web API in Settings first."
            else:
                message = "Not checked yet."
            return {
                "ok": not bool(self._last_error) and not failed_result,
                "configured": bool(config.configured),
                "managed": bool(managed),
                "updateMode": mode,
                "current": dict(self.current),
                "latest": latest,
                "updateAvailable": update_available,
                "downloaded": downloaded,
                "package": package,
                "cachedBytes": package["sizeBytes"] if package else 0,
                "installable": bool(installable),
                "installHint": self._install_hint(installable, managed),
                "message": message,
                "updatesDir": str(self.updates_dir),
            }

    def _install_hint(self, installable, managed=False):
        if managed:
            return "Use your system package manager to update Hathor."
        if not self.frozen:
            return "Update installs are available in the packaged Hathor build."
        if os.name != "nt":
            return "Automatic update install is only available on Windows."
        if not installable:
            return "The Hathor folder is not writable; update manually."
        return ""

    # -- check ----------------------------------------------------------
    def check(self, url=None, key=None) -> dict:
        if platform_support.package_managed():
            with self._lock:
                self._last_error = None
                self._message = "Updates are managed by your system package manager."
            return self.status()
        if url is None or key is None:
            config = self._config()
            url = config.url if url is None else url
            key = config.key if key is None else key
        url = (url or "").strip()
        key = (key or "").strip()
        if not url or not key:
            with self._lock:
                self._client = None
                self._latest = None
                self._last_error = None
                self._message = "Configure the Web API in Settings first."
            return self.status()

        client = HathorApi(url, key, timeout=15, user_agent=USER_AGENT)
        try:
            data = client.get(RELEASE_PATH)
        except HathorApiError as exc:
            with self._lock:
                if exc.status == 404:
                    self._client = None
                    self._latest = None
                    self._last_error = None
                    self._message = "No desktop release has been published yet."
                else:
                    self._last_error = str(exc)
            return self.status()
        except Exception as exc:
            with self._lock:
                self._last_error = f"Update check failed: {exc}"
            return self.status()

        try:
            latest = _normalize_release(data)
        except ValueError as exc:
            with self._lock:
                self._last_error = str(exc)
            return self.status()

        with self._lock:
            self._client = client
            self._latest = latest
            self._last_error = None
            self._message = ""
            self._prune_for_latest(latest)
        return self.status()

    def _prune_for_latest(self, latest):
        keep_name = (
            latest["fileName"]
            if latest["versionCode"] > self.current["versionCode"]
            else None
        )
        keep_code = latest["versionCode"] if keep_name else None
        for path in list(self.updates_dir.iterdir()) if self.updates_dir.is_dir() else []:
            if path.is_file() and (
                path.name.endswith(".zip")
                or path.name.endswith(".part")
                or path.name.endswith(".hathor-part")
            ):
                if path.name != keep_name:
                    try:
                        path.unlink()
                    except OSError:
                        pass
            elif path.is_dir() and path.name.startswith(STAGE_PREFIX):
                try:
                    code = int(path.name[len(STAGE_PREFIX):])
                except ValueError:
                    code = None
                if code != keep_code:
                    shutil.rmtree(path, ignore_errors=True)

    # -- download/stage -------------------------------------------------
    def download(self, progress_cb=None) -> dict:
        if platform_support.package_managed():
            with self._lock:
                self._last_error = (
                    "Updates are managed by your system package manager."
                )
            return self.status()
        if not self._download_lock.acquire(blocking=False):
            with self._lock:
                self._last_error = "An update download is already running."
            return self.status()
        try:
            with self._lock:
                latest = dict(self._latest) if self._latest else None
                client = self._client
                current_code = self.current["versionCode"]
            if latest is None:
                # A package can survive a restart before the first check.
                # Re-check so the installer can verify it again.
                self.check()
                with self._lock:
                    latest = dict(self._latest) if self._latest else None
                    client = self._client
            if latest is None:
                with self._lock:
                    self._last_error = "Check for updates first."
                return self.status()
            if latest["versionCode"] <= current_code:
                with self._lock:
                    self._message = "Up to date."
                    self._last_error = None
                return self.status()
            if client is None:
                config = self._config()
                if not config.configured:
                    with self._lock:
                        self._last_error = "Configure the Web API in Settings first."
                    return self.status()
                client = HathorApi(
                    config.url, config.key, timeout=30, user_agent=USER_AGENT
                )

            self.updates_dir.mkdir(parents=True, exist_ok=True)
            target = self.updates_dir / latest["fileName"]
            self._emit(
                progress_cb,
                "Preparing update...",
                None,
                f"{latest['versionName']} ({_format_mb(latest['sizeBytes'])})",
            )
            if not self._verify_package(target, latest):
                self._remove_packages(keep_name=target.name)
                self._emit(progress_cb, "Downloading update...", 0, "Connecting...")

                def on_bytes(downloaded, total):
                    if total > 0:
                        percent = min(99.0, downloaded / total * 100.0)
                        detail = f"{_format_mb(downloaded)} / {_format_mb(total)}"
                    else:
                        percent = None
                        detail = _format_mb(downloaded)
                    self._emit(
                        progress_cb, "Downloading update...", percent, detail
                    )

                client.stream_to_file(
                    latest["downloadPath"],
                    target,
                    timeout=600,
                    progress_cb=on_bytes,
                )
                if not self._verify_package(target, latest):
                    try:
                        target.unlink()
                    except OSError:
                        pass
                    raise ValueError(
                        "The downloaded update is incomplete or failed verification."
                    )

            self._emit(
                progress_cb,
                "Extracting update...",
                100,
                "Preparing the staged files",
            )
            self._ensure_stage(target, latest, progress_cb)
            with self._lock:
                self._last_error = None
                self._message = "Update downloaded and verified."
                self._last_result = None
                try:
                    (self.updates_dir / "last-result.json").unlink()
                except OSError:
                    pass
            self._emit(
                progress_cb,
                "Update ready.",
                100,
                "Restart Hathor to install it.",
                status="done",
            )
            return self.status()
        except Exception as exc:
            with self._lock:
                self._last_error = f"Update download failed: {exc}"
            return self.status()
        finally:
            self._download_lock.release()

    def _emit(self, progress_cb, label, percent, detail, status="working"):
        if progress_cb is None:
            return
        try:
            progress_cb(
                {
                    "stage": "app",
                    "label": label,
                    "percent": percent,
                    "detail": detail or "",
                    "status": status,
                }
            )
        except Exception:
            pass

    def _verify_package(self, path: Path, latest: dict) -> bool:
        try:
            if not path.is_file():
                return False
            if path.stat().st_size != latest["sizeBytes"]:
                return False
            return _sha256_file(path) == latest["sha256"]
        except OSError:
            return False

    def _ensure_stage(self, package: Path, latest: dict, progress_cb=None) -> Path:
        stage = self.updates_dir / f"{STAGE_PREFIX}{latest['versionCode']}"
        if self._stage_ready(stage, latest["versionCode"]):
            return stage
        shutil.rmtree(stage, ignore_errors=True)
        tmp = self.updates_dir / (
            f".stage-{latest['versionCode']}-{uuid.uuid4().hex}.tmp"
        )
        shutil.rmtree(tmp, ignore_errors=True)
        tmp.mkdir(parents=True, exist_ok=False)
        try:
            with zipfile.ZipFile(package, "r") as archive:
                infos = archive.infolist()
                total = sum(info.file_size for info in infos)
                if total > MAX_STAGE_BYTES:
                    raise ValueError("The update package is too large.")
                for index, info in enumerate(infos):
                    if info.is_dir() or info.filename.endswith(("/", "\\")):
                        continue
                    mode = (info.external_attr >> 16) & 0o170000
                    if mode == 0o120000:
                        raise ValueError("The update package contains a symlink.")
                    member = _safe_member(info.filename)
                    dest = tmp.joinpath(*member.parts)
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    with archive.open(info, "r") as source, open(dest, "wb") as target:
                        shutil.copyfileobj(source, target)
                    if progress_cb and index and index % 200 == 0:
                        percent = min(99.0, 90.0 + index / max(1, len(infos)) * 9.0)
                        self._emit(
                            progress_cb,
                            "Extracting update...",
                            percent,
                            f"{index} / {len(infos)} files",
                        )
            self._validate_stage(tmp, latest)
            os.replace(tmp, stage)
        finally:
            if tmp.exists():
                shutil.rmtree(tmp, ignore_errors=True)
        return stage

    def _validate_stage(self, stage: Path, latest: dict):
        if not (stage / EXE_NAME).is_file():
            raise ValueError("The update package does not contain Hathor.exe.")
        if not (stage / INTERNAL_DIR).is_dir():
            raise ValueError("The update package does not contain _internal.")
        version = _read_version_file(stage / VERSION_FILE)
        if version["versionCode"] != latest["versionCode"]:
            raise ValueError("The update package version does not match the release.")
        if (stage / EXE_NAME).stat().st_size <= 0:
            raise ValueError("The update package contains an empty Hathor.exe.")

    # -- install/remove -------------------------------------------------
    def install(self) -> dict:
        if platform_support.package_managed():
            with self._lock:
                self._last_error = (
                    "Updates are managed by your system package manager."
                )
            return self.status()
        with self._lock:
            package = self._find_package()
            latest = dict(self._latest) if self._latest else None
        if package is None or not package["stageReady"]:
            with self._lock:
                self._last_error = "Download an update before installing it."
            return self.status()
        if latest is None:
            latest = {
                "versionCode": package["versionCode"],
                "versionName": package["versionName"],
                "fileName": package["fileName"],
            }
        if package["versionCode"] <= self.current["versionCode"]:
            with self._lock:
                self._last_error = "The downloaded update is already installed."
            return self.status()
        if not self.frozen:
            with self._lock:
                self._last_error = (
                    "Update installs are available in the packaged Hathor build."
                )
            return self.status()
        if os.name != "nt":
            with self._lock:
                self._last_error = "Automatic update install is only available on Windows."
            return self.status()
        if not self._install_dir_writable():
            with self._lock:
                self._last_error = (
                    "The Hathor folder is not writable; update manually or move the app."
                )
            return self.status()

        try:
            self._launch_updater(
                package["path"],
                package["stageDir"],
                latest,
            )
        except Exception as exc:
            with self._lock:
                self._last_error = f"Could not start the updater: {exc}"
            return self.status()

        with self._lock:
            self._last_error = None
            self._message = f"Installing {latest['versionName']}; Hathor will restart."
        return {
            "ok": True,
            "message": self._message,
            "latest": latest,
            "restarting": True,
        }

    def _launch_updater(self, package_path, stage_dir, latest):
        self.updates_dir.mkdir(parents=True, exist_ok=True)
        script = self.updates_dir / "apply-update.ps1"
        script.write_text(UPDATER_SCRIPT, encoding="utf-8")
        log_path = self.updates_dir / "updater.log"
        try:
            if log_path.is_file() and log_path.stat().st_size > 1024 * 1024:
                log_path.unlink()
        except OSError:
            pass
        result_path = self.updates_dir / "last-result.json"
        try:
            result_path.unlink()
        except OSError:
            pass
        powershell = (
            Path(os.environ.get("SystemRoot", r"C:\Windows"))
            / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
        )
        if not powershell.is_file():
            powershell = Path("powershell.exe")
        command = [
            str(powershell),
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-WindowStyle",
            "Hidden",
            "-File",
            str(script),
            "-WaitPid",
            str(os.getpid()),
            "-InstallDir",
            str(self.install_dir),
            "-StageDir",
            str(stage_dir),
            "-PackagePath",
            str(package_path),
            "-LogPath",
            str(log_path),
            "-ResultPath",
            str(result_path),
        ]
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        subprocess.Popen(
            command,
            cwd=str(self.updates_dir),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=creationflags,
            close_fds=True,
        )

    def remove(self) -> dict:
        if platform_support.package_managed():
            return self.status()
        with self._download_lock:
            self._remove_packages()
            with self._lock:
                self._latest = None
                self._client = None
                self._message = ""
                self._last_error = None
                self._last_result = None
                for name in ("last-result.json",):
                    try:
                        (self.updates_dir / name).unlink()
                    except OSError:
                        pass
            return self.status()

    def _remove_packages(self, keep_name=None):
        if not self.updates_dir.is_dir():
            return
        for path in list(self.updates_dir.iterdir()):
            if path.name == keep_name:
                continue
            if path.is_file() and (
                path.name.endswith(".zip")
                or path.name.endswith(".part")
                or path.name.endswith(".hathor-part")
            ):
                try:
                    path.unlink()
                except OSError:
                    pass
            elif path.is_dir() and (
                path.name.startswith(STAGE_PREFIX)
                or path.name.startswith(".stage-")
            ):
                shutil.rmtree(path, ignore_errors=True)

    # -- startup cleanup ------------------------------------------------
    def cleanup(self):
        if platform_support.package_managed():
            return
        try:
            self.updates_dir.mkdir(parents=True, exist_ok=True)
        except OSError:
            return
        if self._last_result and self._last_result.get("ok", False):
            try:
                (self.updates_dir / "last-result.json").unlink()
            except OSError:
                pass
            self._last_result = None
        for path in list(self.updates_dir.iterdir()):
            if path.is_dir():
                if path.name.startswith(".stage-") and path.name.endswith(".tmp"):
                    shutil.rmtree(path, ignore_errors=True)
                continue
            if path.name.endswith(".part") or path.name.endswith(".hathor-part"):
                try:
                    path.unlink()
                except OSError:
                    pass

        packages = []
        for path in self.updates_dir.iterdir():
            if not path.is_file():
                continue
            match = PACKAGE_RE.match(path.name)
            if not match:
                continue
            try:
                code = int(match.group("code"))
            except ValueError:
                continue
            if code <= self.current["versionCode"]:
                try:
                    path.unlink()
                except OSError:
                    pass
                shutil.rmtree(
                    self.updates_dir / f"{STAGE_PREFIX}{code}", ignore_errors=True
                )
            else:
                packages.append((code, path))

        keep_code = max(packages, default=(None, None))[0]
        for code, path in packages:
            if code != keep_code:
                try:
                    path.unlink()
                except OSError:
                    pass
                shutil.rmtree(
                    self.updates_dir / f"{STAGE_PREFIX}{code}", ignore_errors=True
                )
        for path in list(self.updates_dir.iterdir()):
            if not path.is_dir() or not path.name.startswith(STAGE_PREFIX):
                continue
            try:
                code = int(path.name[len(STAGE_PREFIX):])
            except ValueError:
                code = None
            if code != keep_code:
                shutil.rmtree(path, ignore_errors=True)

#!/usr/bin/env bash
# Hathor Linux installer / updater.
#
#   curl -fsSL <raw-url>/install-linux.sh | bash
#   ./install-linux.sh                 # install the bundle next to this script
#   ./install-linux.sh --zip file.zip  # install a downloaded release zip
#   ./install-linux.sh --update        # update an existing install
#   ./install-linux.sh --uninstall     # remove the app (keeps user data)
#
# The app is installed per-user under ~/.local:
#   ~/.local/opt/hathor/app   runtime tree
#   ~/.local/opt/hathor/venv  private venv (only when system Python misses deps)
#   ~/.local/bin/hathor       launcher
#   ~/.local/share/applications/hathor.desktop
# User data (library DB, config, logs) stays in the normal XDG folders.
#
# System GTK/WebKitGTK/PyGObject packages are still required. This script can
# install them with --install-deps (or after an interactive prompt); it never
# uses sudo for the app files themselves.
set -euo pipefail

REPO="gabrieljdsena/Hathor"
SELF_URL="https://raw.githubusercontent.com/${REPO}/develop/apps/desktop/scripts/install-linux.sh"

PREFIX="${HATHOR_PREFIX:-$HOME/.local}"
ROOT="$PREFIX/opt/hathor"
APP_DIR="$ROOT/app"
VENV_DIR="$ROOT/venv"
LAUNCHER="$PREFIX/bin/hathor"
DESKTOP_DIR="$PREFIX/share/applications"
DESKTOP_FILE="$DESKTOP_DIR/hathor.desktop"
ICON_DIR="$PREFIX/share/icons/hicolor/512x512/apps"
ICON_FILE="$ICON_DIR/hathor.png"
RUNTIME_ITEMS="main.py api.py Download.py settings.py database.sql version.json requirements.txt services ui"

MODE=""
ZIP_PATH=""
WANT_VERSION=""
INSTALL_DEPS=0
NO_DEPS=0
ASSUME_YES=0
NO_DESKTOP=0
FORCE=0
PURGE=0
REQUESTED_PYTHON=""

TMP_DIR=""
HELPER_PY=""
HELPER=""
SRC_APP=""
SCRIPT_FILE=""
NEW_CODE=""
NEW_NAME=""
NEW_TAG=""
UP_TO_DATE=0
INSTALLER_SAVED=0
RUNTIME_PY=""
BASE_PY=""
PKG_FAMILY=""

say() { printf '%s\n' "$*"; }
warn() { printf 'Warning: %s\n' "$*" >&2; }
die() { printf 'Error: %s\n' "$*" >&2; exit 1; }
have() { command -v "$1" >/dev/null 2>&1; }

cleanup() {
    if [ -n "${TMP_DIR:-}" ] && [ -d "$TMP_DIR" ]; then
        rm -rf "$TMP_DIR"
    fi
}
trap cleanup EXIT

usage() {
    cat <<'EOF'
Hathor Linux installer

Usage:
  install-linux.sh [options] [release.zip]

Options:
  --latest               Install the newest desktop release from GitHub.
  --version <v>          Install a specific desktop release (tag or versionName).
  --update               Update an existing install if a newer release exists.
  --zip <path>           Install from a local release zip.
  --force                Reinstall/update even when the version is unchanged.
  --install-deps         Install missing system packages with sudo (no prompt).
  --no-deps              Never touch system packages; fail if something is missing.
  --yes                  Answer yes to confirmation prompts.
  --python <path>        System Python to use (must have PyGObject + GTK 3).
  --prefix <dir>         Install prefix (default: ~/.local).
  --no-desktop           Skip the application-menu entry and icon.
  --uninstall            Remove the app; user data is kept.
  --purge                With --uninstall: also remove XDG app data (not music).
  -h, --help             Show this help.

The installer verifies the release SHA-256 before extracting, installs app
files under ~/.local/opt/hathor, and reuses your distro's Python/GTK/WebKit
stack. Data paths (DB, config, logs) are unchanged.
EOF
}

ask() {
    local prompt="$1" answer=""
    if [ "$ASSUME_YES" -eq 1 ]; then
        return 0
    fi
    if [ -r /dev/tty ] && [ -w /dev/tty ]; then
        printf '%s [Y/n] ' "$prompt" > /dev/tty
        IFS= read -r answer < /dev/tty || answer=""
        case "${answer:-y}" in
            y|Y|yes|YES|"") return 0 ;;
            *) return 1 ;;
        esac
    fi
    return 1
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --latest) MODE="latest" ;;
        --version)
            [ "$#" -ge 2 ] || die "--version needs a value."
            WANT_VERSION="$2"; MODE="version"; shift ;;
        --version=*) WANT_VERSION="${1#*=}"; MODE="version" ;;
        --update) MODE="update" ;;
        --zip)
            [ "$#" -ge 2 ] || die "--zip needs a path."
            ZIP_PATH="$2"; MODE="zip"; shift ;;
        --zip=*) ZIP_PATH="${1#*=}"; MODE="zip" ;;
        --force) FORCE=1 ;;
        --install-deps) INSTALL_DEPS=1 ;;
        --no-deps) NO_DEPS=1 ;;
        --yes|-y) ASSUME_YES=1 ;;
        --python)
            [ "$#" -ge 2 ] || die "--python needs a path."
            REQUESTED_PYTHON="$2"; shift ;;
        --python=*) REQUESTED_PYTHON="${1#*=}" ;;
        --prefix)
            [ "$#" -ge 2 ] || die "--prefix needs a path."
            PREFIX="$2"; shift ;;
        --prefix=*) PREFIX="${1#*=}" ;;
        --no-desktop) NO_DESKTOP=1 ;;
        --uninstall) MODE="uninstall" ;;
        --purge) PURGE=1 ;;
        -h|--help) usage; exit 0 ;;
        -*)
            die "Unknown option: $1 (try --help)" ;;
        *)
            if [ -z "$ZIP_PATH" ] && [ -z "$MODE" ] && [ -f "$1" ]; then
                ZIP_PATH="$1"; MODE="zip"
            else
                die "Unexpected argument: $1"
            fi ;;
    esac
    shift
done

if [ "$INSTALL_DEPS" -eq 1 ] && [ "$NO_DEPS" -eq 1 ]; then
    die "--install-deps and --no-deps are mutually exclusive."
fi
if [ "$PURGE" -eq 1 ] && [ "$MODE" != "uninstall" ]; then
    die "--purge only works together with --uninstall."
fi

# Expand ~ and make PREFIX absolute.
# shellcheck disable=SC2088  # deliberate literal "~/" match
case "$PREFIX" in
    "~") PREFIX="$HOME" ;;
    "~/"*) PREFIX="$HOME/${PREFIX#\~/}" ;;
esac
case "$PREFIX" in
    /*) ;;
    *) PREFIX="$(pwd)/$PREFIX" ;;
esac
PREFIX="${PREFIX%/}"
ROOT="$PREFIX/opt/hathor"
APP_DIR="$ROOT/app"
VENV_DIR="$ROOT/venv"
LAUNCHER="$PREFIX/bin/hathor"
DESKTOP_DIR="$PREFIX/share/applications"
DESKTOP_FILE="$DESKTOP_DIR/hathor.desktop"
ICON_DIR="$PREFIX/share/icons/hicolor/512x512/apps"
ICON_FILE="$ICON_DIR/hathor.png"

# Locate the script on disk (not when piped from curl).
if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    SCRIPT_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
fi

# Local source tree: CI/repo runs or an extracted release zip.
if [ -z "$MODE" ]; then
    if [ -n "$SCRIPT_FILE" ]; then
        _sd="$(dirname "$SCRIPT_FILE")"
        for _cand in "$_sd" "$_sd/.."; do
            if [ -f "$_cand/main.py" ] && [ -d "$_cand/services" ] && [ -d "$_cand/ui" ]; then
                SRC_APP="$(cd "$_cand" && pwd)"
                break
            fi
        done
    fi
    if [ -n "$SRC_APP" ]; then
        MODE="source"
    else
        MODE="latest"
    fi
fi

mkdir -p "$PREFIX/bin" "$ROOT"
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/hathor-install.XXXXXX")"
HELPER="$TMP_DIR/helper.py"

write_helper() {
    cat > "$HELPER" <<'PY'
import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

API = "https://api.github.com/repos/gabrieljdsena/Hathor/releases?per_page=30"
UA = "hathor-linux-installer/1"
WIN_RE = re.compile(r"^hathor-desktop-(\d+)-(.+)\.zip$", re.IGNORECASE)
LNX_RE = re.compile(r"^hathor-linux-(?:(\d+)-)?(.+)\.zip$", re.IGNORECASE)


def die(message, code=1):
    print(message, file=sys.stderr)
    raise SystemExit(code)


def _headers(accept):
    headers = {"User-Agent": UA, "Accept": accept}
    token = (os.environ.get("GITHUB_TOKEN") or "").strip()
    if token:
        headers["Authorization"] = "Bearer " + token
    return headers


def get(url, accept="application/vnd.github+json", timeout=30):
    request = urllib.request.Request(url, headers=_headers(accept))
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()
    except urllib.error.HTTPError as exc:
        hint = ""
        if exc.code == 403:
            hint = " (GitHub rate limit; set GITHUB_TOKEN and retry)"
        die(f"GitHub request failed with HTTP {exc.code}{hint}: {url}")
    except Exception as exc:
        die(f"Cannot reach GitHub: {exc}")


def download(url, dest):
    request = urllib.request.Request(
        url, headers=_headers("application/octet-stream")
    )
    tmp = Path(str(dest) + ".part")
    try:
        with urllib.request.urlopen(request, timeout=60) as response, \
                open(tmp, "wb") as handle:
            raw_total = response.headers.get("Content-Length") or "0"
            total = int(raw_total) if str(raw_total).isdigit() else 0
            got = 0
            if total:
                print(
                    f"Downloading {url.rsplit('/', 1)[-1]} "
                    f"({total / 1048576:.1f} MiB)...",
                    file=sys.stderr,
                )
            else:
                print("Downloading release...", file=sys.stderr)
            last = -10
            while True:
                chunk = response.read(256 * 1024)
                if not chunk:
                    break
                handle.write(chunk)
                got += len(chunk)
                if total:
                    pct = min(100, int(got * 100 / total))
                    if pct >= last + 10:
                        print(f"  {pct}%", file=sys.stderr)
                        last = pct
        tmp.replace(dest)
    except SystemExit:
        raise
    except Exception as exc:
        try:
            tmp.unlink()
        except OSError:
            pass
        die(f"Download failed: {exc}")
    print(f"  {got} bytes downloaded", file=sys.stderr)


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha_from_sums(text, file_name):
    for line in text.splitlines():
        parts = line.strip().split(None, 1)
        if len(parts) != 2:
            continue
        listed = parts[1].strip().lstrip("*").lstrip("./")
        sha = parts[0].strip().lower()
        if listed == file_name and re.fullmatch(r"[0-9a-f]{64}", sha):
            return sha
    return None


def safe_extract(zip_path, dest):
    dest = Path(dest).resolve()
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    try:
        with zipfile.ZipFile(zip_path) as archive:
            for info in archive.infolist():
                name = info.filename
                if not name or name.startswith("/") or "\\" in name:
                    die("Refusing unsafe path in release zip.")
                target = (dest / name).resolve()
                if target != dest and dest not in target.parents:
                    die("Refusing path traversal in release zip.")
                if info.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info) as src, open(target, "wb") as out:
                    shutil.copyfileobj(src, out)
                mode = (info.external_attr >> 16) & 0o777
                if mode:
                    target.chmod(mode)
    except SystemExit:
        raise
    except Exception as exc:
        die(f"Could not extract release zip: {exc}")


def find_app_dir(root):
    root = Path(root)
    if (root / "main.py").is_file() and (root / "services").is_dir():
        return root
    matches = [
        path for path in root.iterdir()
        if path.is_dir() and (path / "main.py").is_file()
        and (path / "services").is_dir()
    ]
    if len(matches) != 1:
        die("Release zip does not contain a single Hathor app directory.")
    return matches[0]


def stage_app_extracted(zip_path, dest, out_root):
    safe_extract(zip_path, dest)
    app = find_app_dir(dest)
    target = Path(out_root) / "app-src"
    if target.exists():
        shutil.rmtree(target)
    shutil.move(str(app), str(target))
    return target


def read_version(app_dir):
    path = Path(app_dir) / "version.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        die(f"Cannot read version.json: {exc}")
    code = data.get("versionCode")
    name = str(data.get("versionName") or "").strip()
    if isinstance(code, bool) or not isinstance(code, int) or code <= 0 or not name:
        die("version.json has an invalid versionCode/versionName.")
    return code, name


def print_version(code, name, tag=""):
    print(f"CODE={code}")
    print(f"NAME={name}")
    if tag:
        print(f"TAG={tag}")


def cmd_version(args):
    code, name = read_version(args.dir)
    print_version(code, name)


def cmd_extract(args):
    out_root = Path(args.dest)
    out_root.mkdir(parents=True, exist_ok=True)
    app = stage_app_extracted(args.zip, out_root / "unpacked", out_root)
    code, name = read_version(app)
    print_version(code, name)


def cmd_fetch(args):
    raw = get(API)
    try:
        releases = json.loads(raw.decode("utf-8"))
    except Exception:
        die("GitHub returned an invalid release feed.")
    if not isinstance(releases, list):
        die("GitHub returned an invalid release feed.")

    entries = []
    for index, release in enumerate(releases):
        if not isinstance(release, dict) or release.get("draft"):
            continue
        tag = str(release.get("tag_name") or "")
        if not tag.startswith("desktop-v"):
            continue
        assets = release.get("assets") or []
        windows = None
        linux_assets = []
        for asset in assets:
            if not isinstance(asset, dict):
                continue
            name = str(asset.get("name") or "")
            win_match = WIN_RE.match(name)
            if win_match and windows is None:
                windows = (int(win_match.group(1)), name, asset)
            if LNX_RE.match(name):
                linux_assets.append((name, asset))
        if not linux_assets:
            continue
        asset_name, asset = linux_assets[0]
        match = LNX_RE.match(asset_name)
        code = int(match.group(1)) if match.group(1) else None
        if code is None and windows is not None:
            code = windows[0]
        entries.append({
            "index": index,
            "tag": tag,
            "code": code,
            "name": match.group(2),
            "asset": asset,
            "release": release,
        })

    if not entries:
        die("No desktop release with a Linux zip was found on GitHub.")

    if args.version:
        want = args.version.strip()
        tag_want = want if want.startswith("desktop-v") else "desktop-v" + want
        entries = [
            entry for entry in entries
            if entry["tag"] in (want, tag_want)
            or entry["name"] == want
            or (entry["code"] is not None and str(entry["code"]) == want)
        ]
        if not entries:
            die(f"No desktop release matches '{args.version}'.")

    best = max(
        entries,
        key=lambda entry: (
            entry["code"] if entry["code"] is not None else -1,
            -entry["index"],
        ),
    )

    if args.update and not args.force:
        installed_code = int(args.installed_code or -1)
        if best["code"] is not None and installed_code >= 0:
            if best["code"] <= installed_code:
                print("UP_TO_DATE=1")
                print_version(best["code"], best["name"], best["tag"])
                return
        elif args.installed_name and args.installed_name == best["name"]:
            print("UP_TO_DATE=1")
            print_version(best["code"] or installed_code, best["name"],
                          best["tag"])
            return

    asset = best["asset"]
    url = str(asset.get("browser_download_url") or "")
    asset_name = str(asset.get("name") or "")
    if not url.startswith("https://"):
        die("Release asset URL is not HTTPS; refusing to download.")

    out_root = Path(args.dest)
    out_root.mkdir(parents=True, exist_ok=True)
    zip_path = out_root / "release.zip"
    download(url, zip_path)

    expected = ""
    digest = str(asset.get("digest") or "")
    if digest.lower().startswith("sha256:"):
        candidate = digest.split(":", 1)[1].strip().lower()
        if re.fullmatch(r"[0-9a-f]{64}", candidate):
            expected = candidate
    if not expected:
        sums = next(
            (
                item for item in best["release"].get("assets", [])
                if isinstance(item, dict)
                and str(item.get("name") or "") == "SHA256SUMS.txt"
            ),
            None,
        )
        if sums:
            sums_url = str(sums.get("browser_download_url") or "")
            if sums_url.startswith("https://"):
                expected = sha_from_sums(
                    get(sums_url, accept="text/plain").decode(
                        "utf-8", errors="replace"
                    ),
                    asset_name,
                ) or ""
    if not expected:
        die("Release has no SHA-256 digest; refusing to install an unverified zip.")
    actual = sha256_file(zip_path)
    if actual != expected:
        die(f"SHA-256 mismatch (expected {expected}, got {actual}).")

    app = stage_app_extracted(zip_path, out_root / "unpacked", out_root)
    code, name = read_version(app)
    print_version(code, name, best["tag"])


def main(argv=None):
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)

    fetch = sub.add_parser("fetch")
    fetch.add_argument("--dest", required=True)
    fetch.add_argument("--version", default="")
    fetch.add_argument("--update", action="store_true")
    fetch.add_argument("--force", action="store_true")
    fetch.add_argument("--installed-code", default="")
    fetch.add_argument("--installed-name", default="")
    fetch.set_defaults(func=cmd_fetch)

    extract = sub.add_parser("extract")
    extract.add_argument("--zip", required=True)
    extract.add_argument("--dest", required=True)
    extract.set_defaults(func=cmd_extract)

    version = sub.add_parser("version")
    version.add_argument("--dir", required=True)
    version.set_defaults(func=cmd_version)

    args = parser.parse_args(argv)
    args.func(args)


if __name__ == "__main__":
    main()
PY
}

detect_family() {
    local id="" like=""
    if [ -r /etc/os-release ]; then
        # shellcheck disable=SC1091
        . /etc/os-release
        id="${ID:-}"
        like="${ID_LIKE:-}"
    fi
    case " $id $like " in
        *" arch "*|*" cachyos "*) PKG_FAMILY="arch"; return ;;
        *" debian "*|*" ubuntu "*) PKG_FAMILY="debian"; return ;;
        *" fedora "*|*" rhel "*) PKG_FAMILY="fedora"; return ;;
    esac
    if have pacman; then PKG_FAMILY="arch"
    elif have apt-get; then PKG_FAMILY="debian"
    elif have dnf; then PKG_FAMILY="fedora"
    else PKG_FAMILY="unknown"
    fi
}

debian_webkit_package() {
    if have apt-cache && apt-cache show gir1.2-webkit2-4.1 >/dev/null 2>&1; then
        printf '%s' "gir1.2-webkit2-4.1"
    elif have apt-cache && apt-cache show gir1.2-webkit2-4.0 >/dev/null 2>&1; then
        printf '%s' "gir1.2-webkit2-4.0"
    else
        printf '%s' "gir1.2-webkit2-4.1"
    fi
}

fedora_webkit_package() {
    if have dnf && dnf -q info webkit2gtk4.1 >/dev/null 2>&1; then
        printf '%s' "webkit2gtk4.1"
    elif have dnf && dnf -q info webkit2gtk4.0 >/dev/null 2>&1; then
        printf '%s' "webkit2gtk4.0"
    else
        printf '%s' "webkit2gtk4.1"
    fi
}

package_plan() {
    case "$PKG_FAMILY" in
        arch)
            REQUIRED_PKGS=(python python-pywebview python-gobject webkit2gtk-4.1 python-pygame)
            OPTIONAL_PKGS=(python-mutagen python-pillow python-psutil python-pykakasi python-dotenv python-keyring yt-dlp ffmpeg)
            INSTALL_CMD_HINT="sudo pacman -S --needed ${REQUIRED_PKGS[*]} ${OPTIONAL_PKGS[*]}"
            ;;
        debian)
            local webkit soup
            webkit="$(debian_webkit_package)"
            DEBIAN_WEBKIT_PKG="$webkit"
            soup="gir1.2-soup-2.4"
            case "$webkit" in *4.1) soup="gir1.2-soup-3.0" ;; esac
            REQUIRED_PKGS=(python3 python3-venv python3-gi python3-gi-cairo gir1.2-gtk-3.0 python3-pygame "$webkit" "$soup")
            OPTIONAL_PKGS=(yt-dlp ffmpeg)
            INSTALL_CMD_HINT="sudo apt-get install -y ${REQUIRED_PKGS[*]} ${OPTIONAL_PKGS[*]}"
            ;;
        fedora)
            local webkit soup
            webkit="$(fedora_webkit_package)"
            FEDORA_WEBKIT_PKG="$webkit"
            soup="libsoup"
            case "$webkit" in *4.1) soup="libsoup3" ;; esac
            REQUIRED_PKGS=(python3 python3-pip python3-gobject gtk3 "$webkit" "$soup")
            OPTIONAL_PKGS=(python3-pygame yt-dlp ffmpeg)
            INSTALL_CMD_HINT="sudo dnf install -y ${REQUIRED_PKGS[*]} ${OPTIONAL_PKGS[*]}"
            ;;
        *)
            REQUIRED_PKGS=()
            OPTIONAL_PKGS=()
            INSTALL_CMD_HINT=""
            ;;
    esac
}

run_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    elif have sudo; then
        sudo "$@"
    else
        die "Root privileges are required: $*"
    fi
}

install_packages() {
    if [ "$PKG_FAMILY" = "unknown" ]; then
        die "Unsupported distribution. Install GTK 3, WebKitGTK 4.x, PyGObject, pygame and Python 3.10+, then retry with --no-deps."
    fi
    package_plan
    say "Installing system packages with the $PKG_FAMILY package manager..."
    [ -n "$INSTALL_CMD_HINT" ] && say "  $INSTALL_CMD_HINT"
    case "$PKG_FAMILY" in
        arch)
            run_root pacman -S --needed --noconfirm "${REQUIRED_PKGS[@]}"
            run_root pacman -S --needed --noconfirm "${OPTIONAL_PKGS[@]}" || \
                warn "Some optional packages (yt-dlp/ffmpeg) could not be installed."
            ;;
        debian)
            run_root apt-get update -qq || warn "apt-get update failed; continuing."
            if ! run_root apt-get install -y --no-install-recommends "${REQUIRED_PKGS[@]}"; then
                if [ "${DEBIAN_WEBKIT_PKG:-}" = "gir1.2-webkit2-4.1" ]; then
                    warn "WebKitGTK 4.1 install failed; retrying with 4.0."
                    REQUIRED_PKGS=(python3 python3-venv python3-gi python3-gi-cairo gir1.2-gtk-3.0 python3-pygame gir1.2-webkit2-4.0 gir1.2-soup-2.4)
                    run_root apt-get install -y --no-install-recommends "${REQUIRED_PKGS[@]}" || \
                        die "Could not install the required GTK/WebKitGTK packages."
                else
                    die "Could not install the required GTK/WebKitGTK packages."
                fi
            fi
            run_root apt-get install -y --no-install-recommends "${OPTIONAL_PKGS[@]}" || \
                warn "Some optional packages (yt-dlp/ffmpeg) could not be installed."
            ;;
        fedora)
            if ! run_root dnf install -y "${REQUIRED_PKGS[@]}"; then
                if [ "${FEDORA_WEBKIT_PKG:-}" = "webkit2gtk4.1" ]; then
                    warn "WebKitGTK 4.1 install failed; retrying with 4.0."
                    REQUIRED_PKGS=(python3 python3-pip python3-gobject gtk3 webkit2gtk4.0 libsoup)
                    run_root dnf install -y "${REQUIRED_PKGS[@]}" || \
                        die "Could not install the required GTK/WebKitGTK packages."
                else
                    die "Could not install the required GTK/WebKitGTK packages."
                fi
            fi
            run_root dnf install -y "${OPTIONAL_PKGS[@]}" || \
                warn "Some optional packages (yt-dlp/ffmpeg) could not be installed."
            ;;
    esac
}

python_version_ok() {
    "$1" -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 10) else 1)' \
        >/dev/null 2>&1
}

python_gui_ok() {
    "$1" - <<'PY' >/dev/null 2>&1
import gi
gi.require_version('Gtk', '3.0')
try:
    gi.require_version('WebKit2', '4.1')
    gi.require_version('Soup', '3.0')
except ValueError:
    gi.require_version('WebKit2', '4.0')
    gi.require_version('Soup', '2.4')
from gi.repository import Gtk, WebKit2  # noqa: F401
PY
}

python_full_ok() {
    "$1" - <<'PY' >/dev/null 2>&1
import sys
if sys.version_info < (3, 10):
    raise SystemExit(1)
import gi
gi.require_version('Gtk', '3.0')
try:
    gi.require_version('WebKit2', '4.1')
    gi.require_version('Soup', '3.0')
except ValueError:
    gi.require_version('WebKit2', '4.0')
    gi.require_version('Soup', '2.4')
from gi.repository import Gtk, WebKit2  # noqa: F401
import pygame  # noqa: F401
import mutagen  # noqa: F401
import PIL  # noqa: F401
import psutil  # noqa: F401
import pykakasi  # noqa: F401
import dotenv  # noqa: F401
import keyring  # noqa: F401
import yt_dlp  # noqa: F401
import webview  # noqa: F401
PY
}

find_any_python() {
    local candidate
    for candidate in /usr/bin/python3 /usr/bin/python python3 python; do
        if [ -x "$candidate" ] && python_version_ok "$candidate"; then
            printf '%s' "$candidate"
            return 0
        fi
        if have "$candidate" && python_version_ok "$candidate"; then
            printf '%s' "$candidate"
            return 0
        fi
    done
    return 1
}

find_base_python() {
    local candidate
    if [ -n "$REQUESTED_PYTHON" ]; then
        if [ ! -x "$REQUESTED_PYTHON" ]; then
            return 1
        fi
        if python_version_ok "$REQUESTED_PYTHON" && \
                python_gui_ok "$REQUESTED_PYTHON"; then
            printf '%s' "$REQUESTED_PYTHON"
            return 0
        fi
        return 1
    fi
    for candidate in /usr/bin/python3 /usr/bin/python \
            /usr/bin/python3.14 /usr/bin/python3.13 /usr/bin/python3.12 \
            /usr/bin/python3.11 /usr/bin/python3.10 python3 python; do
        if [ -x "$candidate" ] && python_version_ok "$candidate" && \
                python_gui_ok "$candidate"; then
            printf '%s' "$candidate"
            return 0
        fi
        if have "$candidate" && python_version_ok "$candidate" && \
                python_gui_ok "$candidate"; then
            printf '%s' "$candidate"
            return 0
        fi
    done
    return 1
}

ensure_any_python() {
    HELPER_PY="$(find_any_python || true)"
    if [ -n "$HELPER_PY" ]; then
        return
    fi
    detect_family
    if [ "$NO_DEPS" -eq 1 ]; then
        package_plan
        die "Python 3.10+ was not found. Install it, then retry. Example: $INSTALL_CMD_HINT"
    fi
    if [ "$INSTALL_DEPS" -ne 1 ] && \
            ! ask "Python 3.10+ is missing. Install the system dependencies now?"; then
        package_plan
        die "Install the dependencies and retry. Example: $INSTALL_CMD_HINT"
    fi
    install_packages
    HELPER_PY="$(find_any_python || true)"
    [ -n "$HELPER_PY" ] || die "Python is still missing after installing packages."
}

ensure_gui_deps() {
    BASE_PY="$(find_base_python || true)"
    if [ -n "$BASE_PY" ]; then
        return
    fi
    if [ -n "$REQUESTED_PYTHON" ]; then
        die "--python $REQUESTED_PYTHON cannot import GTK 3 + WebKitGTK (PyGObject). Install the GUI packages or pick another interpreter."
    fi
    detect_family
    if [ "$NO_DEPS" -eq 1 ]; then
        package_plan
        die "GTK 3 + WebKitGTK + PyGObject (and pygame for the system path) are missing. Install them, then retry with --no-deps. Example: $INSTALL_CMD_HINT"
    fi
    if [ "$INSTALL_DEPS" -ne 1 ] && \
            ! ask "GTK/WebKitGTK/PyGObject packages are missing. Install them now?"; then
        package_plan
        die "Install the dependencies and retry. Example: $INSTALL_CMD_HINT"
    fi
    install_packages
    BASE_PY="$(find_base_python || true)"
    [ -n "$BASE_PY" ] || die "PyGObject/GTK/WebKit still cannot be imported after installing packages."
}

desktop_escape() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//%/%%}"
    printf '%s' "$value"
}

sh_escape() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    value="${value//\$/\\\$}"
    value="${value//\`/\\\`}"
    printf '%s' "$value"
}

read_version_dir() {
    local dir="$1" meta="$TMP_DIR/version.env"
    : > "$meta"
    "$HELPER_PY" "$HELPER" version --dir "$dir" > "$meta" || \
        die "Could not read version.json from $dir."
    NEW_CODE=""; NEW_NAME=""
    while IFS='=' read -r key value; do
        case "$key" in
            CODE) NEW_CODE="$value" ;;
            NAME) NEW_NAME="$value" ;;
        esac
    done < "$meta"
    [ -n "$NEW_CODE" ] && [ -n "$NEW_NAME" ] || die "Invalid version.json."
}

fetch_release() {
    local meta="$TMP_DIR/release.env" installed_code="" installed_name=""
    if [ "$MODE" = "update" ]; then
        [ -f "$APP_DIR/version.json" ] || \
            die "Hathor is not installed under $ROOT. Run without --update for a fresh install."
        read_version_dir "$APP_DIR"
        installed_code="$NEW_CODE"
        installed_name="$NEW_NAME"
    fi
    : > "$meta"
    local args=(fetch --dest "$TMP_DIR" --installed-code "$installed_code" \
                --installed-name "$installed_name")
    [ -n "$WANT_VERSION" ] && args+=(--version "$WANT_VERSION")
    [ "$MODE" = "update" ] && args+=(--update)
    [ "$FORCE" -eq 1 ] && args+=(--force)
    if ! "$HELPER_PY" "$HELPER" "${args[@]}" > "$meta"; then
        die "Could not download the release."
    fi
    UP_TO_DATE=0; NEW_CODE=""; NEW_NAME=""; NEW_TAG=""
    while IFS='=' read -r key value; do
        case "$key" in
            CODE) NEW_CODE="$value" ;;
            NAME) NEW_NAME="$value" ;;
            TAG) NEW_TAG="$value" ;;
            UP_TO_DATE) UP_TO_DATE="$value" ;;
        esac
    done < "$meta"
    [ -n "$NEW_NAME" ] || die "Release metadata is incomplete."
    SRC_APP="$TMP_DIR/app-src"
    if [ "$UP_TO_DATE" = "1" ]; then
        say "Hathor $NEW_NAME is already installed (up to date)."
        exit 0
    fi
}

choose_runtime() {
    local py="$BASE_PY"
    if python_full_ok "$py"; then
        RUNTIME_PY="$py"
        return
    fi

    local pip_args=(-m pip install --disable-pip-version-check --no-input -r "$SRC_APP/requirements.txt")
    if [ -x "$VENV_DIR/bin/python" ]; then
        if "$VENV_DIR/bin/python" -c 'import sys' >/dev/null 2>&1; then
            say "Installing/checking Python dependencies in the private venv..."
            if "$VENV_DIR/bin/python" "${pip_args[@]}"; then
                if python_full_ok "$VENV_DIR/bin/python"; then
                    RUNTIME_PY="$VENV_DIR/bin/python"
                    return
                fi
            else
                warn "pip install failed; recreating the venv."
            fi
        fi
        rm -rf "$VENV_DIR"
    fi

    say "Creating a private venv (system GTK/WebKit bindings stay in use)..."
    rm -rf "$VENV_DIR"
    if ! "$py" -m venv --system-site-packages "$VENV_DIR"; then
        die "Could not create a venv. On Debian/Ubuntu install python3-venv and retry."
    fi
    say "Installing Python dependencies in the private venv..."
    if ! "$VENV_DIR/bin/python" "${pip_args[@]}"; then
        die "pip install failed. Check your network/PyPI access and retry."
    fi
    python_full_ok "$VENV_DIR/bin/python" || \
        die "The Python runtime is still missing required modules after pip install."
    RUNTIME_PY="$VENV_DIR/bin/python"
}

write_launcher() {
    local py main
    py="$(sh_escape "$RUNTIME_PY")"
    main="$(sh_escape "$APP_DIR/main.py")"
    mkdir -p "$PREFIX/bin"
    cat > "$LAUNCHER" <<EOF
#!/bin/sh
# Generated by Hathor install-linux.sh
export HATHOR_INSTALLER_MANAGED=1
export HATHOR_PACKAGE_MANAGED=1
export HATHOR_WEBVIEW_GUI=gtk
exec "$py" "$main" "\$@"
EOF
    chmod 755 "$LAUNCHER"
}

write_desktop_entry() {
    [ "$NO_DESKTOP" -eq 1 ] && return 0
    mkdir -p "$DESKTOP_DIR" "$ICON_DIR"
    local icon_src=""
    local candidate
    for candidate in "$SRC_APP/packaging-arch/hathor.png" \
            "$SRC_APP/packaging/arch/hathor.png" \
            "$SRC_APP/../packaging/arch/hathor.png"; do
        if [ -f "$candidate" ]; then
            icon_src="$candidate"
            break
        fi
    done
    if [ -n "$icon_src" ]; then
        cp -f "$icon_src" "$ICON_FILE"
    elif [ ! -f "$ICON_FILE" ]; then
        warn "App icon not found in the bundle; the menu entry will use a generic icon."
    fi
    local exec_path
    exec_path="$(desktop_escape "$LAUNCHER")"
    cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=Hathor
GenericName=Music Player
Comment=Play your local library, podcasts and YouTube downloads
Exec="$exec_path"
Icon=hathor
Terminal=false
Categories=AudioVideo;Audio;Player;
StartupNotify=true
StartupWMClass=Hathor
EOF
    chmod 644 "$DESKTOP_FILE"
    if have update-desktop-database; then
        update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
    fi
    if have gtk-update-icon-cache; then
        gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true
    fi
}

save_installer() {
    mkdir -p "$ROOT"
    if [ -n "$SCRIPT_FILE" ] && [ -f "$SCRIPT_FILE" ]; then
        cp -f "$SCRIPT_FILE" "$ROOT/install-linux.sh"
        chmod 755 "$ROOT/install-linux.sh"
        INSTALLER_SAVED=1
        return
    fi
    if [ -f "$SRC_APP/install-linux.sh" ]; then
        cp -f "$SRC_APP/install-linux.sh" "$ROOT/install-linux.sh"
        chmod 755 "$ROOT/install-linux.sh"
        INSTALLER_SAVED=1
        return
    fi
    local tmp="$TMP_DIR/install-linux.sh"
    if have curl && curl -fsSL "$SELF_URL" -o "$tmp" 2>/dev/null; then
        mv -f "$tmp" "$ROOT/install-linux.sh"
        chmod 755 "$ROOT/install-linux.sh"
        INSTALLER_SAVED=1
    elif have wget && wget -qO "$tmp" "$SELF_URL" 2>/dev/null; then
        mv -f "$tmp" "$ROOT/install-linux.sh"
        chmod 755 "$ROOT/install-linux.sh"
        INSTALLER_SAVED=1
    else
        warn "Could not save the installer for later updates. Re-run the one-liner to update."
    fi
}

install_app() {
    [ -f "$SRC_APP/main.py" ] || die "App source is missing main.py: $SRC_APP"
    [ -f "$SRC_APP/requirements.txt" ] || die "App source is missing requirements.txt."

    local stage="$ROOT/.app-new.$$" item
    rm -rf "$stage"
    mkdir -p "$stage"
    for item in $RUNTIME_ITEMS; do
        if [ ! -e "$SRC_APP/$item" ]; then
            rm -rf "$stage"
            die "Release bundle is missing $item."
        fi
        cp -a "$SRC_APP/$item" "$stage/"
    done
    : > "$stage/.installer-managed"
    if [ ! -f "$stage/ui/output.css" ]; then
        warn "ui/output.css is missing; the UI may be unstyled until Tailwind is built."
    fi

    read_version_dir "$SRC_APP"
    ensure_gui_deps
    choose_runtime

    local old="$ROOT/.app-old.$$"
    rm -rf "$old"
    if [ -d "$APP_DIR" ]; then
        mv "$APP_DIR" "$old"
    fi
    if ! mv "$stage" "$APP_DIR"; then
        [ -d "$old" ] && mv "$old" "$APP_DIR"
        die "Could not move the new app into place."
    fi
    rm -rf "$old"

    "$RUNTIME_PY" -m compileall -q "$APP_DIR" >/dev/null 2>&1 || \
        warn "Some Python files did not compile; the app may still work."

    write_launcher
    write_desktop_entry
    save_installer

    say ""
    say "Hathor $NEW_NAME is installed."
    say "  App:      $APP_DIR"
    say "  Runtime:  $RUNTIME_PY"
    say "  Launcher: $LAUNCHER"
    [ "$NO_DESKTOP" -eq 1 ] || say "  Menu:     $DESKTOP_FILE"
    [ -n "$NEW_TAG" ] && say "  Release:  $NEW_TAG"
    if [ "$INSTALLER_SAVED" -eq 1 ]; then
        say "  Update:   $ROOT/install-linux.sh --update"
        say "  Remove:   $ROOT/install-linux.sh --uninstall"
    else
        say "  Update:   re-run the install one-liner"
        say "  Remove:   re-run the one-liner with --uninstall"
    fi
    say "  Data:     ~/.local/share/hathor (unchanged XDG paths)"
    case ":$PATH:" in
        *":$PREFIX/bin:"*) ;;
        *) say ""
           say "Note: $PREFIX/bin is not in PATH; use the application menu or the full launcher path." ;;
    esac
}

uninstall_app() {
    local found=0
    if [ -f "$APP_DIR/.installer-managed" ] || [ -f "$ROOT/install-linux.sh" ]; then
        found=1
    fi
    if [ "$found" -eq 0 ]; then
        die "No installer-managed Hathor found under $ROOT. Nothing was removed."
    fi
    say "Removing the Hathor application from $ROOT..."
    if [ -f "$LAUNCHER" ] && grep -q "HATHOR_INSTALLER_MANAGED" "$LAUNCHER" 2>/dev/null; then
        rm -f "$LAUNCHER"
    fi
    [ -f "$DESKTOP_FILE" ] && rm -f "$DESKTOP_FILE"
    [ -f "$ICON_FILE" ] && rm -f "$ICON_FILE"
    rm -rf "$ROOT"
    if have update-desktop-database; then
        update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
    fi
    if [ "$PURGE" -eq 1 ]; then
        rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/hathor"
        rm -rf "${XDG_CONFIG_HOME:-$HOME/.config}/hathor"
        rm -rf "${XDG_STATE_HOME:-$HOME/.local/state}/hathor"
        rm -rf "${XDG_CACHE_HOME:-$HOME/.cache}/hathor"
        say "Removed app data and configuration. Music folders were not touched."
    else
        say "Application removed. User data was kept (use --purge to delete it)."
    fi
}

if [ "$MODE" = "uninstall" ]; then
    uninstall_app
    exit 0
fi

write_helper
ensure_any_python

case "$MODE" in
    zip)
        [ -f "$ZIP_PATH" ] || die "Zip not found: $ZIP_PATH"
        ZIP_PATH="$(cd "$(dirname "$ZIP_PATH")" && pwd)/$(basename "$ZIP_PATH")"
        say "Extracting $ZIP_PATH..."
        : > "$TMP_DIR/extract.env"
        if ! "$HELPER_PY" "$HELPER" extract --zip "$ZIP_PATH" --dest "$TMP_DIR" > "$TMP_DIR/extract.env"; then
            die "Could not extract the release zip."
        fi
        while IFS='=' read -r key value; do
            case "$key" in
                CODE) NEW_CODE="$value" ;;
                NAME) NEW_NAME="$value" ;;
            esac
        done < "$TMP_DIR/extract.env"
        SRC_APP="$TMP_DIR/app-src"
        ;;
    latest|version|update)
        fetch_release
        ;;
    source)
        read_version_dir "$SRC_APP"
        ;;
    *)
        die "Nothing to do."
        ;;
esac

install_app

#!/usr/bin/env bash
# Build the portable Linux zip used by GitHub Releases. This is a source/
# runtime bundle that runs with the system Python + GTK/WebKitGTK stack; the
# Arch PKGBUILD remains the preferred native install. The zip also contains
# install-linux.sh, the recommended one-command per-user installer.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
python="$(command -v python3 || command -v python)"
version="$("$python" -c 'import json,sys; print(json.load(open(sys.argv[1]))["versionName"])' "$root/version.json")"
version_code="$("$python" -c 'import json,sys; print(json.load(open(sys.argv[1]))["versionCode"])' "$root/version.json")"
out="$root/dist/hathor-linux-$version_code-$version.zip"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

mkdir -p "$stage/hathor"
for item in main.py api.py Download.py settings.py database.sql version.json requirements.txt services ui; do
  cp -a "$root/$item" "$stage/hathor/"
done
cp -a "$root/packaging/arch" "$stage/hathor/packaging-arch"
cp -a "$root/scripts/install-linux.sh" "$stage/hathor/install-linux.sh"
chmod 755 "$stage/hathor/install-linux.sh"
rm -f "$stage/hathor/packaging-arch"/*.tar.gz "$stage/hathor/packaging-arch"/*.pkg.tar.*
rm -rf "$stage/hathor/packaging-arch/src" "$stage/hathor/packaging-arch/pkg"
find "$stage/hathor" -type d -name __pycache__ -prune -exec rm -rf {} +
find "$stage/hathor" -type f -name '*.pyc' -delete

cat > "$stage/hathor/run.sh" <<'RUN'
#!/bin/sh
# Run without installing. Requires Python 3.10+ with GTK 3, WebKitGTK 4.x,
# PyGObject and the Python packages from requirements.txt. The installer
# (./install-linux.sh) sets all of this up automatically.
cd "$(dirname "$0")" || exit 1
export HATHOR_WEBVIEW_GUI=gtk
# The zip ships the compiled ui/output.css; don't require Tailwind/npx.
export HATHOR_SKIP_TAILWIND=1
for py in /usr/bin/python3 /usr/bin/python python3 python; do
    if command -v "$py" >/dev/null 2>&1 && "$py" - <<'PY' >/dev/null 2>&1
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
import webview  # noqa: F401
import pygame  # noqa: F401
PY
    then
        exec "$py" main.py "$@"
    fi
done
echo "No Python with GTK/WebKitGTK/pywebview/pygame found." >&2
echo "Run ./install-linux.sh (recommended) or see README-LINUX.txt." >&2
exit 1
RUN
chmod +x "$stage/hathor/run.sh"

cat > "$stage/hathor/README-LINUX.txt" <<'README'
Hathor Linux bundle
===================

Recommended install (per-user, adds an application-menu entry):

    ./install-linux.sh

Or from the GitHub release without extracting:

    curl -fsSL https://raw.githubusercontent.com/gabrieljdsena/Hathor/develop/apps/desktop/scripts/install-linux.sh | bash

The installer needs Python 3.10+ plus GTK 3/WebKitGTK 4.x/PyGObject. It can
install the missing distro packages for you:

    ./install-linux.sh --install-deps

Update or remove an existing install:

    ~/.local/opt/hathor/install-linux.sh --update
    ~/.local/opt/hathor/install-linux.sh --uninstall

The app itself is installed under ~/.local/opt/hathor (no sudo). User data
uses the normal XDG paths:

  ~/.local/share/hathor/    (SQLite database)
  ~/.config/hathor/         (.env fallback)
  ~/.local/state/hathor/    (log)
  ~/.cache/hathor/          (updates/cache)
  ~/Music/Hathor            (default songs folder)
  ~/Music/Hathor Podcasts   (default podcasts folder)

Quick run without installing:

    ./run.sh

This requires the system Python and the GTK/WebKitGTK packages. Web account
sign-in uses the same username/password as the web app. Only the refresh token
is stored, in Secret Service/KWallet via keyring. API keys remain available
under Settings -> Web API -> Advanced.

For a native Arch package, use packaging-arch/PKGBUILD.
README

mkdir -p "$root/dist"
rm -f "$out"
"$python" - "$stage" "$out" <<'PY'
import os
import sys
import zipfile

stage, out = sys.argv[1], sys.argv[2]
base = os.path.join(stage, "hathor")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for root, dirs, files in os.walk(base):
        dirs[:] = sorted(d for d in dirs if d != "__pycache__")
        for name in sorted(files):
            if name.endswith(".pyc"):
                continue
            path = os.path.join(root, name)
            archive.write(path, os.path.relpath(path, stage))
PY
echo "Linux bundle: $out"
sha256sum "$out" | awk '{print "SHA-256:   " $1}'

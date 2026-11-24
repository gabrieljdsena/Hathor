#!/usr/bin/env bash
# Build the portable Linux zip used by GitHub Releases. This is a source/
# runtime bundle that runs with the system Python + GTK/WebKitGTK stack; the
# Arch PKGBUILD remains the preferred native install. No node_modules or user
# data is included.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/.." && pwd)"
python="$(command -v python3 || command -v python)"
version="$("$python" -c 'import json,sys; print(json.load(open(sys.argv[1]))["versionName"])' "$root/version.json")"
out="$root/dist/hathor-linux-$version.zip"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

mkdir -p "$stage/hathor"
for item in main.py api.py Download.py settings.py database.sql version.json requirements.txt services ui; do
  cp -a "$root/$item" "$stage/hathor/"
done
cp -a "$root/packaging/arch" "$stage/hathor/packaging-arch"
rm -f "$stage/hathor/packaging-arch"/*.tar.gz "$stage/hathor/packaging-arch"/*.pkg.tar.*
rm -rf "$stage/hathor/packaging-arch/src" "$stage/hathor/packaging-arch/pkg"
find "$stage/hathor" -type d -name __pycache__ -prune -exec rm -rf {} +
find "$stage/hathor" -type f -name '*.pyc' -delete

cat > "$stage/hathor/run.sh" <<'RUN'
#!/bin/sh
# Run from the extracted folder. Required on Arch:
#   python python-pywebview python-gobject webkit2gtk-4.1 python-pygame
#   python-mutagen python-pillow python-psutil python-pykakasi
#   python-dotenv python-keyring yt-dlp ffmpeg
cd "$(dirname "$0")"
exec python3 main.py "$@"
RUN
chmod +x "$stage/hathor/run.sh"

cat > "$stage/hathor/README-LINUX.txt" <<'README'
Hathor Linux bundle
===================

This zip contains the Python/UI runtime, not a self-contained WebKit bundle.
Install the Arch dependencies listed in run.sh, then:

    ./run.sh

User data is stored under XDG paths:
  ~/.local/share/hathor/
  ~/.config/hathor/
  ~/.local/state/hathor/
  ~/.cache/hathor/

Web account sign-in uses the same username/password as the web app. Only the
refresh token is stored, in Secret Service/KWallet via keyring. API keys remain
available under Settings -> Web API -> Advanced.

For a native package, use packaging-arch/PKGBUILD.
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

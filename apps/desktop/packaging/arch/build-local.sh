#!/usr/bin/env bash
# Make a source tarball from this working tree, then let makepkg build it.
# Run on Arch (or in an Arch container/VM); PyInstaller is not involved.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
version="$(python -c 'import json,sys; print(json.load(open(sys.argv[1]))["versionName"])' "$repo/apps/desktop/version.json")"
pkgver="${version%%-*}"
package_version="$(sed -n 's/^pkgver=//p' "$here/PKGBUILD")"
if [[ "$package_version" != "$pkgver" ]]; then
  echo "version.json is $pkgver but PKGBUILD has $package_version; update PKGBUILD first." >&2
  exit 1
fi

tarball="$here/hathor-$pkgver.tar.gz"
rm -f "$tarball"
tar -C "$repo" \
  --exclude='apps/desktop/node_modules' \
  --exclude='apps/desktop/dist' \
  --exclude='apps/desktop/build' \
  --exclude='apps/desktop/.env' \
  --exclude='apps/desktop/music_player.db' \
  --exclude='apps/desktop/hathor.log' \
  --exclude='apps/desktop/__pycache__' \
  --exclude='apps/desktop/.pytest_cache' \
  --exclude='apps/desktop/packaging/arch/*.tar.gz' \
  -czf "$tarball" apps/desktop

cd "$here"
exec makepkg -si

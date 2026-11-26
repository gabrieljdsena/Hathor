# Arch package

This is the primary Linux distribution path: a normal Arch package backed by
system Python, GTK 3 and WebKitGTK 4.1. It does not use PyInstaller.

```bash
sudo pacman -S --needed base-devel
cd apps/desktop/packaging/arch
./build-local.sh
```

`build-local.sh` creates a source tarball from the current `apps/desktop`
working tree, updates `pkgver` from `version.json`, and runs `makepkg -si`.

For an AUR/release package, replace the local source with a tagged release
tarball and a real `sha256sums` entry.

The app installs to:

```text
/usr/bin/hathor
/usr/lib/hathor/
/usr/share/applications/hathor.desktop
/usr/share/icons/hicolor/512x512/apps/hathor.png
```

The launcher sets `HATHOR_PACKAGE_MANAGED=1`. The app then writes database,
logs and configuration under `~/.local/share/hathor`,
`~/.local/state/hathor` and `~/.config/hathor`, and reports that updates are
managed by pacman instead of using the Windows self-updater.

Runtime dependencies are system packages, so WebKit security updates arrive
with `pacman -Syu`. The installed tree is never modified by the app.

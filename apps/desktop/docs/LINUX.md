# Linux notes

Arch Linux is the first-class Linux target. The app uses system Python and
GTK 3/WebKitGTK 4.1; PyInstaller is only the Windows portable build.

## Dependencies (Arch)

```bash
sudo pacman -S python python-pywebview python-gobject webkit2gtk-4.1 \
  python-pygame python-mutagen python-pillow python-psutil \
  python-pykakasi python-dotenv yt-dlp ffmpeg
```

Run from the source tree:

```bash
cd apps/desktop
python main.py
```

`pywebview` is forced to the GTK backend on Linux, even on KDE where it would
otherwise prefer Qt. The window uses native server-side decorations; the
custom frameless titlebar and Aero Snap code remain Windows-only.

## XDG paths

| Data | Path |
| --- | --- |
| SQLite DB | `~/.local/share/hathor/music_player.db` |
| `.env` fallback | `~/.config/hathor/.env` |
| Log | `~/.local/state/hathor/hathor.log` |
| Cache/updates | `~/.cache/hathor/` |
| Default songs | `~/Music/Hathor` |
| Default podcasts | `~/Music/Hathor Podcasts` |

Override with `HATHOR_DATA_DIR`, `HATHOR_CONFIG_DIR`, `HATHOR_CACHE_DIR`, and
`HATHOR_STATE_DIR`. `HATHOR_PORTABLE=1` restores the old "everything next to
the app" layout.

The first Linux launch copies a legacy `music_player.db`/`.env` from the
application directory into the XDG locations if one exists.

## Package vs self-update

The Arch launcher sets `HATHOR_PACKAGE_MANAGED=1` (and the package also installs
a `.package-managed` marker). In that mode:

- the Windows zip/PowerShell updater is disabled
- `Download.py` and `startup_maintenance.py` do not run `pip install`
- FFmpeg and yt-dlp are updated with `pacman -Syu`
- **Settings → Application** reports that the system package manager owns updates

Build/install:

```bash
sudo pacman -S --needed base-devel
cd apps/desktop/packaging/arch
./build-local.sh
```

## WSL2 / WSLg

The GTK app can run inside WSLg, but WSL2 requires the Virtual Machine Platform
and virtualization enabled in firmware. Check with:

```powershell
wsl --status
```

A Linux distribution must be installed (`wsl --install -d Ubuntu`) before
running the app. WSL is useful for bridge/package smoke tests; a real Arch VM
or Arch host is still the right place to build and validate the PKGBUILD.

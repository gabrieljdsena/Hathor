# Linux notes

Arch Linux is the first-class Linux target. The app uses system Python and
GTK 3/WebKitGTK 4.1; PyInstaller is only the Windows portable build.

## Install from a GitHub release (recommended)

The release installer is a single per-user install under `~/.local`:

```bash
curl -fsSL https://raw.githubusercontent.com/gabrieljdsena/Hathor/develop/apps/desktop/scripts/install-linux.sh | bash
```

It downloads the newest `desktop-v*` release, verifies the SHA-256 from
`SHA256SUMS.txt`, and writes:

```text
~/.local/opt/hathor/app             runtime files
~/.local/opt/hathor/venv            private venv (only if system Python misses deps)
~/.local/opt/hathor/install-linux.sh
~/.local/bin/hathor                 launcher
~/.local/share/applications/hathor.desktop
~/.local/share/icons/hicolor/512x512/apps/hathor.png
```

The installer prefers Arch's system Python (`/usr/bin/python3`) and uses the
system GTK/WebKitGTK/PyGObject packages. On distros without the Python modules
it creates a venv with `--system-site-packages` and pip-installs the missing
packages, so PEP 668 never blocks the install.

```bash
# install missing GTK/WebKit/Python packages with the detected package manager
curl ... | bash -s -- --install-deps

# update / remove an existing install
~/.local/opt/hathor/install-linux.sh --update
~/.local/opt/hathor/install-linux.sh --uninstall
~/.local/opt/hathor/install-linux.sh --uninstall --purge   # also delete app data

# offline release zip
./install-linux.sh --zip ~/Downloads/hathor-linux-*.zip
```

Script installs set `HATHOR_INSTALLER_MANAGED=1`; Settings reports that the
installer owns updates instead of the Windows self-updater.

## Dependencies (Arch)

```bash
sudo pacman -S python python-pywebview python-gobject webkit2gtk-4.1 \
  python-pygame python-mutagen python-pillow python-psutil \
  python-pykakasi python-dotenv python-keyring yt-dlp ffmpeg
```

Web account sign-in stores only the refresh token, using Secret Service
(GNOME Keyring/KWallet). The password is never written to disk.

Run from the source tree:

```bash
cd apps/desktop
python main.py
```

The source bundle in each GitHub release also contains `run.sh`, which picks a
Python that can import GTK/WebKitGTK instead of blindly using PATH.

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

import os
import shutil
import sys
import subprocess
from dotenv import load_dotenv

from services import platform_support

# First match wins (python-dotenv default): cwd, user config, bundle, exe dir.
for _env_file in platform_support.env_files():
    load_dotenv(_env_file)

_runtime_dirs = platform_support.ensure_runtime_dirs()
_migration = platform_support.migrate_legacy_data()

if (not platform_support.is_frozen()
        and not platform_support.package_managed()
        and shutil.which("npx")):
    print(" [Dev] Compiling Tailwind CSS...")
    current_dir = str(platform_support.app_root())
    input_css = os.path.join(current_dir, 'ui', 'input.css')
    output_css = os.path.join(current_dir, 'ui', 'output.css')
    subprocess.run(f"npx @tailwindcss/cli -i \"{input_css}\" -o \"{output_css}\"", shell=True)

import webview
import threading
import sqlite3
import json
import logging
import pygame
#import monitor
import settings
from api import Api


# Persistent file log (XDG state on Linux, next to the exe on Windows).
def _setup_file_logging():
    try:
        log_file = platform_support.log_path()
        log_file.parent.mkdir(parents=True, exist_ok=True)
        logging.basicConfig(
            level=logging.INFO,
            format='%(asctime)s [%(levelname)s] %(message)s',
            handlers=[
                logging.FileHandler(log_file, encoding='utf-8'),
                logging.StreamHandler(sys.stdout),
            ],
            force=True,
        )
    except Exception as e:
        print(f" [Python] Could not set up file logging: {e}")

_setup_file_logging()
_log = logging.getLogger('hathor')
_log.info("=== Hathor starting ===")
_log.info("platform=%s package_managed=%s update_mode=%s",
          platform_support.platform_key(), platform_support.package_managed(),
          platform_support.update_mode())
for _name, _path in _runtime_dirs.items():
    if str(_path).startswith("error:"):
        _log.warning("Could not create %s dir: %s", _name, _path)
if _migration.get("migrated_db"):
    _log.info("Migrated legacy database from %s", _migration["migrated_db"])
if _migration.get("migrated_env"):
    _log.info("Migrated legacy .env from %s", _migration["migrated_env"])


def _log_uncaught(exc_type, exc_value, exc_tb):
    try:
        _log.error("Uncaught exception", exc_info=(exc_type, exc_value, exc_tb))
    except Exception:
        pass

sys.excepthook = _log_uncaught

if hasattr(threading, 'excepthook'):
    _orig_thread_excepthook = threading.excepthook

    def _log_thread_uncaught(args):
        try:
            _log.error(
                "Uncaught thread exception in %s", getattr(args, 'thread', None),
                exc_info=(args.exc_type, args.exc_value, args.exc_traceback),
            )
        except Exception:
            pass
        try:
            _orig_thread_excepthook(args)
        except Exception:
            pass

    threading.excepthook = _log_thread_uncaught


# 1. Setup paths
def get_base_path():
    return str(platform_support.app_root())

def get_data_path():
    return str(platform_support.data_dir())

current_dir = get_base_path()
data_dir = get_data_path()
db_path = str(platform_support.db_path())
html_file = os.path.join(current_dir, 'ui', 'index.html')

# Detect first run: DB file doesn't exist yet
is_first_run = not os.path.exists(db_path)

#Initialize SQLITE DB
def init_db():
    sql_file = os.path.join(current_dir, 'database.sql')
    
    if os.path.exists(sql_file):
        with open(sql_file, 'r', encoding='utf-8') as f:
            sql_script = f.read()
        try:
            with sqlite3.connect(db_path) as conn:
                conn.executescript(sql_script)
                print(" [Python] Database initialized successfully.")
        except Exception as e:
            print(f" [Python] Database initialization error: {e}")

init_db()
if is_first_run:
    print(" [Python] First run detected (database was just created).")

# 2. Instantiate API first
api = Api(db_path)

# Calculate center position based on the primary screen
try:
    screen = webview.screens[0]
    center_x = (screen.width - settings.window_size['width']) // 2
    center_y = (screen.height - settings.window_size['height']) // 2
except Exception:
    center_x, center_y = None, None

# 3. Create window and pass the api instance
window_caps = platform_support.window_capabilities()
window = webview.create_window(
    title='Hathor',
    url=html_file,
    js_api=api,
    width=settings.window_size['width'],
    height=settings.window_size['height'],
    x=center_x,
    y=center_y,
    min_size=(895, 400),
    # Windows uses the custom titlebar; Linux uses native GTK decorations.
    frameless=window_caps['frameless'],
    background_color='#09090b',
    # The app implements its own titlebar drag (pointer events + move_window).
    # pywebview's native easy_drag would make EVERY mousedown in the app move
    # the window (and fight with sliders/seekbars), so it must stay off.
    easy_drag=False
)

# 4. Link the window back to the API so it can use evaluate_js
api._window = window

resize_timer = None

def save_window_size(width, height):
    try:
        with sqlite3.connect(db_path) as conn:
            conn.execute("UPDATE Settings SET window_width = ?, window_height = ?", (width, height))
    except Exception as e:
        print(f" [Python] Database error: {e}")

def on_resized(width, height):
    global resize_timer
    # Don't persist the maximized footprint or a snapped half/quarter-screen
    # size as the restored window size.
    try:
        if not api.should_persist_window_size():
            return
    except Exception:
        pass
    if resize_timer is not None:
        resize_timer.cancel()
    resize_timer = threading.Timer(0.4, save_window_size, args=(width, height))
    resize_timer.start()

# 5. Remote sync is manual-only (Settings buttons). No background thread.
# `remote_configured` gates the first-run pull prompt; pushes/pulls otherwise
# happen exclusively via Api.sync_local_to_remote /
# Api.sync_remote_to_local_and_download.
from services.api_sync import sync_backend as _sync_backend
remote_configured = _sync_backend() == "api"
if not remote_configured:
    print(" [Sync] No sync server configured in Settings -> Web API or .env, sync disabled.")

def _run_startup_maintenance(window):
    """Background FFmpeg auto-download + YouTube-lib update checks.

    Non-blocking: pushes progress to the startup toast in the UI so the
    user can keep using the app while it works.
    """
    import time as _time
    import json as _json
    try:
        from services import startup_maintenance as _maint
    except Exception as e:
        print(f" [Startup] maintenance module unavailable: {e}")
        return

    # Give the webview DOM a moment to define window.startupToast.
    _time.sleep(1.0)

    def safe_js(code):
        try:
            window.evaluate_js(code)
        except Exception:
            pass

    def push(event):
        try:
            safe_js(
                "if (window.startupToast && window.startupToast.update) "
                f"window.startupToast.update({_json.dumps(event)});"
            )
        except Exception:
            pass

    # Only show the popup when there is actual work to do, otherwise stay
    # out of the way. A quick synchronous pre-check decides that.
    try:
        ffmpeg_ok = _maint.find_ffmpeg().get('found', False)
    except Exception:
        ffmpeg_ok = True
    try:
        lib_status = _maint.check_libraries_status()
        libs_ok = all(r.get('status') == 'up-to-date' for r in lib_status) if lib_status else True
    except Exception:
        libs_ok = True

    if ffmpeg_ok and libs_ok:
        print(" [Startup] FFmpeg + libraries up to date, no popup needed.")
        return

    safe_js(
        "if (window.startupToast && window.startupToast.show) "
        "window.startupToast.show('Checking for updates…');"
    )
    try:
        summary = _maint.run_startup_checks(push)
        print(f" [Startup] summary: ffmpeg={summary.get('ffmpeg', {}).get('status')} "
              f"libs={[ (r.get('package'), r.get('status')) for r in summary.get('libraries', []) ]}")
        safe_js(
            "if (window.startupToast && window.startupToast.complete) "
            f"window.startupToast.complete({_json.dumps(summary)});"
        )
    except Exception as e:
        print(f" [Startup] maintenance failed: {e}")
        safe_js(
            "if (window.startupToast && window.startupToast.update) "
            f"window.startupToast.update({_json.dumps({'stage': 'done', 'label': 'Startup check failed', 'percent': 100, 'detail': str(e)[:160], 'status': 'error'})});"
        )


def _enable_windows_snap_when_ready(window):
    """Apply native resize/snap styles once the Windows form is created.

    pywebview runs ``on_start`` on a worker thread before the native form is
    created, so ``window.native`` is still None at that point.
    """
    if not platform_support.is_windows():
        return
    try:
        if not window.events.shown.wait(15):
            print(" [Python] Window did not show; Windows snap not enabled.")
            return
        if not api.enable_windows_snap():
            print(" [Python] Windows snap was not enabled.")
    except Exception as e:
        print(f" [Python] Could not enable Windows snap: {e}")


def on_start(window):
    try:
        pygame.init()
        pygame.mixer.init()
    except Exception as e:
        # WSLg/headless sessions may have no audio device yet; the UI should
        # still open and report playback failures when a track is played.
        _log.warning("Audio initialization failed: %s", e)

    # pywebview invokes on_start before the native form exists, so wait for
    # the shown event before touching the HWND. Linux uses native decorations.
    if platform_support.is_windows():
        threading.Thread(
            target=_enable_windows_snap_when_ready, args=(window,), daemon=True
        ).start()

    # Startup maintenance (FFmpeg + yt-dlp updates) in the background.
    threading.Thread(target=_run_startup_maintenance, args=(window,), daemon=True).start()

    # Start the resource monitor in a background thread so it doesn't block the app
    #monitor_thread = threading.Thread(target=monitor.monitor_usage, daemon=True)
    #monitor_thread.start()

    # On first run with a sync server, ask the user if they want to load from it
    if is_first_run and remote_configured:
        _prompt_remote_sync(window)
    else:
        api.load_current_song()

def _prompt_remote_sync(window):
    """Show a SweetAlert2 dialog asking the user to load data from the sync server."""
    import time as _time
    
    # Wait for the webview DOM to be ready
    _time.sleep(1.5)
    
    js_code = """
    (async () => {
        try {
            const result = await Swal.fire({
                title: 'Sync Server Found',
                html: 'A sync server is configured.<br>Would you like to <b>load your music library</b> from it?<br><br><small>This will sync songs, playlists, lyrics, and history, then download any missing songs.</small>',
                icon: 'question',
                showCancelButton: true,
                confirmButtonText: 'Yes, load from remote',
                cancelButtonText: 'No, start fresh',
                allowOutsideClick: false,
                allowEscapeKey: false,
                customClass: { popup: 'swal-dark' }
            });
            return result.isConfirmed ? 'yes' : 'no';
        } catch (e) {
            return 'no';
        }
    })()
    """
    
    def do_prompt():
        try:
            answer = window.evaluate_js(js_code)
            if answer == 'yes':
                print(" [Python] User chose to load from sync server.")
                # Show loading indicator
                window.evaluate_js("""
                    Swal.fire({
                        title: 'Syncing from Server...',
                        html: 'Downloading your library data and queuing song downloads.',
                        allowOutsideClick: false,
                        allowEscapeKey: false,
                        didOpen: () => { Swal.showLoading(); },
                        customClass: { popup: 'swal-dark' }
                    });
                """)
                result = api.sync_remote_to_local_and_download()
                print(f" [Python] Remote sync result: {result}")
                window.evaluate_js(f"""
                    Swal.fire({{  
                        title: 'Sync Complete',
                        text: {json.dumps(str(result))},
                        icon: 'success',
                        timer: 3000,
                        showConfirmButton: false,
                        customClass: {{ popup: 'swal-dark' }}
                    }});
                """)
            else:
                print(" [Python] User chose to start fresh.")
            
            # Load songs regardless of choice (sync stays manual-only)
            api.load_current_song()
                 
        except Exception as e:
            print(f" [Python] Remote sync prompt error: {e}")
            api.load_current_song()
    
    threading.Thread(target=do_prompt, daemon=True).start()

def on_closing():
    """Flush playlist metadata held back because pygame owned the file."""
    try:
        pygame.mixer.music.stop()
        if hasattr(pygame.mixer.music, 'unload'):
            pygame.mixer.music.unload()
    except Exception:
        pass
    try:
        api.playing = False
        api.current_filename = None
        api.last_song = {}
    except Exception:
        pass
    try:
        if getattr(api, 'metadata', None):
            api.metadata.apply_pending_edits()
    except Exception as e:
        print(f" [Python] Pending metadata flush on close failed: {e}")
    try:
        if getattr(api, 'telemetry', None):
            api.telemetry.flush(wait=True)
    except Exception as e:
        print(f" [Python] Playback telemetry flush on close failed: {e}")
    try:
        if getattr(api, 'history_sync', None):
            api.history_sync.close()
    except Exception as e:
        print(f" [Python] History sync shutdown failed: {e}")


if __name__ == '__main__':
    window.events.resized += on_resized
    if hasattr(window.events, 'closing'):
        window.events.closing += on_closing
    webview.start(on_start, window, debug=False, gui=platform_support.webview_gui())
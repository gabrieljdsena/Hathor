import base64
import ctypes
import os
import sqlite3
import json
import logging
import platform
import pygame
import uuid
import Download
import settings
from ctypes import wintypes
from pathlib import Path
import random
import time

from services import (
    PlaybackController,
    MetadataManager,
    DatabaseManager,
    WindowsMediaOverlay,
    LyricsService,
    AppleService,
    DownloadManager,
    startup_maintenance
)

# ==========================================================================
# Native Windows window chrome
# ==========================================================================
# pywebview creates a frameless window with FormBorderStyle.None, which
# removes WS_THICKFRAME. Windows refuses to Aero-Snap (drag to edge/corner,
# snap layouts) a window without that style, even when the drag itself is
# started natively with WM_NCLBUTTONDOWN/HTCAPTION. Adding the style normally
# also paints a 7px native frame, so WM_NCCALCSIZE is answered with "client
# area = whole window" and maximized bounds are clamped to the work area.
_GWL_STYLE = -16
_GWLP_WNDPROC = -4
_WS_THICKFRAME = 0x00040000
_WS_MAXIMIZEBOX = 0x00010000
_WS_MINIMIZEBOX = 0x00020000
_WS_SYSMENU = 0x00080000
_WM_NCCALCSIZE = 0x0083
_WM_GETMINMAXINFO = 0x0024
_WM_NCLBUTTONDOWN = 0x00A1
_HTCAPTION = 2
_SWP_NOSIZE = 0x0001
_SWP_NOMOVE = 0x0002
_SWP_NOZORDER = 0x0004
_SWP_FRAMECHANGED = 0x0020
_MONITOR_DEFAULTTONEAREST = 2


class _RECT(ctypes.Structure):
    _fields_ = [
        ('left', ctypes.c_long), ('top', ctypes.c_long),
        ('right', ctypes.c_long), ('bottom', ctypes.c_long),
    ]


class _POINT(ctypes.Structure):
    _fields_ = [('x', ctypes.c_long), ('y', ctypes.c_long)]


class _MINMAXINFO(ctypes.Structure):
    _fields_ = [
        ('ptReserved', _POINT), ('ptMaxSize', _POINT),
        ('ptMaxPosition', _POINT), ('ptMinTrackSize', _POINT),
        ('ptMaxTrackSize', _POINT),
    ]


class _MONITORINFO(ctypes.Structure):
    _fields_ = [
        ('cbSize', wintypes.DWORD), ('rcMonitor', _RECT),
        ('rcWork', _RECT), ('dwFlags', wintypes.DWORD),
    ]


_WNDPROC = ctypes.WINFUNCTYPE(
    ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT,
    wintypes.WPARAM, wintypes.LPARAM)


def _win_user32():
    """Configure 64-bit-safe prototypes once and return user32."""
    user32 = ctypes.windll.user32
    user32.GetWindowLongW.restype = ctypes.c_long
    user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
    user32.SetWindowLongW.restype = ctypes.c_long
    user32.SetWindowLongW.argtypes = [
        wintypes.HWND, ctypes.c_int, ctypes.c_long]
    user32.GetWindowLongPtrW.restype = ctypes.c_void_p
    user32.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
    user32.SetWindowLongPtrW.restype = ctypes.c_void_p
    user32.SetWindowLongPtrW.argtypes = [
        wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
    user32.CallWindowProcW.restype = ctypes.c_ssize_t
    user32.CallWindowProcW.argtypes = [
        ctypes.c_void_p, wintypes.HWND, wintypes.UINT,
        wintypes.WPARAM, wintypes.LPARAM]
    user32.SetWindowPos.restype = wintypes.BOOL
    user32.SetWindowPos.argtypes = [
        wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
        ctypes.c_int, ctypes.c_int, wintypes.UINT]
    user32.MonitorFromWindow.restype = wintypes.HMONITOR
    user32.MonitorFromWindow.argtypes = [wintypes.HWND, wintypes.DWORD]
    user32.GetMonitorInfoW.restype = wintypes.BOOL
    user32.GetMonitorInfoW.argtypes = [
        wintypes.HMONITOR, ctypes.POINTER(_MONITORINFO)]
    user32.ReleaseCapture.restype = wintypes.BOOL
    user32.ReleaseCapture.argtypes = []
    user32.SendMessageW.restype = ctypes.c_ssize_t
    user32.SendMessageW.argtypes = [
        wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    return user32

class Api:
    def __init__(self, db_path):
        self.db_path = db_path
        
        # Public state variables accessed by JS or other modules
        self.playing = False
        self.first_play = True
        self.last_song = {'File': None, 'Artist': "", 'Title': "", 'Album': "", 'Year': "", 'Duration': 0, 'CoverArt': None}
        self.current_filename = None
        self.shuffle = False
        self.repeat = False
        self.song_list = []
        # pywebview 6.x `window.state` is a State dict (never the string
        # 'maximized'), so the maximized flag is tracked here instead.
        self._is_maximized = False
        
        self._window = None
        self._snap_state = None
        self._last_native_drag = 0.0
        self.downloader = Download.MusicDownloader()
        self._ensure_schema()
        # Stable identity for cross-device resume (generated once, persisted).
        self._device_id, self._device_label = self._ensure_device_identity()
        self._resume_spot_last = None
        self._resume_spot_next_at = 0.0

        # Initialize services
        self.playback = PlaybackController(self)
        self.metadata = MetadataManager(self)
        self.db = DatabaseManager(self, self.db_path)
        self.media_controls = WindowsMediaOverlay(self)
        self.lyrics = LyricsService(self)
        self.apple = AppleService(self)
        self.download_manager = DownloadManager(self)

    def _ensure_schema(self):
        """Add columns/tables introduced after the database was first created."""
        try:
            with sqlite3.connect(self.db_path) as conn:
                for column, ddl in {
                    'queue_songs': 'TEXT',
                    'custom_queue': 'INTEGER DEFAULT 0',
                    'queue_source': 'TEXT',
                    'podcasts_path': 'TEXT',
                    'crossfade_enabled': 'INTEGER DEFAULT 0',
                    'crossfade_seconds': 'REAL DEFAULT 5',
                    'chapter_skip': 'INTEGER DEFAULT 0',
                    'normalize_volume': 'INTEGER DEFAULT 1',
                    'device_id': 'TEXT',
                    'device_label': 'TEXT',
                }.items():
                    try:
                        conn.execute(f"ALTER TABLE Settings ADD COLUMN {column} {ddl}")
                    except sqlite3.OperationalError:
                        pass
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS Download_Queue (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        qid TEXT UNIQUE,
                        url TEXT,
                        title TEXT,
                        artist TEXT,
                        status TEXT DEFAULT 'queued',
                        progress REAL DEFAULT 0,
                        error TEXT,
                        filename TEXT,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    )
                """)
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS Daily_Mix (
                        mix_date varchar(10) PRIMARY KEY,
                        song_files text not null,
                        created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    )
                """)
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcasts (
                        file varchar(255) PRIMARY KEY,
                        downloaded_link varchar(255),
                        title varchar(255) NOT NULL,
                        date_download DATETIME DEFAULT CURRENT_TIMESTAMP,
                        artist varchar(255)
                    )
                """)
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcast_Tags (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        name varchar(255) NOT NULL UNIQUE
                    )
                """)
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcast_Tag_Links (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        podcast_file varchar(255) NOT NULL,
                        tag_id bigint NOT NULL
                    )
                """)
                conn.execute("""
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_podcast_tag_links_pair
                    ON Podcast_Tag_Links(podcast_file, tag_id)
                """)
                conn.execute("""
                    CREATE TABLE IF NOT EXISTS Podcast_Chapters (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        podcast_file varchar(255) NOT NULL,
                        name varchar(255) NOT NULL,
                        start_secs real NOT NULL,
                        end_secs real NULL
                    )
                """)
                conn.execute("""
                    CREATE INDEX IF NOT EXISTS idx_podcast_chapters_file
                    ON Podcast_Chapters(podcast_file, start_secs)
                """)
                try:
                    conn.execute("ALTER TABLE Lyrics ADD COLUMN offset_ms INTEGER DEFAULT 0")
                except sqlite3.OperationalError:
                    pass
                try:
                    conn.execute("ALTER TABLE Download_Queue ADD COLUMN is_podcast INTEGER DEFAULT 0")
                except sqlite3.OperationalError:
                    pass
        except Exception as e:
            print(f" [Python] Schema migration error: {e}")

    def _ensure_device_identity(self):
        """Stable device id/label for cross-device resume (write once)."""
        device_id = device_label = None
        try:
            with sqlite3.connect(self.db_path) as conn:
                row = conn.execute(
                    "SELECT device_id, device_label FROM Settings LIMIT 1"
                ).fetchone()
                if row:
                    device_id = (row[0] or "").strip() or None
                    device_label = (row[1] or "").strip() or None
        except Exception as e:
            print(f" [Python] Device identity read error: {e}")
        if device_id and device_label:
            return device_id, device_label

        if not device_id:
            device_id = str(uuid.uuid4())
        if not device_label:
            try:
                system = (platform.system() or "Desktop").strip()
            except Exception:
                system = "Desktop"
            try:
                host = (platform.node() or os.getenv("COMPUTERNAME") or "").strip()
            except Exception:
                host = ""
            device_label = (f"{system} - {host}" if host else system)[:64]

        try:
            with sqlite3.connect(self.db_path) as conn:
                cur = conn.execute(
                    "UPDATE Settings SET device_id = ?, device_label = ?",
                    (device_id, device_label),
                )
                if cur.rowcount == 0:
                    conn.execute(
                        "INSERT INTO Settings (id, device_id, device_label) "
                        "VALUES (1, ?, ?)",
                        (device_id, device_label),
                    )
        except Exception as e:
            print(f" [Python] Device identity save error: {e}")
        return device_id, device_label

    # ==========================
    # Playback & Queue Wrappers
    # ==========================
    def populate_queue(self, current_song):
        return self.playback.populate_queue(current_song)

    def populate_queue_from_list(self, current_song, song_list, playlist_id=None, source=None):
        return self.playback.populate_queue_from_list(current_song, song_list, playlist_id, source)
            
    def jump_to_queue_index(self, index):
        return self.playback.jump_to_queue_index(index)

    def add_to_queue(self, song_data):
        return self.playback.add_to_queue(song_data)

    def next_to_queue(self, song_data):
        return self.playback.next_to_queue(song_data)

    def clear_queue(self):
        return self.playback.clear_queue()
        
    def remove_from_queue(self, index):
        return self.playback.remove_from_queue(index)
            
    def reorder_queue(self, old_index, new_index):
        return self.playback.reorder_queue(old_index, new_index)
    
    def toggle_shuffle(self):
        return self.playback.toggle_shuffle()

    def chapter_next(self):
        """Seek to the next podcast chapter; False when none applies."""
        return self.playback.chapter_seek(1)

    def chapter_prev(self):
        """Seek to the previous podcast chapter; False when none applies."""
        return self.playback.chapter_seek(-1)

    def play_button(self, current_song=None, opening=False):
        return self.playback.play_button(current_song, opening)

    # ==========================
    # Cross-device resume (server playback spots)
    # ==========================
    def push_resume_spot(self, file=None, position_secs=None, is_podcast=None):
        """Upsert this device's paused spot (best effort, never blocks).

        Only paused state is meaningful for resume: pushing live positions
        would clobber a genuine paused spot on another device.
        """
        try:
            if getattr(self, 'playing', False):
                return False
            filename = file or self.current_filename
            if not filename:
                return False
            if position_secs is None:
                position_secs = self.playback.get_current_pos()
            if is_podcast is None:
                is_podcast = bool(
                    (getattr(self, 'last_song', None) or {}).get('IsPodcast'))
            from services.api_sync import ApiSyncClient
            client = ApiSyncClient(self.db_path, timeout=8)
            client.push_playback(
                getattr(self, '_device_id', None),
                getattr(self, '_device_label', None),
                filename, position_secs, is_podcast)
            return True
        except Exception as e:
            print(f" [Python] Resume push skipped: {e}")
            return False

    def get_resume_spot(self):
        """Latest paused spot from another device, or None.

        Cached briefly so track changes and the 60s UI poll cannot hammer a
        slow/unreachable sync server; failures back off for a minute.
        """
        now = time.time()
        if now < getattr(self, '_resume_spot_next_at', 0.0):
            return getattr(self, '_resume_spot_last', None)

        spot = None
        try:
            from services.api_sync import ApiSyncClient
            client = ApiSyncClient(self.db_path, timeout=6)
            raw = client.get_latest_playback(getattr(self, '_device_id', None))
            if raw and raw.get('file'):
                own_id = getattr(self, '_device_id', None)
                if not (raw.get('userKey') and own_id
                        and raw.get('userKey') == own_id):
                    file = raw.get('file')
                    is_podcast = bool(raw.get('isPodcast'))
                    spot = {
                        'file': file,
                        'position_secs': max(
                            0.0, float(raw.get('positionSec') or 0)),
                        'is_podcast': is_podcast,
                        'device': raw.get('device') or '',
                        'updated_utc': raw.get('updatedUtc') or '',
                    }
                    # Friendly pill text: resolve the local title when we
                    # already have the file (the web does the same lookup).
                    try:
                        table = 'Podcasts' if is_podcast else 'Songs'
                        with sqlite3.connect(self.db_path) as conn:
                            row = conn.execute(
                                f"SELECT title, artist FROM {table} "
                                "WHERE file = ?",
                                (file,),
                            ).fetchone()
                        if row:
                            spot['title'] = row[0] or ''
                            spot['artist'] = row[1] or ''
                    except Exception:
                        pass
        except Exception:
            # Sync unconfigured/offline: resume is optional, never break UI.
            self._resume_spot_last = None
            self._resume_spot_next_at = time.time() + 60
            return None

        self._resume_spot_last = spot
        self._resume_spot_next_at = time.time() + (10 if spot else 20)
        return spot

    def resume_spot_play(self, file, position_secs, is_podcast=False):

        """Play a synced resume spot at its position. Returns True on success."""
        try:
            table = 'Podcasts' if is_podcast else 'Songs'
            with sqlite3.connect(self.db_path) as conn:
                row = conn.execute(
                    f"SELECT file, title, artist FROM {table} WHERE file = ?", (file,)
                ).fetchone()
            if not row:
                return False  # Not downloaded here: nothing to resume.
            song = {'File': row[0], 'Title': row[1], 'Artist': row[2],
                    'IsPodcast': bool(is_podcast)}
            self.playback.play_button(song)
            try:
                position = max(0.0, float(position_secs or 0))
            except (TypeError, ValueError):
                position = 0.0
            if position > 1.0:
                self.progress_slider_click(position)
            return True
        except Exception as e:
            print(f" [Python] Resume spot play failed: {str(e)}")
            return False

    def get_current_pos(self):
        return self.playback.get_current_pos()
    
    def toggle_repeat(self):
        self.repeat = not self.repeat
        return self.repeat

    def play_next(self, auto=False):
        return self.playback.play_next(auto)

    def play_prev(self):
        return self.playback.play_prev()

    def progress_slider_click(self, sec):
        try:
            self.playback._cancel_crossfade()
        except Exception:
            pass
        # Always (re)load through the music module: after a crossfade the
        # module may still hold the previous song.
        try:
            if self.current_filename:
                base = settings.podcasts_path if (self.last_song or {}).get('IsPodcast') else settings.path
                pygame.mixer.music.load(os.path.join(base, str(self.current_filename)))
        except Exception as e:
            print(f" [Python] Could not load song file for seek: {e}")
            return
        pygame.mixer.music.play(0, float(sec))
        self.playback.current_time_offset = float(sec)
        self.playback.last_play_time = time.time()
        if not self.playing:
            pygame.mixer.music.pause()
            self.playback.pause_time = float(sec)
            # Seek-while-paused moves this device's resume spot.
            try:
                self.playback._push_resume_spot()
            except Exception:
                pass

    # ==========================
    # Metadata & Files Wrappers
    # ==========================
    def get_song_metadata(self, file_path, file_name, include_cover=False):
        return self.metadata.get_song_metadata(file_path, file_name, include_cover)

    def get_cover_art_base64(self, file_name):
        return self.metadata.get_cover_art_base64(file_name)

    def get_local_image_base64(self, file_path):
        return self.metadata.get_local_image_base64(file_path)

    def delete_song(self, song_data):
        return self.metadata.delete_song(song_data)

    def update_song_metadata(self, song_data):
        return self.metadata.update_song_metadata(song_data)

    # ==========================
    # Lyrics Wrappers
    # ==========================
    def romanize_text(self, text, is_lrc=False):
        return self.lyrics.romanize_text(text, is_lrc)

    def get_lyrics(self, track_name, artist_name, album_name=None, duration_seconds=None):
        return self.lyrics.get_lyrics(track_name, artist_name, album_name, duration_seconds)

    def search_lyrics(self, track_name, artist_name, album_name=None, duration_seconds=None):
        return self.lyrics.search_lyrics(track_name, artist_name, album_name, duration_seconds)

    def save_lyrics(self, synced, plain, chapter_id=None):
        return self.lyrics.save_lyrics_for_current(synced, plain, chapter_id)

    def delete_lyrics(self, chapter_id=None):
        return self.lyrics.delete_lyrics_for_current(chapter_id)

    def get_active_chapter(self):
        """Active podcast chapter for the current track (chapter-aware lyrics)."""
        try:
            return self.lyrics._active_chapter(self.current_filename)
        except Exception:
            return None

    def get_artist_image(self, artist):
        return self.apple.get_artist_image(artist)

    def get_album_image(self, album, artist=None):
        return self.apple.get_album_image(album, artist)

    # ==========================
    # Frameless Window Controls
    # ==========================
    def get_window_geometry(self):
        if not self._window:
            return None
        return {
            "x": int(self._window.x or 0),
            "y": int(self._window.y or 0),
            "width": int(self._window.width or 0),
            "height": int(self._window.height or 0),
        }

    def move_window(self, x, y):
        if self._window:
            self._window.move(int(x), int(y))
        return True

    def resize_window(self, width, height, x=None, y=None):
        if not self._window:
            return False
        self._window.resize(int(width), int(height))
        if x is not None and y is not None:
            self._window.move(int(x), int(y))
        return True

    def _window_handle(self):
        """Return the native HWND for the pywebview form, if available."""
        if not self._window:
            return None
        native = getattr(self._window, "native", None)
        if native is None:
            return None
        handle = native.Handle
        try:
            return int(handle.ToInt64())
        except Exception:
            return int(handle.ToInt32())

    def enable_windows_snap(self):
        """Enable native resize styles while keeping the UI full-bleed.

        A frameless WinForms window (FormBorderStyle.None) has no
        WS_THICKFRAME, so Windows will not snap it. We add the style and
        answer WM_NCCALCSIZE with 0 so the client area still covers the whole
        window (no 7px native border), then clamp maximized bounds to the
        monitor work area.
        """
        if os.name != "nt":
            return False
        hwnd = self._window_handle()
        if not hwnd:
            return False
        if self._snap_state and self._snap_state.get("hwnd") == hwnd:
            return True
        log = logging.getLogger("hathor.window")
        try:
            user32 = _win_user32()
            old_proc = user32.GetWindowLongPtrW(hwnd, _GWLP_WNDPROC)

            def _wndproc(h, msg, wp, lp):
                if msg == _WM_GETMINMAXINFO:
                    # Re-assert prototypes in case another module configured
                    # user32 differently; GetMonitorInfoW fails silently with
                    # a stale prototype and the taskbar would be covered.
                    u = _win_user32()
                    result = u.CallWindowProcW(old_proc, h, msg, wp, lp)
                    info = ctypes.cast(
                        lp, ctypes.POINTER(_MINMAXINFO)).contents
                    monitor = u.MonitorFromWindow(
                        h, _MONITOR_DEFAULTTONEAREST)
                    mi = _MONITORINFO()
                    mi.cbSize = ctypes.sizeof(_MONITORINFO)
                    if monitor and u.GetMonitorInfoW(
                            monitor, ctypes.byref(mi)):
                        info.ptMaxPosition.x = (
                            mi.rcWork.left - mi.rcMonitor.left)
                        info.ptMaxPosition.y = (
                            mi.rcWork.top - mi.rcMonitor.top)
                        info.ptMaxSize.x = (
                            mi.rcWork.right - mi.rcWork.left)
                        info.ptMaxSize.y = (
                            mi.rcWork.bottom - mi.rcWork.top)
                    return result
                if msg == _WM_NCCALCSIZE and wp:
                    # Client rect = window rect. Without this, adding
                    # WS_THICKFRAME insets the webview by the frame size.
                    return 0
                return user32.CallWindowProcW(old_proc, h, msg, wp, lp)

            proc = _WNDPROC(_wndproc)
            user32.SetWindowLongPtrW(
                hwnd, _GWLP_WNDPROC, ctypes.cast(proc, ctypes.c_void_p))
            style = user32.GetWindowLongW(hwnd, _GWL_STYLE)
            style |= (_WS_THICKFRAME | _WS_MAXIMIZEBOX
                      | _WS_MINIMIZEBOX | _WS_SYSMENU)
            user32.SetWindowLongW(hwnd, _GWL_STYLE, style)
            user32.SetWindowPos(
                hwnd, None, 0, 0, 0, 0,
                _SWP_NOMOVE | _SWP_NOSIZE | _SWP_NOZORDER
                | _SWP_FRAMECHANGED)
            self._snap_state = {
                "hwnd": hwnd,
                "old_proc": old_proc,
                "proc": proc,
                "user32": user32,
            }
            log.info("Windows snap enabled (hwnd=%s)", hwnd)
            return True
        except Exception:
            log.exception("enable_windows_snap failed")
            return False

    def start_window_drag(self):
        """Start a native caption drag so Windows handles snap and magnetism.

        ``ReleaseCapture`` is thread-scoped, so the whole sequence must run on
        the WinForms UI thread that owns the webview's mouse capture. Running
        it on pywebview's worker thread leaves the webview holding capture and
        the drag loop exits immediately. ``Invoke`` blocks the worker thread
        until the drop, which is fine because JS does not await this call.
        """
        if os.name != "nt":
            return False
        hwnd = self._window_handle()
        native = getattr(self._window, "native", None) if self._window else None
        if not hwnd or native is None:
            return False

        def _drag():
            user32 = _win_user32()
            user32.ReleaseCapture()
            self._last_native_drag = time.monotonic()
            user32.SendMessageW(hwnd, _WM_NCLBUTTONDOWN, _HTCAPTION, 0)
            self._last_native_drag = time.monotonic()
            self._sync_native_state()

        try:
            from System import Action
            native.Invoke(Action(_drag))
            return True
        except Exception:
            logging.getLogger("hathor.window").exception(
                "start_window_drag: native invoke failed; trying direct")
        try:
            _drag()
            return True
        except Exception:
            logging.getLogger("hathor.window").exception(
                "start_window_drag failed")
            return False

    def _sync_native_state(self):
        """Refresh _is_maximized from the real WinForms window state."""
        try:
            native = self._window.native if self._window else None
            if native is not None:
                self._is_maximized = str(native.WindowState) == "Maximized"
        except Exception:
            pass
        return bool(getattr(self, "_is_maximized", False))

    def is_window_maximized(self):
        return self._sync_native_state()

    def should_persist_window_size(self):
        """Skip saving while maximized or right after a native snap drag."""
        if self.is_window_maximized():
            return False
        last_drag = getattr(self, "_last_native_drag", 0.0)
        return (time.monotonic() - last_drag) >= 2.0

    def toggle_maximize_window(self):
        if not self._window:
            return False
        try:
            # The native state can change via Aero Snap, so don't trust a
            # previously cached flag here.
            maximized = self.is_window_maximized()
            if maximized:
                self._window.restore()
            else:
                self._window.maximize()
            self._is_maximized = not maximized
            return True
        except Exception as e:
            print(f" [Python] toggle_maximize_window error: {e}")
            return False

    def window_action(self, action):
        if not self._window:
            return False
        if action == 'minimize':
            self._window.minimize()
        elif action == 'restore':
            self._window.restore()
        elif action == 'maximize':
            self._window.maximize()
        elif action == 'close':
            self._window.destroy()
        return True

    # ==========================
    # Database Wrappers
    # ==========================
    def load_playlists(self):
        return self.db.load_playlists()

    def new_playlist(self, data):
        return self.db.new_playlist(data)

    def getImage(self, id):
        return self.db.getImage(id)

    def update_playlist(self, playlist_id, data):
        return self.db.update_playlist(playlist_id, data)

    def delete_playlist(self, playlist_id):
        return self.db.delete_playlist(playlist_id)

    def get_song_playlists(self, song_file):
        return self.db.get_song_playlists(song_file)

    def update_song_playlists(self, song_file, song_title, playlist_ids):
        return self.db.update_song_playlists(song_file, song_title, playlist_ids)

    def get_playlist_songs(self, playlist_id):
        return self.db.get_playlist_songs(playlist_id)

    def get_songs_by_artist(self, artist):
        return self.db.get_songs_by_artist(artist)

    def get_songs_by_album(self, album):
        return self.db.get_songs_by_album(album)

    def get_artists(self):
        return self.db.get_artists()

    def get_albums(self):
        return self.db.get_albums()

    def get_all_songs(self):
        return self.db.get_all_songs()

    def get_podcasts(self):
        return self.db.get_podcasts()

    def get_podcast_details(self, file):
        return self.db.get_podcast_details(file)

    def get_podcast_tags(self):
        return self.db.get_podcast_tags()

    def get_podcast_tag_map(self):
        return self.db.get_podcast_tag_map()

    def new_podcast_tag(self, name):
        return self.db.new_podcast_tag(name)

    def rename_podcast_tag(self, tag_id, name):
        return self.db.rename_podcast_tag(tag_id, name)

    def delete_podcast_tag(self, tag_id):
        return self.db.delete_podcast_tag(tag_id)

    def assign_podcast_tag(self, file, tag_id):
        return self.db.assign_podcast_tag(file, tag_id)

    def unassign_podcast_tag(self, file, tag_id):
        return self.db.unassign_podcast_tag(file, tag_id)

    # ==========================
    # Podcast chapters (web parity)
    # ==========================
    def get_podcast_chapters(self, file):
        return self.db.get_podcast_chapters(file)

    def add_podcast_chapter(self, file, name, start_secs, end_secs=None):
        return self.db.add_podcast_chapter(file, name, start_secs, end_secs)

    def update_podcast_chapter(self, chapter_id, name, start_secs, end_secs=None):
        return self.db.update_podcast_chapter(chapter_id, name, start_secs, end_secs)

    def delete_podcast_chapter(self, chapter_id):
        return self.db.delete_podcast_chapter(chapter_id)

    def get_chapter_skip(self):
        try:
            return bool(getattr(settings, 'chapter_skip', False))
        except Exception:
            return False

    def set_chapter_skip(self, enabled):
        """Persist chapter auto-skip and apply it live (no restart needed)."""
        if isinstance(enabled, str):
            enabled = enabled.lower() in ('1', 'true', 'on', 'yes')
        else:
            enabled = bool(enabled)
        settings.chapter_skip = enabled
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "UPDATE Settings SET chapter_skip = ?",
                    (int(enabled),),
                )
        except Exception as e:
            print(f" [Python] Database error: {e}")
        # Refresh the cached state so the current episode obeys immediately.
        try:
            self.playback._refresh_chapter_state(getattr(self, 'last_song', None))
        except Exception:
            pass
        return enabled

    # ==========================
    # Lyrics highlight offset (web parity: ms, clamped ±20000)
    # ==========================
    def get_lyrics_offset(self, song_file):
        return self.db.get_lyrics_offset(song_file)

    def set_lyrics_offset(self, song_file, offset_ms):
        return self.db.set_lyrics_offset(song_file, offset_ms)

    def sync_local_podcasts_to_db(self):
        return self.db.sync_local_podcasts_to_db()

    def delete_podcast(self, song_data):
        return self.metadata.delete_podcast(song_data)

    def get_library_count(self):
        return self.db.get_library_count()

    def get_daily_mix(self):
        return self.db.get_daily_mix()

    def regenerate_daily_mix(self):
        """Force a fresh mix for today (web parity: Regenerate button)."""
        return self.db.regenerate_daily_mix()

    def get_recently_played(self, limit=15):
        return self.db.get_recently_played(limit)

    def get_recently_downloaded(self, limit=15):
        return self.db.get_recently_downloaded(limit)

    def get_download_history(self, page=1, limit=10):
        return self.db.get_download_history(page, limit)

    def get_played_history(self, page=1, limit=10):
        return self.db.get_played_history(page, limit)

    def sync_local_songs_to_db(self):
        return self.db.sync_local_songs_to_db()

    def _sync_progress(self, library):
        """Return a callback that mirrors pull progress into Settings.

        Sync runs on a pywebview worker thread; ``evaluate_js`` marshals the
        update onto the UI thread. Duplicate events are dropped, and a missing
        Settings view (or a teardown race) must never abort the pull.
        """
        log = logging.getLogger("hathor.sync")
        last = {"key": None}

        def emit(event):
            if not self._window:
                return
            try:
                payload = dict(event or {})
                payload["library"] = library
                phase = str(payload.get("phase") or "")
                done = int(payload.get("done") or 0)
                total = int(payload.get("total") or 0)
                message = str(payload.get("message") or "")
                key = (phase, done, total, message)
                if key == last["key"]:
                    return
                last["key"] = key
                self._window.evaluate_js(
                    "if (window.syncProgress) { window.syncProgress("
                    + json.dumps(payload) + "); }"
                )
            except Exception:
                log.debug("pull(%s): progress UI update failed", library,
                          exc_info=True)

        return emit

    def sync_remote_to_local_and_download(self, library="all"):
        from services.api_sync import sync_backend, ApiSyncClient, NOT_CONFIGURED
        log = logging.getLogger("hathor.sync")
        progress = self._sync_progress(library)
        if sync_backend() != "api":
            log.warning("pull(%s) skipped: sync server not configured", library)
            progress({"phase": "error", "message": NOT_CONFIGURED})
            return f"Error: {NOT_CONFIGURED}"
        log.info("pull(%s) requested from Settings", library)
        try:
            result = ApiSyncClient(self.db_path).pull_once(
                library, progress=progress)
        except Exception as e:
            log.exception("pull(%s) crashed", library)
            progress({"phase": "error", "message": f"Error: {e}"})
            return f"Error: {e}"
        if isinstance(result, str) and result.startswith("Error"):
            progress({"phase": "error", "message": result})
        log.info("pull(%s) result: %s", library, result)
        return result

    def sync_local_to_remote(self, library="all"):
        """Scan the selected folder, then push that library to the sync server."""
        from services.api_sync import sync_backend, ApiSyncClient, NOT_CONFIGURED
        log = logging.getLogger("hathor.sync")
        if sync_backend() != "api":
            log.warning("push(%s) skipped: sync server not configured", library)
            return f"Error: {NOT_CONFIGURED}"
        try:
            # Picking up files added outside the app used to be a separate
            # "Sync Now" button. Fold it into Push so Settings stays simple.
            log.info("push(%s) requested from Settings; scanning folders", library)
            if library != "podcasts":
                log.info("push(%s) audio scan: %s", library,
                         self.db.sync_local_songs_to_db())
            if library in ("all", "podcasts"):
                log.info("push(%s) podcast scan: %s", library,
                         self.db.sync_local_podcasts_to_db())
            result = ApiSyncClient(self.db_path).push_once(library)
        except Exception as e:
            log.exception("push(%s) crashed", library)
            return f"Error: {e}"
        log.info("push(%s) result: %s", library, result)
        return result

    # ==========================
    # Native Application Logic
    # ==========================
    def volume_slider(self, volume):
        pygame.mixer.music.set_volume(float(volume))
        settings.volume = float(volume)
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("UPDATE Settings SET current_volume = ?", (volume,))
        except Exception as e:
            print(f" [Python] Database error: {e}")
        # Keep the crossfade channel in sync when it is the active output
        try:
            self.playback.sync_output_volume()
        except Exception:
            pass

    def get_playback_settings(self):
        """Crossfade + chapter-skip prefs for the Settings UI."""
        try:
            enabled = bool(getattr(settings, 'crossfade_enabled', False))
        except Exception:
            enabled = False
        try:
            seconds = float(getattr(settings, 'crossfade_seconds', 5) or 0)
        except (TypeError, ValueError):
            seconds = 5.0
        try:
            skip = bool(getattr(settings, 'chapter_skip', False))
        except Exception:
            skip = False
        try:
            normalize = bool(getattr(settings, 'normalize_volume', True))
        except Exception:
            normalize = True
        return {'crossfade_enabled': enabled, 'crossfade_seconds': seconds,
                'chapter_skip': skip, 'normalize_volume': normalize}

    def set_crossfade(self, enabled, seconds):
        """Persist crossfade prefs and apply them live."""
        if isinstance(enabled, str):
            enabled = enabled.lower() in ('1', 'true', 'on', 'yes')
        else:
            enabled = bool(enabled)
        try:
            seconds = max(0.0, min(12.0, float(seconds)))
        except (TypeError, ValueError):
            seconds = 5.0
        settings.crossfade_enabled = enabled
        settings.crossfade_seconds = seconds
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "UPDATE Settings SET crossfade_enabled = ?, crossfade_seconds = ?",
                    (int(enabled), seconds),
                )
        except Exception as e:
            print(f" [Python] Database error: {e}")
        try:
            self.playback.crossfade_enabled = enabled
            self.playback.crossfade_seconds = seconds
        except Exception:
            pass
        if enabled and seconds > 0:
            return f"Crossfade on ({seconds:g}s). Gapless handoff always on."
        return "Crossfade off. Gapless handoff always on."

    def set_normalize_volume(self, enabled):
        """Persist loudness normalization and apply it to the current track."""
        if isinstance(enabled, str):
            enabled = enabled.lower() in ('1', 'true', 'on', 'yes')
        else:
            enabled = bool(enabled)
        settings.normalize_volume = enabled
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute(
                    "UPDATE Settings SET normalize_volume = ?",
                    (int(enabled),),
                )
        except Exception as e:
            print(f" [Python] Database error: {e}")
        try:
            self.playback.refresh_track_gain(getattr(self, 'last_song', None))
            self.playback.sync_output_volume()
        except Exception:
            pass
        return enabled

    def update_download_limit(self, limit):
        settings.limit_downloads = str(limit)
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("UPDATE Settings SET limit_downloads = ?", (limit,))
        except Exception as e:
            print(f" [Python] Database error: {e}")

    def update_background(self, background):
        settings.background = str(background)
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("UPDATE Settings SET background_path = ?", (background,))
        except Exception as e:
            print(f" [Python] Database error: {e}")

    def remove_background(self):
        settings.background = None
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("UPDATE Settings SET background_path = ?", (settings.background,))
            return True
        except Exception as e:
            print(f" [Python] Database error: {e}")
            return False

    def update_songs_path(self, songs_path):
        settings.path = str(songs_path)
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("UPDATE Settings SET songs_path = ?", (songs_path,))
        except Exception as e:
            print(f" [Python] Database error: {e}")

    def update_podcasts_path(self, podcasts_path):
        settings.podcasts_path = str(podcasts_path)
        try:
            with sqlite3.connect(self.db_path) as conn:
                conn.execute("UPDATE Settings SET podcasts_path = ?", (podcasts_path,))
        except Exception as e:
            print(f" [Python] Database error: {e}")

    def search_yt(self, query):
        return self.downloader.search_yt(query)

    # ==========================
    # Startup Maintenance
    # ==========================
    def get_ffmpeg_status(self):
        """Non-blocking status check for the settings/startup UI."""
        try:
            return startup_maintenance.find_ffmpeg()
        except Exception as e:
            return {'found': False, 'exe': None, 'dir': None, 'error': str(e)}

    def get_library_status(self):
        """Read-only yt-dlp / youtube-lib version comparison (no installs)."""
        try:
            return startup_maintenance.check_libraries_status()
        except Exception as e:
            print(f" [Python] Library status error: {e}")
            return []

    def get_system_status(self):
        """Offline-safe FFmpeg + installed YouTube-library status."""
        try:
            ffmpeg = self.get_ffmpeg_status()
        except Exception:
            ffmpeg = {'found': False, 'exe': None, 'dir': None}
        libraries = []
        try:
            for package in startup_maintenance.installed_youtube_libs():
                current = startup_maintenance.get_current_version(package)
                libraries.append({
                    'package': package,
                    'current': current,
                    'status': 'installed' if current else 'missing',
                })
        except Exception as e:
            print(f" [Python] System status error: {e}")
        return {'ffmpeg': ffmpeg, 'libraries': libraries}

    def backfill_loudness(self, limit=20):
        """Measure up to `limit` songs missing loudness (resumable)."""
        try:
            limit = max(1, min(100, int(limit)))
        except (TypeError, ValueError):
            limit = 20
        try:
            files = self.db.get_files_missing_loudness(limit)
        except Exception as e:
            return {'scanned': 0, 'remaining': 0, 'error': str(e)}
        try:
            remaining = self.db.count_files_missing_loudness()
        except Exception:
            remaining = 0
        if not files:
            return {'scanned': 0, 'remaining': remaining}
        found = startup_maintenance.find_ffmpeg()
        exe = found.get('exe') if found.get('found') else None
        if not exe:
            return {'scanned': 0, 'remaining': remaining,
                    'error': 'FFmpeg not found.'}
        from services import loudness
        scanned = 0
        for file_name in files:
            file_path = os.path.join(settings.path, file_name)
            if not os.path.isfile(file_path):
                continue
            value = loudness.analyze_file(exe, file_path)
            if value is not None:
                self.db.set_song_loudness(file_name, value)
                scanned += 1
        try:
            remaining = self.db.count_files_missing_loudness()
        except Exception:
            remaining = max(0, remaining - scanned)
        return {'scanned': scanned, 'remaining': remaining}

    def run_startup_maintenance(self):
        """Blocking re-run of the startup checks (Retry button). Pushes progress to UI."""
        try:
            window = self._window

            def push(event):
                if not window:
                    return
                try:
                    window.evaluate_js(
                        "if (window.startupToast && window.startupToast.update) "
                        f"window.startupToast.update({json.dumps(event)});"
                    )
                except Exception:
                    pass

            try:
                if window:
                    window.evaluate_js(
                        "if (window.startupToast && window.startupToast.show) "
                        "window.startupToast.show('Checking for updates…');"
                    )
            except Exception:
                pass
            summary = startup_maintenance.run_startup_checks(push)
            try:
                if window:
                    window.evaluate_js(
                        "if (window.startupToast && window.startupToast.complete) "
                        f"window.startupToast.complete({json.dumps(summary)});"
                    )
            except Exception:
                pass
            return summary
        except Exception as e:
            print(f" [Python] Startup maintenance error: {e}")
            return {'ffmpeg': {'status': 'error', 'error': str(e)},
                    'libraries': [], 'all_ok': False}

    def recieve_download(self, data):
        print(f" [Python] Received from JS: {data}")
        return self.download_manager.submit(data, block=True)

    def get_download_jobs(self, limit=15):
        return self.download_manager.get_jobs(limit)

    def retry_download(self, data):
        return self.download_manager.retry(data)

    def send_song_list(self):
        path = Path(settings.path)
        files = [f.name for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3']
        
        date_lookup = {}
        try:
            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.execute("SELECT file, date_download FROM Songs")
                for row in cursor.fetchall():
                    if row[1]:
                        date_lookup[row[0]] = row[1].replace(' ', 'T') + 'Z' if 'T' not in str(row[1]) else str(row[1])
                    else:
                        date_lookup[row[0]] = None
        except Exception as e:
            print(f" [Python] Error fetching date_download: {e}")
        
        local_list = []
        for file in files:
            file_path = str(path / file)
            song_data = self.metadata.get_song_metadata(file_path, file)
            song_data['DateDownload'] = date_lookup.get(file, None)
            local_list.append(song_data)
        self.song_list = local_list
        if self._window:
            try:
                self._window.evaluate_js(f"if (typeof window.song_list === 'function') window.song_list({json.dumps(self.song_list)})")
            except Exception as e:
                print(f" [Python] song_list JS error (page may not be ready yet): {e}")
    
    def load_settings(self):
        safe_bg = json.dumps(settings.background)
        safe_path = json.dumps(settings.path)
        safe_podcasts = json.dumps(getattr(settings, 'podcasts_path', ''))
        if self._window:
            self._window.evaluate_js(f"if (typeof window.load_settings === 'function') window.load_settings({settings.volume}, {settings.limit_downloads}, {safe_bg}, {safe_path}, {safe_podcasts});")

    def load_current_song(self):
        try:
            if not self.song_list:
                path = Path(settings.path)
                if path.exists():
                    files = [f.name for f in path.iterdir() if f.is_file() and f.suffix.lower() == '.mp3']
                    local_list = []
                    for file in files:
                        file_path = str(path / file)
                        local_list.append(self.metadata.get_song_metadata(file_path, file))
                    if not self.song_list:
                        self.song_list = local_list

            with sqlite3.connect(self.db_path) as conn:
                cursor = conn.cursor().execute("SELECT current_song, current_playlist, queue_source FROM Settings LIMIT 1")
                data = cursor.fetchone()
                if data and data[0]:
                    file = data[0]
                    playlist_id = data[1]
                    try:
                        source = json.loads(data[2]) if len(data) > 2 and data[2] else None
                    except Exception:
                        source = None
                    # Legacy rows predate queue_source: a stored playlist id
                    # still means "playing from that playlist".
                    if not isinstance(source, dict) and playlist_id is not None:
                        source = {'type': 'playlist', 'id': playlist_id}
                    if isinstance(source, dict):
                        self.playback.current_playlist_id = (
                            source.get('id') if source.get('type') == 'playlist' else None
                        )
                    else:
                        self.playback.current_playlist_id = playlist_id
                    
                    base_path = settings.podcasts_path if isinstance(source, dict) and source.get('type') == 'podcast' else settings.path
                    file_path = os.path.join(base_path, file)
                    song_data = self.metadata.get_song_metadata(file_path, file, include_cover=True)
                    if isinstance(source, dict) and source.get('type') == 'podcast':
                        song_data['IsPodcast'] = True
                    self.play_button(song_data, True)
                    
                    if not self.playback.restore_saved_queue(song_data):
                        if not self._rebuild_queue_from_source(song_data, file, source):
                            self.populate_queue(song_data)
                
        except Exception as e:
            print(f" [Python] Database error: {e}")

    def _rebuild_queue_from_source(self, song_data, file, source):
        """Rebuild the queue from the persisted playback context.

        Returns True when the queue was rebuilt (current song found in the
        source list), False to let the caller fall back to the general list.
        A stale source (deleted playlist, reset daily mix, renamed artist)
        simply misses and falls back.
        """
        try:
            if not isinstance(source, dict):
                return False
            stype = source.get('type')
            sid = source.get('id')
            songs = None
            playlist_id = None
            refresh_source = source

            if stype == 'playlist':
                try:
                    playlist_id = int(sid)
                except (TypeError, ValueError):
                    return False
                songs = self.db.get_playlist_songs(playlist_id)
            elif stype == 'daily_mix':
                # The mix resets every day: rebuild from TODAY's mix. If the
                # song isn't in it anymore, the caller falls back gracefully.
                mix = self.db.get_daily_mix()
                songs = mix.get('songs', []) if isinstance(mix, dict) else []
                refresh_source = {'type': 'daily_mix', 'id': mix.get('date') if isinstance(mix, dict) else None}
            elif stype == 'artist' and sid:
                songs = self.db.get_songs_by_artist(str(sid))
            elif stype == 'album' and sid:
                songs = self.db.get_songs_by_album(str(sid))
            elif stype == 'recently_played':
                songs = self.db.get_recently_played()
            elif stype == 'recently_downloaded':
                songs = self.db.get_recently_downloaded()
            elif stype == 'podcast' and file:
                # Single-episode queue (no playlists/queue for podcasts). The
                # episode may be gone (folder moved, file deleted) -> fall back.
                pod_path = os.path.join(settings.podcasts_path, file)
                if os.path.exists(pod_path):
                    song_data = self.metadata.get_song_metadata(pod_path, file)
                    song_data['IsPodcast'] = True
                    self.populate_queue_from_list(song_data, [song_data], None, source)
                    return True
                return False
            elif stype == 'all_songs':
                return False
            else:
                return False

            if not songs or not any(s.get('File') == file for s in songs):
                return False
            self.populate_queue_from_list(song_data, songs, playlist_id, refresh_source)
            return True
        except Exception as e:
            print(f" [Python] Queue rebuild error: {e}")
            return False

    def js_log(self, message):
        """Receive log lines from the frontend (window.onerror /
        unhandledrejection forwarders). Written to hathor.log."""
        try:
            import logging
            logging.getLogger('hathor').info("JS: %s", str(message)[:2000])
        except Exception:
            pass
        return True

    def get_current_song_ui_state(self):
        """Let the frontend pull the current song when it's ready.

        Covers the startup race where Python pushes `playing_view` /
        `plaiyng_info` before the webview DOM has parsed its scripts
        (which previously surfaced as `ReferenceError: playing_view
        is not defined`). The UI calls this on startup and renders
        locally if the early push was skipped.
        """
        try:
            song = getattr(self, 'last_song', None)
            if not song or not song.get('File'):
                return {'song': None, 'is_playing': False}
            return {'song': song, 'is_playing': bool(getattr(self, 'playing', False))}
        except Exception as e:
            print(f" [Python] get_current_song_ui_state error: {e}")
            return {'song': None, 'is_playing': False}

    def search_itunes_metadata(self, title, artist=None):
        try:
            artist_str = artist if artist and artist not in ['Unknown', 'Unknown Artist'] else None
            metadata = self.downloader.search_itunes(title, artist_str)
            if metadata:
                if metadata.get('artwork'):
                    img_data = base64.b64encode(metadata['artwork']).decode('utf-8')
                    metadata['artwork'] = f"data:image/jpeg;base64,{img_data}"
                return metadata
        except Exception as e:
            print(f" [Python] iTunes Search Error: {str(e)}")
        return None

    def search_itunes_metadata_multi(self, title, artist=None):
        """Search iTunes and return multiple metadata options for user selection."""
        try:
            artist_str = artist if artist and artist not in ['Unknown', 'Unknown Artist'] else None
            results = self.downloader.search_itunes_multi(title, artist_str)
            return results if results else []
        except Exception as e:
            print(f" [Python] iTunes Multi-Search Error: {str(e)}")
            return []

    def pick_folder(self):
        import webview
        if self._window:
            result = self._window.create_file_dialog(
                webview.FOLDER_DIALOG,
                allow_multiple=False
            )
            if result and len(result) > 0:
                self.update_songs_path(result[0])
                return result[0]
        return None

    def pick_podcasts_folder(self):
        import webview
        if self._window:
            result = self._window.create_file_dialog(
                webview.FOLDER_DIALOG,
                allow_multiple=False
            )
            if result and len(result) > 0:
                self.update_podcasts_path(result[0])
                return result[0]
        return None

    def pick_background(self):
        import webview
        if self._window:
            result = self._window.create_file_dialog(
                webview.OPEN_DIALOG,
                allow_multiple=False,
                file_types=('Image Files (*.bmp;*.jpg;*.jpeg;*.gif;*.png)', 'All files (*.*)')
            )
            if result and len(result) > 0:
                self.update_background(result[0])
                return result[0]
        return None

    def pick_playlist_image(self):
        import webview
        if self._window:
            result = self._window.create_file_dialog(
                webview.OPEN_DIALOG,
                allow_multiple=False,
                file_types=('Image Files (*.bmp;*.jpg;*.jpeg;*.gif;*.png)', 'All files (*.*)')
            )
            if result and len(result) > 0:
                return self.get_local_image_base64(result[0])
        return None
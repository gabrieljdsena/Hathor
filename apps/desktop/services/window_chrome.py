"""Platform window chrome (custom frameless titlebar support).

Windows uses ``ctypes`` + Aero Snap caption drags. Linux uses GTK's
``begin_move_drag``/``begin_resize_drag`` so the compositor handles the
gesture while the app keeps the same HTML titlebar as Windows. The Windows
imports stay behind a platform guard: ``ctypes.wintypes`` does not exist on
Linux and would make the whole desktop bridge fail before GTK starts.

GTK3 on KWin/Wayland ignores ``set_decorated(False)`` for plain windows and
keeps drawing a server-side titlebar. Giving the window an empty custom
titlebar before it is shown forces the client-side-decoration negotiation;
disabling decorations afterwards then leaves no titlebar at all.
"""

from __future__ import annotations

import logging
import sys
import time

IS_WINDOWS = sys.platform.startswith("win")
IS_LINUX = sys.platform.startswith("linux")

if IS_WINDOWS:
    import ctypes
    from ctypes import wintypes

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
            ("left", ctypes.c_long), ("top", ctypes.c_long),
            ("right", ctypes.c_long), ("bottom", ctypes.c_long),
        ]

    class _POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    class _MINMAXINFO(ctypes.Structure):
        _fields_ = [
            ("ptReserved", _POINT), ("ptMaxSize", _POINT),
            ("ptMaxPosition", _POINT), ("ptMinTrackSize", _POINT),
            ("ptMaxTrackSize", _POINT),
        ]

    class _MONITORINFO(ctypes.Structure):
        _fields_ = [
            ("cbSize", wintypes.DWORD), ("rcMonitor", _RECT),
            ("rcWork", _RECT), ("dwFlags", wintypes.DWORD),
        ]

    _WNDPROC = ctypes.WINFUNCTYPE(
        ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT,
        wintypes.WPARAM, wintypes.LPARAM,
    )
    _USER32 = None

    def _user32():
        """Configure 64-bit-safe prototypes once and return a private user32."""
        global _USER32
        if _USER32 is not None:
            return _USER32
        user32 = ctypes.WinDLL("user32", use_last_error=True)
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
        _USER32 = user32
        return user32


_LINUX_LAST_BUTTON = None
_LINUX_TRACKER_WIDGET = None
_LINUX_ENTER_TIMEOUT = 0
_LINUX_RESIZE_STARTED_AT = 0.0
_GDK = None

_GDK_EDGE_NAMES = {
    "n": "NORTH",
    "s": "SOUTH",
    "w": "WEST",
    "e": "EAST",
    "nw": "NORTH_WEST",
    "ne": "NORTH_EAST",
    "sw": "SOUTH_WEST",
    "se": "SOUTH_EAST",
}


def _gdk():
    global _GDK
    if _GDK is None:
        import gi
        gi.require_version("Gdk", "3.0")
        from gi.repository import Gdk
        _GDK = Gdk
    return _GDK


def _gdk_edge(edge, gdk=None):
    """Map an HTML edge name to a Gdk.WindowEdge, or None."""
    name = _GDK_EDGE_NAMES.get((edge or "").strip().lower())
    if name is None:
        return None
    Gdk = gdk or _gdk()
    return getattr(Gdk.WindowEdge, name, None)


def _edge_for_point(x, y, width, height, margin=10,
                    control_band=150, titlebar_height=40):
    """Return the resize edge under a window-local point, if any.

    The top-right control band is excluded so presses on the HTML window
    buttons never turn into a compositor resize.
    """
    if width <= 2 * margin or height <= 2 * margin:
        return None
    if 0 <= y < titlebar_height and x > width - control_band:
        # Keep the titlebar buttons clickable, but the outer corner is still
        # a resize handle.
        if not (x >= width - margin and y <= margin):
            return None
    left = x <= margin
    right = x >= width - margin
    top = y <= margin
    bottom = y >= height - margin
    edge = ("n" if top else "") + ("s" if bottom else "") + \
        ("w" if left else "") + ("e" if right else "")
    return edge or None


def _event_edge(widget, event, margin=10):
    """Resolve the resize edge for a GTK button event."""
    try:
        toplevel = widget.get_toplevel()
        if toplevel is None:
            return None
        x, y = int(event.x), int(event.y)
        try:
            translated = widget.translate_coordinates(toplevel, x, y)
            if translated and translated[0]:
                x, y = int(translated[1]), int(translated[2])
        except Exception:
            pass
        width, height = toplevel.get_size()
        return _edge_for_point(x, y, int(width), int(height), margin=margin)
    except Exception:
        return None


def _start_linux_resize(toplevel, edge, event):
    """Start a compositor resize directly from the GTK press event.

    Calling this synchronously is important: GTK/Wayland needs the serial of
    the actual press. Routing it through the JS bridge and an idle callback can
    lose that serial, which makes every other drag need a second click.
    """
    global _LINUX_RESIZE_STARTED_AT
    try:
        gdk_edge = _gdk_edge(edge)
        if gdk_edge is None or not hasattr(toplevel, "begin_resize_drag"):
            return False
        # The JS bridge may also reach the same press when WebKit handles it
        # before this GTK signal. Never start the same gesture twice.
        if time.monotonic() - _LINUX_RESIZE_STARTED_AT < 0.15:
            return True
        if hasattr(toplevel, "is_active") and not toplevel.is_active() and \
                hasattr(toplevel, "present"):
            toplevel.present()
        toplevel.begin_resize_drag(
            gdk_edge, 1,
            int(getattr(event, "x_root", 0)),
            int(getattr(event, "y_root", 0)),
            int(getattr(event, "time", 0)),
        )
        _LINUX_RESIZE_STARTED_AT = time.monotonic()
        logging.getLogger("hathor.window").info(
            "Linux edge resize start: %s", edge)
        return True
    except Exception:
        logging.getLogger("hathor.window").debug(
            "Linux edge resize failed", exc_info=True)
        return False


def _record_linux_button_press(widget, event):
    """Cache the real GTK left press, focus, and start edge resizes.

    KWin's inactive-window handling can otherwise consume the first press
    (resize, buttons and hover all feel like they need a double click).
    Presenting on the press keeps the click itself intact while making the
    window active before the JS bridge handles it. Edge presses are consumed
    and handed straight to the compositor, using the real event serial.
    """
    global _LINUX_LAST_BUTTON
    if getattr(event, "button", None) != 1:
        return False
    try:
        _LINUX_LAST_BUTTON = (
            int(event.time), int(event.x_root), int(event.y_root),
            time.monotonic(),
        )
        toplevel = widget.get_toplevel() if widget else None
        if toplevel is None:
            return False
        if hasattr(toplevel, "is_active") and not toplevel.is_active() and \
                hasattr(toplevel, "present"):
            toplevel.present()
        edge = _event_edge(widget, event)
        if edge and _start_linux_resize(toplevel, edge, event):
            return True
    except Exception:
        pass
    return False


def _focus_on_enter(widget, _event=None):
    """Delay-focus the window when the pointer rests over it.

    KWin's click-to-focus handling can otherwise swallow the first click and
    leave the web UI without hover state until it happens. A short delay keeps
    a quick pass over the window from stealing focus.
    """
    global _LINUX_ENTER_TIMEOUT
    if not widget or _LINUX_ENTER_TIMEOUT:
        return False
    try:
        from gi.repository import GLib

        def _focus():
            global _LINUX_ENTER_TIMEOUT
            _LINUX_ENTER_TIMEOUT = 0
            try:
                toplevel = widget.get_toplevel()
                if toplevel is not None and hasattr(toplevel, "is_active") and \
                        not toplevel.is_active() and hasattr(toplevel, "present"):
                    toplevel.present()
            except Exception:
                pass
            return False

        _LINUX_ENTER_TIMEOUT = GLib.timeout_add(180, _focus)
    except Exception:
        pass
    return False


def _focus_on_motion(widget, event=None):
    """Re-focus when the pointer moves onto a resize edge.

    After a compositor resize KWin can leave the window inactive even though
    the pointer is still over it. There is no new enter event in that case, so
    the next edge press would only activate the window. Moving to the next
    edge now re-focuses it before the press is handled.
    """
    if not widget:
        return False
    try:
        toplevel = widget.get_toplevel()
        if toplevel is None or not hasattr(toplevel, "is_active") or \
                toplevel.is_active():
            return False
        if _event_edge(widget, event) is None:
            return False
        if hasattr(toplevel, "present"):
            toplevel.present()
    except Exception:
        pass
    return False


def _cancel_focus_on_enter(_widget=None, _event=None):
    global _LINUX_ENTER_TIMEOUT
    if _LINUX_ENTER_TIMEOUT:
        try:
            from gi.repository import GLib
            GLib.source_remove(_LINUX_ENTER_TIMEOUT)
        except Exception:
            pass
        _LINUX_ENTER_TIMEOUT = 0
    return False


def _attach_linux_tracker(widget):
    global _LINUX_TRACKER_WIDGET
    if widget is None:
        return False
    try:
        if getattr(widget, "_hathor_button_tracker", False):
            _LINUX_TRACKER_WIDGET = widget
            return True
        widget.connect("button-press-event", _record_linux_button_press)
        widget.connect("enter-notify-event", _focus_on_enter)
        widget.connect("motion-notify-event", _focus_on_motion)
        widget.connect("leave-notify-event", _cancel_focus_on_enter)
        widget._hathor_button_tracker = True
        _LINUX_TRACKER_WIDGET = widget
        return True
    except Exception:
        logging.getLogger("hathor.window").exception(
            "Could not attach the Linux button tracker")
        return False


def install_linux_frameless_workaround():
    """Make pywebview's GTK windows truly undecorated on KWin/Wayland.

    Must run before ``webview.start`` creates the BrowserViews. The empty
    titlebar is only a negotiation trick; it is removed by disabling
    decorations before the window is shown, so the HTML titlebar is the only
    visible one.
    """
    if not IS_LINUX:
        return False
    try:
        from webview.platforms import gtk as backend
        from gi.repository import GLib
        # Match hathor.desktop so KWin uses the app icon instead of the
        # generic Wayland "W" fallback in the task switcher.
        GLib.set_prgname("hathor")
        GLib.set_application_name("Hathor")
    except Exception:
        logging.getLogger("hathor.window").exception(
            "Could not import the pywebview GTK backend")
        return False
    original_show = backend.BrowserView.show
    if getattr(original_show, "_hathor_frameless_patch", False):
        return True

    def show(self):
        try:
            if not getattr(self, "_hathor_frameless_applied", False):
                import gi
                gi.require_version("Gtk", "3.0")
                from gi.repository import Gtk
                self.window.set_titlebar(Gtk.Grid())
                self.window.set_decorated(False)
                self.window.set_icon_name("hathor")
                _attach_linux_tracker(getattr(self, "webview", None))
                self._hathor_frameless_applied = True
        except Exception:
            logging.getLogger("hathor.window").exception(
                "Could not apply the GTK frameless workaround")
        return original_show(self)

    show._hathor_frameless_patch = True
    backend.BrowserView.show = show
    return True


class WindowsWindowChrome:
    """No-op on Linux; native snap/drag on Windows."""

    def __init__(self):
        self._snap_state = None
        self._last_native_drag = 0.0
        self._linux_tracker = None
        self._last_button = None  # (time, x_root, y_root, monotonic)
        self._log = logging.getLogger("hathor.window")

    def window_handle(self, window):
        if not IS_WINDOWS or not window:
            return None
        native = getattr(window, "native", None)
        if native is None:
            return None
        try:
            return int(native.Handle.ToInt64())
        except Exception:
            try:
                return int(native.Handle.ToInt32())
            except Exception:
                return None

    def enable_snap(self, window):
        if not IS_WINDOWS:
            return False
        hwnd = self.window_handle(window)
        if not hwnd:
            return False
        if self._snap_state and self._snap_state.get("hwnd") == hwnd:
            return True
        try:
            user32 = _user32()
            old_proc = user32.GetWindowLongPtrW(hwnd, _GWLP_WNDPROC)

            def _wndproc(h, msg, wp, lp):
                if msg == _WM_GETMINMAXINFO:
                    u = _user32()
                    result = u.CallWindowProcW(old_proc, h, msg, wp, lp)
                    info = ctypes.cast(
                        lp, ctypes.POINTER(_MINMAXINFO)).contents
                    monitor = u.MonitorFromWindow(h, _MONITOR_DEFAULTTONEAREST)
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
            self._log.info("Windows snap enabled (hwnd=%s)", hwnd)
            return True
        except Exception:
            self._log.exception("enable_windows_snap failed")
            return False

    # -- GTK helpers (Linux) --------------------------------------------
    def _gtk(self):
        if not IS_LINUX:
            return None
        try:
            import gi
            gi.require_version("Gtk", "3.0")
            gi.require_version("Gdk", "3.0")
            from gi.repository import Gdk, GLib, Gtk
            return Gdk, GLib, Gtk
        except Exception:
            self._log.exception("GTK window chrome is unavailable")
            return None

    def _gtk_window(self, window):
        native = getattr(window, "native", None) if window else None
        if native is None:
            return None
        if not hasattr(native, "begin_move_drag"):
            return None
        return native

    def _pointer_root(self, x=None, y=None):
        """Best-effort root coordinates + timestamp for a GTK drag call."""
        last = self._last_button or _LINUX_LAST_BUTTON
        if last and (time.monotonic() - last[3]) < 3.0:
            return last[1], last[2], last[0]
        mods = self._gtk()
        if mods is None:
            return int(x or 0), int(y or 0), 0
        Gdk, _GLib, Gtk = mods
        px = py = None
        try:
            display = Gdk.Display.get_default()
            seat = display.get_default_seat() if display else None
            pointer = seat.get_pointer() if seat else None
            if pointer is not None:
                _screen, px, py = pointer.get_position()
        except Exception:
            px = py = None
        if px is None or py is None or (not px and not py):
            px, py = int(x or 0), int(y or 0)
        try:
            timestamp = int(Gtk.get_current_event_time())
        except Exception:
            timestamp = 0
        return int(px), int(py), timestamp

    def enable_linux_chrome(self, window):
        """Record GTK button-press timestamps for compositor move/resize drags.

        Wayland needs the serial/timestamp of the real button press. The JS
        bridge call arrives after WebKit handled the event, so connect to the
        WebKit widget once and cache its last left press. GTK calls are
        scheduled on the main loop because the startup hook runs in a worker.
        """
        if not IS_LINUX:
            return False
        mods = self._gtk()
        if mods is None:
            return False
        _Gdk, GLib, _Gtk = mods
        native = getattr(window, "native", None) if window else None
        if native is None:
            return False
        if self._linux_tracker is not None:
            return True
        if _LINUX_TRACKER_WIDGET is not None:
            self._linux_tracker = _LINUX_TRACKER_WIDGET
            return True

        def _walk(widget):
            yield widget
            try:
                children = widget.get_children()
            except Exception:
                children = []
            for child in children:
                yield from _walk(child)

        def _install():
            target = None
            try:
                for widget in _walk(native):
                    if hasattr(widget, "get_estimated_load_progress") and \
                            hasattr(widget, "load_uri"):
                        target = widget
                        break
            except Exception:
                self._log.exception("Could not walk the GTK widget tree")
                return False
            if target is None:
                self._log.warning("WebKit widget not found; Linux drag tracker off")
                return False

            if not _attach_linux_tracker(target):
                return False
            self._linux_tracker = target
            self._log.info("Linux compositor window chrome enabled")
            return False

        GLib.idle_add(_install)
        return True

    def _start_drag_linux(self, window, x=None, y=None):
        mods = self._gtk()
        if mods is None:
            return False
        _Gdk, GLib, _Gtk = mods
        native = self._gtk_window(window)
        if native is None:
            return False

        def _drag():
            try:
                if hasattr(native, "present"):
                    native.present()
                root_x, root_y, timestamp = self._pointer_root(x, y)
                native.begin_move_drag(1, root_x, root_y, timestamp)
            except Exception:
                self._log.exception("Linux window drag failed")
            return False

        GLib.idle_add(_drag)
        return True

    def start_resize(self, window, edge, x=None, y=None):
        """Start a compositor resize drag (Linux). Windows uses the JS loop."""
        if not IS_LINUX:
            return False
        mods = self._gtk()
        if mods is None:
            return False
        Gdk, GLib, _Gtk = mods
        gdk_edge = _gdk_edge(edge, Gdk)
        native = self._gtk_window(window)
        if gdk_edge is None or native is None:
            return False
        global _LINUX_RESIZE_STARTED_AT
        if time.monotonic() - _LINUX_RESIZE_STARTED_AT < 0.15:
            return True
        _LINUX_RESIZE_STARTED_AT = time.monotonic()

        def _resize():
            try:
                if hasattr(native, "present"):
                    native.present()
                root_x, root_y, timestamp = self._pointer_root(x, y)
                native.begin_resize_drag(
                    gdk_edge, 1, root_x, root_y, timestamp)
            except Exception:
                self._log.exception("Linux window resize failed")
            return False

        self._log.info("Linux resize bridge: %s", edge)
        GLib.idle_add(_resize)
        return True

    def activate(self, window):
        """Ask the compositor to focus/raise the window (Linux best effort)."""
        if not IS_LINUX or not window:
            return False
        native = getattr(window, "native", None)
        if native is None or not hasattr(native, "present"):
            return False
        try:
            native.present()
            return True
        except Exception:
            self._log.exception("Linux window activation failed")
            return False

    def toggle_maximize(self, window):
        """Toggle maximize through GTK, which knows the real state on Linux."""
        if not IS_LINUX or not window:
            return False
        mods = self._gtk()
        if mods is None:
            return False
        _Gdk, GLib, _Gtk = mods
        native = getattr(window, "native", None)
        if native is None or not hasattr(native, "maximize"):
            return False

        def _toggle():
            try:
                if native.is_maximized():
                    native.unmaximize()
                else:
                    native.maximize()
            except Exception:
                self._log.exception("Linux maximize toggle failed")
            return False

        GLib.idle_add(_toggle)
        return True

    # -- dispatching ----------------------------------------------------
    def start_drag(self, window, x=None, y=None):
        """Start a compositor/native caption drag on Windows or Linux."""
        if IS_WINDOWS:
            return self._start_drag_windows(window)
        if IS_LINUX:
            return self._start_drag_linux(window, x, y)
        return False

    def _start_drag_windows(self, window):
        """Start a native caption drag so Windows handles snap and magnetism."""
        hwnd = self.window_handle(window)
        native = getattr(window, "native", None) if window else None
        if not hwnd or native is None:
            return False

        def _drag():
            user32 = _user32()
            user32.ReleaseCapture()
            self._last_native_drag = time.monotonic()
            user32.SendMessageW(hwnd, _WM_NCLBUTTONDOWN, _HTCAPTION, 0)
            self._last_native_drag = time.monotonic()

        try:
            from System import Action
            native.Invoke(Action(_drag))
            return True
        except Exception:
            self._log.exception(
                "start_window_drag: native invoke failed; trying direct")
        try:
            _drag()
            return True
        except Exception:
            self._log.exception("start_window_drag failed")
            return False

    def sync_native_state(self, window, fallback=False):
        if not window:
            return bool(fallback)
        if IS_WINDOWS:
            try:
                native = getattr(window, "native", None)
                if native is not None:
                    return str(native.WindowState) == "Maximized"
            except Exception:
                pass
            return bool(fallback)
        if IS_LINUX:
            try:
                native = getattr(window, "native", None)
                if native is not None and hasattr(native, "is_maximized"):
                    return bool(native.is_maximized())
            except Exception:
                pass
        return bool(fallback)

    def can_persist_size(self):
        return (time.monotonic() - self._last_native_drag) >= 2.0

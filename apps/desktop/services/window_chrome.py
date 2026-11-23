"""Native Windows window chrome, isolated so Linux can import ``api``.

Keeping ``ctypes.wintypes`` and ``ctypes.WINFUNCTYPE`` behind a platform guard
is required: neither is available on Linux, and importing them at module level
would make the whole desktop bridge fail before GTK starts.
"""

from __future__ import annotations

import logging
import sys
import time

IS_WINDOWS = sys.platform.startswith("win")

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


class WindowsWindowChrome:
    """No-op on Linux; native snap/drag on Windows."""

    def __init__(self):
        self._snap_state = None
        self._last_native_drag = 0.0
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

    def start_drag(self, window):
        """Start a native caption drag so Windows handles snap and magnetism."""
        if not IS_WINDOWS:
            return False
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
        if not IS_WINDOWS or not window:
            return bool(fallback)
        try:
            native = getattr(window, "native", None)
            if native is not None:
                return str(native.WindowState) == "Maximized"
        except Exception:
            pass
        return bool(fallback)

    def can_persist_size(self):
        return (time.monotonic() - self._last_native_drag) >= 2.0

import time
import unittest
from unittest import mock

from services import window_chrome as wc


class FakeWindowEdge:
    NORTH = "north"
    SOUTH = "south"
    WEST = "west"
    EAST = "east"
    NORTH_WEST = "north-west"
    NORTH_EAST = "north-east"
    SOUTH_WEST = "south-west"
    SOUTH_EAST = "south-east"


class FakeGdk:
    WindowEdge = FakeWindowEdge


class FakeGLib:
    @staticmethod
    def idle_add(callback, *args, **kwargs):
        callback(*args, **kwargs)
        return 1


class FakeGtk:
    @staticmethod
    def get_current_event_time():
        return 777


class FakeGtkWindow:
    def __init__(self):
        self.moves = []
        self.resizes = []
        self.maximized = False
        self.maximize_calls = 0
        self.unmaximize_calls = 0
        self.present_calls = 0
        self.size = (1000, 700)
        self.active = True
        self.children = []
        self.handlers = {}

    def begin_move_drag(self, button, x, y, timestamp):
        self.moves.append((button, x, y, timestamp))

    def begin_resize_drag(self, edge, button, x, y, timestamp):
        self.resizes.append((edge, button, x, y, timestamp))

    def is_maximized(self):
        return self.maximized

    def get_size(self):
        return self.size

    def is_active(self):
        return self.active

    def present(self):
        self.active = True
        self.present_calls += 1

    def maximize(self):
        self.maximized = True
        self.maximize_calls += 1

    def unmaximize(self):
        self.maximized = False
        self.unmaximize_calls += 1

    def get_children(self):
        return list(self.children)

    def connect(self, signal, handler):
        self.handlers[signal] = handler


class FakeWebViewWidget:
    def __init__(self):
        self.handlers = {}

    def get_estimated_load_progress(self):
        return 1.0

    def load_uri(self, _uri):
        return None

    def get_children(self):
        return []

    def connect(self, signal, handler):
        self.handlers[signal] = handler


class FakePywebviewWindow:
    def __init__(self, native):
        self.native = native


class FakeEvent:
    def __init__(self, button=1, when=12345, x=100, y=200,
                 x_root=None, y_root=None):
        self.button = button
        self.time = when
        self.x = x
        self.y = y
        self.x_root = x if x_root is None else x_root
        self.y_root = y if y_root is None else y_root


class FakeEdgeWidget(FakeWebViewWidget):
    def __init__(self, toplevel):
        super().__init__()
        self.toplevel = toplevel

    def get_toplevel(self):
        return self.toplevel

    def translate_coordinates(self, _toplevel, x, y):
        return (True, x, y)


class LinuxWindowChromeTests(unittest.TestCase):
    def setUp(self):
        wc._LINUX_LAST_BUTTON = None
        wc._LINUX_TRACKER_WIDGET = None
        wc._LINUX_ENTER_TIMEOUT = 0
        wc._LINUX_RESIZE_STARTED_AT = 0.0
        self.chrome = wc.WindowsWindowChrome()
        self.native = FakeGtkWindow()
        self.window = FakePywebviewWindow(self.native)
        self.gtk = mock.patch.object(
            self.chrome, "_gtk", return_value=(FakeGdk, FakeGLib, FakeGtk)
        )
        self.gtk.start()
        self.linux = mock.patch.object(wc, "IS_LINUX", True)
        self.linux.start()
        self.windows = mock.patch.object(wc, "IS_WINDOWS", False)
        self.windows.start()

    def tearDown(self):
        wc._LINUX_LAST_BUTTON = None
        wc._LINUX_TRACKER_WIDGET = None
        wc._LINUX_ENTER_TIMEOUT = 0
        wc._LINUX_RESIZE_STARTED_AT = 0.0
        self.windows.stop()
        self.linux.stop()
        self.gtk.stop()

    def test_drag_uses_recorded_button_press(self):
        self.chrome._last_button = (12345, 100, 200, time.monotonic())
        self.assertTrue(self.chrome.start_drag(self.window))
        self.assertEqual(self.native.moves, [(1, 100, 200, 12345)])

    def test_resize_maps_edges(self):
        self.chrome._last_button = (12345, 100, 200, time.monotonic())
        self.assertTrue(self.chrome.start_resize(self.window, "se"))
        self.assertEqual(
            self.native.resizes,
            [("south-east", 1, 100, 200, 12345)],
        )
        self.assertFalse(self.chrome.start_resize(self.window, "wat"))

    def test_maximize_state_and_toggle(self):
        self.native.maximized = True
        self.assertTrue(self.chrome.sync_native_state(self.window))
        self.assertTrue(self.chrome.toggle_maximize(self.window))
        self.assertEqual(self.native.unmaximize_calls, 1)
        self.assertFalse(self.chrome.sync_native_state(self.window))
        self.assertTrue(self.chrome.toggle_maximize(self.window))
        self.assertEqual(self.native.maximize_calls, 1)

    def test_edge_for_point_maps_edges_and_corners(self):
        self.assertIsNone(wc._edge_for_point(500, 300, 1000, 700))
        self.assertEqual(wc._edge_for_point(3, 300, 1000, 700), "w")
        self.assertEqual(wc._edge_for_point(997, 300, 1000, 700), "e")
        self.assertEqual(wc._edge_for_point(500, 3, 1000, 700), "n")
        self.assertEqual(wc._edge_for_point(500, 697, 1000, 700), "s")
        self.assertEqual(wc._edge_for_point(3, 3, 1000, 700), "nw")
        self.assertEqual(wc._edge_for_point(3, 697, 1000, 700), "sw")
        self.assertEqual(wc._edge_for_point(997, 697, 1000, 700), "se")
        self.assertIsNone(wc._edge_for_point(5, 5, 20, 20))

    def test_top_right_control_band_allows_only_the_corner(self):
        self.assertIsNone(wc._edge_for_point(997, 20, 1000, 700))
        self.assertIsNone(wc._edge_for_point(989, 3, 1000, 700))
        self.assertEqual(wc._edge_for_point(997, 3, 1000, 700), "ne")
        self.assertEqual(wc._edge_for_point(995, 5, 1000, 700), "ne")
        self.assertEqual(wc._edge_for_point(997, 300, 1000, 700), "e")

    def test_edge_press_starts_native_resize_synchronously(self):
        widget = FakeEdgeWidget(self.native)
        event = FakeEvent(button=1, when=4242, x=3, y=300,
                          x_root=100, y_root=400)
        with mock.patch.object(wc, "_gdk", return_value=FakeGdk):
            handled = wc._record_linux_button_press(widget, event)
        self.assertTrue(handled)
        self.assertEqual(
            self.native.resizes,
            [(FakeWindowEdge.WEST, 1, 100, 400, 4242)],
        )
        center = FakeEvent(button=1, when=5555, x=500, y=300)
        with mock.patch.object(wc, "_gdk", return_value=FakeGdk):
            self.assertFalse(wc._record_linux_button_press(widget, center))
        self.assertEqual(len(self.native.resizes), 1)

    def test_enable_linux_chrome_tracks_left_button(self):
        webview = FakeWebViewWidget()
        self.native.children = [webview]
        self.assertTrue(self.chrome.enable_linux_chrome(self.window))
        handler = webview.handlers.get("button-press-event")
        self.assertIsNotNone(handler)
        handler(webview, FakeEvent(button=1, when=999, x=10, y=20))
        self.assertEqual(wc._LINUX_LAST_BUTTON[:3], (999, 10, 20))
        handler(webview, FakeEvent(button=3, when=1111, x=1, y=2))
        self.assertEqual(wc._LINUX_LAST_BUTTON[:3], (999, 10, 20))


if __name__ == "__main__":
    unittest.main()

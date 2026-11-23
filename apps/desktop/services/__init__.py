"""Domain services.

Imports are lazy so lightweight modules (API config, HTTP client, tests)
can be imported without pulling in pygame/pywebview. Existing call sites
keep using ``from services import PlaybackController`` unchanged.
"""

from importlib import import_module

__all__ = [
    "PlaybackController",
    "MetadataManager",
    "DatabaseManager",
    "WindowsMediaOverlay",
    "LyricsService",
    "AppleService",
    "DownloadManager",
    "DiscoverService",
    "AiDjController",
    "AppUpdateService",
    "startup_maintenance",
]

_LAZY = {
    "PlaybackController": (".playback", "PlaybackController"),
    "MetadataManager": (".metadata", "MetadataManager"),
    "DatabaseManager": (".database", "DatabaseManager"),
    "WindowsMediaOverlay": (".windows_media", "WindowsMediaOverlay"),
    "LyricsService": (".lyrics", "LyricsService"),
    "AppleService": (".apple", "AppleService"),
    "DownloadManager": (".downloads", "DownloadManager"),
    "DiscoverService": (".discover", "DiscoverService"),
    "AiDjController": (".ai_dj", "AiDjController"),
    "AppUpdateService": (".app_update", "AppUpdateService"),
    "startup_maintenance": (".startup_maintenance", None),
}


def __getattr__(name):
    target = _LAZY.get(name)
    if target is None:
        raise AttributeError(f"module {__name__!r} has no attribute {name!r}")
    module_name, attribute = target
    module = import_module(module_name, __name__)
    value = module if attribute is None else getattr(module, attribute)
    globals()[name] = value
    return value


def __dir__():
    return sorted(set(globals()) | set(__all__))

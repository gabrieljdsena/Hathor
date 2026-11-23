import unittest
from pathlib import Path


class ShellScaffoldingTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(__file__).resolve().parents[1]

    def test_server_views_and_nav_exist(self):
        index = (self.root / "ui" / "index.html").read_text(encoding="utf-8")
        for target in ("all_songs", "discover", "ai_dj"):
            self.assertIn(f'data-target="{target}"', index)
        self.assertIn('id="discover-preview-dock"', index)
        self.assertIn('id="dj-vibe-btn"', index)
        self.assertIn("./js/discover-preview.js", index)
        self.assertIn("./js/fuzzy.js", index)
        self.assertIn("./js/download-batch.js", index)
        self.assertIn("./js/chunked-list.js", index)
        self.assertIn("./js/ai-dj-voice.js", index)
        self.assertIn("./js/ai-dj-recorder.js", index)
        self.assertIn("./js/ai-dj-state.js", index)
        self.assertIn("./js/app-update.js", index)
        self.assertIn("./js/platform.js", index)
        self.assertIn('id="dj-vibe-popover"', index)
        self.assertIn('id="nav-ai-dj-label"', index)
        self.assertIn('id="bento-move-podcast-btn"', index)
        self.assertIn('id="np-lyrics-search-btn"', index)
        self.assertIn('id="np-romaji-btn"', index)
        self.assertIn('id="np-lyrics-search-panel"', index)
        self.assertIn('id="np_resume_spot_banner"', index)
        self.assertIn("data-view-error", index)
        for name in ("discover-preview.js", "fuzzy.js", "download-batch.js", "chunked-list.js", "ai-dj-voice.js", "ai-dj-recorder.js", "ai-dj-state.js", "app-update.js", "platform.js"):
            self.assertTrue((self.root / "ui" / "js" / name).exists(), name)
        for view in ("discover.html", "ai_dj.html"):
            self.assertTrue((self.root / "ui" / "views" / view).exists(), view)
        self.assertTrue((self.root / "ui" / "assets" / "vohithe-portrait.jpg").exists())
        discover = (self.root / "ui" / "views" / "discover.html").read_text(encoding="utf-8")
        self.assertIn('id="discover-grid"', discover)
        self.assertIn('id="discover-source-filters"', discover)
        ai_dj = (self.root / "ui" / "views" / "ai_dj.html").read_text(encoding="utf-8")
        self.assertIn('id="ai-dj-visualizer"', ai_dj)
        self.assertIn('id="ai-dj-brief"', ai_dj)
        home = (self.root / "ui" / "views" / "home.html").read_text(encoding="utf-8")
        self.assertIn('id="home-card-ai-dj"', home)
        self.assertIn('id="home-card-podcasts"', home)
        self.assertIn('id="home-daily-play"', home)
        self.assertIn('id="home-podcasts-play"', home)
        podcasts = (self.root / "ui" / "views" / "podcasts.html").read_text(encoding="utf-8")
        self.assertIn('id="podm-move-song"', podcasts)
        playlist_view = (self.root / "ui" / "views" / "playlist_view.html").read_text(encoding="utf-8")
        self.assertIn("removePlaylistCover", playlist_view)
        playlist_home = (self.root / "ui" / "views" / "playlist_home.html").read_text(encoding="utf-8")
        self.assertIn("clearPlaylistCover", playlist_home)
        self.assertIn('id="av-shuffle-btn"', (self.root / "ui" / "views" / "artist.html").read_text(encoding="utf-8"))
        for view in ("artists.html", "albums.html"):
            self.assertIn(
                "renderChunkedList",
                (self.root / "ui" / "views" / view).read_text(encoding="utf-8"),
            )

    def test_settings_exposes_web_api_controls(self):
        settings = (self.root / "ui" / "views" / "settings.html").read_text(encoding="utf-8")
        for element_id in (
            "api-url-input",
            "api-username-input",
            "api-password-input",
            "api-remember-input",
            "api-key-input",
            "api-key-save-btn",
            "api-config-status",
            "api-transport-warning",
            "api-save-btn",
            "api-logout-btn",
            "api-test-btn",
            "api-clear-btn",
            "app-update-version",
            "app-update-status",
            "app-update-check-btn",
            "app-update-install-btn",
            "app-update-remove-btn",
        ):
            self.assertIn(f'id="{element_id}"', settings)


if __name__ == "__main__":
    unittest.main()

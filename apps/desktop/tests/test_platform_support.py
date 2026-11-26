import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from services import platform_support as ps


class PlatformPathTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.app = self.root / "app"
        self.install = self.root / "install"
        self.home = self.root / "home"
        self.app.mkdir()
        self.install.mkdir()
        self.home.mkdir()
        self.env = mock.patch.dict(os.environ, {}, clear=False)
        self.env.start()

    def tearDown(self):
        self.env.stop()
        self.tmp.cleanup()

    def _clear(self, *names):
        for name in names:
            os.environ.pop(name, None)

    def test_windows_keeps_portable_layout(self):
        local = self.root / "local"
        os.environ["LOCALAPPDATA"] = str(local)
        self._clear("HATHOR_DATA_DIR", "HATHOR_CONFIG_DIR", "HATHOR_CACHE_DIR",
                    "HATHOR_STATE_DIR", "HATHOR_PORTABLE", "HATHOR_PACKAGE_MANAGED")
        with mock.patch.object(ps, "IS_WINDOWS", True), \
                mock.patch.object(ps, "is_frozen", return_value=True), \
                mock.patch.object(ps, "app_root", return_value=self.app), \
                mock.patch.object(ps, "install_dir", return_value=self.install):
            self.assertEqual(ps.data_dir(), self.install)
            self.assertEqual(ps.config_dir(), self.install)
            self.assertEqual(ps.db_path(), self.install / "music_player.db")
            self.assertEqual(ps.log_path(), self.install / "hathor.log")
            self.assertEqual(ps.cache_dir(), local / "Hathor")
            self.assertEqual(ps.updates_dir(), local / "Hathor" / "updates")

    def test_linux_uses_xdg_directories(self):
        xdg = self.root / "xdg"
        os.environ.update({
            "XDG_DATA_HOME": str(xdg / "data"),
            "XDG_CONFIG_HOME": str(xdg / "config"),
            "XDG_CACHE_HOME": str(xdg / "cache"),
            "XDG_STATE_HOME": str(xdg / "state"),
        })
        self._clear("HATHOR_DATA_DIR", "HATHOR_CONFIG_DIR", "HATHOR_CACHE_DIR",
                    "HATHOR_STATE_DIR", "HATHOR_PORTABLE", "HATHOR_PACKAGE_MANAGED")
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "IS_LINUX", True), \
                mock.patch.object(ps, "is_frozen", return_value=False), \
                mock.patch.object(ps, "app_root", return_value=self.app), \
                mock.patch.object(ps, "install_dir", return_value=self.install), \
                mock.patch.object(ps, "_home", return_value=self.home):
            self.assertEqual(ps.data_dir(), xdg / "data" / "hathor")
            self.assertEqual(ps.config_dir(), xdg / "config" / "hathor")
            self.assertEqual(ps.cache_dir(), xdg / "cache" / "hathor")
            self.assertEqual(ps.state_dir(), xdg / "state" / "hathor")
            self.assertEqual(ps.db_path(), xdg / "data" / "hathor" / "music_player.db")
            self.assertEqual(ps.log_path(), xdg / "state" / "hathor" / "hathor.log")
            self.assertEqual(ps.updates_dir(), xdg / "cache" / "hathor" / "updates")

    def test_explicit_directory_overrides(self):
        custom = self.root / "custom"
        os.environ.update({
            "HATHOR_DATA_DIR": str(custom / "data"),
            "HATHOR_CONFIG_DIR": str(custom / "config"),
            "HATHOR_CACHE_DIR": str(custom / "cache"),
            "HATHOR_STATE_DIR": str(custom / "state"),
        })
        self.assertEqual(ps.data_dir(), custom / "data")
        self.assertEqual(ps.config_dir(), custom / "config")
        self.assertEqual(ps.cache_dir(), custom / "cache")
        self.assertEqual(ps.state_dir(), custom / "state")

    def test_portable_mode_wins_on_linux(self):
        os.environ["HATHOR_PORTABLE"] = "1"
        self._clear("HATHOR_DATA_DIR", "HATHOR_CONFIG_DIR", "HATHOR_CACHE_DIR",
                    "HATHOR_STATE_DIR", "HATHOR_PACKAGE_MANAGED")
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "app_root", return_value=self.app), \
                mock.patch.object(ps, "install_dir", return_value=self.install):
            self.assertEqual(ps.data_dir(), self.install)
            self.assertEqual(ps.config_dir(), self.install)
            self.assertEqual(ps.state_dir(), self.install)

    def test_package_managed_by_env_or_marker(self):
        self._clear("HATHOR_PACKAGE_MANAGED", "HATHOR_INSTALLER_MANAGED")
        with mock.patch.object(ps, "app_root", return_value=self.app):
            self.assertFalse(ps.package_managed())
            (self.app / ps.PACKAGE_MARKER).write_text("", encoding="utf-8")
            self.assertTrue(ps.package_managed())
        os.environ["HATHOR_PACKAGE_MANAGED"] = "1"
        self.assertTrue(ps.package_managed())

    def test_installer_managed_by_env_or_marker(self):
        self._clear("HATHOR_PACKAGE_MANAGED", "HATHOR_INSTALLER_MANAGED")
        with mock.patch.object(ps, "app_root", return_value=self.app):
            self.assertFalse(ps.installer_managed())
            self.assertFalse(ps.externally_managed())
            (self.app / ps.INSTALLER_MARKER).write_text("", encoding="utf-8")
            self.assertTrue(ps.installer_managed())
            self.assertTrue(ps.externally_managed())
        os.environ["HATHOR_INSTALLER_MANAGED"] = "1"
        self.assertTrue(ps.installer_managed())

    def test_python_self_update_policy(self):
        with mock.patch.object(ps, "IS_WINDOWS", True), \
                mock.patch.object(ps, "is_frozen", return_value=False), \
                mock.patch.object(ps, "installer_managed", return_value=False), \
                mock.patch.object(ps, "package_managed", return_value=False):
            self.assertTrue(ps.python_self_update_allowed())
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "is_frozen", return_value=False), \
                mock.patch.object(ps, "installer_managed", return_value=False), \
                mock.patch.object(ps, "package_managed", return_value=False), \
                mock.patch.object(ps.sys, "prefix", "C:/Python"), \
                mock.patch.object(ps.sys, "base_prefix", "C:/Python"):
            self.assertFalse(ps.python_self_update_allowed())
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "is_frozen", return_value=False), \
                mock.patch.object(ps, "installer_managed", return_value=False), \
                mock.patch.object(ps, "package_managed", return_value=False), \
                mock.patch.object(ps.sys, "prefix", "C:/venv"), \
                mock.patch.object(ps.sys, "base_prefix", "C:/Python"):
            self.assertTrue(ps.python_self_update_allowed())
        # Installer-managed: only the private venv may pip-update.
        with mock.patch.object(ps, "is_frozen", return_value=False), \
                mock.patch.object(ps, "installer_managed", return_value=True), \
                mock.patch.object(ps, "package_managed", return_value=True), \
                mock.patch.object(ps.sys, "prefix", "/usr/lib/python3.14"), \
                mock.patch.object(ps.sys, "base_prefix", "/usr/lib/python3.14"):
            self.assertFalse(ps.python_self_update_allowed())
        with mock.patch.object(ps, "is_frozen", return_value=False), \
                mock.patch.object(ps, "installer_managed", return_value=True), \
                mock.patch.object(ps, "package_managed", return_value=True), \
                mock.patch.object(ps.sys, "prefix", "/home/u/.local/opt/hathor/venv"), \
                mock.patch.object(ps.sys, "base_prefix", "/usr/lib/python3.14"):
            self.assertTrue(ps.python_self_update_allowed())

    def test_update_mode_and_window_capabilities(self):
        self._clear("HATHOR_PACKAGE_MANAGED", "HATHOR_INSTALLER_MANAGED")
        with mock.patch.object(ps, "IS_WINDOWS", True), \
                mock.patch.object(ps, "app_root", return_value=self.app):
            self.assertEqual(ps.update_mode(), "self")
            caps = ps.window_capabilities()
            self.assertTrue(caps["customChrome"])
            self.assertFalse(caps["nativeDecorations"])
            self.assertTrue(caps["frameless"])
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "IS_LINUX", True), \
                mock.patch.object(ps, "app_root", return_value=self.app):
            self.assertEqual(ps.update_mode(), "manual")
            caps = ps.window_capabilities()
            self.assertFalse(caps["customChrome"])
            self.assertTrue(caps["nativeDecorations"])
            self.assertFalse(caps["frameless"])
            self.assertEqual(ps.webview_gui(), "gtk")
        os.environ["HATHOR_PACKAGE_MANAGED"] = "1"
        self.assertEqual(ps.update_mode(), "package")
        os.environ["HATHOR_INSTALLER_MANAGED"] = "1"
        self.assertEqual(ps.update_mode(), "installer")

    def test_default_library_directories(self):
        os.environ["XDG_CONFIG_HOME"] = str(self.root / "config")
        self._clear("HATHOR_MUSIC_DIR")
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "_home", return_value=self.home):
            self.assertEqual(ps.default_music_dir(), self.home / "Music" / "Hathor")
            self.assertEqual(
                ps.default_podcasts_dir(),
                self.home / "Music" / "Hathor Podcasts",
            )

    def test_env_files_are_unique_and_user_config_precedes_bundle(self):
        config = self.root / "config"
        os.environ["XDG_CONFIG_HOME"] = str(config)
        self._clear("HATHOR_CONFIG_DIR", "HATHOR_PORTABLE")
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "app_root", return_value=self.app), \
                mock.patch.object(ps, "install_dir", return_value=self.install), \
                mock.patch.object(ps, "config_dir", return_value=config / "hathor"):
            files = ps.env_files()
        self.assertEqual(len(files), len({str(p.resolve()) for p in files}))
        self.assertLess(files.index(config / "hathor" / ".env"),
                        files.index(self.app / ".env"))

    def test_migrates_legacy_db_and_env(self):
        old_db = self.install / "music_player.db"
        old_db.write_bytes(b"legacy-db")
        old_env = self.install / ".env"
        old_env.write_text("HATHOR_API_URL=http://old\n", encoding="utf-8")
        target_root = self.root / "target"
        with mock.patch.object(ps, "IS_WINDOWS", False), \
                mock.patch.object(ps, "db_path", return_value=target_root / "music_player.db"), \
                mock.patch.object(ps, "config_dir", return_value=target_root / "config"), \
                mock.patch.object(ps, "app_root", return_value=self.app), \
                mock.patch.object(ps, "install_dir", return_value=self.install):
            result = ps.migrate_legacy_data()
        self.assertEqual((target_root / "music_player.db").read_bytes(), b"legacy-db")
        self.assertTrue((target_root / "config" / ".env").is_file())
        self.assertEqual(result["migrated_db"], str(old_db))
        self.assertEqual(result["migrated_env"], str(old_env))

    def test_ensure_runtime_dirs_creates_targets(self):
        data = self.root / "data"
        config = self.root / "config"
        cache = self.root / "cache"
        state = self.root / "state"
        with mock.patch.object(ps, "data_dir", return_value=data), \
                mock.patch.object(ps, "config_dir", return_value=config), \
                mock.patch.object(ps, "cache_dir", return_value=cache), \
                mock.patch.object(ps, "state_dir", return_value=state):
            result = ps.ensure_runtime_dirs()
        for path in (data, config, cache, state):
            self.assertTrue(path.is_dir())
        self.assertNotIn("error:", " ".join(result.values()))


if __name__ == "__main__":
    unittest.main()

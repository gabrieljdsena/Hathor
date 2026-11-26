import unittest
from pathlib import Path


class PackagingTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(__file__).resolve().parents[1]

    def test_windows_spec_includes_lazy_services(self):
        spec = (self.root / "music_player.spec").read_text(encoding="utf-8")
        for name in (
            "services.playback",
            "services.metadata",
            "services.database",
            "services.windows_media",
            "services.lyrics",
            "services.apple",
            "services.downloads",
            "services.discover",
            "services.ai_dj",
            "services.app_update",
            "services.startup_maintenance",
            "services.api_sync",
            "services.api_config",
            "services.hathor_api",
            "services.history_sync",
            "services.player_hub",
            "services.telemetry",
            "services.images",
            "services.platform_support",
            "services.window_chrome",
        ):
            self.assertIn(f"'{name}'", spec, name)
        self.assertNotIn("('node_modules'", spec)

    def test_frontend_assets_are_vendored_under_ui(self):
        for relative in (
            "ui/notyf.min.css",
            "ui/notyf.min.js",
            "ui/cdn.min.js",
            "ui/sweetalert2/dist/sweetalert2.all.min.js",
            "ui/js/platform.js",
            "ui/js/app-update.js",
        ):
            self.assertTrue((self.root / relative).is_file(), relative)

        index = (self.root / "ui" / "index.html").read_text(encoding="utf-8")
        for asset in (
            "./notyf.min.css",
            "./notyf.min.js",
            "./cdn.min.js",
            "./sweetalert2/dist/sweetalert2.all.min.js",
        ):
            self.assertIn(asset, index)
        self.assertNotIn('src="../cdn.min.js"', index)

    def test_linux_installer_is_bundled_with_releases(self):
        installer = self.root / "scripts" / "install-linux.sh"
        self.assertTrue(installer.is_file(), "install-linux.sh is missing")
        text = installer.read_text(encoding="utf-8")
        for marker in (
            "HATHOR_INSTALLER_MANAGED=1",
            "--update",
            "--uninstall",
            "--install-deps",
            "SHA256SUMS.txt",
            "/usr/bin/python3",
        ):
            self.assertIn(marker, text)

        packager = (self.root / "scripts" / "package-linux.sh").read_text(
            encoding="utf-8"
        )
        self.assertIn("install-linux.sh", packager)
        self.assertIn("versionCode", packager)
        self.assertIn("/usr/bin/python3", packager)
        self.assertIn("README-LINUX.txt", packager)

    def test_arch_package_uses_system_python_and_webkit(self):
        arch = self.root / "packaging" / "arch"
        pkgbuild = (arch / "PKGBUILD").read_text(encoding="utf-8")
        for dependency in (
            "python-pywebview",
            "python-gobject",
            "webkit2gtk-4.1",
            "python-pygame",
            "yt-dlp",
            "ffmpeg",
        ):
            self.assertIn(dependency, pkgbuild)
        self.assertNotIn("python -m pyinstaller", pkgbuild.lower())
        self.assertNotIn("pyinstaller music_player.spec", pkgbuild.lower())
        self.assertIn("HATHOR_PACKAGE_MANAGED=1",
                      (arch / "hathor").read_text(encoding="utf-8"))
        self.assertTrue((arch / "hathor.desktop").is_file())
        self.assertTrue((arch / "hathor.png").is_file())


if __name__ == "__main__":
    unittest.main()

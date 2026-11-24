import hashlib
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

from services.app_update import AppUpdateService, PACKAGE_RE
from tests.support import FakeHathorServer


def make_package(code=2, name="1.0.1-hathor", exe=True, extra=None):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        if exe:
            archive.writestr("Hathor.exe", b"MZ" + b"0" * 64)
        archive.writestr(
            "version.json",
            json.dumps({"versionCode": code, "versionName": name}),
        )
        archive.writestr("_internal/runtime.txt", b"runtime")
        for member, data in (extra or {}).items():
            archive.writestr(member, data)
    return buffer.getvalue()


def release_payload(package, code=2, name="1.0.1-hathor", sha=None):
    file_name = f"hathor-desktop-{code}-{name}.zip"
    return {
        "versionCode": code,
        "versionName": name,
        "fileName": file_name,
        "sizeBytes": len(package),
        "sha256": sha or hashlib.sha256(package).hexdigest(),
        "publishedAtUtc": "2026-11-23T12:00:00+00:00",
        "downloadPath": f"/api/v1/desktop/download/{file_name}",
    }


def github_release(
    package,
    code=2,
    name="1.0.1-hathor",
    download_url="https://example.invalid/package.zip",
    digest=True,
):
    file_name = f"hathor-desktop-{code}-{name}.zip"
    asset = {
        "name": file_name,
        "size": len(package),
        "browser_download_url": download_url,
    }
    if digest:
        asset["digest"] = "sha256:" + hashlib.sha256(package).hexdigest()
    return {
        "tag_name": f"desktop-v{name.split('-')[0]}",
        "draft": False,
        "prerelease": True,
        "published_at": "2026-11-23T12:00:00Z",
        "assets": [asset],
    }


class AppUpdateServiceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.install = self.root / "install"
        self.updates = self.root / "updates"
        self.install.mkdir()
        self.updates.mkdir()
        self.db = self.root / "missing.db"

    def tearDown(self):
        self.tmp.cleanup()

    def service(self, code=1, frozen=False, github_api=None):
        return AppUpdateService(
            str(self.db),
            install_dir=self.install,
            updates_dir=self.updates,
            current={"versionCode": code, "versionName": f"{code}.0.0-hathor"},
            frozen=frozen,
            github_api=github_api,
        )

    def test_version_json_and_package_name_contract(self):
        (self.install / "version.json").write_text(
            json.dumps({"versionCode": 7, "versionName": "1.0.7-hathor"}),
            encoding="utf-8",
        )
        service = AppUpdateService(
            str(self.db), install_dir=self.install, updates_dir=self.updates
        )
        self.assertEqual(service.current["versionCode"], 7)
        self.assertEqual(service.current["versionName"], "1.0.7-hathor")
        self.assertTrue(PACKAGE_RE.match("hathor-desktop-7-1.0.7-hathor.zip"))
        self.assertFalse(PACKAGE_RE.match("hathor-desktop-7-1.0.7-hathor.zip.part"))

    def test_package_managed_install_does_not_use_self_updater(self):
        with mock.patch("services.platform_support.package_managed", return_value=True):
            service = self.service()
            status = service.status()
            checked = service.check()
            installed = service.install()
        self.assertTrue(status["managed"])
        self.assertEqual(status["updateMode"], "package")
        self.assertFalse(status["installable"])
        self.assertIn("package manager", status["message"])
        self.assertTrue(checked["managed"])
        self.assertFalse(installed["ok"])
        self.assertIn("package manager", installed["message"])

    def test_check_reports_update_and_sends_auth(self):
        package = make_package()
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            service = self.service()
            status = service.check(server.base_url, "hth_secret")

        self.assertTrue(status["ok"])
        self.assertTrue(status["updateAvailable"])
        self.assertEqual(status["latest"]["versionCode"], 2)
        self.assertEqual(server.requests[-1]["headers"].get("Authorization"),
                         "Bearer hth_secret")

    def test_check_same_version_is_up_to_date(self):
        package = make_package()
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            status = self.service(code=2).check(server.base_url, "hth_secret")

        self.assertTrue(status["ok"])
        self.assertFalse(status["updateAvailable"])
        self.assertIn("Up to date", status["message"])

    def test_check_404_keeps_running(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", status=404,
                       json_body={"message": "none"})
            status = self.service().check(server.base_url, "hth_secret")

        self.assertTrue(status["ok"])
        self.assertFalse(status["updateAvailable"])
        self.assertIn("No desktop release", status["message"])

    def test_download_verifies_and_stages_package(self):
        package = make_package()
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            server.add("GET", payload["downloadPath"], body=package,
                       content_type="application/zip")
            service = self.service()
            service.check(server.base_url, "hth_secret")
            status = service.download()

        self.assertTrue(status["ok"])
        self.assertTrue(status["downloaded"])
        self.assertEqual(status["cachedBytes"], len(package))
        zip_path = self.updates / payload["fileName"]
        self.assertTrue(zip_path.is_file())
        stage = self.updates / "stage-2"
        self.assertTrue((stage / "Hathor.exe").is_file())
        self.assertTrue((stage / "_internal" / "runtime.txt").is_file())
        self.assertTrue((stage / "version.json").is_file())

    def test_download_rejects_bad_hash_and_removes_partial(self):
        package = make_package()
        payload = release_payload(package, sha="0" * 64)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            server.add("GET", payload["downloadPath"], body=package,
                       content_type="application/zip")
            service = self.service()
            service.check(server.base_url, "hth_secret")
            status = service.download()

        self.assertFalse(status["ok"])
        self.assertFalse(status["downloaded"])
        self.assertFalse((self.updates / payload["fileName"]).exists())
        self.assertFalse(list(self.updates.glob("*.hathor-part")))

    def test_download_rejects_zip_traversal(self):
        package = make_package(extra={"../evil.txt": b"nope"})
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            server.add("GET", payload["downloadPath"], body=package,
                       content_type="application/zip")
            service = self.service()
            service.check(server.base_url, "hth_secret")
            status = service.download()

        self.assertFalse(status["ok"])
        self.assertFalse((self.root / "evil.txt").exists())
        self.assertFalse((self.updates / "stage-2").exists())

    def test_download_rejects_missing_executable(self):
        package = make_package(exe=False)
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            server.add("GET", payload["downloadPath"], body=package,
                       content_type="application/zip")
            service = self.service()
            service.check(server.base_url, "hth_secret")
            status = service.download()

        self.assertFalse(status["ok"])
        self.assertFalse((self.updates / "stage-2").exists())

    def test_cleanup_removes_obsolete_package_and_partials(self):
        obsolete = self.updates / "hathor-desktop-1-1.0.0-hathor.zip"
        obsolete.write_bytes(b"old")
        keep = self.updates / "hathor-desktop-2-1.0.1-hathor.zip"
        keep.write_bytes(b"new")
        older = self.updates / "hathor-desktop-3-1.0.2-hathor.zip"
        older.write_bytes(b"newer")
        partial = self.updates / "hathor-desktop-4-1.0.3-hathor.zip.hathor-part"
        partial.write_bytes(b"partial")
        (self.updates / "stage-1").mkdir()
        (self.updates / "stage-2").mkdir()
        (self.updates / "stage-3").mkdir()
        (self.updates / ".stage-3-abc.tmp").mkdir()
        (self.updates / "last-result.json").write_text(
            json.dumps({"ok": True, "message": "done"}), encoding="utf-8"
        )

        self.service(code=1).cleanup()

        self.assertFalse(obsolete.exists())
        self.assertFalse(keep.exists())
        self.assertTrue(older.exists())
        self.assertFalse(partial.exists())
        self.assertFalse((self.updates / "stage-1").exists())
        self.assertFalse((self.updates / "stage-2").exists())
        self.assertTrue((self.updates / "stage-3").exists())
        self.assertFalse((self.updates / ".stage-3-abc.tmp").exists())
        self.assertFalse((self.updates / "last-result.json").exists())

    def test_failed_result_survives_a_new_check(self):
        (self.updates / "last-result.json").write_text(
            json.dumps({"ok": False, "message": "disk full"}),
            encoding="utf-8-sig",
        )
        package = make_package()
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            status = self.service().check(server.base_url, "hth_secret")
        self.assertFalse(status["ok"])
        self.assertIn("disk full", status["message"])

    def test_reads_powershell_bom_result(self):
        (self.updates / "last-result.json").write_text(
            json.dumps({"ok": False, "message": "disk full"}),
            encoding="utf-8-sig",
        )
        status = self.service().status()
        self.assertFalse(status["ok"])
        self.assertIn("disk full", status["message"])

    def test_remove_clears_package_and_stage(self):
        package = make_package()
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            server.add("GET", payload["downloadPath"], body=package,
                       content_type="application/zip")
            service = self.service()
            service.check(server.base_url, "hth_secret")
            service.download()

        status = service.remove()

        self.assertFalse(status["downloaded"])
        self.assertEqual(status["cachedBytes"], 0)
        self.assertFalse(list(self.updates.glob("*.zip")))
        self.assertFalse((self.updates / "stage-2").exists())

    def test_install_is_rejected_outside_frozen_build(self):
        package = make_package()
        payload = release_payload(package)
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            server.add("GET", payload["downloadPath"], body=package,
                       content_type="application/zip")
            service = self.service(frozen=False)
            service.check(server.base_url, "hth_secret")
            service.download()
            status = service.install()

        self.assertFalse(status["ok"])
        self.assertIn("packaged Hathor build", status["message"])

    def test_github_check_and_download_without_web_api(self):
        package = make_package()
        with FakeHathorServer() as server:
            item = github_release(
                package,
                code=2,
                name="1.0.1-hathor",
                download_url=server.base_url + "/download/pkg.zip",
            )
            server.add("GET", "/releases", json_body=[item])
            server.add("GET", "/download/pkg.zip", body=package,
                       content_type="application/zip")
            service = self.service(github_api=server.base_url + "/releases")
            status = service.check()
            downloaded = service.download()

        self.assertTrue(status["ok"])
        self.assertTrue(status["updateAvailable"])
        self.assertEqual(status["latest"]["versionCode"], 2)
        self.assertEqual(status["source"], "github")
        self.assertIsNone(server.requests[0]["headers"].get("Authorization"))
        self.assertTrue(downloaded["downloaded"])
        self.assertTrue((self.updates / "stage-2" / "Hathor.exe").is_file())

    def test_github_selects_highest_desktop_and_ignores_android(self):
        package = make_package()
        with FakeHathorServer() as server:
            feed = [
                github_release(package, code=2, name="1.0.1-hathor"),
                github_release(package, code=3, name="1.0.2-hathor"),
                {
                    "tag_name": "android-v9.9.9",
                    "draft": False,
                    "prerelease": False,
                    "assets": [{
                        "name": "hathor-android-99-9.9.9-hathor.apk",
                        "size": len(package),
                        "browser_download_url": "https://example.invalid/app.apk",
                        "digest": "sha256:" + hashlib.sha256(package).hexdigest(),
                    }],
                },
            ]
            server.add("GET", "/releases", json_body=feed)
            status = self.service(github_api=server.base_url + "/releases").check()

        self.assertTrue(status["updateAvailable"])
        self.assertEqual(status["latest"]["versionCode"], 3)
        self.assertEqual(status["latest"]["versionName"], "1.0.2-hathor")

    def test_github_sha256sums_fallback(self):
        package = make_package()
        digest = hashlib.sha256(package).hexdigest()
        with FakeHathorServer() as server:
            item = github_release(
                package,
                code=2,
                name="1.0.1-hathor",
                download_url=server.base_url + "/download/pkg.zip",
                digest=False,
            )
            sums = f"{digest}  hathor-desktop-2-1.0.1-hathor.zip\n"
            item["assets"].append({
                "name": "SHA256SUMS.txt",
                "size": len(sums),
                "browser_download_url": server.base_url + "/sums",
            })
            server.add("GET", "/releases", json_body=[item])
            server.add("GET", "/sums", body=sums, content_type="text/plain")
            status = self.service(github_api=server.base_url + "/releases").check()

        self.assertTrue(status["updateAvailable"])
        self.assertEqual(status["latest"]["sha256"], digest)

    def test_github_empty_feed_reports_no_release(self):
        with FakeHathorServer() as server:
            server.add("GET", "/releases", json_body=[])
            status = self.service(github_api=server.base_url + "/releases").check()

        self.assertTrue(status["ok"])
        self.assertFalse(status["updateAvailable"])
        self.assertIn("No desktop release", status["message"])

    def test_release_path_cannot_escape_the_server(self):
        package = make_package()
        payload = release_payload(package)
        payload["downloadPath"] = "http://evil.test/hathor-desktop-2-1.0.1-hathor.zip"
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/desktop/latest", json_body=payload)
            status = self.service().check(server.base_url, "hth_secret")

        self.assertFalse(status["ok"])
        self.assertIn("download path", status["message"])


if __name__ == "__main__":
    unittest.main()

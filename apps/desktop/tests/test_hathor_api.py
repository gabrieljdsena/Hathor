import os
import tempfile
import unittest

from services.hathor_api import HathorApi, HathorApiError
from tests.support import FakeHathorServer


class HathorApiTests(unittest.TestCase):
    def test_get_sends_auth_and_query(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/echo", json_body={"ok": True})
            client = HathorApi(server.base_url, "hth_secret")
            result = client.get("/api/v1/echo", query={"q": "a b"})

            self.assertEqual(result, {"ok": True})
            request = server.requests[-1]
            self.assertEqual(request["route"], "/api/v1/echo")
            self.assertEqual(request["query"], "q=a+b")
            self.assertEqual(request["headers"].get("Authorization"), "Bearer hth_secret")
            self.assertEqual(request["headers"].get("User-Agent"), "hathor-desktop/1")

    def test_post_json_and_empty_response(self):
        with FakeHathorServer() as server:
            server.add("POST", "/api/v1/thing", status=204)
            client = HathorApi(server.base_url, "hth_secret")
            result = client.post("/api/v1/thing", payload={"title": "Hello"})

            self.assertEqual(result, {})
            request = server.requests[-1]
            self.assertEqual(request["body"], b'{"title": "Hello"}')
            self.assertEqual(
                request["headers"].get("Content-Type"),
                "application/json; charset=utf-8",
            )

    def test_http_error_is_user_facing(self):
        with FakeHathorServer() as server:
            server.add("GET", "/api/v1/nope", status=401,
                       json_body={"message": "Invalid API key."})
            client = HathorApi(server.base_url, "hth_bad")
            with self.assertRaises(HathorApiError) as ctx:
                client.get("/api/v1/nope")

            self.assertEqual(ctx.exception.status, 401)
            self.assertIn("rejected the API key", str(ctx.exception))
            self.assertNotIn("hth_bad", str(ctx.exception))

    def test_network_error_mentions_base_url(self):
        client = HathorApi("http://127.0.0.1:1", "hth_secret", timeout=0.2)
        with self.assertRaises(HathorApiError) as ctx:
            client.get("/api/v1/anything")
        self.assertIn("Cannot reach Hathor API", str(ctx.exception))

    def test_media_url_includes_token(self):
        client = HathorApi("http://example.test:5051/", "hth_secret")
        url = client.media_url("/api/v1/ai-dj/clips/abc/audio", {"x": "1"})
        self.assertEqual(
            url,
            "http://example.test:5051/api/v1/ai-dj/clips/abc/audio?x=1&token=hth_secret",
        )

    def test_stream_to_file_and_upload(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            server.add("GET", "/api/v1/file", body=b"audio-bytes",
                       content_type="audio/mpeg")
            server.add("PUT", "/api/v1/file", json_body={"ok": True})
            client = HathorApi(server.base_url, "hth_secret")
            dest = os.path.join(tmp, "song.mp3")

            client.stream_to_file("/api/v1/file", dest)
            with open(dest, "rb") as fh:
                self.assertEqual(fh.read(), b"audio-bytes")
            self.assertEqual(server.requests[-1]["headers"].get("Authorization"),
                             "Bearer hth_secret")

            src = os.path.join(tmp, "upload.mp3")
            with open(src, "wb") as fh:
                fh.write(b"upload-bytes")
            result = client.upload_file("/api/v1/file", src)
            self.assertEqual(result, {"ok": True})
            self.assertEqual(server.requests[-1]["body"], b"upload-bytes")
            self.assertEqual(server.requests[-1]["headers"].get("Content-Type"),
                             "audio/mpeg")

    def test_absolute_urls_are_rejected(self):
        client = HathorApi("http://example.test", "hth_secret")
        with self.assertRaises(HathorApiError):
            client.get("http://other.test/api/v1/anything")


if __name__ == "__main__":
    unittest.main()

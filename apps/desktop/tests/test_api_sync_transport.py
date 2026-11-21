import json
import tempfile
import unittest
from unittest.mock import patch

from services.api_sync import ApiSyncClient, ApiSyncError, SERVER_MISSING
from tests.support import FakeHathorServer


class ApiSyncTransportTests(unittest.TestCase):
    def test_delta_uses_shared_client_and_path(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            server.add("GET", "/api/v1/sync/delta",
                       json_body={"cursor": "next", "songs": []})
            with patch("services.api_sync.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                client = ApiSyncClient(tmp)
                result = client.get_delta("")

            self.assertEqual(result["cursor"], "next")
            request = server.requests[-1]
            self.assertEqual(request["route"], "/api/v1/sync/delta")
            self.assertEqual(request["headers"].get("Authorization"), "Bearer hth_secret")

    def test_missing_file_endpoint_maps_to_friendly_error(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            # No /api/v1/sync/delta route -> 404.
            with patch("services.api_sync.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                client = ApiSyncClient(tmp)
                with self.assertRaises(ApiSyncError) as ctx:
                    client.get_delta("")

            self.assertEqual(str(ctx.exception), SERVER_MISSING)


    def test_playback_spot_carries_queue_snapshot(self):
        with FakeHathorServer() as server, tempfile.TemporaryDirectory() as tmp:
            server.add("POST", "/api/v1/sync/playback", json_body=True)
            with patch("services.api_sync.get_api_config",
                       return_value=(server.base_url, "hth_secret")):
                client = ApiSyncClient(tmp)
                client.push_playback(
                    "device-1", "Windows", "a.mp3", 42.5, False,
                    queue_files=["a.mp3", "b.mp3"],
                    queue_source={"type": "album", "id": "Album"},
                )

            body = json.loads(server.requests[-1]["body"])
            self.assertEqual(body["queueFiles"], ["a.mp3", "b.mp3"])
            self.assertEqual(body["queueSource"], {"type": "album", "id": "Album"})
            self.assertEqual(body["positionSec"], 42.5)


if __name__ == "__main__":
    unittest.main()

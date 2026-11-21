import base64
import io
import unittest

from services.images import normalize_image_data_uri

try:
    from PIL import Image
    HAS_PILLOW = True
except Exception:
    HAS_PILLOW = False


def _data_uri(image, fmt="PNG"):
    buf = io.BytesIO()
    image.save(buf, fmt)
    return "data:image/png;base64," + base64.b64encode(buf.getvalue()).decode("ascii")


@unittest.skipUnless(HAS_PILLOW, "Pillow is not installed")
class PlaylistCoverTests(unittest.TestCase):
    def test_large_image_is_downscaled_to_jpeg(self):
        uri = _data_uri(Image.new("RGB", (2000, 1000), (10, 20, 30)))
        out = normalize_image_data_uri(uri)

        self.assertTrue(out.startswith("data:image/jpeg;base64,"))
        raw = base64.b64decode(out.split(",", 1)[1])
        with Image.open(io.BytesIO(raw)) as image:
            self.assertEqual(image.format, "JPEG")
            self.assertEqual(image.size, (800, 400))

    def test_small_image_keeps_size(self):
        uri = _data_uri(Image.new("RGB", (320, 200), (200, 100, 50)))
        out = normalize_image_data_uri(uri, max_edge=800)
        raw = base64.b64decode(out.split(",", 1)[1])
        with Image.open(io.BytesIO(raw)) as image:
            self.assertEqual(image.size, (320, 200))

    def test_transparent_image_flattens_to_white(self):
        uri = _data_uri(Image.new("RGBA", (100, 100), (255, 0, 0, 0)))
        out = normalize_image_data_uri(uri)
        raw = base64.b64decode(out.split(",", 1)[1])
        with Image.open(io.BytesIO(raw)) as image:
            self.assertEqual(image.mode, "RGB")
            self.assertEqual(image.getpixel((50, 50)), (255, 255, 255))

    def test_non_image_values_are_untouched(self):
        self.assertIsNone(normalize_image_data_uri(None))
        self.assertEqual(normalize_image_data_uri("REMOVE"), "REMOVE")
        self.assertEqual(normalize_image_data_uri("data:text/plain;base64,QQ=="),
                         "data:text/plain;base64,QQ==")


if __name__ == "__main__":
    unittest.main()

"""Small image helpers for playlist covers.

Covers live in SQLite and sync payloads, so a 12 MP phone photo must never be
stored verbatim. Pillow is imported lazily: the rest of the desktop app (and
the stdlib-compatible test suite) can import this module without it.
"""

from __future__ import annotations

import base64
import io


def normalize_image_data_uri(data_uri, max_edge=800, quality=85):
    """Downscale a data-URI image to JPEG on the long edge (if Pillow exists).

    Returns the original value when it is not a base64 image, Pillow is not
    installed, or decoding fails. Transparent images are flattened over white
    because JPEG has no alpha channel.
    """
    if not isinstance(data_uri, str) or not data_uri.startswith("data:image/"):
        return data_uri
    if ";base64," not in data_uri:
        return data_uri
    try:
        from PIL import Image, ImageOps
    except Exception:
        return data_uri

    try:
        header, encoded = data_uri.split(",", 1)
        raw = base64.b64decode(encoded, validate=False)
        with Image.open(io.BytesIO(raw)) as opened:
            image = ImageOps.exif_transpose(opened)
            if max_edge:
                image.thumbnail((int(max_edge), int(max_edge)), Image.LANCZOS)
            if image.mode in ("RGBA", "LA") or (
                image.mode == "P" and "transparency" in image.info
            ):
                rgba = image.convert("RGBA")
                canvas = Image.new("RGB", rgba.size, (255, 255, 255))
                canvas.paste(rgba, mask=rgba.getchannel("A"))
                image = canvas
            else:
                image = image.convert("RGB")
            out = io.BytesIO()
            image.save(out, "JPEG", quality=int(quality), optimize=True)
        return "data:image/jpeg;base64," + base64.b64encode(out.getvalue()).decode("ascii")
    except Exception:
        return data_uri

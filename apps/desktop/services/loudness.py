"""Per-track loudness (web parity): integrated LUFS via ffmpeg loudnorm.

Stored per file (Songs.loudness_db), applied at play time as an
attenuate-only gain so tracks level out without ever clipping (pygame
volumes cap at 1.0, so quiet tracks stay as-is instead of boosting).
Target and clamp mirror the web engine (-14 LUFS, +/-12 dB).
"""

import json
import subprocess

TARGET_LUFS = -14.0
MAX_CORRECTION_DB = 12.0


def gain_for_lufs(lufs):
    """Linear playback multiplier for a measurement. 1.0 when unknown."""
    try:
        correction = TARGET_LUFS - float(lufs)
    except (TypeError, ValueError):
        return 1.0
    correction = max(-MAX_CORRECTION_DB, min(MAX_CORRECTION_DB, correction))
    return min(1.0, 10.0 ** (correction / 20.0))


def parse_loudnorm_output(text):
    """Integrated LUFS out of ffmpeg loudnorm JSON (stderr). None if unusable."""
    try:
        data = json.loads(text)
    except (TypeError, ValueError):
        return None
    if not isinstance(data, dict):
        return None
    try:
        value = float(data.get("input_i"))
    except (TypeError, ValueError):
        return None
    if value != value or value in (float("inf"), float("-inf")):
        return None
    return value


def analyze_file(ffmpeg_exe, path, timeout_secs=180):
    """Single-pass loudnorm measure. None on any failure. Never raises."""
    if not ffmpeg_exe or not path:
        return None
    try:
        proc = subprocess.run(
            [ffmpeg_exe, "-hide_banner", "-nostats", "-i", path,
             "-map", "0:a:0", "-af", "loudnorm=print_format=json",
             "-f", "null", "-"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            timeout=timeout_secs,
            check=False,
        )
    except Exception:
        return None
    if proc.stderr is None:
        return None
    try:
        text = proc.stderr.decode("utf-8", errors="ignore")
    except Exception:
        return None
    # The JSON block trails the progress lines: parse from the last '{'.
    idx = text.rfind("{")
    return parse_loudnorm_output(text[idx:] if idx >= 0 else text)

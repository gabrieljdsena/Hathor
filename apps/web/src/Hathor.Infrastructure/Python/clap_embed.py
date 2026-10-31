"""Batch CLAP audio embeddings sidecar.

Reads {"paths": [...], "checkpoint": "..."} on stdin and writes one
HATHOR_EMBEDDING_RESULT:{json} line on stdout. Missing deps/model are a
recoverable error (the .NET caller falls back to no audio signal).
"""

import json
import sys

MARKER = "HATHOR_EMBEDDING_RESULT:"


def reply(payload):
    print(MARKER + json.dumps(payload), flush=True)


try:
    import laion_clap
except Exception as exc:  # noqa: BLE001 - sidecar must never crash the API
    reply({"error": f"laion_clap unavailable: {exc}"})
    sys.exit(0)

try:
    request = json.load(sys.stdin)
    paths = [p for p in (request.get("paths") or []) if isinstance(p, str) and p]
    if not paths:
        reply({"error": "no paths"})
        sys.exit(0)

    model = laion_clap.CLAP_Module(enable_fusion=False, amodel="HTSAT-base")
    checkpoint = (request.get("checkpoint") or "").strip()
    if checkpoint:
        model.load_ckpt(checkpoint)

    vectors = model.get_audio_embedding_from_filelist(x=paths, use_tensor=False)
    reply({"vectors": [[float(x) for x in vector] for vector in vectors]})
except Exception as exc:  # noqa: BLE001 - include the reason for logs
    reply({"error": str(exc)})

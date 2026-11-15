"""Helpers shared by the desktop unit tests.

Deliberately stdlib-only: the test suite must run on a machine without
pygame/pywebview installed.
"""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit


class FakeHathorServer:
    """Tiny HTTP server with per-(method, path) routes and request capture."""

    def __init__(self):
        self.routes = {}
        self.requests = []
        self._httpd = None
        self._thread = None

    def add(self, method, path, status=200, json_body=None, body=None,
            content_type="application/json"):
        self.routes[(method.upper(), path)] = {
            "status": status,
            "json": json_body,
            "body": body,
            "content_type": content_type,
        }
        return self

    @property
    def base_url(self):
        host, port = self._httpd.server_address[:2]
        return f"http://{host}:{port}"

    def __enter__(self):
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                fake._handle(self, "GET")

            def do_POST(self):
                fake._handle(self, "POST")

            def do_PUT(self):
                fake._handle(self, "PUT")

            def do_DELETE(self):
                fake._handle(self, "DELETE")

        self._httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)
        self._thread.start()
        return self

    def __exit__(self, exc_type, exc, tb):
        if self._httpd:
            self._httpd.shutdown()
            self._httpd.server_close()
        if self._thread:
            self._thread.join(timeout=3)

    def _handle(self, handler, method):
        parsed = urlsplit(handler.path)
        length = int(handler.headers.get("Content-Length") or 0)
        request_body = handler.rfile.read(length) if length else b""
        self.requests.append({
            "method": method,
            "path": handler.path,
            "route": parsed.path,
            "query": parsed.query,
            "headers": dict(handler.headers),
            "body": request_body,
        })
        route = self.routes.get((method, parsed.path))
        if route is None:
            payload = json.dumps({"message": "not found"}).encode("utf-8")
            handler.send_response(404)
            handler.send_header("Content-Type", "application/json")
            handler.send_header("Content-Length", str(len(payload)))
            handler.end_headers()
            handler.wfile.write(payload)
            return

        status = int(route["status"])
        body = route["body"]
        if body is None and route["json"] is not None:
            body = json.dumps(route["json"]).encode("utf-8")
        if body is None:
            body = b""
        if isinstance(body, str):
            body = body.encode("utf-8")
        handler.send_response(status)
        if body:
            handler.send_header("Content-Type", route["content_type"])
            handler.send_header("Content-Length", str(len(body)))
        handler.end_headers()
        if body and method != "HEAD":
            handler.wfile.write(body)

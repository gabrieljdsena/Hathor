"""Shared HTTP client for the Hathor Web API.

Everything that talks to the server (sync, Discover, AI DJ, player state)
should go through this module. Keeping auth, timeouts, error parsing and
media URLs in one place means the API key never has to reach the webview and
new features cannot drift into their own urllib dialects.

Stdlib only: urllib is enough, and it keeps the frozen build small.
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.parse
import urllib.request


class HathorApiError(Exception):
    """User-facing API failure (message is safe to show in the UI)."""

    def __init__(self, message, status=None, body=None, url=None):
        super().__init__(message)
        self.message = message
        self.status = status
        self.body = body
        self.url = url

    def __str__(self):
        return self.message


def _message_from_body(body):
    """Best-effort message extraction from an ASP.NET error payload."""
    if not body:
        return ""
    try:
        data = json.loads(body.decode("utf-8"))
    except Exception:
        try:
            return body.decode("utf-8", "replace").strip()[:300]
        except Exception:
            return ""
    if isinstance(data, dict):
        for key in ("message", "error", "detail", "title"):
            value = data.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()[:300]
    return ""


def _http_error_message(status, detail, url):
    base = f"Hathor API error ({status})"
    if detail:
        base += f": {detail}"
    if status == 401:
        return (
            "Hathor API rejected the credentials (401). Sign in again or "
            "check the API key in Settings -> Web API."
        )
    if status == 403:
        return (
            "Hathor API key is missing a required scope (403)"
            + (f": {detail}" if detail else ".")
        )
    if status == 404:
        return (
            "Hathor API endpoint not found (404). Check the server URL"
            + (f": {detail}" if detail else ".")
        )
    if status >= 500:
        return f"Hathor API server error ({status})" + (f": {detail}" if detail else ".")
    return base


class HathorApi:
    """Small authenticated JSON client for one Hathor server."""

    def __init__(self, base_url, api_key=None, timeout=30.0,
                 user_agent="hathor-desktop/1", auth_session=None):
        self.base_url = (base_url or "").strip().rstrip("/")
        self.api_key = (api_key or "").strip()
        self.timeout = float(timeout or 30.0)
        self.user_agent = user_agent or "hathor-desktop/1"
        self.auth_session = auth_session

    def _token(self):
        if self.auth_session is not None:
            try:
                return self.auth_session.access_token() or self.api_key
            except Exception:
                return self.api_key
        return self.api_key

    def _refresh_for_retry(self, failed_token=None):
        if self.auth_session is None:
            return False
        try:
            if not self.auth_session.refresh(force=True, failed_token=failed_token):
                return False
            self.api_key = self.auth_session.access_token() or ""
            return bool(self.api_key)
        except Exception:
            return False

    # -- URLs -----------------------------------------------------------
    def url(self, path, query=None):
        if not self.base_url:
            raise HathorApiError("Hathor API URL is not configured. Open Settings -> Web API.")
        path = path or "/"
        if path.startswith("http://") or path.startswith("https://"):
            # Do not let callers accidentally bypass the configured server.
            raise HathorApiError("Absolute API URLs are not allowed.")
        if not path.startswith("/"):
            path = "/" + path
        url = self.base_url + path
        if query:
            filtered = {k: v for k, v in query.items() if v is not None}
            if filtered:
                url += "?" + urllib.parse.urlencode(filtered, doseq=True)
        return url

    def media_url(self, path, query=None):
        """URL usable by <audio>/<video> where Authorization headers are not sent.

        Media endpoints accept the same hth_ token as a ?token= query. The
        key is only placed in a local URL handed to the webview, never logged.
        """
        params = dict(query or {})
        token = self._token()
        if token:
            params.setdefault("token", token)
        return self.url(path, params)

    # -- JSON transport -------------------------------------------------
    def request(self, method, path, payload=None, query=None, timeout=None,
                expect_json=True, extra_headers=None):
        effective_timeout = float(timeout or self.timeout)
        for attempt in (0, 1):
            url = self.url(path, query)
            headers = {
                "Accept": "application/json",
                "User-Agent": self.user_agent,
            }
            token = self._token()
            if token:
                headers["Authorization"] = f"Bearer {token}"
            data = None
            if payload is not None:
                data = json.dumps(payload).encode("utf-8")
                headers["Content-Type"] = "application/json; charset=utf-8"
            if extra_headers:
                headers.update(extra_headers)

            req = urllib.request.Request(
                url, data=data, headers=headers,
                method=(method or "GET").upper(),
            )
            try:
                with urllib.request.urlopen(req, timeout=effective_timeout) as resp:
                    raw = resp.read()
                break
            except urllib.error.HTTPError as exc:
                body = b""
                try:
                    body = exc.read()
                except Exception:
                    pass
                finally:
                    try:
                        exc.close()
                    except Exception:
                        pass
                if exc.code == 401 and attempt == 0 and self._refresh_for_retry(token):
                    continue
                detail = _message_from_body(body)
                raise HathorApiError(
                    _http_error_message(exc.code, detail, url),
                    status=exc.code,
                    body=body,
                    url=url,
                ) from exc
            except urllib.error.URLError as exc:
                raise HathorApiError(
                    f"Cannot reach Hathor API at {self.base_url}: {exc.reason}",
                    url=url,
                ) from exc
            except TimeoutError as exc:
                raise HathorApiError(
                    f"Hathor API timed out after {effective_timeout:g}s: {url}",
                    url=url,
                ) from exc
            except Exception as exc:
                raise HathorApiError(
                    f"Hathor API request failed: {exc}",
                    url=url,
                ) from exc

        if not raw:
            return {}
        if not expect_json:
            return raw
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception as exc:
            raise HathorApiError(
                "Hathor API returned a non-JSON response.", body=raw, url=url
            ) from exc

    def get(self, path, query=None, **kwargs):
        return self.request("GET", path, query=query, **kwargs)

    def post(self, path, payload=None, query=None, **kwargs):
        return self.request("POST", path, payload=payload, query=query, **kwargs)

    def put(self, path, payload=None, query=None, **kwargs):
        return self.request("PUT", path, payload=payload, query=query, **kwargs)

    def delete(self, path, query=None, **kwargs):
        return self.request("DELETE", path, query=query, **kwargs)

    # -- binary transport ----------------------------------------------
    def get_bytes(self, path, query=None, timeout=None):
        for attempt in (0, 1):
            url = self.url(path, query)
            headers = {"Accept": "*/*", "User-Agent": self.user_agent}
            token = self._token()
            if token:
                headers["Authorization"] = f"Bearer {token}"
            req = urllib.request.Request(url, headers=headers, method="GET")
            try:
                with urllib.request.urlopen(
                        req, timeout=float(timeout or self.timeout)) as resp:
                    return resp.read()
            except urllib.error.HTTPError as exc:
                body = b""
                try:
                    body = exc.read()
                except Exception:
                    pass
                finally:
                    try:
                        exc.close()
                    except Exception:
                        pass
                if exc.code == 401 and attempt == 0 and self._refresh_for_retry(token):
                    continue
                raise HathorApiError(
                    _http_error_message(exc.code, _message_from_body(body), url),
                    status=exc.code,
                    body=body,
                    url=url,
                ) from exc
            except urllib.error.URLError as exc:
                raise HathorApiError(
                    f"Cannot reach Hathor API at {self.base_url}: {exc.reason}",
                    url=url,
                ) from exc

    def stream_to_file(self, path, dest, query=None, timeout=120, chunk_size=256 * 1024,
                       progress_cb=None):
        """Download to a temp file and atomically replace *dest* on success.

        ``progress_cb`` receives ``(downloaded_bytes, total_bytes)`` where
        ``total_bytes`` is 0 when the server omits Content-Length.
        """
        for attempt in (0, 1):
            url = self.url(path, query)
            headers = {"Accept": "*/*", "User-Agent": self.user_agent}
            token = self._token()
            if token:
                headers["Authorization"] = f"Bearer {token}"
            req = urllib.request.Request(url, headers=headers, method="GET")
            tmp = str(dest) + ".hathor-part"
            try:
                with urllib.request.urlopen(
                        req, timeout=float(timeout)) as resp, open(tmp, "wb") as fh:
                    raw_total = resp.headers.get("Content-Length")
                    total = int(raw_total) if raw_total and str(raw_total).isdigit() else 0
                    downloaded = 0
                    while True:
                        chunk = resp.read(chunk_size)
                        if not chunk:
                            break
                        fh.write(chunk)
                        downloaded += len(chunk)
                        if progress_cb is not None:
                            try:
                                progress_cb(downloaded, total)
                            except Exception:
                                pass
                os.replace(tmp, dest)
                return dest
            except urllib.error.HTTPError as exc:
                try:
                    os.remove(tmp)
                except OSError:
                    pass
                body = b""
                try:
                    body = exc.read()
                except Exception:
                    pass
                finally:
                    try:
                        exc.close()
                    except Exception:
                        pass
                if exc.code == 401 and attempt == 0 and self._refresh_for_retry(token):
                    continue
                raise HathorApiError(
                    _http_error_message(exc.code, _message_from_body(body), url),
                    status=exc.code,
                    body=body,
                    url=url,
                ) from exc
            except urllib.error.URLError as exc:
                try:
                    os.remove(tmp)
                except OSError:
                    pass
                raise HathorApiError(
                    f"Cannot reach Hathor API at {self.base_url}: {exc.reason}",
                    url=url,
                ) from exc
            except Exception:
                try:
                    os.remove(tmp)
                except OSError:
                    pass
                raise

    def upload_file(self, path, source, query=None, content_type="audio/mpeg",
                    timeout=300):
        """PUT one local file. Reads it into memory, matching the sync API."""
        with open(source, "rb") as fh:
            data = fh.read()
        for attempt in (0, 1):
            url = self.url(path, query)
            headers = {
                "Accept": "application/json",
                "User-Agent": self.user_agent,
                "Content-Type": content_type,
            }
            token = self._token()
            if token:
                headers["Authorization"] = f"Bearer {token}"
            req = urllib.request.Request(
                url, data=data, headers=headers, method="PUT"
            )
            try:
                with urllib.request.urlopen(
                        req, timeout=float(timeout)) as resp:
                    raw = resp.read()
                break
            except urllib.error.HTTPError as exc:
                body = b""
                try:
                    body = exc.read()
                except Exception:
                    pass
                finally:
                    try:
                        exc.close()
                    except Exception:
                        pass
                if exc.code == 401 and attempt == 0 and self._refresh_for_retry(token):
                    continue
                raise HathorApiError(
                    _http_error_message(exc.code, _message_from_body(body), url),
                    status=exc.code,
                    body=body,
                    url=url,
                ) from exc
            except urllib.error.URLError as exc:
                raise HathorApiError(
                    f"Cannot reach Hathor API at {self.base_url}: {exc.reason}",
                    url=url,
                ) from exc
        if raw:
            try:
                return json.loads(raw.decode("utf-8"))
            except Exception:
                return {}
        return {}

"""Automatic Music_History outbox flush.

Playback inserts a row into ``History_Sync_Queue``; this background service
batches those rows and pushes only the ``MusicHistory`` section to
``POST /api/v1/sync/import``. Rows stay queued on any failure, so the next
notify or app start retries them. Playback never waits on this service.
"""

from __future__ import annotations

import logging
import threading

from .api_sync import ApiSyncClient

_log = logging.getLogger("hathor.history_sync")


class HistorySync:
    def __init__(
        self,
        db_path,
        start=True,
        client_factory=None,
        debounce_seconds=1.0,
        retry_seconds=30.0,
        batch_limit=200,
    ):
        self.db_path = db_path
        self.debounce_seconds = max(0.0, float(debounce_seconds))
        self.retry_seconds = max(1.0, float(retry_seconds))
        self.batch_limit = max(1, int(batch_limit))
        self._client_factory = client_factory or (lambda: ApiSyncClient(self.db_path))
        self._wake = threading.Event()
        self._stop = threading.Event()
        self._thread = None
        if start:
            self._thread = threading.Thread(
                target=self._run, daemon=True, name="history-sync"
            )
            self._thread.start()

    def notify(self):
        """Wake the worker; safe to call from playback/UI threads."""
        self._wake.set()

    def _flush_once(self):
        return self._client_factory().push_pending_history(self.batch_limit)

    def _run(self):
        # Retry anything left from the previous session immediately.
        self._wake.set()
        while not self._stop.is_set():
            self._wake.wait(timeout=self.retry_seconds)
            if self._stop.is_set():
                break
            self._wake.clear()
            if self.debounce_seconds and self._stop.wait(self.debounce_seconds):
                break
            try:
                pushed = self._flush_once()
                if pushed >= self.batch_limit:
                    self._wake.set()  # drain a large backlog promptly
            except Exception:
                # Never surface network/config failures to playback. The
                # outbox is durable, so the loop retries after its timeout.
                pass

    def close(self):
        self._stop.set()
        self._wake.set()
        thread = self._thread
        if thread is not None:
            thread.join(timeout=self.debounce_seconds + 2.0)
            self._thread = None

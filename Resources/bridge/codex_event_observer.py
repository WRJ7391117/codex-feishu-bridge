"""Observe shared app-server events without polling Codex Task storage."""

from __future__ import annotations

import logging
import os
from pathlib import Path
import queue
import select
import threading
import time
from typing import Any, Callable


_LOG = logging.getLogger(__name__)


class SharedEventObserver:
    """Run once in a worker thread; callbacks run in that same thread.

    ``client_factory()`` returns an AppServerClient connected to the shared
    daemon. ``targets_callback()`` supplies authorized Task IDs, ``event_callback``
    receives a complete protocol frame, and ``reconcile_callback()`` repairs the
    delivery window on connection and after terminal events. ``client`` exposes
    the active connection for answering server requests.

    On platforms without kqueue, call ``wake()`` after replacing state.json.
    ``close()`` only signals cancellation, so it can safely run on the main thread.
    """

    _RECONNECT_MIN_DELAY = 0.25
    _RECONNECT_MAX_DELAY = 8.0
    _RESUME_RETRY_INTERVAL = 30.0

    def __init__(
        self,
        client_factory: Callable[[], Any],
        targets_callback: Callable[[], set[str]],
        event_callback: Callable[[dict[str, Any]], None],
        reconcile_callback: Callable[[], None],
        state_path: str | Path,
        shutdown_event: threading.Event,
        quiesce_event: threading.Event,
    ) -> None:
        self._client_factory = client_factory
        self._targets_callback = targets_callback
        self._event_callback = event_callback
        self._reconcile_callback = reconcile_callback
        self._state_path = Path(state_path)
        self._shutdown_event = shutdown_event
        self._quiesce_event = quiesce_event
        self._stop = threading.Event()
        self._queue: queue.Queue[Any] = queue.Queue()
        self._lock = threading.Lock()
        self._client: Any = None
        self._connected = False
        self._targets_ready = False
        self._connection_lost = False
        self._state_stamp = self._stat_state()
        self._state_pending = False
        self._reconcile_pending = False
        self._watch_writer: int | None = None
        self._watcher: threading.Thread | None = None
        self._reader: threading.Thread | None = None
        self._stop_monitor: threading.Thread | None = None

    @property
    def client(self) -> Any:
        with self._lock:
            return self._client

    @property
    def connected(self) -> bool:
        with self._lock:
            return self._connected and not self._stopping()

    def _stopping(self) -> bool:
        return (
            self._stop.is_set()
            or self._shutdown_event.is_set()
            or self._quiesce_event.is_set()
        )

    def _stat_state(self) -> Any:
        try:
            stat = self._state_path.stat()
        except FileNotFoundError:
            return None
        return (stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns)

    def wake(self) -> None:
        """Coalesce actual state-file changes into a single target refresh."""
        stamp = self._stat_state()
        with self._lock:
            if stamp == self._state_stamp or self._stop.is_set():
                return
            self._state_stamp = stamp
            if self._state_pending:
                return
            self._state_pending = True
            self._queue.put(("state", None, None))

    def request_reconciliation(self) -> None:
        """Queue delivery work after a bridge worker releases its active run."""
        with self._lock:
            if self._stopping() or self._reconcile_pending:
                return
            self._reconcile_pending = True
            self._queue.put(("reconcile", None, None))

    def close(self) -> None:
        self._stop.set()
        with self._lock:
            self._connected = False
            writer = self._watch_writer
            if writer is not None:
                try:
                    os.write(writer, b"\0")
                except OSError:
                    pass
        self._queue.put(("stop", None, None))

    def _close_client(self, expected: Any = None) -> None:
        with self._lock:
            if expected is not None and self._client is not expected:
                return
            client, self._client = self._client, None
            self._connected = False
        if client is not None:
            try:
                client.close()
            except Exception:
                _LOG.exception("Could not close the shared Codex event connection")

    def _monitor_stop(self) -> None:
        while not self._stop.wait(0.05):
            if self._stopping():
                self.close()
                break
        self._close_client()

    def _start_watcher(self) -> None:
        if not hasattr(select, "kqueue"):
            return
        directory = os.open(self._state_path.parent, getattr(os, "O_EVTONLY", os.O_RDONLY))
        watcher = select.kqueue()
        reader, writer = os.pipe()
        os.set_blocking(writer, False)
        try:
            watcher.control([
                select.kevent(
                    directory, filter=select.KQ_FILTER_VNODE,
                    flags=select.KQ_EV_ADD | select.KQ_EV_CLEAR,
                    fflags=select.KQ_NOTE_WRITE | select.KQ_NOTE_RENAME,
                ),
                select.kevent(reader, filter=select.KQ_FILTER_READ, flags=select.KQ_EV_ADD),
            ], 0)
        except Exception:
            watcher.close()
            for descriptor in (directory, reader, writer):
                os.close(descriptor)
            raise
        with self._lock:
            self._watch_writer = writer

        def watch() -> None:
            try:
                while not self._stopping():
                    events = watcher.control(None, 8, None)
                    if self._stopping():
                        return
                    if any(event.ident == directory for event in events):
                        self.wake()
            finally:
                watcher.close()
                with self._lock:
                    self._watch_writer = None
                    os.close(writer)
                for descriptor in (directory, reader):
                    os.close(descriptor)

        self._watcher = threading.Thread(target=watch, name="codex-shared-state", daemon=True)
        self._watcher.start()
        self.wake()

    def _read_events(self, client: Any) -> None:
        try:
            while not self._stopping():
                self._queue.put(("event", client, client.next_event()))
        except Exception:
            with self._lock:
                if self._client is client:
                    self._connected = False
                    self._connection_lost = True
            self._queue.put(("disconnect", client, None))

    def _sync_targets(
        self, client: Any, subscribed: set[str], previous: Any,
        failed_attempts: dict[str, float],
    ) -> Any:
        try:
            desired = set(self._targets_callback())
        except Exception:
            _LOG.exception("Could not read shared Codex event targets")
            return previous
        with self._lock:
            self._targets_ready = desired <= subscribed
            if not self._targets_ready:
                self._connected = False
        changed = desired != previous
        if changed:
            for task_id in failed_attempts.keys() - desired:
                failed_attempts.pop(task_id)
            for task_id in sorted(subscribed - desired):
                if self._stopping():
                    return desired
                try:
                    client.request("thread/unsubscribe", {"threadId": task_id})
                    subscribed.remove(task_id)
                except Exception:
                    _LOG.exception("Could not unsubscribe a shared Codex event target")
        for task_id in sorted(desired - subscribed):
            if self._stopping():
                return desired
            if (
                not changed and task_id in failed_attempts
                and time.monotonic() - failed_attempts[task_id] < self._RESUME_RETRY_INTERVAL
            ):
                continue
            try:
                client.request("thread/resume", {"threadId": task_id, "excludeTurns": True})
                subscribed.add(task_id)
                failed_attempts.pop(task_id, None)
            except Exception:
                # Retry only on state changes, with a per-target cooldown unless
                # the desired set changed. Successful targets stay subscribed.
                failed_attempts[task_id] = time.monotonic()
                _LOG.exception("Could not resume a shared Codex event target")
        with self._lock:
            self._targets_ready = desired <= subscribed
        return desired

    def _reconcile(self, client: Any) -> None:
        try:
            self._reconcile_callback()
        except Exception:
            with self._lock:
                if self._client is client:
                    self._connected = False
            _LOG.exception("Could not reconcile shared Codex event delivery")
            return
        with self._lock:
            if self._client is client and not self._connection_lost and not self._stopping():
                self._connected = self._targets_ready

    def run(self) -> None:
        if self._stopping():
            return
        delay = self._RECONNECT_MIN_DELAY
        try:
            self._start_watcher()
            self._stop_monitor = threading.Thread(
                target=self._monitor_stop, name="codex-shared-stop", daemon=True,
            )
            self._stop_monitor.start()
            while not self._stopping():
                client = None
                connected_at = None
                try:
                    client = self._client_factory()
                    with self._lock:
                        self._client = client
                        self._targets_ready = False
                        self._connection_lost = False
                    if self._stopping():
                        break
                    client.initialize()
                    if self._stopping():
                        break
                    self._reader = threading.Thread(
                        target=self._read_events, args=(client,),
                        name="codex-shared-events", daemon=True,
                    )
                    self._reader.start()
                    subscribed: set[str] = set()
                    failed_attempts: dict[str, float] = {}
                    desired = self._sync_targets(client, subscribed, None, failed_attempts)
                    if self._stopping():
                        break
                    self._reconcile(client)
                    connected_at = time.monotonic()
                    while not self._stopping():
                        kind, source, frame = self._queue.get()
                        if kind == "stop":
                            break
                        if kind == "state":
                            with self._lock:
                                self._state_pending = False
                            previous_desired = desired
                            previous_subscribed = set(subscribed)
                            desired = self._sync_targets(client, subscribed, desired, failed_attempts)
                            if desired != previous_desired or subscribed != previous_subscribed:
                                self._reconcile(client)
                        elif kind == "reconcile":
                            with self._lock:
                                self._reconcile_pending = False
                            self._reconcile(client)
                        elif source is client:
                            if kind == "disconnect":
                                break
                            try:
                                self._event_callback(frame)
                            except Exception:
                                _LOG.exception("Could not handle a shared Codex event")
                            if frame.get("method") in ("turn/completed", "thread/compacted"):
                                self._reconcile(client)
                except Exception:
                    if not self._stopping():
                        _LOG.exception("Shared Codex event connection failed")
                finally:
                    self._close_client(client)
                    if self._reader is not None:
                        self._reader.join()
                if connected_at is not None and time.monotonic() - connected_at >= 30:
                    delay = self._RECONNECT_MIN_DELAY
                if self._stop.wait(delay):
                    break
                delay = min(delay * 2, self._RECONNECT_MAX_DELAY)
        finally:
            self.close()
            self._close_client()
            if self._stop_monitor is not None:
                self._stop_monitor.join()
            if self._watcher is not None:
                self._watcher.join()

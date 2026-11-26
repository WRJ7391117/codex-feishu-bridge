"""Event-driven JSON-RPC transport for the public Codex app-server protocol."""

from __future__ import annotations

import base64
import hashlib
import json
import os
import queue
import selectors
import struct
import subprocess
import threading
import time
from typing import Any


class AppServerError(RuntimeError):
    def __init__(
        self,
        message: str,
        *,
        request_sent: bool = False,
        server_error: Any = None,
    ) -> None:
        super().__init__(message)
        self.request_sent = request_sent
        self.server_error = server_error


class AppServerClient:
    """Keep responses, notifications, and server requests on separate paths.

    No request is retried. ``request_sent`` means a failed request may have
    reached the server, including a failed pipe write or a response timeout.
    ``next_event`` returns full notification/server-request frames; callers
    must preserve server request IDs when answering through ``respond``.
    """

    def __init__(self, executable: str, *, shared_daemon: bool = False) -> None:
        self._condition = threading.Condition()
        self._write_lock = threading.Lock()
        self._close_lock = threading.Lock()
        self._pending: dict[int, dict[str, Any]] = {}
        self._next_id = 0
        self._failure = ""
        self._closed = False
        self._events: queue.Queue[Any] = queue.Queue()
        self._end = object()
        self.stderr_tail = ""
        self._websocket = shared_daemon
        self._websocket_ready = not shared_daemon
        self._fragment = bytearray()
        self._fragment_open = False
        command = [executable, "app-server"]
        command.extend(["proxy"] if shared_daemon else ["--listen", "stdio://"])
        try:
            self.process = subprocess.Popen(
                command,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=0,
            )
        except OSError as exc:
            raise AppServerError("无法启动 Codex app-server。") from exc
        self._wake_reader, self._wake_writer = os.pipe()
        self._reader = threading.Thread(
            target=self._read_messages, name="codex-app-server-events", daemon=True
        )
        if shared_daemon:
            key = base64.b64encode(os.urandom(16)).decode("ascii")
            self._websocket_accept = base64.b64encode(
                hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()
            ).decode("ascii")
        self._reader.start()
        if shared_daemon:
            upgrade = (
                "GET /rpc HTTP/1.1\r\nHost: localhost\r\nConnection: Upgrade\r\n"
                "Upgrade: websocket\r\nSec-WebSocket-Version: 13\r\n"
                f"Sec-WebSocket-Key: {key}\r\n\r\n"
            )
            try:
                self._write_bytes(upgrade.encode("ascii"))
            except AppServerError as exc:
                self.close()
                raise AppServerError("无法建立 Codex 本地共享连接。") from exc

    def __enter__(self) -> "AppServerClient":
        return self

    def __exit__(self, _type: Any, _value: Any, _traceback: Any) -> None:
        self.close()

    def initialize(
        self,
        client_info: dict[str, str] | None = None,
        *,
        experimental_api: bool = True,
        timeout: float = 15,
    ) -> dict[str, Any]:
        result = self.request(
            "initialize",
            {
                "clientInfo": client_info or {
                    "name": "codex-feishu-bridge",
                    "title": "DeepOri Bridge",
                    "version": "1",
                },
                "capabilities": {"experimentalApi": experimental_api},
            },
            timeout=timeout,
        )
        self.notify("initialized", {})
        return result

    def request(
        self, method: str, params: dict[str, Any] | None = None, timeout: float = 30
    ) -> Any:
        with self._condition:
            self._next_id += 1
            request_id = self._next_id
            pending: dict[str, Any] = {"sent": False}
            self._pending[request_id] = pending
        try:
            deadline = time.monotonic() + timeout
            self._send(
                {"id": request_id, "method": method, "params": params}, pending, deadline
            )
            with self._condition:
                while "response" not in pending:
                    if self._failure or self._closed:
                        raise AppServerError(
                            self._failure or "Codex app-server 已关闭。",
                            request_sent=pending["sent"],
                        )
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise AppServerError(
                            "等待 Codex app-server 响应超时。", request_sent=pending["sent"]
                        )
                    self._condition.wait(remaining)
                response = pending["response"]
            if "error" in response:
                raise AppServerError(
                    "Codex app-server 拒绝了请求。",
                    request_sent=True,
                    server_error=response["error"],
                )
            return response.get("result")
        finally:
            with self._condition:
                self._pending.pop(request_id, None)

    def notify(self, method: str, params: dict[str, Any] | None = None) -> None:
        self._send({"method": method, "params": params})

    def respond(self, request_id: str | int, result: Any) -> None:
        self._send({"id": request_id, "result": result})

    def respond_error(self, request_id: str | int, message: str, code: int = -32601) -> None:
        self._send({"id": request_id, "error": {"code": code, "message": message}})

    def next_event(self, timeout: float | None = None) -> dict[str, Any]:
        event = self._events.get(timeout=timeout)
        if event is self._end:
            self._events.put(self._end)
            raise AppServerError(self._failure or "Codex app-server 已关闭。")
        return event

    def _send(
        self,
        message: dict[str, Any],
        pending: dict[str, Any] | None = None,
        deadline: float | None = None,
    ) -> None:
        try:
            encoded = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        except (TypeError, ValueError) as exc:
            raise AppServerError("Codex app-server 请求无法编码。") from exc
        deadline = deadline if deadline is not None else time.monotonic() + 30
        with self._condition:
            while not self._websocket_ready:
                if self._failure or self._closed:
                    raise AppServerError(self._failure or "Codex app-server 已关闭。")
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise AppServerError("等待 Codex 本地共享连接超时。")
                self._condition.wait(remaining)
        packet = self._websocket_frame(encoded) if self._websocket else encoded + b"\n"
        self._write_bytes(packet, pending)

    def _write_bytes(
        self, packet: bytes, pending: dict[str, Any] | None = None
    ) -> None:
        with self._write_lock:
            with self._condition:
                if self._failure or self._closed:
                    raise AppServerError(self._failure or "Codex app-server 已关闭。")
                if pending is not None:
                    pending["sent"] = True
            try:
                data = memoryview(packet)
                while data:
                    written = self.process.stdin.write(data)
                    if not written:
                        raise BrokenPipeError
                    data = data[written:]
                self.process.stdin.flush()
            except (OSError, ValueError) as exc:
                self._disconnect("Codex app-server 输入连接已断开。")
                raise AppServerError(
                    "Codex app-server 输入连接已断开。", request_sent=True
                ) from exc

    def _disconnect(self, message: str) -> None:
        with self._condition:
            if not self._failure:
                self._failure = message
            self._condition.notify_all()

    def _read_messages(self) -> None:
        failure = "Codex app-server 输出连接已断开。"
        selector = selectors.DefaultSelector()
        pending_data = b""
        try:
            selector.register(self.process.stdout, selectors.EVENT_READ, "stdout")
            selector.register(self.process.stderr, selectors.EVENT_READ, "stderr")
            selector.register(self._wake_reader, selectors.EVENT_READ, "close")
            while True:
                for key, _ in selector.select():
                    if key.data == "close":
                        return
                    chunk = os.read(key.fd, 4096)
                    if key.data == "stderr":
                        if not chunk:
                            selector.unregister(key.fileobj)
                        else:
                            with self._condition:
                                self.stderr_tail = (
                                    self.stderr_tail + chunk.decode("utf-8", errors="replace")
                                )[-8192:]
                        continue
                    if not chunk:
                        if not self._websocket and pending_data.strip():
                            self._dispatch(json.loads(pending_data))
                        return
                    pending_data += chunk
                    if self._websocket:
                        pending_data = self._read_websocket(pending_data)
                        continue
                    while b"\n" in pending_data:
                        line, _, pending_data = pending_data.partition(b"\n")
                        self._dispatch(json.loads(line))
        except (OSError, ValueError, TypeError, AppServerError):
            failure = "Codex app-server 返回了无效响应或连接已断开。"
        finally:
            selector.close()
            self._disconnect(failure)
            self._events.put(self._end)

    @staticmethod
    def _websocket_frame(payload: bytes, opcode: int = 1) -> bytes:
        size = len(payload)
        if size < 126:
            header = bytes((0x80 | opcode, 0x80 | size))
        elif size <= 65535:
            header = bytes((0x80 | opcode, 0x80 | 126)) + struct.pack("!H", size)
        else:
            header = bytes((0x80 | opcode, 0x80 | 127)) + struct.pack("!Q", size)
        mask = os.urandom(4)
        return header + mask + bytes(value ^ mask[index % 4] for index, value in enumerate(payload))

    def _read_websocket(self, data: bytes) -> bytes:
        if not self._websocket_ready:
            if b"\r\n\r\n" not in data:
                if len(data) > 16384:
                    raise ValueError("oversized websocket handshake")
                return data
            header, data = data.split(b"\r\n\r\n", 1)
            lines = header.decode("ascii").split("\r\n")
            headers = {
                name.lower(): value.strip()
                for name, value in (line.split(":", 1) for line in lines[1:])
            }
            if (
                not lines[0].startswith("HTTP/1.1 101 ")
                or headers.get("sec-websocket-accept", "") != self._websocket_accept
            ):
                raise ValueError("websocket upgrade rejected")
            with self._condition:
                self._websocket_ready = True
                self._condition.notify_all()
        while len(data) >= 2:
            first, second = data[0], data[1]
            opcode, size, offset = first & 0x0F, second & 0x7F, 2
            if first & 0x70 or second & 0x80:
                raise ValueError("invalid websocket frame")
            if size in (126, 127):
                width = 2 if size == 126 else 8
                if len(data) < offset + width:
                    return data
                size = int.from_bytes(data[offset:offset + width], "big")
                offset += width
            if size > 64 * 1024 * 1024:
                raise ValueError("oversized websocket message")
            if len(data) < offset + size:
                return data
            payload, data = data[offset:offset + size], data[offset + size:]
            if opcode == 8:
                raise ConnectionError("websocket closed")
            if opcode == 9:
                self._write_bytes(self._websocket_frame(payload, 10))
                continue
            if opcode == 10:
                continue
            if opcode not in (0, 1, 2) or (opcode == 0) != self._fragment_open:
                raise ValueError("invalid websocket continuation")
            self._fragment.extend(payload)
            self._fragment_open = not bool(first & 0x80)
            if len(self._fragment) > 64 * 1024 * 1024:
                raise ValueError("oversized websocket message")
            if not self._fragment_open:
                self._dispatch(json.loads(self._fragment))
                self._fragment.clear()
        return data

    def _dispatch(self, message: Any) -> None:
        if not isinstance(message, dict):
            raise ValueError("non-object response")
        if "method" in message:
            self._events.put(message)
        elif "id" in message and ("result" in message or "error" in message):
            with self._condition:
                pending = self._pending.get(message["id"])
                if pending is not None:
                    pending["response"] = message
                    self._condition.notify_all()
        else:
            raise ValueError("invalid response")

    def close(self) -> None:
        with self._close_lock:
            with self._condition:
                if self._closed:
                    return
                self._closed = True
                self._condition.notify_all()
            if self.process.poll() is None:
                try:
                    self.process.terminate()
                except ProcessLookupError:
                    pass
                try:
                    self.process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    self.process.kill()
                    self.process.wait(timeout=3)
            os.write(self._wake_writer, b"\0")
            self._reader.join(timeout=3)
            os.close(self._wake_reader)
            os.close(self._wake_writer)
            for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
                try:
                    stream.close()
                except (OSError, ValueError):
                    pass

import importlib.util
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import unittest
from unittest import mock


MODULE_PATH = Path(__file__).resolve().parents[1] / "Resources/bridge/codex_app_server.py"
SPEC = importlib.util.spec_from_file_location("codex_app_server_test", MODULE_PATH)
transport = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(transport)


FAKE_WEBSOCKET = r'''
import base64, hashlib, struct, sys
websocket = True
headers = {}
request_line = sys.stdin.buffer.readline()
assert request_line == b"GET /rpc HTTP/1.1\r\n"
while True:
    line = sys.stdin.buffer.readline()
    if line == b"\r\n":
        break
    name, value = line.decode("ascii").split(":", 1)
    headers[name.lower()] = value.strip()
key = headers["sec-websocket-key"]
accept = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
if reject_upgrade:
    accept = "invalid-accept"
sys.stdout.buffer.write(("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n").encode())
sys.stdout.buffer.flush()
def wire_frame(payload, opcode=1, final=True):
    length = len(payload)
    prefix = bytes(((0x80 if final else 0) | opcode,))
    if length < 126:
        return prefix + bytes((length,)) + payload
    if length < 65536:
        return prefix + b"\x7e" + struct.pack("!H", length) + payload
    return prefix + b"\x7f" + struct.pack("!Q", length) + payload
def exact(length):
    output = b""
    while len(output) < length:
        part = sys.stdin.buffer.read(length - len(output))
        if not part:
            raise EOFError
        output += part
    return output
def websocket_messages():
    while True:
        head = sys.stdin.buffer.read(2)
        if not head:
            return
        assert head[0] & 0x80 and head[1] & 0x80, "client frames must be final and masked"
        length = head[1] & 0x7f
        if length == 126:
            length = struct.unpack("!H", exact(2))[0]
        elif length == 127:
            length = struct.unpack("!Q", exact(8))[0]
        mask, content = exact(4), exact(length)
        content = bytes(v ^ mask[i % 4] for i, v in enumerate(content))
        if head[0] & 0x0f == 10:
            assert content == b"keepalive"
            send({"method": "pong-received", "params": {}})
            continue
        yield content.decode("utf-8")
'''


FAKE_SERVER = r'''
import json, os, sys
initialized = False
delayed = None
def send(message):
    content = json.dumps(message, ensure_ascii=False).encode("utf-8")
    sys.stdout.buffer.write(wire_frame(content) if websocket else content + b"\n")
    sys.stdout.buffer.flush()
def result(request, value):
    send({"id": request["id"], "result": value})
for line in (websocket_messages() if websocket else sys.stdin):
    request = json.loads(line)
    method = request.get("method")
    if method == "initialize":
        result(request, request["params"])
    elif method == "initialized":
        initialized = True
    elif method == "probe":
        result(request, {"initialized": initialized})
    elif method == "echo":
        result(request, request["params"])
    elif method == "fragmented":
        data = json.dumps({"method": "fragmented-event", "params": {"text": "完整中文"}}, ensure_ascii=False).encode("utf-8")
        split = data.index("中".encode("utf-8")) + 1
        sys.stdout.buffer.write(wire_frame(data[:split], final=False) + wire_frame(data[split:], opcode=0))
        sys.stdout.buffer.flush()
        result(request, {})
    elif method == "ping":
        sys.stdout.buffer.write(wire_frame(b"keepalive", opcode=9))
        sys.stdout.buffer.flush()
        result(request, {})
    elif method == "turn/start":
        send({"method": "turn/started", "params": {"turn": {"id": "turn-1"}}})
        send({"id": 7, "method": "item/commandExecution/requestApproval", "params": {}})
        result(request, {"turn": {"id": "turn-1"}})
    elif method == "defer":
        delayed = request
        send({"method": "deferred", "params": {}})
    elif method == "second":
        result(request, {"which": "second"})
        result(delayed, {"which": "first"})
    elif method == "never":
        send({"method": "waiting", "params": {}})
    elif method == "exit":
        send({"method": "last-event", "params": {}})
        sys.exit(0)
    elif method == "noisy":
        sys.stderr.write("stderr-content\n" * 20000)
        sys.stderr.flush()
        result(request, {"done": True})
    elif method == "reject":
        send({"id": request["id"], "error": {"code": -32600, "message": "busy"}})
    elif method == "malformed":
        sys.stdout.write("not-json\n")
        sys.stdout.flush()
    elif method == "emit-request":
        send({"id": request["params"]["id"], "method": "unsupported/request", "params": {}})
        result(request, {})
    elif method is None and "id" in request:
        send({"method": "server-request-answered", "params": request})
'''


class AppServerTransportTests(unittest.TestCase):
    def client(self, shared_daemon=False, reject_upgrade=False):
        popen = subprocess.Popen
        started = []

        def spawn(command, **kwargs):
            started.append(command)
            prelude = (
                f"reject_upgrade = {reject_upgrade!r}\n" + FAKE_WEBSOCKET
                if shared_daemon else "websocket = False\n"
            )
            return popen([sys.executable, "-u", "-c", prelude + FAKE_SERVER], **kwargs)

        with mock.patch.object(transport.subprocess, "Popen", side_effect=spawn):
            client = transport.AppServerClient("/test/codex", shared_daemon=shared_daemon)
        self.addCleanup(client.close)
        expected = ["/test/codex", "app-server"]
        expected.extend(["proxy"] if shared_daemon else ["--listen", "stdio://"])
        self.assertEqual(started, [expected])
        return client

    def test_initialize_negotiates_capabilities_and_sends_initialized_notification(self):
        client = self.client()
        result = client.initialize(experimental_api=True)
        self.assertTrue(result["capabilities"]["experimentalApi"])
        self.assertEqual(result["clientInfo"]["name"], "codex-feishu-bridge")
        self.assertTrue(client.request("probe")["initialized"])

    def test_shared_daemon_uses_the_official_stdio_proxy(self):
        client = self.client(shared_daemon=True)
        client.initialize()
        self.assertTrue(client.request("probe")["initialized"])
        client.close()
        self.assertIsNotNone(client.process.poll())

    def test_shared_daemon_validates_upgrade_before_sending_any_rpc(self):
        client = self.client(shared_daemon=True, reject_upgrade=True)
        with self.assertRaises(transport.AppServerError) as raised:
            client.initialize(timeout=1)
        self.assertFalse(raised.exception.request_sent)

    def test_shared_daemon_masks_unicode_requests_and_handles_all_frame_lengths(self):
        client = self.client(shared_daemon=True)
        client.initialize()
        for length in (5, 100, 25000):
            with self.subTest(length=length):
                params = {"text": "字" * length}
                self.assertEqual(client.request("echo", params), params)

    def test_shared_daemon_reassembles_fragmented_utf8_notifications(self):
        client = self.client(shared_daemon=True)
        client.initialize()
        client.request("fragmented")
        self.assertEqual(client.next_event(timeout=1)["params"], {"text": "完整中文"})

    def test_shared_daemon_answers_ping_without_consuming_notifications(self):
        client = self.client(shared_daemon=True)
        client.initialize()
        client.request("ping")
        self.assertEqual(client.next_event(timeout=1)["method"], "pong-received")

    def test_notifications_and_approval_before_response_are_not_swallowed(self):
        client = self.client()
        result = client.request("turn/start", {"threadId": "task", "input": []})
        self.assertEqual(result["turn"]["id"], "turn-1")
        self.assertEqual(client.next_event(timeout=1)["method"], "turn/started")
        approval = client.next_event(timeout=1)
        self.assertEqual(approval["id"], 7)
        client.respond(approval["id"], {"decision": "accept"})
        answered = client.next_event(timeout=1)["params"]
        self.assertEqual(answered, {"id": 7, "result": {"decision": "accept"}})

    def test_concurrent_responses_can_arrive_out_of_order(self):
        client = self.client()
        first = []
        worker = threading.Thread(target=lambda: first.append(client.request("defer")))
        worker.start()
        self.assertEqual(client.next_event(timeout=1)["method"], "deferred")
        self.assertEqual(client.request("second"), {"which": "second"})
        worker.join(timeout=1)
        self.assertFalse(worker.is_alive())
        self.assertEqual(first, [{"which": "first"}])

    def test_timeout_marks_request_as_maybe_sent_and_preserves_notifications(self):
        client = self.client()
        with self.assertRaises(transport.AppServerError) as raised:
            client.request("never", timeout=0.05)
        self.assertTrue(raised.exception.request_sent)
        self.assertIsNone(raised.exception.server_error)
        self.assertEqual(client.next_event(timeout=1)["method"], "waiting")
        self.assertEqual(client._pending, {})
        self.assertEqual(client.request("probe"), {"initialized": False})

    def test_process_exit_wakes_request_and_delivers_queued_event_before_eof(self):
        client = self.client()
        with self.assertRaises(transport.AppServerError) as raised:
            client.request("exit")
        self.assertTrue(raised.exception.request_sent)
        self.assertEqual(client.next_event(timeout=1)["method"], "last-event")
        for _ in range(2):
            with self.assertRaises(transport.AppServerError):
                client.next_event(timeout=1)

    def test_stderr_is_drained_separately_and_kept_bounded(self):
        client = self.client()
        self.assertEqual(client.request("noisy"), {"done": True})
        client.close()
        self.assertTrue(client.stderr_tail)
        self.assertLessEqual(len(client.stderr_tail), 8192)
        self.assertNotIn("stderr-content", str(transport.AppServerError("closed")))

    def test_server_rejection_retains_error_without_breaking_the_connection(self):
        client = self.client()
        with self.assertRaises(transport.AppServerError) as raised:
            client.request("reject")
        self.assertEqual(raised.exception.server_error, {"code": -32600, "message": "busy"})
        self.assertTrue(raised.exception.request_sent)
        self.assertNotIn("busy", str(raised.exception))
        self.assertEqual(client.request("probe"), {"initialized": False})

    def test_malformed_server_frame_wakes_request(self):
        client = self.client()
        with self.assertRaises(transport.AppServerError) as raised:
            client.request("malformed")
        self.assertTrue(raised.exception.request_sent)

    def test_closed_client_never_marks_a_new_request_as_sent(self):
        client = self.client()
        client.close()
        with self.assertRaises(transport.AppServerError) as raised:
            client.request("turn/start", {"input": []})
        self.assertFalse(raised.exception.request_sent)

    def test_encoding_failure_does_not_send_or_break_the_connection(self):
        client = self.client()
        with self.assertRaises(transport.AppServerError) as raised:
            client.request("turn/start", {"input": object()})
        self.assertFalse(raised.exception.request_sent)
        self.assertEqual(client.request("probe"), {"initialized": False})

    def test_pipe_write_failure_is_conservatively_marked_as_maybe_sent(self):
        client = self.client()
        with mock.patch.object(client.process.stdin, "write", side_effect=BrokenPipeError):
            with self.assertRaises(transport.AppServerError) as raised:
                client.request("turn/start", {"input": []})
        self.assertTrue(raised.exception.request_sent)
        self.assertEqual(client._pending, {})

    def test_close_keeps_already_queued_notifications(self):
        client = self.client()
        client.request("turn/start", {"input": []})
        client.close()
        self.assertEqual(client.next_event(timeout=1)["method"], "turn/started")
        self.assertEqual(client.next_event(timeout=1)["id"], 7)
        with self.assertRaises(transport.AppServerError):
            client.next_event(timeout=1)

    def test_event_wait_timeout_is_queue_empty(self):
        client = self.client()
        with self.assertRaises(queue.Empty):
            client.next_event(timeout=0)

    def test_unsupported_server_request_is_rejected_with_original_id_type(self):
        client = self.client()
        for request_id in (0, "approval-string-id"):
            with self.subTest(request_id=request_id):
                client.request("emit-request", {"id": request_id})
                request = client.next_event(timeout=1)
                client.respond_error(request["id"], "Unsupported request")
                answered = client.next_event(timeout=1)["params"]
                self.assertEqual(answered["id"], request_id)
                self.assertEqual(type(answered["id"]), type(request_id))
                self.assertEqual(answered["error"]["code"], -32601)
                self.assertNotIn("result", answered)

    def test_close_wakes_blocked_request_and_event_reader_and_reaps_process(self):
        client = self.client()
        failures = []

        def wait_request():
            try:
                client.request("never")
            except transport.AppServerError as exc:
                failures.append(exc.request_sent)

        request_worker = threading.Thread(target=wait_request)
        request_worker.start()
        self.assertEqual(client.next_event(timeout=1)["method"], "waiting")
        event_finished = threading.Event()

        def wait_event():
            try:
                client.next_event()
            except transport.AppServerError:
                event_finished.set()

        event_worker = threading.Thread(target=wait_event)
        event_worker.start()
        client.close()
        request_worker.join(timeout=1)
        event_worker.join(timeout=1)
        self.assertEqual(failures, [True])
        self.assertTrue(event_finished.is_set())
        self.assertFalse(client._reader.is_alive())
        self.assertIsNotNone(client.process.poll())
        for stream in (client.process.stdin, client.process.stdout, client.process.stderr):
            self.assertTrue(stream.closed)
        client.close()

    def test_start_failure_is_definitely_not_sent(self):
        with mock.patch.object(transport.subprocess, "Popen", side_effect=OSError):
            with self.assertRaises(transport.AppServerError) as raised:
                transport.AppServerClient("/missing/codex")
        self.assertFalse(raised.exception.request_sent)


if __name__ == "__main__":
    unittest.main()

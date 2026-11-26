import importlib.util
from pathlib import Path
import queue
import tempfile
import threading
import time
import unittest
from unittest import mock


MODULE_PATH = Path(__file__).resolve().parents[1] / "Resources/bridge/codex_event_observer.py"
SPEC = importlib.util.spec_from_file_location("codex_event_observer_test", MODULE_PATH)
observer_module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(observer_module)


class FakeClient:
    def __init__(self, failed_targets=(), block_initialize=False):
        self.failed_targets = set(failed_targets)
        self.requests = []
        self.events = queue.Queue()
        self.closed = threading.Event()
        self.initializing = threading.Event()
        self.initialize_gate = threading.Event()
        self.reading = threading.Event()
        self.initialize_count = 0
        if not block_initialize:
            self.initialize_gate.set()

    def initialize(self):
        self.initialize_count += 1
        self.initializing.set()
        self.initialize_gate.wait()
        if self.closed.is_set():
            raise RuntimeError("closed")

    def request(self, method, params):
        self.requests.append((method, params))
        if method == "thread/resume" and params["threadId"] in self.failed_targets:
            raise RuntimeError("target unavailable")
        return {}

    def next_event(self):
        self.reading.set()
        event = self.events.get()
        if isinstance(event, Exception):
            raise event
        return event

    def close(self):
        self.closed.set()
        self.initialize_gate.set()
        self.events.put(RuntimeError("closed"))


class SharedEventObserverTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.state_path = Path(directory.name) / "state.json"
        self.state_path.write_text("{}")
        self.targets = {"task-a"}
        self.shutdown = threading.Event()
        self.quiesce = threading.Event()
        self.frames = []
        self.target_reads = mock.Mock(side_effect=lambda: set(self.targets))
        self.reconcile = mock.Mock()
        self.event_callback = mock.Mock(side_effect=self.frames.append)
        self.log_patch = mock.patch.object(observer_module, "_LOG")
        self.log_patch.start()
        self.addCleanup(self.log_patch.stop)

    def wait_for(self, predicate):
        deadline = time.monotonic() + 2
        while not predicate():
            if time.monotonic() >= deadline:
                self.fail("observer did not reach the expected state")
            time.sleep(0.005)

    def start(self, clients=None, native_watcher=False):
        clients = clients or [FakeClient()]
        self.factory = mock.Mock(side_effect=clients)
        self.observer = observer_module.SharedEventObserver(
            self.factory, self.target_reads, self.event_callback, self.reconcile,
            self.state_path, self.shutdown, self.quiesce,
        )
        self.observer._RECONNECT_MIN_DELAY = 0.01
        self.observer._RECONNECT_MAX_DELAY = 0.02
        if not native_watcher:
            self.observer._start_watcher = mock.Mock()
        self.errors = []

        def run():
            try:
                self.observer.run()
            except Exception as error:
                self.errors.append(error)

        self.thread = threading.Thread(target=run)
        self.thread.start()
        self.addCleanup(self.stop)
        return clients[0]

    def stop(self):
        self.observer.close()
        self.thread.join(2)
        self.assertFalse(self.thread.is_alive())
        self.assertEqual(self.errors, [])
        for worker in (self.observer._reader, self.observer._watcher, self.observer._stop_monitor):
            if worker is not None:
                self.assertFalse(worker.is_alive())

    def replace_state(self, text):
        replacement = self.state_path.with_suffix(".tmp")
        replacement.write_text(text)
        replacement.replace(self.state_path)
        self.observer.wake()

    def barrier(self, client):
        frame = {"method": "test/barrier", "params": {"id": len(self.frames)}}
        client.events.put(frame)
        self.wait_for(lambda: frame in self.frames)

    def test_add_and_remove_targets_reconcile_once_without_reconnecting(self):
        client = self.start()
        self.wait_for(lambda: self.observer.connected)
        self.assertEqual(client.requests, [
            ("thread/resume", {"threadId": "task-a", "excludeTurns": True}),
        ])
        self.targets = {"task-b"}
        self.replace_state('{"targets":["task-b"]}')
        self.barrier(client)
        self.assertEqual(client.requests[1:], [
            ("thread/unsubscribe", {"threadId": "task-a"}),
            ("thread/resume", {"threadId": "task-b", "excludeTurns": True}),
        ])
        self.factory.assert_called_once()
        self.assertEqual(self.reconcile.call_count, 2)
        self.assertIs(self.observer.client, client)

    def test_state_changes_coalesce_and_unchanged_targets_send_no_rpc(self):
        entered, release = threading.Event(), threading.Event()
        self.addCleanup(release.set)

        def handle(frame):
            self.frames.append(frame)
            if frame["method"] == "test/hold":
                entered.set()
                release.wait(2)

        self.event_callback.side_effect = handle
        client = self.start()
        self.wait_for(lambda: self.observer.connected)
        client.events.put({"method": "test/hold"})
        self.assertTrue(entered.wait(1))
        self.targets.add("task-b")
        for number in range(15):
            self.replace_state('{"sequence":%d}' % number)
        release.set()
        self.barrier(client)
        self.assertEqual(self.target_reads.call_count, 2)
        self.assertEqual(len(client.requests), 2)
        reads = self.target_reads.call_count
        for _ in range(10):
            self.observer.wake()
        self.barrier(client)
        self.assertEqual(self.target_reads.call_count, reads)
        self.replace_state('{"unrelated":"changed"}')
        self.barrier(client)
        self.assertEqual(self.target_reads.call_count, reads + 1)
        self.assertEqual(len(client.requests), 2)
        self.assertEqual(self.reconcile.call_count, 2)

    def test_failed_target_is_isolated_until_the_target_set_changes(self):
        self.targets.add("task-b")
        client = self.start([FakeClient(failed_targets={"task-a"})])
        self.wait_for(lambda: self.reconcile.call_count == 1)
        self.barrier(client)
        self.assertFalse(self.observer.connected)
        expected = [
            ("thread/resume", {"threadId": "task-a", "excludeTurns": True}),
            ("thread/resume", {"threadId": "task-b", "excludeTurns": True}),
        ]
        self.assertEqual(client.requests, expected)
        for number in range(5):
            self.replace_state('{"unrelated":%d}' % number)
            self.barrier(client)
        self.assertEqual(client.requests, expected)
        self.factory.assert_called_once()
        self.targets.add("task-c")
        self.replace_state('{"targets":"changed"}')
        self.barrier(client)
        self.assertEqual(client.requests[2:], [
            ("thread/resume", {"threadId": "task-a", "excludeTurns": True}),
            ("thread/resume", {"threadId": "task-c", "excludeTurns": True}),
        ])
        self.factory.assert_called_once()
        self.assertEqual(self.reconcile.call_count, 2)
        self.assertFalse(self.observer.connected)
        self.targets.remove("task-a")
        self.replace_state('{"targets":"failed-target-removed"}')
        self.barrier(client)
        self.assertTrue(self.observer.connected)
        self.assertEqual(len(client.requests), 4)

    def test_successful_reconciliation_cannot_hide_a_failed_subscription(self):
        self.targets.add("task-b")
        client = self.start([FakeClient(failed_targets={"task-a"})])
        self.wait_for(lambda: self.reconcile.call_count == 1)
        self.barrier(client)
        self.assertFalse(self.observer.connected)
        self.observer.request_reconciliation()
        self.barrier(client)
        self.assertEqual(self.reconcile.call_count, 2)
        self.assertFalse(self.observer.connected)
        self.assertEqual(len(client.requests), 2)
        self.factory.assert_called_once()

    def test_failed_target_retries_after_thirty_seconds_only_on_a_state_change(self):
        self.targets.add("task-b")
        clock = mock.Mock()
        clock.monotonic.return_value = 100.0
        with mock.patch.object(observer_module, "time", clock):
            client = self.start([FakeClient(failed_targets={"task-a"})])
            self.wait_for(lambda: self.reconcile.call_count == 1)
            self.barrier(client)
            self.assertFalse(self.observer.connected)
            initial_requests = list(client.requests)
            clock.monotonic.return_value = 129.0
            self.replace_state('{"unrelated":"before-cooldown"}')
            self.barrier(client)
            self.assertEqual(client.requests, initial_requests)
            self.assertFalse(self.observer.connected)
            clock.monotonic.return_value = 131.0
            client.failed_targets.clear()
            self.barrier(client)
            self.assertEqual(client.requests, initial_requests)
            self.assertFalse(self.observer.connected)
            self.replace_state('{"unrelated":"after-cooldown"}')
            self.barrier(client)
            self.assertEqual(client.requests, initial_requests + [
                ("thread/resume", {"threadId": "task-a", "excludeTurns": True}),
            ])
            self.assertTrue(self.observer.connected)
            clock.monotonic.return_value = 191.0
            self.replace_state('{"unrelated":"already-subscribed"}')
            self.barrier(client)
            self.assertEqual(len(client.requests), len(initial_requests) + 1)
            self.factory.assert_called_once()
            self.assertEqual(self.reconcile.call_count, 2)
            self.stop()

    def test_explicit_reconciliation_requests_coalesce_without_a_state_change(self):
        entered, release = threading.Event(), threading.Event()

        def handle(frame):
            self.frames.append(frame)
            if frame["method"] == "test/hold":
                entered.set()
                release.wait(2)

        self.event_callback.side_effect = handle
        client = self.start()
        self.addCleanup(release.set)
        self.wait_for(lambda: self.observer.connected)
        client.events.put({"method": "test/hold"})
        self.assertTrue(entered.wait(1))
        for _ in range(15):
            self.observer.request_reconciliation()
        release.set()
        self.barrier(client)
        self.assertEqual(self.reconcile.call_count, 2)
        self.assertEqual(len(client.requests), 1)
        self.target_reads.assert_called_once_with()
        self.observer.request_reconciliation()
        self.barrier(client)
        self.assertEqual(self.reconcile.call_count, 3)

    def test_reconciliation_failure_enables_fallback_until_reconciliation_succeeds(self):
        self.reconcile.side_effect = [None, RuntimeError("database unavailable"), None]
        client = self.start()
        self.wait_for(lambda: self.observer.connected)
        client.events.put({"method": "turn/completed", "params": {"threadId": "task-a"}})
        self.barrier(client)
        self.assertFalse(self.observer.connected)
        self.factory.assert_called_once()
        self.observer.request_reconciliation()
        self.barrier(client)
        self.assertTrue(self.observer.connected)
        self.assertEqual(self.reconcile.call_count, 3)

    def test_disconnect_reconnects_resubscribes_and_repairs_once(self):
        first, second = FakeClient(), FakeClient()
        self.start([first, second])
        self.wait_for(lambda: self.observer.connected)
        first.events.put(RuntimeError("disconnected"))
        self.wait_for(lambda: self.observer.client is second and self.observer.connected)
        self.assertTrue(first.closed.is_set())
        self.assertEqual(self.factory.call_count, 2)
        self.assertEqual(second.requests, first.requests)
        self.assertEqual(self.reconcile.call_count, 2)
        self.barrier(second)
        self.replace_state('{"unrelated":"changed"}')
        self.barrier(second)
        self.assertEqual(self.reconcile.call_count, 2)

    def test_complete_frames_are_forwarded_and_only_terminal_events_reconcile(self):
        client = self.start()
        self.wait_for(lambda: self.observer.connected)
        frames = [
            {"method": "turn/started", "params": {"threadId": "task-a"}},
            {"id": 0, "method": "item/commandExecution/requestApproval",
             "params": {"threadId": "task-a", "command": "pwd"}},
            {"method": "item/completed", "params": {"item": {"text": "done"}}},
        ]
        for frame in frames:
            client.events.put(frame)
        self.barrier(client)
        self.assertEqual(self.frames[:3], frames)
        self.assertIs(self.frames[1], frames[1])
        self.reconcile.assert_called_once_with()
        client.events.put({"method": "turn/completed", "params": {"threadId": "task-a"}})
        client.events.put({"method": "thread/compacted", "params": {"threadId": "task-a"}})
        self.barrier(client)
        self.assertEqual(self.reconcile.call_count, 3)

    def test_connected_waits_for_the_initial_reconcile_to_finish(self):
        entered, release = threading.Event(), threading.Event()
        self.addCleanup(release.set)

        def reconcile():
            entered.set()
            release.wait(2)

        self.reconcile.side_effect = reconcile
        self.start()
        self.assertTrue(entered.wait(1))
        self.assertFalse(self.observer.connected)
        release.set()
        self.wait_for(lambda: self.observer.connected)

    def test_disconnect_during_reconcile_cannot_mark_the_connection_ready(self):
        reconciling, reconciled = threading.Event(), threading.Event()
        handling, handled = threading.Event(), threading.Event()

        def reconcile():
            reconciling.set()
            reconciled.wait(2)

        def handle(frame):
            handling.set()
            handled.wait(2)

        self.reconcile.side_effect = reconcile
        self.event_callback.side_effect = handle
        client = self.start()
        self.addCleanup(reconciled.set)
        self.addCleanup(handled.set)
        self.assertTrue(reconciling.wait(1))
        client.events.put({"method": "test/before-disconnect"})
        client.events.put(RuntimeError("disconnected"))
        self.wait_for(lambda: not self.observer._reader.is_alive())
        reconciled.set()
        self.assertTrue(handling.wait(1))
        self.assertFalse(self.observer.connected)
        self.observer.close()
        handled.set()

    def test_shutdown_and_quiesce_close_the_client_and_all_worker_threads(self):
        for event in (self.shutdown, self.quiesce):
            with self.subTest(event=event):
                self.shutdown.clear()
                self.quiesce.clear()
                client = self.start()
                self.wait_for(lambda: self.observer.connected)
                self.assertTrue(client.reading.wait(1))
                event.set()
                self.assertFalse(self.observer.connected)
                self.thread.join(1)
                self.assertFalse(self.thread.is_alive())
                self.assertTrue(client.closed.is_set())
                self.assertIsNone(self.observer.client)
                self.stop()

    def test_quiesce_interrupts_a_blocked_initialize(self):
        client = self.start([FakeClient(block_initialize=True)])
        self.assertTrue(client.initializing.wait(1))
        self.quiesce.set()
        self.thread.join(1)
        self.assertFalse(self.thread.is_alive())
        self.assertTrue(client.closed.is_set())
        self.assertFalse(self.observer.connected)
        self.reconcile.assert_not_called()

    @unittest.skipUnless(hasattr(observer_module.select, "kqueue"), "requires kqueue")
    def test_native_directory_watcher_handles_atomic_replacement_and_ignores_other_files(self):
        client = self.start(native_watcher=True)
        self.wait_for(lambda: self.observer.connected)
        self.observer.wake = mock.Mock(wraps=self.observer.wake)
        (self.state_path.parent / "unrelated.txt").write_text("unrelated")
        self.wait_for(lambda: self.observer.wake.called)
        self.barrier(client)
        self.target_reads.assert_called_once_with()
        self.targets.add("task-b")
        replacement = self.state_path.with_suffix(".tmp")
        replacement.write_text('{"targets":"changed"}')
        replacement.replace(self.state_path)
        self.wait_for(lambda: len(client.requests) == 2)
        self.assertEqual(client.requests[-1], (
            "thread/resume", {"threadId": "task-b", "excludeTurns": True},
        ))
        self.stop()
        self.assertFalse(self.observer._watcher.is_alive())


if __name__ == "__main__":
    unittest.main()

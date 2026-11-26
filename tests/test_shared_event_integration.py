from pathlib import Path
import queue
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest import mock

from tests.test_codex_event_observer import FakeClient
from tests.test_runtime import load_bridge


class SharedEventIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.bridge.STATE_PATH = Path(directory.name) / "state.json"
        self.bridge.log = mock.Mock()
        self.task = {"id": "task-a", "title": "Shared Task", "project": "Paper"}
        self.bridge.task_by_id = mock.Mock(return_value=self.task)
        self.bridge._shared_target_signature = None
        self.bridge._shared_target_ids = set()
        self.bridge.schedule_promlight_task_status = mock.Mock()

    def handoff(self, message_id="om_handoff"):
        return {
            "task_id": self.task["id"], "turn_id": "turn-1",
            "message_id": message_id, "cursor_offset": 0,
            "images": [], "created_at": time.time(), "next_check_at": 0,
        }

    def wait_for(self, predicate):
        deadline = time.monotonic() + 2
        while not predicate():
            if time.monotonic() >= deadline:
                self.fail("shared integration did not reach the expected state")
            time.sleep(0.005)

    def test_handoff_only_state_is_an_observer_target(self):
        self.bridge.save_state({
            "desktop_result_subscriptions": {"ou_admin": self.handoff()},
        })
        self.assertEqual(self.bridge.shared_codex_targets(), {"task-a"})

    def test_handoff_terminal_event_triggers_the_existing_reconciler(self):
        self.bridge.save_state({
            "desktop_result_subscriptions": {"ou_admin": self.handoff()},
        })
        self.bridge.poll_task_subscriptions = mock.Mock()
        self.bridge.poll_promlight_task_statuses = mock.Mock()
        self.bridge.retry_desktop_result_subscriptions = mock.Mock(return_value=False)
        self.bridge.retry_recoverable_runs = mock.Mock(return_value=False)
        client = FakeClient()
        observer = self.bridge.SharedEventObserver(
            lambda: client, self.bridge.shared_codex_targets,
            self.bridge.handle_shared_codex_event, self.bridge.reconcile_shared_codex_results,
            self.bridge.STATE_PATH, self.bridge._shutdown_event,
            self.bridge._update_quiesce_requested,
        )
        observer._start_watcher = mock.Mock()
        self.bridge._shared_codex_observer = observer
        thread = threading.Thread(target=observer.run)

        def stop():
            observer.close()
            thread.join(2)
            self.assertFalse(thread.is_alive())

        self.addCleanup(stop)
        thread.start()
        self.wait_for(lambda: observer.connected)
        self.assertEqual(client.requests, [
            ("thread/resume", {"threadId": "task-a", "excludeTurns": True}),
        ])
        initial = self.bridge.retry_desktop_result_subscriptions.call_count
        client.events.put({"method": "turn/completed", "params": {
            "threadId": "task-a", "turn": {"id": "turn-1", "status": "completed", "items": []},
        }})
        self.wait_for(lambda: self.bridge.retry_desktop_result_subscriptions.call_count > initial)
        self.bridge.schedule_promlight_task_status.assert_called_once_with(
            "task-a", "idle", "app-server", True, turn_id="turn-1",
        )

    def test_one_terminal_reconcile_settles_all_users_waiting_for_the_same_task(self):
        self.bridge.ALLOWED_USERS["ou_second"] = {"*"}
        self.bridge.save_state({
            "desktop_result_subscriptions": {
                "ou_admin": self.handoff(), "ou_second": self.handoff("om_second"),
            },
        })
        self.bridge.poll_task_subscriptions = mock.Mock()
        self.bridge.poll_promlight_task_statuses = mock.Mock()
        self.bridge.advance_task_turn = mock.Mock(return_value={
            "status": "completed", "turn_id": "turn-1", "message": "done", "images": [],
        })
        self.bridge.deliver_desktop_sync_result = mock.Mock()
        self.bridge.reconcile_shared_codex_results()
        self.assertEqual(self.bridge.deliver_desktop_sync_result.call_count, 2)
        self.assertEqual(self.bridge.load_state()["desktop_result_subscriptions"], {})

    def test_fast_terminal_reconcile_ignores_fallback_retry_time(self):
        self.bridge.save_state({
            "desktop_result_subscriptions": {"ou_admin": self.handoff()},
            "recoverable_runs": {"turn-2": {
                "task": self.task, "user_id": "ou_admin", "turn_id": "turn-2",
                "source_message_id": "om_source", "progress_message_id": "om_progress",
                "created_at": time.time(), "started_at": time.time(), "next_check_at": 0,
            }},
        })
        self.bridge.poll_task_subscriptions = mock.Mock()
        self.bridge.poll_promlight_task_statuses = mock.Mock()
        self.bridge.patch_card = mock.Mock()
        self.bridge.deliver_desktop_sync_result = mock.Mock()
        self.bridge.advance_task_turn = mock.Mock(return_value={"status": "running"})
        self.bridge.reconcile_shared_codex_results()
        state = self.bridge.load_state()
        self.assertGreater(state["desktop_result_subscriptions"]["ou_admin"]["next_check_at"], time.time())
        self.assertGreater(state["recoverable_runs"]["turn-2"]["next_check_at"], time.time())
        self.bridge.advance_task_turn.return_value = {"status": "completed", "message": "done"}
        self.bridge.reconcile_shared_codex_results()
        self.assertEqual(self.bridge.deliver_desktop_sync_result.call_count, 2)
        self.assertEqual(self.bridge.load_state()["desktop_result_subscriptions"], {})
        self.assertEqual(self.bridge.load_state()["recoverable_runs"], {})

    def test_event_and_fallback_reconcile_do_not_deliver_the_same_handoff_twice(self):
        self.bridge.save_state({"desktop_result_subscriptions": {"ou_admin": self.handoff()}})
        self.bridge.advance_task_turn = mock.Mock(return_value={"status": "completed", "message": "done"})
        entered, release = threading.Event(), threading.Event()

        def deliver(*_args, **_kwargs):
            entered.set()
            self.assertTrue(release.wait(2))

        self.bridge.deliver_desktop_sync_result = mock.Mock(side_effect=deliver)
        first = threading.Thread(target=self.bridge.retry_desktop_result_subscriptions)
        second = threading.Thread(target=self.bridge.retry_desktop_result_subscriptions)
        first.start()
        self.assertTrue(entered.wait(2))
        second.start()
        release.set()
        first.join(2)
        second.join(2)
        self.assertFalse(first.is_alive() or second.is_alive())
        self.bridge.deliver_desktop_sync_result.assert_called_once()

    def maintenance_once(self, connected):
        self.bridge._shutdown_event.clear()
        self.bridge._shared_codex_observer = SimpleNamespace(connected=connected)
        for name in (
            "expire_cli_fallbacks", "refresh_codex_usage", "write_runtime_status",
            "retry_pending_replies", "start_pending_inputs", "retry_workflow_notifications",
            "retry_workflow_recoveries", "retry_desktop_result_subscriptions",
            "retry_recoverable_runs", "poll_task_subscriptions", "poll_promlight_task_statuses",
        ):
            setattr(self.bridge, name, mock.Mock())
        self.bridge.workflow_notifications_enabled = mock.Mock(return_value=False)
        self.bridge.tracked_operation_started = mock.Mock(return_value=True)
        self.bridge.tracked_operation_finished = mock.Mock(side_effect=self.bridge._shutdown_event.set)
        self.bridge.maintenance_loop(queue.Queue())

    def test_connected_maintenance_does_not_scan_task_history(self):
        self.maintenance_once(connected=True)
        self.bridge.retry_desktop_result_subscriptions.assert_not_called()
        self.bridge.retry_recoverable_runs.assert_not_called()
        self.bridge.poll_task_subscriptions.assert_not_called()
        self.bridge.poll_promlight_task_statuses.assert_not_called()

    def test_disconnected_maintenance_retains_bounded_recovery_fallback(self):
        self.maintenance_once(connected=False)
        self.bridge.retry_desktop_result_subscriptions.assert_called_once()
        self.bridge.retry_recoverable_runs.assert_called_once()
        self.bridge.poll_task_subscriptions.assert_called_once()
        self.bridge.poll_promlight_task_statuses.assert_called_once()

    def test_originating_user_is_not_notified_if_turn_is_claimed_during_the_scan(self):
        self.bridge.save_state({"task_subscriptions": {"ou_admin": {"task-a": {
            "task_id": "task-a", "cursor_offset": 0, "active_turn_id": "turn-1",
            "images": [], "history_backend": "database",
        }}}})
        self.bridge.recent_tasks = mock.Mock(return_value=[self.task])
        self.bridge.rollout_path_for_task = mock.Mock(return_value=None)
        self.bridge.task_history_backend = mock.Mock(return_value="database")
        self.bridge.deliver_task_subscription_result = mock.Mock(return_value=True)

        def finish_scan(_task_id, _entry):
            self.bridge.remember_bridge_turn("turn-1", "ou_admin")
            return {
                "available": True, "history_backend": "database", "cursor_offset": 20,
                "active_turn_id": "", "images": [], "deliveries": [{
                    "turn_id": "turn-1", "status": "completed", "message": "done",
                    "cursor_offset": 20, "active_turn_id": "", "remaining_images": [],
                }],
            }

        self.bridge.scan_database_subscription = mock.Mock(side_effect=finish_scan)
        self.bridge.poll_task_subscriptions()
        self.bridge.deliver_task_subscription_result.assert_not_called()


if __name__ == "__main__":
    unittest.main()

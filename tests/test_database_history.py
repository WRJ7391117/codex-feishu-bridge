import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest import mock

from tests.test_multi_user import load_bridge


class DatabaseHistoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        root = Path(self.directory.name)
        self.bridge = load_bridge()
        self.bridge.THREAD_HISTORY_DB = root / "history.sqlite"
        self.bridge.STATE_PATH = root / "bridge-state.json"
        self.bridge.LOG_PATH = root / "bridge.log"
        self.bridge.rollout_path_for_task = lambda task_id: root / "missing.jsonl"
        self.task = {"id": "task-db", "title": "Database Task", "project": "Paper"}
        with sqlite3.connect(self.bridge.THREAD_HISTORY_DB) as connection:
            connection.executescript("""
                CREATE TABLE thread_turns (
                    thread_id TEXT, turn_id TEXT, rollout_ordinal INTEGER,
                    rollout_end_ordinal INTEGER, status TEXT,
                    final_agent_item_id TEXT, PRIMARY KEY(thread_id, turn_id)
                );
                CREATE TABLE thread_items (
                    thread_id TEXT, turn_id TEXT, item_id TEXT,
                    item_json TEXT, item_type TEXT, rollout_ordinal INTEGER,
                    PRIMARY KEY(thread_id, turn_id, item_id)
                );
            """)

    def add_turn(self, turn_id, ordinal, status="completed", text="完整结果"):
        with sqlite3.connect(self.bridge.THREAD_HISTORY_DB) as connection:
            connection.execute(
                "INSERT OR REPLACE INTO thread_turns VALUES (?, ?, ?, ?, ?, ?)",
                ("task-db", turn_id, ordinal, ordinal + 5 if status != "inProgress" else None,
                 status, "final" if status == "completed" else None),
            )
            if status == "completed":
                connection.execute(
                    "INSERT OR REPLACE INTO thread_items VALUES (?, ?, ?, ?, ?, ?)",
                    ("task-db", turn_id, "final", json.dumps({
                        "type": "agentMessage", "phase": "final_answer", "text": text,
                    }), "agentMessage", ordinal + 4),
                )

    def test_missing_jsonl_with_database_history_remains_available(self):
        self.add_turn("turn-1", 10)
        self.assertTrue(self.bridge.task_record_is_available({
            "id": "task-db", "preview": "existing conversation", "rollout_path": "",
        }))
        self.assertFalse(self.bridge.task_record_is_available({
            "id": "task-lost", "preview": "lost conversation", "rollout_path": "",
        }))
        snapshot = self.bridge.latest_task_turn("task-db")
        self.assertEqual(snapshot["status"], "completed")
        self.assertEqual(snapshot["message"], "完整结果")

    def test_database_subscription_does_not_backfill_and_delivers_new_turns_once(self):
        self.add_turn("turn-1", 10, text="historical")
        entry = self.bridge.new_task_subscription(self.task)
        self.assertEqual(entry["history_backend"], "database")
        self.assertEqual(self.bridge.scan_database_subscription("task-db", entry)["deliveries"], [])
        self.add_turn("turn-2", 20, text="new result")
        scan = self.bridge.scan_database_subscription("task-db", entry)
        self.assertEqual([s["message"] for s in scan["deliveries"]], ["new result"])
        self.bridge.save_state({"task_subscriptions": {"ou_admin": {"task-db": entry}}})
        self.bridge.update_task_subscription_checkpoint(
            "ou_admin", "task-db", scan["cursor_offset"], "", [], "database"
        )
        restored = self.bridge.load_state()["task_subscriptions"]["ou_admin"]["task-db"]
        self.assertEqual(self.bridge.scan_database_subscription("task-db", restored)["deliveries"], [])

    def test_running_database_turn_completion_is_detected(self):
        self.add_turn("turn-1", 10, status="inProgress")
        entry = self.bridge.new_task_subscription(self.task)
        self.assertEqual(entry["active_turn_id"], "turn-1")
        self.add_turn("turn-1", 10, text="done")
        scan = self.bridge.scan_database_subscription("task-db", entry)
        self.assertEqual([s["message"] for s in scan["deliveries"]], ["done"])
        success, result, _images = self.bridge.wait_for_desktop_turn(
            None, 0, "turn-1", thread_id="task-db"
        )
        self.assertTrue(success)
        self.assertEqual(result, "done")

    def test_database_advance_and_failure_keep_target_turn_identity(self):
        self.add_turn("turn-1", 10, status="failed")
        self.add_turn("turn-2", 20)
        snapshot = self.bridge.advance_task_turn("task-db", "turn-1", 0)
        self.assertEqual(snapshot["status"], "failed")
        self.assertEqual(snapshot["turn_id"], "turn-1")

    def test_database_only_task_can_be_submitted_through_desktop(self):
        self.add_turn("turn-1", 10)
        self.bridge.DESKTOP_IPC_SOCKET = Path(self.directory.name) / "ipc.sock"
        self.bridge.DESKTOP_IPC_SOCKET.touch()
        with mock.patch.object(self.bridge.socket, "socket"), mock.patch.object(
            self.bridge, "initialize_desktop_connection", return_value="client"
        ), mock.patch.object(self.bridge, "send_ipc_message") as send, mock.patch.object(
            self.bridge, "wait_for_ipc_response", return_value={
                "resultType": "success", "result": {"result": {"turn": {"id": "turn-new"}}},
            }
        ), mock.patch.object(self.bridge, "task_working_directory", return_value=""), mock.patch.object(
            self.bridge, "remember_bridge_turn"
        ), mock.patch.object(self.bridge, "begin_desktop_following"), mock.patch.object(
            self.bridge, "wait_for_desktop_turn", return_value=(True, "received", [])
        ) as wait:
            result = self.bridge.run_codex_via_desktop("task-db", "test")
        self.assertEqual(result, ("completed", "received", []))
        self.assertEqual(send.call_count, 1)
        self.assertEqual(wait.call_args.kwargs, {"thread_id": "task-db"})

    def test_migrated_subscription_establishes_database_baseline(self):
        self.add_turn("turn-1", 10, text="do not replay")
        scan = self.bridge.scan_database_subscription("task-db", {
            "cursor_offset": 123456, "active_turn_id": "", "images": [],
        })
        self.assertEqual(scan["deliveries"], [])
        self.assertEqual(scan["cursor_offset"], 15)
        self.assertEqual(scan["history_backend"], "database")

    def test_migrated_subscription_preserves_its_running_turn_result(self):
        self.add_turn("tracked-turn", 10, text="tracked result")
        scan = self.bridge.scan_database_subscription("task-db", {
            "cursor_offset": 123456, "active_turn_id": "tracked-turn", "images": [],
        })
        self.assertEqual([s["message"] for s in scan["deliveries"]], ["tracked result"])

    def test_empty_authorized_projects_remain_selectable_without_access_leak(self):
        self.bridge.ALLOWED_USERS = {"ou_admin": {"Paper", "Empty"}}
        self.bridge.desktop_projects = lambda: [
            {"name": name} for name in ("Paper", "Empty", "Private")
        ]
        projects = self.bridge.task_project_options("ou_admin", [self.task])
        self.assertEqual(projects, ["Paper", "Empty"])
        for card, name in (
            (self.bridge.build_task_card([self.task], None, "Empty", project_names=projects), "project_selector"),
            (self.bridge.build_task_subscriptions_card([self.task], {}, None, "Empty", project_names=projects), "subscription_project_selector"),
        ):
            selector = next(e for e in card["body"]["elements"] if e.get("name") == name)
            self.assertEqual(selector["initial_option"], "Empty")
            self.assertEqual([o["value"] for o in selector["options"]], projects)

    def test_subscription_poll_reads_database_and_persists_checkpoint(self):
        self.add_turn("turn-1", 10)
        entry = self.bridge.new_task_subscription(self.task)
        self.bridge.save_state({"task_subscriptions": {"ou_admin": {"task-db": entry}}})
        self.add_turn("turn-2", 20)
        with mock.patch.object(self.bridge, "recent_tasks", return_value=[self.task]), mock.patch.object(
            self.bridge, "deliver_task_subscription_result", return_value=True
        ) as deliver:
            self.assertTrue(self.bridge.poll_task_subscriptions())
            self.assertFalse(self.bridge.poll_task_subscriptions())
        self.assertEqual(deliver.call_count, 1)
        self.assertEqual(deliver.call_args.args[2]["message"], "完整结果")

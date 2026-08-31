import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock


BRIDGE_PATH = (
    Path(__file__).resolve().parents[1]
    / "Resources/bridge/feishu_codex_bridge.py"
)


def load_bridge():
    temporary = tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False)
    temporary.write(
        '{"allowed_users": [{"open_id": "ou_admin", "name": "Admin", '
        '"allowed_projects": ["*"]}]}'
    )
    temporary.close()
    previous = os.environ.get("CODEX_FEISHU_BRIDGE_CONFIG")
    os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = temporary.name
    try:
        spec = importlib.util.spec_from_file_location(
            "bridge_reply_reliability_test",
            BRIDGE_PATH,
        )
        module = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(module)
        return module
    finally:
        Path(temporary.name).unlink()
        if previous is None:
            os.environ.pop("CODEX_FEISHU_BRIDGE_CONFIG", None)
        else:
            os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = previous


class ReplyReliabilityTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        self.temporary = tempfile.TemporaryDirectory()
        self.bridge.STATE_PATH = Path(self.temporary.name) / "state.json"
        self.bridge.LOG_PATH = Path(self.temporary.name) / "bridge.log"

    def tearDown(self):
        self.temporary.cleanup()

    def failure(self, stderr: str, returncode: int = 4):
        return subprocess.CompletedProcess([], returncode, "", stderr)

    def success(self):
        return subprocess.CompletedProcess([], 0, '{"ok":true}', "")

    def test_eof_is_reported_as_network_failure(self):
        result = self.failure(
            'API call failed: Get "https://open.feishu.cn/open-apis/im": EOF'
        )

        self.assertEqual(
            self.bridge.lark_reply_failure_reason(result=result),
            "飞书 API 网络连接失败",
        )

    def test_missing_scope_is_reported_without_leaking_envelope(self):
        result = self.failure(
            json.dumps(
                {
                    "ok": False,
                    "error": {
                        "type": "authorization",
                        "subtype": "missing_scope",
                        "missing_scopes": ["im:message"],
                    },
                }
            ),
            1,
        )

        self.assertEqual(
            self.bridge.lark_reply_failure_reason(result=result),
            "机器人缺少飞书 API 权限",
        )

    def test_failure_metadata_contains_only_safe_api_categories(self):
        result = self.failure(
            json.dumps(
                {
                    "ok": False,
                    "error": {
                        "type": "authorization",
                        "subtype": "missing_scope",
                        "code": 99991679,
                        "message": "sensitive message content",
                    },
                }
            ),
            1,
        )

        metadata = self.bridge.lark_reply_failure_metadata(result)

        self.assertEqual(
            metadata,
            "exit_code=1 api_type=authorization "
            "api_subtype=missing_scope api_code=99991679",
        )
        self.assertNotIn("sensitive", metadata)

    def test_reply_retries_with_backoff_and_same_idempotency_key(self):
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            side_effect=[self.failure("EOF"), self.failure("EOF"), self.success()],
        ) as run, mock.patch.object(self.bridge.time, "sleep") as sleep:
            delivered = self.bridge.reply("om_test", "完成", "final")

        self.assertTrue(delivered)
        self.assertEqual(sleep.call_args_list, [mock.call(1.0), mock.call(2.0)])
        keys = [
            call.args[0][call.args[0].index("--idempotency-key") + 1]
            for call in run.call_args_list
        ]
        self.assertEqual(len(set(keys)), 1)

    def test_final_failure_is_persisted_across_module_reload(self):
        self.bridge._last_reply_failure_reason = "飞书 API 网络连接失败"
        with mock.patch.object(self.bridge, "reply", return_value=False):
            self.assertFalse(
                self.bridge.reply_or_queue("om_test", "最终结果", "final")
            )

        reloaded = load_bridge()
        reloaded.STATE_PATH = self.bridge.STATE_PATH
        pending = reloaded.load_state()["pending_replies"]
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["message_id"], "om_test")
        self.assertEqual(pending[0]["text"], "最终结果")
        self.assertEqual(pending[0]["reason"], "飞书 API 网络连接失败")

    def test_workflow_choice_queue_fsyncs_private_state_and_directory(self):
        real_fsync = os.fsync
        with mock.patch.object(
            self.bridge.os,
            "fsync",
            side_effect=real_fsync,
        ) as fsync:
            self.bridge.queue_pending_reply(
                "om_test",
                "已记录你的选择",
                "workflow-choice",
                "飞书 API 网络连接失败",
                now=100,
            )

        self.assertGreaterEqual(fsync.call_count, 2)
        self.assertEqual(self.bridge.STATE_PATH.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.bridge.STATE_PATH.parent.stat().st_mode & 0o777, 0o700)
        self.assertEqual(
            list(self.bridge.STATE_PATH.parent.glob(f".{self.bridge.STATE_PATH.name}.*.tmp")),
            [],
        )

    def test_runtime_status_failure_is_not_persisted(self):
        with mock.patch.object(self.bridge, "reply", return_value=False):
            self.assertFalse(
                self.bridge.reply_or_queue("om_test", "正在运行", "running")
            )

        self.assertEqual(
            self.bridge.load_state().get("pending_replies", []),
            [],
        )

    def test_durable_final_replies_are_not_evicted_by_transient_limit(self):
        durable = [
            {
                "operation": "text_reply",
                "message_id": f"om_final_{index}",
                "text": "结果",
                "kind": "final",
            }
            for index in range(60)
        ]
        transient = [
            {
                "operation": "card_patch",
                "message_id": f"om_card_{index}",
                "card": {},
            }
            for index in range(60)
        ]

        trimmed = self.bridge.trim_pending_replies(durable + transient)

        self.assertEqual(
            len([item for item in trimmed if item["operation"] == "text_reply"]),
            60,
        )
        self.assertEqual(
            len([item for item in trimmed if item["operation"] == "card_patch"]),
            self.bridge.MAX_PENDING_REPLIES,
        )

    def test_processed_event_history_extends_beyond_legacy_200_window(self):
        state = self.bridge.load_state()
        for index in range(250):
            self.assertTrue(
                self.bridge.mark_processed(state, f"event-{index}", now=1000 + index)
            )

        reloaded = self.bridge.load_state()
        self.assertTrue(
            self.bridge.processed_event_seen(reloaded, "event-0", now=1300)
        )
        self.assertLessEqual(len(reloaded["processed"]), 200)
        self.assertEqual(len(reloaded["processed_events"]), 250)

    def test_failed_message_dispatch_is_not_marked_processed(self):
        event = {"message_id": "om_crash"}
        with mock.patch.object(
            self.bridge,
            "_handle_message_event_once",
            side_effect=RuntimeError("crash"),
        ):
            with self.assertRaises(RuntimeError):
                self.bridge.handle_message_event(event)

        self.assertFalse(
            self.bridge.processed_event_seen(
                self.bridge.load_state(),
                "om_crash",
            )
        )

    def test_access_request_update_preserves_unrelated_state(self):
        self.bridge.save_state(
            {
                "access_requests": [
                    {"open_id": "ou_remove"},
                    {"open_id": "ou_keep"},
                ],
                "pending_inputs": [{"queue_id": "queue-1"}],
            }
        )

        self.assertEqual(self.bridge.remove_access_requests({"ou_remove"}), 1)

        state = self.bridge.load_state()
        self.assertEqual(state["access_requests"], [{"open_id": "ou_keep"}])
        self.assertEqual(state["pending_inputs"], [{"queue_id": "queue-1"}])

    def test_access_request_helper_waits_for_cross_process_state_lock(self):
        home = self.root = Path(self.temporary.name) / "home"
        state_path = home / ".codex/feishu-bridge/state.json"
        config_path = home / "config.json"
        config_path.parent.mkdir(parents=True, exist_ok=True)
        config_path.write_text(
            '{"allowed_users":[{"open_id":"ou_admin","allowed_projects":["*"]}]}',
            encoding="utf-8",
        )
        self.bridge.STATE_PATH = state_path
        self.bridge.save_state(
            {
                "access_requests": [{"open_id": "ou_remove"}],
                "pending_inputs": [{"queue_id": "queue-1"}],
            }
        )
        environment = os.environ.copy()
        environment["HOME"] = str(home)
        environment["CODEX_FEISHU_BRIDGE_CONFIG"] = str(config_path)

        self.bridge._state_lock.acquire()
        try:
            process = subprocess.Popen(
                [sys.executable, str(BRIDGE_PATH), "--remove-access-requests"],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                env=environment,
            )
            assert process.stdin is not None
            process.stdin.write('["ou_remove"]')
            process.stdin.close()
            time.sleep(0.15)
            self.assertIsNone(process.poll())
        finally:
            self.bridge._state_lock.release()

        return_code = process.wait(timeout=5)
        assert process.stdout is not None and process.stderr is not None
        process.stdout.close()
        process.stderr.close()
        self.assertEqual(return_code, 0)
        state = self.bridge.load_state()
        self.assertEqual(state["access_requests"], [])
        self.assertEqual(state["pending_inputs"], [{"queue_id": "queue-1"}])

    def test_due_reply_is_delivered_and_removed_with_recovery_notice(self):
        self.bridge.queue_pending_reply(
            "om_test",
            "最终结果",
            "final",
            "飞书 API 网络连接失败",
            now=100,
        )
        calls = []

        def reply(message_id, text, kind):
            calls.append((message_id, text, kind))
            return True

        with mock.patch.object(self.bridge, "reply", side_effect=reply):
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        self.assertEqual(self.bridge.load_state()["pending_replies"], [])
        self.assertEqual([call[2] for call in calls], ["final", "final-recovered"])
        self.assertIn("网络连接失败", calls[1][1])
        self.assertIn("自动补发", calls[1][1])

    def test_failed_background_retry_updates_reason_and_schedule(self):
        self.bridge.queue_pending_reply(
            "om_test",
            "最终结果",
            "final",
            "飞书 API 调用失败",
            now=100,
        )

        def fail(*args):
            self.bridge._last_reply_failure_reason = "飞书 API 请求超时"
            return False

        with mock.patch.object(self.bridge, "reply", side_effect=fail):
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        item = self.bridge.load_state()["pending_replies"][0]
        self.assertEqual(item["attempts"], 1)
        self.assertEqual(item["reason"], "飞书 API 请求超时")
        self.assertEqual(item["next_attempt_at"], 145)

    def test_slow_pending_delivery_does_not_hold_global_state_lock(self):
        self.bridge.queue_pending_reply(
            "om_test",
            "最终结果",
            "final",
            "飞书 API 网络连接失败",
            now=100,
        )
        entered = threading.Event()
        release = threading.Event()

        def slow_reply(*args):
            entered.set()
            release.wait(1)
            return False

        with mock.patch.object(self.bridge, "reply", side_effect=slow_reply):
            worker = threading.Thread(
                target=self.bridge.retry_pending_replies,
                kwargs={"now": 115},
            )
            worker.start()
            self.assertTrue(entered.wait(0.5))
            self.assertTrue(self.bridge._state_lock.acquire(timeout=0.2))
            self.bridge._state_lock.release()
            release.set()
            worker.join(1)

        self.assertFalse(worker.is_alive())

    def test_failed_card_patch_is_persisted(self):
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=self.failure(
                'API call failed: Get "https://open.feishu.cn/open-apis/im": EOF'
            ),
        ), mock.patch.object(self.bridge.time, "sleep"):
            self.assertFalse(
                self.bridge.patch_card("om_progress", {"version": "latest"})
            )

        pending = self.bridge.load_state()["pending_replies"]
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["operation"], "card_patch")
        self.assertEqual(pending[0]["message_id"], "om_progress")
        self.assertEqual(pending[0]["card"], {"version": "latest"})
        self.assertEqual(pending[0]["reason"], "飞书 API 网络连接失败")

    def test_card_patch_retries_one_transient_transport_failure_immediately(self):
        timeout = subprocess.TimeoutExpired(["lark-cli"], 3)
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            side_effect=(timeout, self.success()),
        ) as run, mock.patch.object(self.bridge.time, "sleep") as sleep:
            self.assertTrue(
                self.bridge.patch_card("om_progress", {"version": "latest"})
            )

        self.assertEqual(run.call_count, 2)
        sleep.assert_called_once_with(0.25)
        self.assertEqual(self.bridge.load_state().get("pending_replies", []), [])

    def test_card_patch_queue_keeps_only_latest_card_for_message(self):
        self.bridge.queue_pending_card_patch(
            "om_progress",
            {"version": "old"},
            "网络失败",
            now=100,
        )
        self.bridge.queue_pending_card_patch(
            "om_progress",
            {"version": "latest"},
            "网络失败",
            now=101,
        )

        pending = self.bridge.load_state()["pending_replies"]
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["card"], {"version": "latest"})
        self.assertEqual(pending[0]["created_at"], 100)
        self.assertEqual(pending[0]["next_attempt_at"], 102)

    def test_due_card_patch_is_delivered_and_removed(self):
        self.bridge.queue_pending_card_patch(
            "om_progress",
            {"version": "latest"},
            "网络失败",
            now=100,
        )

        with mock.patch.object(
            self.bridge,
            "patch_card",
            return_value=True,
        ) as patch:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        patch.assert_called_once_with(
            "om_progress",
            {"version": "latest"},
            persist=False,
        )
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])

    def test_failed_background_card_patch_uses_backoff_without_text_reply(self):
        self.bridge.queue_pending_card_patch(
            "om_progress",
            {"version": "latest"},
            "网络失败",
            now=100,
        )

        with mock.patch.object(
            self.bridge,
            "patch_card",
            return_value=False,
        ), mock.patch.object(self.bridge, "reply") as text_reply:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        text_reply.assert_not_called()
        item = self.bridge.load_state()["pending_replies"][0]
        self.assertEqual(item["attempts"], 1)
        self.assertEqual(item["next_attempt_at"], 120)

    def test_failed_menu_card_is_retried_with_same_context(self):
        card = {
            "schema": "2.0",
            "header": {"title": {"content": "选择 Task"}},
            "body": {
                "elements": [
                    {"tag": "select_static", "name": "task_selector"},
                ]
            },
        }
        with mock.patch.object(
            self.bridge,
            "send_card",
            return_value=(False, None, None),
        ):
            self.assertFalse(
                self.bridge.send_menu_card(
                    "ou_admin",
                    {},
                    card,
                    "select-task-event",
                )
            )

        pending = self.bridge.load_state()["pending_replies"]
        self.assertEqual(pending[0]["operation"], "menu_card")
        with mock.patch.object(
            self.bridge,
            "send_card",
            return_value=(True, "oc_private", "om_task_card"),
        ) as send:
            self.assertTrue(
                self.bridge.retry_pending_replies(
                    now=float(pending[0]["next_attempt_at"]),
                )
            )

        send.assert_called_once_with("ou_admin", card, "select-task-event")
        state = self.bridge.load_state()
        self.assertEqual(state["pending_replies"], [])
        self.assertEqual(state["card_contexts"]["om_task_card"]["type"], "tasks")

    def test_due_queue_card_sets_progress_message_id(self):
        entry = {
            "queue_id": "queue-1",
            "source_message_id": "om_source",
            "task": {"id": "task-1", "project": "DeepOri", "title": "Ori Home"},
            "image_keys": [],
            "file_keys": [],
            "ready": True,
        }
        self.bridge.save_state({"pending_inputs": [entry]})
        self.bridge.queue_pending_queue_card(
            "queue-1",
            "om_source",
            "网络失败",
            now=100,
        )

        with mock.patch.object(
            self.bridge,
            "reply_card_message",
            return_value=(True, "om_progress"),
        ):
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        state = self.bridge.load_state()
        self.assertEqual(state["pending_replies"], [])
        self.assertEqual(
            state["pending_inputs"][0]["progress_message_id"],
            "om_progress",
        )

    def test_failed_background_queue_card_uses_backoff_without_text_reply(self):
        entry = {
            "queue_id": "queue-1",
            "source_message_id": "om_source",
            "task": {"id": "task-1", "project": "DeepOri", "title": "Ori Home"},
            "image_keys": [],
            "file_keys": [],
            "ready": True,
        }
        self.bridge.save_state({"pending_inputs": [entry]})
        self.bridge.queue_pending_queue_card(
            "queue-1",
            "om_source",
            "网络失败",
            now=100,
        )

        with mock.patch.object(
            self.bridge,
            "reply_card_message",
            return_value=(False, None),
        ), mock.patch.object(self.bridge, "reply") as text_reply:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        text_reply.assert_not_called()
        item = self.bridge.load_state()["pending_replies"][0]
        self.assertEqual(item["attempts"], 1)
        self.assertEqual(item["next_attempt_at"], 145)

    def test_queue_card_is_discarded_after_input_has_started(self):
        self.bridge.queue_pending_queue_card(
            "queue-1",
            "om_source",
            "网络失败",
            now=100,
        )

        with mock.patch.object(self.bridge, "reply_card_message") as reply_card:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        reply_card.assert_not_called()
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])

    def test_failed_local_image_is_copied_to_spool_and_retried(self):
        source = Path(self.temporary.name) / "result.png"
        source.write_bytes(b"\x89PNG\r\n\x1a\nresult")

        self.assertTrue(
            self.bridge.queue_pending_image(
                "om_source",
                str(source),
                1,
                "飞书 API 网络连接失败",
                now=100,
            )
        )
        pending = self.bridge.load_state()["pending_replies"]
        spooled = Path(pending[0]["image"])
        self.assertEqual(pending[0]["operation"], "image_reply")
        self.assertNotEqual(spooled, source)
        self.assertTrue(spooled.is_file())

        source.unlink()
        with mock.patch.object(self.bridge, "reply_image", return_value=True) as send:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        send.assert_called_once_with("om_source", str(spooled), 1)
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])
        self.assertFalse(spooled.exists())

    def test_failed_image_retry_keeps_spool_and_advances_backoff(self):
        source = Path(self.temporary.name) / "result.jpg"
        source.write_bytes(b"\xff\xd8\xffresult")
        self.bridge.queue_pending_image(
            "om_source",
            str(source),
            2,
            "飞书 API 调用失败",
            now=100,
        )
        spooled = Path(self.bridge.load_state()["pending_replies"][0]["image"])

        def fail(*_args):
            self.bridge._last_reply_failure_reason = "飞书 API 请求超时"
            return False

        with mock.patch.object(self.bridge, "reply_image", side_effect=fail):
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        item = self.bridge.load_state()["pending_replies"][0]
        self.assertEqual(item["attempts"], 1)
        self.assertEqual(item["reason"], "飞书 API 请求超时")
        self.assertEqual(item["next_attempt_at"], 145)
        self.assertTrue(spooled.is_file())

    def test_failed_local_file_is_copied_to_spool_and_retried(self):
        source = Path(self.temporary.name) / "result.pdf"
        source.write_bytes(b"pdf-result")

        self.assertTrue(
            self.bridge.queue_pending_file(
                "om_source",
                str(source),
                1,
                "飞书 API 网络连接失败",
                now=100,
            )
        )
        pending = self.bridge.load_state()["pending_replies"]
        spooled = Path(pending[0]["file"])
        self.assertEqual(pending[0]["operation"], "file_reply")
        self.assertEqual(spooled.name, "result.pdf")
        self.assertNotEqual(spooled, source)
        self.assertTrue(spooled.is_file())

        source.unlink()
        with mock.patch.object(self.bridge, "reply_file", return_value=True) as send:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        send.assert_called_once_with("om_source", str(spooled), 1)
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])
        self.assertFalse(spooled.exists())


if __name__ == "__main__":
    unittest.main()

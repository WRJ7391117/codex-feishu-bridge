import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
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

    def test_runtime_status_failure_is_not_persisted(self):
        with mock.patch.object(self.bridge, "reply", return_value=False):
            self.assertFalse(
                self.bridge.reply_or_queue("om_test", "正在运行", "running")
            )

        self.assertEqual(
            self.bridge.load_state().get("pending_replies", []),
            [],
        )

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


if __name__ == "__main__":
    unittest.main()

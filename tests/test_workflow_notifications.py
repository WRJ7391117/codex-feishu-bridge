import copy
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import queue
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
BRIDGE_PATH = ROOT / "Resources/bridge/feishu_codex_bridge.py"
WORKFLOW_PATH = ROOT / "Resources/bridge/workflow_notifications.py"
CLIENT_PATH = ROOT / "Resources/bridge/workflow_notify.py"
CONFIG_TOOL_PATH = ROOT / "Resources/bridge/workflow_config.py"
INSTALL_PATH = ROOT / "Resources/bridge/install.sh"


def load_workflow_module():
    spec = importlib.util.spec_from_file_location(
        "workflow_notifications_test_module",
        WORKFLOW_PATH,
    )
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def load_workflow_config_module():
    spec = importlib.util.spec_from_file_location(
        "workflow_config_test_module",
        CONFIG_TOOL_PATH,
    )
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def load_bridge(config_path: Path):
    previous = os.environ.get("CODEX_FEISHU_BRIDGE_CONFIG")
    os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = str(config_path)
    try:
        spec = importlib.util.spec_from_file_location(
            "bridge_workflow_test",
            BRIDGE_PATH,
        )
        module = importlib.util.module_from_spec(spec)
        assert spec.loader is not None
        spec.loader.exec_module(module)
        return module
    finally:
        if previous is None:
            os.environ.pop("CODEX_FEISHU_BRIDGE_CONFIG", None)
        else:
            os.environ["CODEX_FEISHU_BRIDGE_CONFIG"] = previous


def action_payload():
    return [
        {
            "id": "recommended",
            "label": "采用推荐方案",
            "description": "继续可逆验证。",
            "recommended": True,
            "resolution": "resume",
        },
        {
            "id": "pause",
            "label": "保持暂停",
            "description": "暂不继续。",
            "recommended": False,
            "resolution": "pause",
        },
    ]


def workflow_payload(status="user_action_required", event_id="evt-1"):
    return {
        "workflow_id": "ori-one-mind",
        "event_id": event_id,
        "task_id": "ONE-G1-102",
        "status": status,
        "summary": (
            "需要确认下一步。"
            if status == "user_action_required"
            else "任务已完成。"
        ),
        "workbench_url": "https://deepori.cn/ori-one/workbench/automation/",
        "actions": action_payload() if status == "user_action_required" else [],
    }


def workflow_card_envelope(event_id="card-event-1"):
    return {
        "schema": "2.0",
        "header": {
            "event_id": event_id,
            "event_type": "card.action.trigger",
            "create_time": "100",
        },
        "event": {
            "operator": {"open_id": "ou_admin"},
            "host": "im_message",
            "token": "callback-token",
            "context": {
                "open_message_id": "om_workflow",
                "open_chat_id": "oc_private",
            },
            "action": {
                "tag": "button",
                "value": {
                    "action": "workflow_decision",
                    "workflow_id": "ori-one-mind",
                    "event_id": "evt-1",
                    "decision_token": "decision-token",
                    "action_id": "recommended",
                },
            },
        },
    }


class WorkflowStoreTests(unittest.TestCase):
    def setUp(self):
        self.workflow = load_workflow_module()
        self.temporary = tempfile.TemporaryDirectory()
        self.store = self.workflow.WorkflowStore(
            Path(self.temporary.name) / "workflow-state.json"
        )

    def tearDown(self):
        self.temporary.cleanup()

    def test_payload_rejects_recipient_and_chat_fields(self):
        for forbidden in ("recipient_open_id", "recipient_chat_id", "chat_id"):
            payload = workflow_payload()
            payload[forbidden] = "must-not-be-accepted"
            with self.assertRaises(self.workflow.WorkflowNotificationError):
                self.workflow.validate_payload(payload)

    def test_only_two_notification_statuses_are_allowed(self):
        payload = workflow_payload()
        payload["status"] = "turn_completed"
        with self.assertRaises(self.workflow.WorkflowNotificationError):
            self.workflow.validate_payload(payload)

    def test_workflow_id_is_fixed(self):
        payload = workflow_payload()
        payload["workflow_id"] = "ori-one-mind-automation"
        with self.assertRaises(self.workflow.WorkflowNotificationError):
            self.workflow.validate_payload(payload)

    def test_workbench_url_allows_only_private_automation_page(self):
        payload = workflow_payload()
        payload["workbench_url"] = (
            "https://deepori.cn/ori-one/workbench/automation/ONE-G1-102"
        )
        self.assertEqual(
            self.workflow.validate_payload(payload)["workbench_url"],
            payload["workbench_url"],
        )
        invalid_urls = (
            "http://127.0.0.1:4173/ori-one/workbench/automation/",
            "https://deepori.com/ori-one/workbench/automation/",
            "https://deepori.cn/ori-one/workbench/",
            "https://deepori.cn/ori-one/workbench/automation/../admin",
            "https://deepori.cn/ori-one/workbench/automation/?event=1",
            "https://deepori.cn/ori-one/workbench/automation/#latest",
            "https://deepori.cn/ori-one/workbench/automation//admin",
            "https://user@deepori.cn/ori-one/workbench/automation/",
            "https://deepori.cn:443/ori-one/workbench/automation/",
        )
        for invalid_url in invalid_urls:
            payload["workbench_url"] = invalid_url
            with self.subTest(url=invalid_url):
                with self.assertRaises(self.workflow.WorkflowNotificationError):
                    self.workflow.validate_payload(payload)

    def test_action_contract_requires_resolution_and_one_recommendation(self):
        payload = workflow_payload()
        del payload["actions"][0]["resolution"]
        with self.assertRaises(self.workflow.WorkflowNotificationError):
            self.workflow.validate_payload(payload)

        payload = workflow_payload()
        payload["actions"][0]["resolution"] = "approve"
        with self.assertRaises(self.workflow.WorkflowNotificationError):
            self.workflow.validate_payload(payload)

        payload = workflow_payload()
        payload["actions"][1]["recommended"] = True
        with self.assertRaises(self.workflow.WorkflowNotificationError):
            self.workflow.validate_payload(payload)

    def test_visible_text_rejects_obvious_secrets_without_echoing_them(self):
        restricted_values = (
            "ghp_" + "1234567890abcdef",
            "github_pat_" + "1234567890abcdef",
            "Bearer " + "abcdefghijklmnop",
            "sk-proj-" + "1234567890abcdef",
            "AKSRV_" + "1234567890ABCDEF",
            "ou_" + "1234567890abcdef",
            "oc_" + "1234567890abcdef",
            "postgresql" + "://user:password@db.example/test",
            "DATABASE_URL=" + "postgresql" + "://user:password@db.example/test",
            "-----BEGIN " + "PRIVATE KEY-----",
        )
        for field in ("summary", "label", "description"):
            for restricted in restricted_values:
                payload = copy.deepcopy(workflow_payload())
                if field == "summary":
                    payload[field] = restricted
                else:
                    payload["actions"][0][field] = restricted
                with self.subTest(field=field, prefix=restricted[:8]):
                    with self.assertRaises(
                        self.workflow.WorkflowNotificationError
                    ) as raised:
                        self.workflow.validate_payload(payload)
                    self.assertNotIn(restricted, str(raised.exception))

    def test_corrupt_state_never_becomes_an_empty_duplicate_outbox(self):
        payload = workflow_payload()
        self.store.enqueue(payload, "ori-one-mind", now=100)
        corrupt = b'{"version":1,"notifications":'
        self.store.path.write_bytes(corrupt)
        self.store.path.chmod(0o600)

        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.load()
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.enqueue(payload, "ori-one-mind", now=101)
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.save(
                {"version": 1, "notifications": {}, "recoveries": {}}
            )
        self.assertEqual(self.store.path.read_bytes(), corrupt)

    def test_invalid_state_schema_and_permissions_fail_closed(self):
        self.store.path.write_text(
            json.dumps(
                {
                    "version": 1,
                    "notifications": [],
                    "recoveries": {},
                }
            ),
            encoding="utf-8",
        )
        self.store.path.chmod(0o600)
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.load()

        self.store.path.unlink()
        self.store.enqueue(workflow_payload(), "ori-one-mind", now=100)
        original = self.store.path.read_bytes()
        self.store.path.chmod(0o644)
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.load()
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.enqueue(workflow_payload(), "ori-one-mind", now=101)
        self.assertEqual(self.store.path.read_bytes(), original)
        self.assertEqual(self.store.path.stat().st_mode & 0o777, 0o644)

    def test_symlink_and_wrong_owner_state_fail_closed(self):
        external = Path(self.temporary.name) / "external.json"
        external.write_text("{}", encoding="utf-8")
        external.chmod(0o600)
        self.store.path.symlink_to(external)
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.store.load()

        self.store.path.unlink()
        self.store.enqueue(workflow_payload(), "ori-one-mind", now=100)
        with mock.patch.object(
            self.workflow.os,
            "getuid",
            return_value=os.getuid() + 1,
        ):
            with self.assertRaises(self.workflow.WorkflowStateError):
                self.store.load()

    def test_atomic_save_fsyncs_file_and_directory(self):
        real_fsync = os.fsync
        with mock.patch.object(
            self.workflow.os,
            "fsync",
            side_effect=real_fsync,
        ) as fsync:
            self.store.enqueue(workflow_payload(), "ori-one-mind", now=100)
        self.assertGreaterEqual(fsync.call_count, 2)

    def test_first_write_creates_private_parent_and_state(self):
        nested_store = self.workflow.WorkflowStore(
            Path(self.temporary.name) / "private" / "workflow-state.json"
        )
        nested_store.enqueue(workflow_payload(), "ori-one-mind", now=100)
        self.assertEqual(nested_store.path.parent.stat().st_mode & 0o777, 0o700)
        self.assertEqual(nested_store.path.stat().st_mode & 0o777, 0o600)

    def test_enqueue_is_durable_idempotent_and_restricts_permissions(self):
        payload = workflow_payload()
        self.assertEqual(
            self.store.enqueue(payload, "ori-one-mind", now=100),
            "queued",
        )
        self.assertEqual(
            self.store.enqueue(payload, "ori-one-mind", now=101),
            "duplicate",
        )
        self.assertEqual(self.store.path.stat().st_mode & 0o777, 0o600)
        self.assertEqual(len(self.store.load()["notifications"]), 1)

        changed = workflow_payload()
        changed["summary"] = "不同内容"
        with self.assertRaises(self.workflow.WorkflowNotificationError):
            self.store.enqueue(changed, "ori-one-mind", now=102)

    def test_decision_token_can_only_create_one_recovery(self):
        payload = workflow_payload()
        self.store.enqueue(payload, "ori-one-mind", now=100)
        record = self.store.record_for_event(payload["workflow_id"], payload["event_id"])
        token = record["decision_token"]

        first, recovery = self.store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            token,
            "recommended",
            now=110,
        )
        second, repeated = self.store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            token,
            "recommended",
            now=111,
        )

        self.assertEqual(first, "consumed")
        self.assertIsNotNone(recovery)
        self.assertEqual(second, "already_consumed")
        self.assertIsNone(repeated)
        self.assertEqual(len(self.store.load()["recoveries"]), 1)
        self.assertEqual(recovery["attention_request_id"], payload["event_id"])
        self.assertEqual(recovery["selected_action_id"], "recommended")
        self.assertEqual(recovery["resolution"], "resume")

    def test_same_card_source_can_retry_side_effects_without_second_recovery(self):
        payload = workflow_payload()
        self.store.enqueue(payload, "ori-one-mind", now=100)
        record = self.store.record_for_event(payload["workflow_id"], payload["event_id"])

        first, _recovery = self.store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
            source_id="card-event-1",
        )
        retry, _repeated = self.store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=111,
            source_id="card-event-1",
        )
        different, _ignored = self.store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=112,
            source_id="card-event-2",
        )

        self.assertEqual(
            (first, retry, different),
            ("consumed", "consumed_retry", "already_consumed"),
        )
        self.assertEqual(len(self.store.load()["recoveries"]), 1)

    def test_reply_relation_can_only_create_one_recovery(self):
        payload = workflow_payload()
        self.store.enqueue(payload, "ori-one-mind", now=100)
        key = self.workflow.event_key(payload["workflow_id"], payload["event_id"])
        self.store.delivery_succeeded(
            key,
            "initial",
            "om_workflow",
            "oc_private",
            86400,
            now=101,
        )

        result, recovery, _record = self.store.consume_reply_decision(
            "om_workflow", "1", now=102
        )
        repeated, _recovery, _record = self.store.consume_reply_decision(
            "om_workflow", "1", now=103
        )

        self.assertEqual(result, "consumed")
        self.assertIsNotNone(recovery)
        self.assertEqual(repeated, "already_consumed")
        self.assertEqual(len(self.store.load()["recoveries"]), 1)

    def test_reminder_is_scheduled_once_after_24_hours(self):
        payload = workflow_payload()
        self.store.enqueue(payload, "ori-one-mind", now=100)
        key = self.workflow.event_key(payload["workflow_id"], payload["event_id"])
        self.store.delivery_succeeded(
            key,
            "initial",
            "om_workflow",
            "oc_private",
            86400,
            now=100,
        )

        self.assertIsNone(self.store.due_delivery(now=86499))
        due = self.store.due_delivery(now=86500)
        self.assertEqual(due[:2], ("reminder", key))
        self.store.delivery_succeeded(
            key,
            "reminder",
            "om_reminder",
            "oc_private",
            86400,
            now=86500,
        )
        self.assertIsNone(self.store.due_delivery(now=200000))
        self.assertEqual(
            self.store.enqueue(payload, "ori-one-mind", now=200001),
            "duplicate",
        )
        self.assertIsNone(self.store.due_delivery(now=300000))

    def test_recovery_fifo_and_unknown_state_survive_restart(self):
        for index, created_at in ((1, 110), (2, 120)):
            payload = workflow_payload(event_id=f"evt-{index}")
            self.store.enqueue(payload, "ori-one-mind", now=created_at - 10)
            record = self.store.record_for_event(
                payload["workflow_id"], payload["event_id"]
            )
            self.store.consume_token_decision(
                payload["workflow_id"],
                payload["event_id"],
                record["decision_token"],
                "recommended",
                now=created_at,
            )

        restarted = self.workflow.WorkflowStore(self.store.path)
        first_key, first = restarted.due_recovery(now=500)
        self.assertEqual(first["attention_request_id"], "evt-1")
        restarted.recovery_failed(
            first_key,
            "confirmation interrupted",
            retryable=False,
            now=501,
        )
        second_key, second = restarted.due_recovery(now=502)
        self.assertNotEqual(second_key, first_key)
        self.assertEqual(second["attention_request_id"], "evt-2")

        restarted_again = self.workflow.WorkflowStore(self.store.path)
        unknown = restarted_again.unknown_recoveries()
        self.assertEqual([item[0] for item in unknown], [first_key])
        self.assertEqual(
            restarted_again.safe_status()["delivery_unknown"],
            1,
        )

    def test_recovery_backoff_does_not_allow_later_item_to_jump_fifo(self):
        keys = []
        for index, created_at in ((1, 110), (2, 120)):
            payload = workflow_payload(event_id=f"fifo-{index}")
            self.store.enqueue(payload, "ori-one-mind", now=created_at - 10)
            record = self.store.record_for_event(
                payload["workflow_id"], payload["event_id"]
            )
            self.store.consume_token_decision(
                payload["workflow_id"],
                payload["event_id"],
                record["decision_token"],
                "recommended",
                now=created_at,
            )
            keys.append(self.workflow.event_key("ori-one-mind", payload["event_id"]))

        self.store.recovery_failed(
            keys[0],
            "dedicated task busy",
            retryable=True,
            now=130,
        )
        first = self.store.load()["recoveries"][keys[0]]
        self.assertIsNone(
            self.store.due_recovery(now=float(first["next_attempt_at"]) - 1)
        )
        due_key, _recovery = self.store.due_recovery(
            now=float(first["next_attempt_at"])
        )
        self.assertEqual(due_key, keys[0])

    def test_status_contains_counts_but_no_identifiers(self):
        self.store.enqueue(workflow_payload(), "ori-one-mind", now=100)
        status = self.store.safe_status()
        encoded = json.dumps(status)
        self.assertEqual(status["pending_notifications"], 1)
        self.assertNotIn("ONE-G1", encoded)
        self.assertNotIn("ori-one-mind", encoded)


class WorkflowDecisionInboxTests(unittest.TestCase):
    def setUp(self):
        self.workflow = load_workflow_module()
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name) / "workflow-decision-inbox"
        self.directory.mkdir(mode=0o700)
        self.inbox = self.workflow.WorkflowDecisionInbox(self.directory)

    def tearDown(self):
        self.temporary.cleanup()

    def _write_event(self, event_id="card-event-1"):
        envelope = workflow_card_envelope(event_id)
        path = self.inbox.path_for_event(event_id)
        path.write_text(json.dumps(envelope), encoding="utf-8")
        path.chmod(0o600)
        return path

    def test_pending_callback_survives_restart_and_ack_is_durable(self):
        path = self._write_event()

        restarted = self.workflow.WorkflowDecisionInbox(self.directory)
        pending = restarted.pending()

        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["event_id"], "card-event-1")
        self.assertEqual(pending[0]["type"], "card.action.trigger")
        self.assertEqual(
            json.loads(pending[0]["action_value"])["action"],
            "workflow_decision",
        )
        restarted.acknowledge("card-event-1")
        self.assertFalse(path.exists())
        self.assertEqual(restarted.pending(), [])

    def test_invalid_entry_or_directory_permissions_fail_closed(self):
        path = self._write_event()
        path.chmod(0o644)
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.inbox.pending()

        path.chmod(0o600)
        self.directory.chmod(0o755)
        with self.assertRaises(self.workflow.WorkflowStateError):
            self.inbox.pending()


class WorkflowConfigToolTests(unittest.TestCase):
    def setUp(self):
        self.tool = load_workflow_config_module()
        self.temporary = tempfile.TemporaryDirectory()
        self.config_path = Path(self.temporary.name) / "config.json"
        self.config = {
            "allowed_sender_id": "ou_legacy",
            "allowed_users": [
                {
                    "open_id": "ou_legacy",
                    "name": "Legacy",
                    "allowed_projects": ["DeepOri"],
                },
                {
                    "open_id": "ou_other",
                    "name": "Other",
                    "allowed_projects": ["Other"],
                },
            ],
            "allowed_chat_ids": ["oc_unrelated"],
            "workflow_notifications": {
                "enabled": False,
                "allowed_workflow_id": "ori-one-mind",
                "recipient_open_id": "",
                "recipient_chat_id": "",
                "codex_task_id": "",
            },
        }
        self._write(self.config)

    def tearDown(self):
        self.temporary.cleanup()

    def _write(self, config):
        self.config_path.write_text(
            json.dumps(config, ensure_ascii=False),
            encoding="utf-8",
        )
        self.config_path.chmod(0o600)

    def test_enable_reads_task_id_separately_and_selects_legacy_allowlisted_user(self):
        task_id = "11111111-1111-1111-1111-111111111111"
        real_fsync = os.fsync
        with mock.patch.object(
            self.tool.os,
            "fsync",
            side_effect=real_fsync,
        ) as fsync:
            self.tool.enable(self.config_path, task_id + "\n")

        updated = json.loads(self.config_path.read_text(encoding="utf-8"))
        workflow = updated["workflow_notifications"]
        self.assertTrue(workflow["enabled"])
        self.assertEqual(workflow["recipient_open_id"], "ou_legacy")
        self.assertEqual(workflow["recipient_chat_id"], "")
        self.assertEqual(workflow["codex_task_id"], task_id)
        self.assertEqual(self.tool.status(self.config_path), "configured")
        self.assertEqual(self.config_path.stat().st_mode & 0o777, 0o600)
        self.assertGreaterEqual(fsync.call_count, 2)

    def test_disable_preserves_local_binding_and_only_reports_safe_status(self):
        workflow = self.config["workflow_notifications"]
        workflow.update(
            {
                "enabled": True,
                "recipient_open_id": "ou_legacy",
                "recipient_chat_id": "oc_bound",
                "codex_task_id": "11111111-1111-1111-1111-111111111111",
            }
        )
        self._write(self.config)

        self.tool.disable(self.config_path)

        updated = json.loads(self.config_path.read_text(encoding="utf-8"))
        self.assertFalse(updated["workflow_notifications"]["enabled"])
        self.assertEqual(updated["workflow_notifications"]["recipient_chat_id"], "oc_bound")
        self.assertEqual(self.tool.status(self.config_path), "disabled")
        self.assertNotIn("ou_", self.tool.status(self.config_path))
        self.assertNotIn("oc_", self.tool.status(self.config_path))
        self.assertNotIn("11111111", self.tool.status(self.config_path))

    def test_enable_fails_closed_when_legacy_sender_is_not_allowlisted(self):
        self.config["allowed_sender_id"] = "ou_missing"
        self._write(self.config)
        original = self.config_path.read_bytes()

        with self.assertRaises(self.tool.WorkflowConfigError):
            self.tool.enable(
                self.config_path,
                "11111111-1111-1111-1111-111111111111",
            )

        self.assertEqual(self.config_path.read_bytes(), original)
        self.assertEqual(self.tool.status(self.config_path), "disabled")

    def test_unsafe_config_permissions_and_symlink_are_rejected(self):
        self.config_path.chmod(0o644)
        self.assertEqual(self.tool.status(self.config_path), "invalid")

        external = Path(self.temporary.name) / "external.json"
        self.config_path.replace(external)
        self.config_path.symlink_to(external)
        self.assertEqual(self.tool.status(self.config_path), "invalid")

    def test_cli_reads_task_from_stdin_and_never_outputs_identifiers(self):
        home = Path(self.temporary.name) / "home"
        support = home / "Library/Application Support/Codex Feishu Bridge"
        support.mkdir(parents=True)
        support.chmod(0o700)
        config_path = support / "config.json"
        config_path.write_text(
            json.dumps(self.config, ensure_ascii=False),
            encoding="utf-8",
        )
        config_path.chmod(0o600)
        task_id = "11111111-1111-1111-1111-111111111111"
        environment = os.environ.copy()
        environment["HOME"] = str(home)

        enabled = subprocess.run(
            [sys.executable, str(CONFIG_TOOL_PATH), "--enable"],
            input=task_id + "\n",
            text=True,
            capture_output=True,
            env=environment,
        )
        current = subprocess.run(
            [sys.executable, str(CONFIG_TOOL_PATH), "--status"],
            text=True,
            capture_output=True,
            env=environment,
        )

        self.assertEqual(enabled.returncode, 0)
        self.assertEqual(enabled.stdout.strip(), "configured")
        self.assertEqual(current.returncode, 0)
        self.assertEqual(current.stdout.strip(), "configured")
        output = enabled.stdout + enabled.stderr + current.stdout + current.stderr
        self.assertNotIn("ou_legacy", output)
        self.assertNotIn("oc_unrelated", output)
        self.assertNotIn(task_id, output)


class InstallerSafetyTests(unittest.TestCase):
    @staticmethod
    def _installer_fixture(root: Path):
        package = root / "package"
        package.mkdir()
        for name in (
            "feishu_codex_bridge.py",
            "workflow_notifications.py",
            "workflow_notify.py",
            "workflow_config.py",
            "control.sh",
            "diagnose.sh",
            "config.example.json",
            "install.sh",
        ):
            shutil.copy2(ROOT / "Resources/bridge" / name, package / name)
        installer = package / "install.sh"
        isolated_label = (
            f"com.deepori.codex-feishu-bridge.tests.{os.getpid()}.{id(root)}"
        )
        installer.write_text(
            installer.read_text(encoding="utf-8")
            .replace("com.deepori.codex-feishu-bridge", isolated_label)
            .replace(
                "com.openai.codex.feishu-bridge",
                isolated_label + ".legacy",
            ),
            encoding="utf-8",
        )
        lark_cli = package / "lark-cli"
        lark_cli.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
        lark_cli.chmod(0o755)

        home = root / "home"
        support = home / "Library/Application Support/Codex Feishu Bridge"
        state_directory = home / ".codex/feishu-bridge"
        support.mkdir(parents=True, mode=0o700)
        state_directory.mkdir(parents=True, mode=0o700)
        support.chmod(0o700)
        state_directory.chmod(0o700)
        config = {
            "allowed_users": [],
            "allowed_chat_ids": [],
            "workflow_notifications": {
                "enabled": False,
                "allowed_workflow_id": "ori-one-mind",
                "recipient_open_id": "",
                "recipient_chat_id": "",
                "codex_task_id": "",
            },
        }
        config_path = support / "config.json"
        config_path.write_text(json.dumps(config), encoding="utf-8")
        config_path.chmod(0o600)
        workflow_path = state_directory / "workflow-state.json"
        workflow_path.write_text(
            json.dumps({"version": 1, "notifications": {}, "recoveries": {}}),
            encoding="utf-8",
        )
        workflow_path.chmod(0o600)
        runtime = support / "bridge.py"
        runtime.write_text("existing runtime\n", encoding="utf-8")
        runtime.chmod(0o644)
        return package, home, config_path, workflow_path, runtime

    @staticmethod
    def _run_installer(package: Path, home: Path):
        environment = os.environ.copy()
        environment["HOME"] = str(home)
        return subprocess.run(
            ["/bin/zsh", str(package / "install.sh")],
            text=True,
            capture_output=True,
            env=environment,
        )

    def test_preflight_covers_all_sources_and_private_state_before_runtime_copy(self):
        source = INSTALL_PATH.read_text(encoding="utf-8")
        preflight_start = source.index("# Preflight every source")
        runtime_start = source.index("# Stage every runtime file")
        preflight = source[preflight_start:runtime_start]
        for required in (
            "feishu_codex_bridge.py",
            "workflow_notifications.py",
            "workflow_notify.py",
            "workflow_config.py",
            "control.sh",
            "diagnose.sh",
            "config.example.json",
            "lark-cli",
            "config.json",
            "state.json",
            "runtime-status.json",
            "workflow-state.json",
            "workflow-decision-inbox",
            "feishu-bridge.log",
            "feishu-bridge-launchd.log",
        ):
            self.assertIn(required, preflight)
        self.assertLess(preflight_start, source.index("path.mkdir"))
        self.assertLess(preflight_start, runtime_start)
        self.assertGreater(source.index("/bin/launchctl print"), preflight_start)
        self.assertGreater(source.index("/bin/launchctl print"), source.index("._validate_state"))
        self.assertIn("backups", source[runtime_start:])
        self.assertIn("os.replace(backup, destination)", source[runtime_start:])

    def test_installer_refuses_to_interrupt_pending_feishu_work(self):
        with tempfile.TemporaryDirectory() as directory:
            package, home, _config_path, _workflow_path, runtime = (
                self._installer_fixture(Path(directory))
            )
            state_path = home / ".codex/feishu-bridge/state.json"
            state_path.write_text(
                json.dumps({"pending_inputs": [{"ready": True}]}),
                encoding="utf-8",
            )
            state_path.chmod(0o600)
            original_runtime = runtime.read_bytes()

            result = self._run_installer(package, home)

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("pending Feishu work", result.stderr)
            self.assertEqual(runtime.read_bytes(), original_runtime)

    def test_installer_adds_missing_menu_keys_without_overwriting_config(self):
        with tempfile.TemporaryDirectory() as directory:
            package, home, config_path, _workflow_path, _runtime = (
                self._installer_fixture(Path(directory))
            )
            original = json.loads(config_path.read_text(encoding="utf-8"))
            original["task_menu_event_key"] = "custom_select_task"
            original["preserved_setting"] = {"enabled": True}
            config_path.write_text(json.dumps(original), encoding="utf-8")
            config_path.chmod(0o600)

            result = self._run_installer(package, home)

            self.assertEqual(result.returncode, 0, result.stderr)
            migrated = json.loads(config_path.read_text(encoding="utf-8"))
            self.assertEqual(
                {
                    key: migrated.get(key)
                    for key in (
                        "current_task_menu_event_key",
                        "task_menu_event_key",
                        "new_task_menu_event_key",
                        "archive_task_menu_event_key",
                        "usage_menu_event_key",
                        "desktop_sync_menu_event_key",
                    )
                },
                {
                    "current_task_menu_event_key": "current_task",
                    "task_menu_event_key": "custom_select_task",
                    "new_task_menu_event_key": "new_task",
                    "archive_task_menu_event_key": "archive_task",
                    "usage_menu_event_key": "codex_usage",
                    "desktop_sync_menu_event_key": "sync_desktop",
                },
            )
            self.assertEqual(migrated["allowed_users"], original["allowed_users"])
            self.assertEqual(
                migrated["workflow_notifications"],
                original["workflow_notifications"],
            )
            self.assertEqual(migrated["preserved_setting"], {"enabled": True})
            self.assertEqual(config_path.stat().st_mode & 0o777, 0o600)

    def test_invalid_existing_config_fails_before_runtime_or_service_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            package, home, config_path, _workflow_path, runtime = (
                self._installer_fixture(Path(directory))
            )
            config_path.write_text('{"allowed_users":', encoding="utf-8")
            config_path.chmod(0o600)
            original_runtime = runtime.read_bytes()
            original_mode = runtime.stat().st_mode & 0o777

            result = self._run_installer(package, home)

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(runtime.read_bytes(), original_runtime)
            self.assertEqual(runtime.stat().st_mode & 0o777, original_mode)
            self.assertFalse((home / "Library/LaunchAgents").exists())

    def test_invalid_existing_config_schema_fails_before_runtime_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            package, home, config_path, _workflow_path, runtime = (
                self._installer_fixture(Path(directory))
            )
            config_path.write_text(
                json.dumps({"allowed_users": "not-a-list"}),
                encoding="utf-8",
            )
            config_path.chmod(0o600)
            original_runtime = runtime.read_bytes()

            result = self._run_installer(package, home)

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(runtime.read_bytes(), original_runtime)
            self.assertFalse((package / "__pycache__").exists())
            self.assertFalse((home / "Library/LaunchAgents").exists())

    def test_invalid_existing_workflow_schema_fails_before_runtime_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            package, home, _config_path, workflow_path, runtime = (
                self._installer_fixture(Path(directory))
            )
            workflow_path.write_text(
                json.dumps({"version": 1, "notifications": [], "recoveries": {}}),
                encoding="utf-8",
            )
            workflow_path.chmod(0o600)
            original_runtime = runtime.read_bytes()

            result = self._run_installer(package, home)

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(runtime.read_bytes(), original_runtime)
            self.assertFalse((home / "Library/LaunchAgents").exists())

    def test_invalid_existing_workflow_json_fails_before_runtime_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            package, home, _config_path, workflow_path, runtime = (
                self._installer_fixture(Path(directory))
            )
            workflow_path.write_text('{"version":', encoding="utf-8")
            workflow_path.chmod(0o600)
            original_runtime = runtime.read_bytes()

            result = self._run_installer(package, home)

            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(runtime.read_bytes(), original_runtime)
            self.assertFalse((home / "Library/LaunchAgents").exists())


class WorkflowBridgeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        directory = Path(self.temporary.name)
        self.config_path = directory / "config.json"
        self.config_path.write_text(
            json.dumps(
                {
                    "allowed_users": [
                        {
                            "open_id": "ou_admin",
                            "name": "Admin",
                            "allowed_projects": ["*"],
                        }
                    ],
                    "workflow_notifications": {
                        "enabled": True,
                        "allowed_workflow_id": "ori-one-mind",
                        "recipient_open_id": "ou_admin",
                        "recipient_chat_id": "oc_private",
                        "codex_task_id": "11111111-1111-1111-1111-111111111111",
                    },
                }
            ),
            encoding="utf-8",
        )
        self.config_path.chmod(0o600)
        self.bridge = load_bridge(self.config_path)
        self.bridge.STATE_PATH = directory / "bridge-state.json"
        self.bridge.LOG_PATH = directory / "bridge.log"
        self.bridge.WORKFLOW_STATE_PATH = directory / "workflow-state.json"
        self.bridge._workflow_store = self.bridge.WorkflowStore(
            self.bridge.WORKFLOW_STATE_PATH
        )
        self.bridge.WORKFLOW_DECISION_INBOX_PATH = directory / "workflow-decision-inbox"
        self.bridge.WORKFLOW_DECISION_INBOX_PATH.mkdir(mode=0o700)
        self.bridge._workflow_decision_inbox = self.bridge.WorkflowDecisionInbox(
            self.bridge.WORKFLOW_DECISION_INBOX_PATH
        )
        self.bridge.WORKFLOW_SOCKET_PATH = directory / "workflow-notifications.sock"
        self.bridge.WORKFLOW_CONTROL_SOCKET_PATH = directory / "workflow-control.sock"

    def tearDown(self):
        self.bridge.stop_workflow_socket_server()
        self.temporary.cleanup()

    def test_expired_cli_fallback_is_removed_when_card_is_opened(self):
        state = self.bridge.load_state()
        state["pending_cli_fallbacks"] = {
            "expired": {
                "fallback_id": "expired",
                "user_id": "ou_admin",
                "chat_id": "oc_private",
                "created_at": 100,
            }
        }
        self.bridge.save_state(state)

        with mock.patch.object(self.bridge.time, "time", return_value=100 + self.bridge.CLI_FALLBACK_TTL_SECONDS + 1):
            entry = self.bridge.cli_fallback_entry(
                "expired",
                "ou_admin",
                "oc_private",
            )

        self.assertIsNone(entry)
        self.assertEqual(
            self.bridge.load_state().get("pending_cli_fallbacks"),
            {},
        )

    def test_started_task_invalidates_only_matching_cli_fallbacks(self):
        state = self.bridge.load_state()
        state["pending_cli_fallbacks"] = {
            "matching": {
                "user_id": "ou_admin",
                "task": {"id": "task-a"},
                "created_at": 100,
            },
            "other-task": {
                "user_id": "ou_admin",
                "task": {"id": "task-b"},
                "created_at": 100,
            },
            "other-user": {
                "user_id": "ou_other",
                "task": {"id": "task-a"},
                "created_at": 100,
            },
        }
        self.bridge.save_state(state)

        removed = self.bridge.invalidate_cli_fallbacks("ou_admin", "task-a")

        self.assertEqual(removed, 1)
        self.assertEqual(
            set(self.bridge.load_state()["pending_cli_fallbacks"]),
            {"other-task", "other-user"},
        )

    def test_task_list_retries_transient_sqlite_operational_error(self):
        with mock.patch.object(
            self.bridge,
            "_read_tasks_by_archive_state",
            side_effect=[self.bridge.sqlite3.OperationalError("busy"), []],
        ) as read, mock.patch.object(self.bridge.time, "sleep") as sleep:
            tasks = self.bridge.tasks_by_archive_state("ou_admin", False)

        self.assertEqual(tasks, [])
        self.assertEqual(read.call_count, 2)
        sleep.assert_called_once_with(0.2)

    def test_task_lookup_stops_after_bounded_sqlite_retries(self):
        with mock.patch.object(
            self.bridge,
            "_read_task_by_id",
            side_effect=self.bridge.sqlite3.OperationalError("unavailable"),
        ) as read, mock.patch.object(self.bridge.time, "sleep") as sleep:
            with self.assertRaises(self.bridge.sqlite3.OperationalError):
                self.bridge.task_by_id("task-a", "ou_admin")

        self.assertEqual(read.call_count, 3)
        self.assertEqual(
            [call.args[0] for call in sleep.call_args_list],
            [0.2, 0.5],
        )

    def test_desktop_no_client_found_is_retried_before_cli_fallback(self):
        statuses = iter(
            [
                ("unavailable", "no-client-found", []),
                ("completed", "done", []),
            ]
        )

        def desktop_call(*_args, **kwargs):
            status = next(statuses)
            if status[0] == "completed":
                kwargs["on_started"]()
            return status

        started = []
        with mock.patch.object(
            self.bridge,
            "run_codex_via_desktop",
            side_effect=desktop_call,
        ) as desktop, mock.patch.object(
            self.bridge,
            "activate_desktop_task",
            return_value=True,
        ) as activate, mock.patch.object(self.bridge.time, "sleep") as sleep:
            result = self.bridge.run_codex(
                "task-a",
                "hello",
                on_started=started.append,
            )

        self.assertEqual(result, (True, "done", []))
        self.assertEqual(desktop.call_count, 2)
        activate.assert_called_once_with("task-a")
        sleep.assert_called_once_with(0.3)
        self.assertEqual(started, ["Codex Desktop 已接收，正在运行"])

    def test_desktop_uncertain_submission_is_never_retried(self):
        with mock.patch.object(
            self.bridge,
            "run_codex_via_desktop",
            return_value=("failed", "提交状态不确定", []),
        ) as desktop, mock.patch.object(self.bridge.time, "sleep") as sleep:
            result = self.bridge.run_codex("task-a", "hello")

        self.assertEqual(result, (False, "提交状态不确定", []))
        desktop.assert_called_once()
        sleep.assert_not_called()

    def test_menu_audit_log_records_only_leaf_key_and_result(self):
        event = {
            "operator_id": "ou_admin",
            "event_key": self.bridge.TASK_MENU_EVENT_KEY,
            "event_id": "menu-event",
        }
        with mock.patch.object(self.bridge, "load_state", return_value={}), mock.patch.object(
            self.bridge,
            "mark_processed",
            return_value=True,
        ), mock.patch.object(self.bridge, "send_task_card") as send, mock.patch.object(
            self.bridge,
            "log",
        ) as log:
            self.bridge.handle_menu_event(event)

        send.assert_called_once()
        log.assert_called_once_with(
            f"menu handled key={self.bridge.TASK_MENU_EVENT_KEY} result=task-selector"
        )
        self.assertNotIn("ou_admin", log.call_args.args[0])
        self.assertNotIn("menu-event", log.call_args.args[0])

    def enqueue_delivered_request(self):
        payload = workflow_payload()
        self.bridge._workflow_store.enqueue(
            payload,
            "ori-one-mind",
            now=100,
        )
        key = self.bridge.workflow_event_key(payload["workflow_id"], payload["event_id"])
        self.bridge._workflow_store.delivery_succeeded(
            key,
            "initial",
            "om_workflow",
            "oc_private",
            86400,
            now=101,
        )
        return payload

    def test_workflow_config_requires_0600_and_local_recipient(self):
        self.assertTrue(self.bridge.workflow_configuration_valid())
        self.config_path.chmod(0o644)
        self.assertFalse(self.bridge.workflow_configuration_valid())

    def test_workflow_config_requires_the_fixed_workflow_id(self):
        self.bridge.WORKFLOW_CONFIG["allowed_workflow_id"] = (
            "ori-one-mind-automation"
        )
        self.assertFalse(self.bridge.workflow_configuration_valid())

    def test_send_target_comes_only_from_local_config(self):
        record = workflow_payload(status="milestone_completed")
        result = subprocess.CompletedProcess(
            [],
            0,
            '{"data":{"message_id":"om_sent","chat_id":"oc_private"}}',
            "",
        )
        with mock.patch.object(self.bridge.subprocess, "run", return_value=result) as run:
            delivered = self.bridge.send_workflow_card("event-key", record)

        self.assertTrue(delivered[0])
        command = run.call_args.args[0]
        self.assertEqual(command[command.index("--user-id") + 1], "ou_admin")
        card = json.loads(command[command.index("--content") + 1])
        encoded_card = json.dumps(card, ensure_ascii=False)
        self.assertNotIn("ou_admin", encoded_card)
        self.assertNotIn("oc_private", encoded_card)

    def test_workflow_card_explains_target_and_current_task_without_switching(self):
        target = {
            "id": "11111111-1111-1111-1111-111111111111",
            "project": "Ori One Mind",
            "title": "自动研发",
        }
        current = {
            "id": "22222222-2222-2222-2222-222222222222",
            "project": "Evolution",
            "title": "实现飞书Bot双向通信",
        }
        with mock.patch.object(
            self.bridge,
            "workflow_task_route",
            return_value=(target, current),
        ):
            card = self.bridge.build_workflow_card(
                workflow_payload(),
                completed=True,
                user_id="ou_admin",
            )

        encoded = json.dumps(card, ensure_ascii=False)
        self.assertIn("Ori One Mind → 自动研发", encoded)
        self.assertIn("Evolution → 实现飞书Bot双向通信", encoded)
        self.assertIn("不会自动切换当前 Task", encoded)
        self.assertIn("无需再发送“已点击”等确认文字", encoded)
        self.assertIn("切换到目标 Task", encoded)
        self.assertIn("保持当前 Task", encoded)

    def test_workflow_route_switch_requires_explicit_button(self):
        payload = self.enqueue_delivered_request()
        target = {
            "id": "11111111-1111-1111-1111-111111111111",
            "project": "Ori One Mind",
            "title": "自动研发",
        }
        state = self.bridge.load_state()
        state.setdefault("selected", {})["ou_admin"] = (
            "22222222-2222-2222-2222-222222222222"
        )
        self.bridge.save_state(state)
        action = {
            "action": "workflow_switch_task",
            "workflow_id": payload["workflow_id"],
            "event_id": payload["event_id"],
            "task_id": target["id"],
        }
        event = {
            "event_id": "workflow-route-switch",
            "operator_id": "ou_admin",
            "chat_id": "oc_private",
            "message_id": "om_workflow",
        }
        with mock.patch.object(
            self.bridge,
            "task_by_id",
            return_value=target,
        ), mock.patch.object(
            self.bridge,
            "patch_workflow_completed_cards",
        ) as patch, mock.patch.object(
            self.bridge,
            "refresh_user_task_identity_cards",
        ) as refresh:
            self.assertTrue(self.bridge.handle_workflow_card_action(event, action))

        saved = self.bridge.load_state()
        self.assertEqual(saved["selected"]["ou_admin"], target["id"])
        self.assertEqual(saved["last_projects"]["ou_admin"], "Ori One Mind")
        patch.assert_called_once_with(mock.ANY, "已切换到目标 Task")
        refresh.assert_called_once_with("ou_admin", "当前 Task 已切换", target)

    def test_workflow_route_keep_does_not_change_current_task(self):
        payload = self.enqueue_delivered_request()
        current_id = "22222222-2222-2222-2222-222222222222"
        state = self.bridge.load_state()
        state.setdefault("selected", {})["ou_admin"] = current_id
        self.bridge.save_state(state)
        action = {
            "action": "workflow_keep_current_task",
            "workflow_id": payload["workflow_id"],
            "event_id": payload["event_id"],
            "task_id": "11111111-1111-1111-1111-111111111111",
        }
        event = {
            "event_id": "workflow-route-keep",
            "operator_id": "ou_admin",
            "chat_id": "oc_private",
            "message_id": "om_workflow",
        }
        with mock.patch.object(
            self.bridge,
            "patch_workflow_completed_cards",
        ) as patch:
            self.assertTrue(self.bridge.handle_workflow_card_action(event, action))

        self.assertEqual(
            self.bridge.load_state()["selected"]["ou_admin"],
            current_id,
        )
        patch.assert_called_once_with(mock.ANY, "已保持当前 Task")

    def test_card_callback_persists_once_and_patches_card_once(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        action = {
            "action": "workflow_decision",
            "workflow_id": payload["workflow_id"],
            "event_id": payload["event_id"],
            "decision_token": record["decision_token"],
            "action_id": "recommended",
        }
        event = {
            "event_id": "card-event-1",
            "operator_id": "ou_admin",
            "chat_id": "oc_private",
            "message_id": "om_workflow",
        }
        with mock.patch.object(self.bridge, "patch_card", return_value=True) as patch:
            self.assertTrue(self.bridge.handle_workflow_card_action(event, action))
            event["event_id"] = "card-event-2"
            self.assertTrue(self.bridge.handle_workflow_card_action(event, action))

        state = self.bridge._workflow_store.load()
        self.assertEqual(len(state["recoveries"]), 1)
        self.assertEqual(
            next(iter(state["notifications"].values()))["decision_status"],
            "consumed",
        )
        self.assertEqual(patch.call_count, 1)

    def test_card_callback_retries_after_workflow_state_failure(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        action = {
            "action": "workflow_decision",
            "workflow_id": payload["workflow_id"],
            "event_id": payload["event_id"],
            "decision_token": record["decision_token"],
            "action_id": "recommended",
        }
        event = {
            "event_id": "card-event-retry",
            "operator_id": "ou_admin",
            "chat_id": "oc_private",
            "message_id": "om_workflow",
        }
        original = self.bridge._workflow_store.consume_token_decision
        attempts = 0

        def flaky_consume(*args, **kwargs):
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                raise self.bridge.WorkflowStateError("temporary failure")
            return original(*args, **kwargs)

        with mock.patch.object(
            self.bridge._workflow_store,
            "consume_token_decision",
            side_effect=flaky_consume,
        ), self.assertRaises(self.bridge.WorkflowStateError):
            self.bridge.handle_workflow_card_action(event, action)

        self.assertNotIn(
            "workflow-card:card-event-retry",
            self.bridge.load_state().get("processed", []),
        )
        with mock.patch.object(self.bridge, "patch_card", return_value=True) as patch:
            self.assertTrue(self.bridge.handle_workflow_card_action(event, action))

        state = self.bridge._workflow_store.load()
        self.assertEqual(len(state["recoveries"]), 1)
        self.assertEqual(
            next(iter(state["notifications"].values()))["decision_status"],
            "consumed",
        )
        patch.assert_called_once()

    def test_text_reply_retries_after_workflow_state_failure(self):
        payload = self.enqueue_delivered_request()
        event = {
            "event_id": "reply-event-retry",
            "message_id": "om_reply",
            "parent_id": "om_workflow",
            "sender_id": "ou_admin",
            "chat_id": "oc_private",
        }
        original = self.bridge._workflow_store.consume_reply_decision
        attempts = 0

        def flaky_consume(*args, **kwargs):
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                raise self.bridge.WorkflowStateError("temporary failure")
            return original(*args, **kwargs)

        with mock.patch.object(
            self.bridge._workflow_store,
            "consume_reply_decision",
            side_effect=flaky_consume,
        ), self.assertRaises(self.bridge.WorkflowStateError):
            self.bridge.handle_workflow_text_reply(event, "1")

        self.assertNotIn(
            "workflow-reply:om_reply",
            self.bridge.load_state().get("processed", []),
        )
        with mock.patch.object(self.bridge, "patch_card", return_value=True) as patch, mock.patch.object(
            self.bridge, "reply", return_value=True
        ) as reply:
            self.assertTrue(self.bridge.handle_workflow_text_reply(event, "1"))

        state = self.bridge._workflow_store.load()
        self.assertEqual(len(state["recoveries"]), 1)
        self.assertEqual(
            next(iter(state["notifications"].values()))["decision_status"],
            "consumed",
        )
        patch.assert_called_once()
        reply.assert_called_once()

    def test_decision_patches_initial_and_reminder_cards(self):
        payload = self.enqueue_delivered_request()
        key = self.bridge.workflow_event_key(payload["workflow_id"], payload["event_id"])
        self.bridge._workflow_store.delivery_succeeded(
            key,
            "reminder",
            "om_reminder",
            "oc_private",
            86400,
            now=102,
        )
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        action = {
            "action": "workflow_decision",
            "workflow_id": payload["workflow_id"],
            "event_id": payload["event_id"],
            "decision_token": record["decision_token"],
            "action_id": "recommended",
        }
        event = {
            "event_id": "card-event-reminder",
            "operator_id": "ou_admin",
            "chat_id": "oc_private",
            "message_id": "om_reminder",
        }
        with mock.patch.object(self.bridge, "patch_card", return_value=True) as patch:
            self.assertTrue(self.bridge.handle_workflow_card_action(event, action))

        self.assertEqual(
            [call.args[0] for call in patch.call_args_list],
            ["om_workflow", "om_reminder"],
        )

    def test_failed_initial_and_reminder_patches_enter_durable_queue(self):
        payload = self.enqueue_delivered_request()
        key = self.bridge.workflow_event_key(payload["workflow_id"], payload["event_id"])
        self.bridge._workflow_store.delivery_succeeded(
            key,
            "reminder",
            "om_reminder",
            "oc_private",
            86400,
            now=102,
        )
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        failed = subprocess.CompletedProcess([], 1, "", "network down")
        with mock.patch.object(
            self.bridge,
            "CARD_PATCH_RETRY_DELAYS",
            (),
        ), mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=failed,
        ):
            self.bridge.patch_workflow_completed_cards(record)

        pending = self.bridge.load_state()["pending_replies"]
        self.assertEqual(
            {
                item["message_id"]
                for item in pending
                if item.get("operation") == "card_patch"
            },
            {"om_workflow", "om_reminder"},
        )

    def test_durable_card_inbox_replays_after_crash_without_second_recovery(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        envelope = workflow_card_envelope("card-event-crash")
        envelope["event"]["action"]["value"]["decision_token"] = record[
            "decision_token"
        ]
        inbox_path = self.bridge._workflow_decision_inbox.path_for_event(
            "card-event-crash"
        )
        inbox_path.write_text(json.dumps(envelope), encoding="utf-8")
        inbox_path.chmod(0o600)
        events = queue.Queue()
        self.bridge.enqueue_workflow_decision_inbox(events)
        event = events.get_nowait()

        with mock.patch.object(
            self.bridge,
            "patch_card",
            side_effect=RuntimeError("simulated crash"),
        ), self.assertRaises(RuntimeError):
            self.bridge.dispatch_event(event)

        self.assertTrue(inbox_path.exists())
        self.assertEqual(len(self.bridge._workflow_store.load()["recoveries"]), 1)
        self.assertNotIn(
            "workflow-card:card-event-crash",
            self.bridge.load_state().get("processed", []),
        )

        self.bridge._workflow_store = self.bridge.WorkflowStore(
            self.bridge.WORKFLOW_STATE_PATH
        )
        self.bridge._workflow_decision_inbox = self.bridge.WorkflowDecisionInbox(
            self.bridge.WORKFLOW_DECISION_INBOX_PATH
        )
        replay = queue.Queue()
        self.bridge.enqueue_workflow_decision_inbox(replay)
        restarted_event = replay.get_nowait()
        with mock.patch.object(self.bridge, "patch_card", return_value=True) as patch:
            self.bridge.dispatch_event(restarted_event)
            self.bridge.acknowledge_workflow_decision_inbox(restarted_event)

        patch.assert_called_once()
        self.assertFalse(inbox_path.exists())
        self.assertEqual(len(self.bridge._workflow_store.load()["recoveries"]), 1)
        self.assertIn(
            "workflow-card:card-event-crash",
            self.bridge.load_state().get("processed", []),
        )

    def test_text_reply_replays_side_effects_after_crash_without_second_recovery(self):
        self.enqueue_delivered_request()
        event = {
            "event_id": "reply-event-crash",
            "message_id": "om_reply_crash",
            "parent_id": "om_workflow",
            "sender_id": "ou_admin",
            "chat_id": "oc_private",
        }
        with mock.patch.object(
            self.bridge,
            "patch_card",
            return_value=True,
        ), mock.patch.object(
            self.bridge,
            "reply",
            side_effect=RuntimeError("simulated crash"),
        ), self.assertRaises(RuntimeError):
            self.bridge.handle_workflow_text_reply(event, "1")

        self.assertEqual(len(self.bridge._workflow_store.load()["recoveries"]), 1)
        self.assertNotIn(
            "workflow-reply:om_reply_crash",
            self.bridge.load_state().get("processed", []),
        )
        self.bridge._workflow_store = self.bridge.WorkflowStore(
            self.bridge.WORKFLOW_STATE_PATH
        )
        with mock.patch.object(self.bridge, "patch_card", return_value=True) as patch, mock.patch.object(
            self.bridge,
            "reply",
            return_value=True,
        ) as reply:
            self.assertTrue(self.bridge.handle_workflow_text_reply(event, "1"))
            self.assertTrue(self.bridge.handle_workflow_text_reply(event, "1"))

        patch.assert_called_once()
        reply.assert_called_once()
        self.assertEqual(len(self.bridge._workflow_store.load()["recoveries"]), 1)

    def test_failed_workflow_choice_reply_is_durably_retried(self):
        self.enqueue_delivered_request()
        event = {
            "event_id": "reply-event-network",
            "message_id": "om_reply_network",
            "parent_id": "om_workflow",
            "sender_id": "ou_admin",
            "chat_id": "oc_private",
        }
        with mock.patch.object(
            self.bridge,
            "patch_card",
            return_value=True,
        ), mock.patch.object(
            self.bridge,
            "reply",
            return_value=False,
        ):
            self.assertTrue(self.bridge.handle_workflow_text_reply(event, "1"))

        pending = self.bridge.load_state()["pending_replies"]
        self.assertEqual(len(pending), 1)
        self.assertEqual(pending[0]["kind"], "workflow-choice")
        with mock.patch.object(
            self.bridge,
            "reply",
            return_value=True,
        ) as reply:
            self.assertTrue(
                self.bridge.retry_pending_replies(
                    now=float(pending[0]["next_attempt_at"])
                )
            )

        reply.assert_called_once_with(
            "om_reply_network",
            "已记录你的选择，自动研发工作流将从原检查点继续。",
            "workflow-choice",
        )
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])

    def test_unauthorized_callback_cannot_consume_decision(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        action = {
            "action": "workflow_decision",
            "workflow_id": payload["workflow_id"],
            "event_id": payload["event_id"],
            "decision_token": record["decision_token"],
            "action_id": "recommended",
        }
        self.bridge.handle_workflow_card_action(
            {
                "event_id": "card-event",
                "operator_id": "ou_other",
                "chat_id": "oc_private",
                "message_id": "om_workflow",
            },
            action,
        )
        self.assertEqual(self.bridge._workflow_store.safe_status()["pending_decisions"], 1)

    def test_network_failure_stays_in_outbox_and_reuses_event(self):
        payload = workflow_payload(status="milestone_completed")
        self.bridge._workflow_store.enqueue(
            payload,
            "ori-one-mind",
            now=100,
        )
        with mock.patch.object(
            self.bridge,
            "send_workflow_card",
            return_value=(False, "", "", "飞书 API 网络连接失败"),
        ) as send:
            self.assertTrue(self.bridge.retry_workflow_notifications(now=100))
        state = self.bridge._workflow_store.load()
        record = next(iter(state["notifications"].values()))
        self.assertEqual(record["delivery_status"], "pending")
        self.assertEqual(record["delivery_attempts"], 1)
        key = send.call_args.args[0]

        with mock.patch.object(
            self.bridge,
            "send_workflow_card",
            return_value=(True, "om_sent", "oc_private", ""),
        ) as recovered:
            self.assertTrue(
                self.bridge.retry_workflow_notifications(
                    now=record["next_delivery_at"]
                )
            )
        self.assertEqual(recovered.call_args.args[0], key)
        self.assertEqual(
            next(iter(self.bridge._workflow_store.load()["notifications"].values()))[
                "delivery_status"
            ],
            "sent",
        )

    def test_busy_recovery_retries_then_delivers_once(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        self.bridge._workflow_store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
        )
        with mock.patch.object(
            self.bridge,
            "submit_workflow_recovery",
            return_value=("retry", "Codex Task 当前正忙"),
        ):
            self.assertTrue(self.bridge.retry_workflow_recoveries(now=110))
        recovery = next(iter(self.bridge._workflow_store.load()["recoveries"].values()))
        self.assertEqual(recovery["status"], "pending")

        with mock.patch.object(
            self.bridge,
            "submit_workflow_recovery",
            return_value=("accepted", "turn-1"),
        ) as submit:
            self.assertTrue(
                self.bridge.retry_workflow_recoveries(
                    now=recovery["next_attempt_at"]
                )
            )
            self.assertFalse(
                self.bridge.retry_workflow_recoveries(
                    now=recovery["next_attempt_at"] + 1
                )
            )
        self.assertEqual(submit.call_count, 1)
        delivered = next(iter(self.bridge._workflow_store.load()["recoveries"].values()))
        self.assertEqual(delivered["status"], "delivered")

    def test_recovery_prompt_uses_resolve_contract_without_local_token(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        _outcome, recovery = self.bridge._workflow_store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
        )
        prompt = self.bridge.workflow_recovery_prompt(recovery)

        self.assertIn("resolve-attention", prompt)
        self.assertIn("--request-id evt-1", prompt)
        self.assertIn("--action-id recommended", prompt)
        self.assertIn("--action-label", prompt)
        self.assertIn("--resolution resume", prompt)
        self.assertIn("attention_request_id: evt-1", prompt)
        self.assertNotIn(str(recovery["marker"]), prompt)
        self.assertNotIn(str(record["decision_token"]), prompt)

    def test_roundtrip_recovery_only_reports_receipt_without_research_side_effects(self):
        payload = workflow_payload()
        payload["event_id"] = "test-roundtrip-event"
        payload["task_id"] = "TEST-ROUNDTRIP"
        self.bridge._workflow_store = self.bridge.WorkflowStore(
            Path(self.temporary.name) / "roundtrip-state.json"
        )
        self.bridge._workflow_store.enqueue(payload, "ori-one-mind", now=100)
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        _outcome, recovery = self.bridge._workflow_store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
        )

        prompt = self.bridge.workflow_recovery_prompt(recovery)
        signature = self.bridge.workflow_recovery_signature(recovery)

        self.assertIn("TEST-ROUNDTRIP", prompt)
        self.assertIn("只回报这次测试回执", prompt)
        self.assertIn("不得调用 Neon", prompt)
        self.assertIn("不得读取或修改仓库文件", prompt)
        self.assertIn("不得租用、推进或改变任何 ONE 研发任务", prompt)
        self.assertNotIn("resolve-attention --request-id", prompt)
        self.assertNotIn("bin/orchestrator.mjs", prompt)
        self.assertIn("roundtrip_event_id: test-roundtrip-event", signature)
        self.assertNotIn("attention_request_id", signature)

    def test_delivery_unknown_reconciles_from_task_without_resubmitting(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        _outcome, recovery = self.bridge._workflow_store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
        )
        key = self.bridge.workflow_event_key(
            payload["workflow_id"], payload["event_id"]
        )
        self.bridge._workflow_store.recovery_failed(
            key,
            "Codex Desktop 提交确认中断",
            retryable=False,
            now=111,
        )

        with mock.patch.object(
            self.bridge,
            "workflow_recovery_in_rollout",
            return_value=True,
        ), mock.patch.object(self.bridge, "submit_workflow_recovery") as submit:
            self.assertTrue(self.bridge.retry_workflow_recoveries(now=112))

        submit.assert_not_called()
        reconciled = self.bridge._workflow_store.load()["recoveries"][key]
        self.assertEqual(reconciled["status"], "delivered")
        self.assertEqual(reconciled["turn_id"], "reconciled")

    def test_reconciliation_requires_exact_request_action_and_resolution(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        _outcome, recovery = self.bridge._workflow_store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
        )
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        rollout.write_text(
            json.dumps(
                {
                    "type": "response_item",
                    "payload": {
                        "type": "message",
                        "role": "user",
                        "content": [
                            {
                                "type": "input_text",
                                "text": self.bridge.workflow_recovery_signature(recovery),
                            }
                        ],
                    },
                }
            )
            + "\n",
            encoding="utf-8",
        )
        with mock.patch.object(
            self.bridge,
            "rollout_path_for_task",
            return_value=rollout,
        ):
            self.assertTrue(
                self.bridge.workflow_recovery_in_rollout("dedicated-task", recovery)
            )
            mismatched = dict(recovery)
            mismatched["resolution"] = "pause"
            self.assertFalse(
                self.bridge.workflow_recovery_in_rollout("dedicated-task", mismatched)
            )
            assistant_event = json.loads(rollout.read_text(encoding="utf-8"))
            assistant_event["payload"]["role"] = "assistant"
            rollout.write_text(json.dumps(assistant_event) + "\n", encoding="utf-8")
            self.assertFalse(
                self.bridge.workflow_recovery_in_rollout("dedicated-task", recovery)
            )

    def test_delivery_unknown_without_task_evidence_is_not_retried(self):
        payload = self.enqueue_delivered_request()
        record = self.bridge._workflow_store.record_for_event(
            payload["workflow_id"], payload["event_id"]
        )
        self.bridge._workflow_store.consume_token_decision(
            payload["workflow_id"],
            payload["event_id"],
            record["decision_token"],
            "recommended",
            now=110,
        )
        key = self.bridge.workflow_event_key(
            payload["workflow_id"], payload["event_id"]
        )
        self.bridge._workflow_store.recovery_failed(
            key,
            "Codex Desktop 提交确认中断",
            retryable=False,
            now=111,
        )

        with mock.patch.object(
            self.bridge,
            "workflow_recovery_in_rollout",
            return_value=False,
        ), mock.patch.object(self.bridge, "submit_workflow_recovery") as submit:
            self.assertFalse(self.bridge.retry_workflow_recoveries(now=112))

        submit.assert_not_called()
        self.assertEqual(
            self.bridge._workflow_store.load()["recoveries"][key]["status"],
            "delivery_unknown",
        )

    def test_socket_notification_schema_and_safe_control_status(self):
        left, right = socket.socketpair()
        try:
            left.sendall(json.dumps(workflow_payload()).encode() + b"\n")
            self.bridge.handle_workflow_socket_connection(right)
            response = json.loads(left.recv(4096))
        finally:
            left.close()
            right.close()
        self.assertEqual(response, {"ok": True, "result": "queued"})

        left, right = socket.socketpair()
        try:
            left.sendall(b'{"command":"status"}\n')
            self.bridge.handle_workflow_control_connection(right)
            status = json.loads(left.recv(4096))
        finally:
            left.close()
            right.close()
        self.assertTrue(status["ok"])
        self.assertNotIn("ONE-G1", json.dumps(status))
        self.assertNotIn("ou_admin", json.dumps(status))

    def test_corrupt_state_is_reported_without_overwriting_outbox(self):
        payload = workflow_payload()
        self.bridge._workflow_store.enqueue(payload, "ori-one-mind", now=100)
        corrupt = b'{"version":1,"notifications":'
        self.bridge.WORKFLOW_STATE_PATH.write_bytes(corrupt)
        self.bridge.WORKFLOW_STATE_PATH.chmod(0o600)

        left, right = socket.socketpair()
        try:
            left.sendall(json.dumps(payload).encode() + b"\n")
            self.bridge.handle_workflow_socket_connection(right)
            response = json.loads(left.recv(4096))
        finally:
            left.close()
            right.close()

        self.assertEqual(
            response,
            {"ok": False, "error": "workflow_state_unavailable"},
        )
        self.assertEqual(self.bridge.WORKFLOW_STATE_PATH.read_bytes(), corrupt)

    def test_log_file_is_restricted_to_current_user(self):
        self.bridge.log("safe message")
        self.assertEqual(self.bridge.LOG_PATH.stat().st_mode & 0o777, 0o600)


class WorkflowClientTests(unittest.TestCase):
    def test_dry_run_accepts_exact_schema(self):
        result = subprocess.run(
            [sys.executable, str(CLIENT_PATH), "--dry-run"],
            input=json.dumps(workflow_payload()),
            text=True,
            capture_output=True,
        )
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), {"ok": True, "result": "valid"})

    def test_dry_run_rejects_recipient_field_with_exit_2(self):
        payload = workflow_payload()
        payload["recipient_open_id"] = "ou_forbidden"
        result = subprocess.run(
            [sys.executable, str(CLIENT_PATH), "--dry-run"],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
        )
        self.assertEqual(result.returncode, 2)
        self.assertEqual(json.loads(result.stdout)["error"], "invalid_request")

    def test_dry_run_rejects_secret_without_echoing_it(self):
        payload = workflow_payload()
        secret = "github_pat_" + "1234567890abcdef"
        payload["summary"] = secret
        result = subprocess.run(
            [sys.executable, str(CLIENT_PATH), "--dry-run"],
            input=json.dumps(payload),
            text=True,
            capture_output=True,
        )
        self.assertEqual(result.returncode, 2)
        self.assertEqual(json.loads(result.stdout)["error"], "invalid_request")
        self.assertNotIn(secret, result.stdout)
        self.assertNotIn(secret, result.stderr)

    def test_roundtrip_test_generates_isolated_safe_payload(self):
        with tempfile.TemporaryDirectory() as directory:
            socket_path = Path(directory) / "workflow.sock"
            server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            server.bind(str(socket_path))
            server.listen(1)
            captured = {}

            def receive_once():
                connection, _address = server.accept()
                with connection:
                    raw = b""
                    while b"\n" not in raw:
                        raw += connection.recv(4096)
                    captured.update(json.loads(raw.split(b"\n", 1)[0]))
                    connection.sendall(b'{"ok":true,"result":"queued"}')

            receiver = threading.Thread(target=receive_once)
            receiver.start()
            environment = os.environ.copy()
            environment["CODEX_FEISHU_WORKFLOW_SOCKET"] = str(socket_path)
            result = subprocess.run(
                [sys.executable, str(CLIENT_PATH), "--roundtrip-test"],
                text=True,
                capture_output=True,
                env=environment,
            )
            receiver.join(timeout=5)
            server.close()

        self.assertFalse(receiver.is_alive())
        self.assertEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout), {"ok": True, "result": "queued"})
        self.assertEqual(captured["workflow_id"], "ori-one-mind")
        self.assertTrue(captured["event_id"].startswith("test-roundtrip-"))
        self.assertEqual(captured["task_id"], "TEST-ROUNDTRIP")
        self.assertEqual(captured["status"], "user_action_required")
        self.assertEqual(len(captured["actions"]), 2)
        self.assertEqual(
            sum(bool(action["recommended"]) for action in captured["actions"]),
            1,
        )
        self.assertFalse(
            {"recipient_open_id", "recipient_chat_id", "chat_id"} & set(captured)
        )

    def test_health_returns_exit_1_when_bridge_is_unavailable(self):
        with tempfile.TemporaryDirectory() as directory:
            environment = os.environ.copy()
            environment["CODEX_FEISHU_WORKFLOW_CONTROL_SOCKET"] = str(
                Path(directory) / "missing.sock"
            )
            result = subprocess.run(
                [sys.executable, str(CLIENT_PATH), "--health"],
                text=True,
                capture_output=True,
                env=environment,
            )
        self.assertEqual(result.returncode, 1)
        self.assertEqual(json.loads(result.stdout)["error"], "bridge_unavailable")


class DiagnosticScriptTests(unittest.TestCase):
    @staticmethod
    def _run_diagnose(doctor_payload: dict) -> subprocess.CompletedProcess:
        temporary = tempfile.TemporaryDirectory()
        home = Path(temporary.name)
        support = home / "Library/Application Support/Codex Feishu Bridge"
        support.mkdir(parents=True)
        (support / "config.json").write_text(
            json.dumps({"lark_profile": "codex-notify"}),
            encoding="utf-8",
        )
        (support / "bridge.py").write_text(
            'print("{\\"ok\\": true}")\n',
            encoding="utf-8",
        )
        lark_cli = support / "lark-cli"
        lark_cli.write_text(
            "#!/bin/sh\n"
            "case \"$*\" in\n"
            "  *--version*) printf '%s\\n' '1.0.89-codex-feishu.2' ;;\n"
            f"  *doctor*) printf '%s\\n' '{json.dumps(doctor_payload)}' ;;\n"
            "  *'event status'*) printf '%s\\n' "
            "'{\"apps\":[{\"active_consumers\":3}]}' ;;\n"
            "  *) exit 1 ;;\n"
            "esac\n",
            encoding="utf-8",
        )
        lark_cli.chmod(0o755)
        workflow_config = support / "workflow-config"
        workflow_config.write_text(
            "#!/bin/sh\nprintf '%s\\n' disabled\n",
            encoding="utf-8",
        )
        workflow_config.chmod(0o755)
        environment = os.environ.copy()
        environment["HOME"] = str(home)
        result = subprocess.run(
            [str(ROOT / "Resources/bridge/diagnose.sh")],
            text=True,
            capture_output=True,
            env=environment,
        )
        temporary.cleanup()
        return result

    def test_diagnose_hides_only_same_base_patched_cli_update_warning(self):
        payload = {
            "checks": [
                {
                    "name": "cli_version",
                    "status": "pass",
                    "message": "1.0.89-codex-feishu.2",
                },
                {
                    "name": "cli_update",
                    "status": "warn",
                    "message": "1.0.89-codex-feishu.2 → 1.0.89 available",
                },
                {
                    "name": "user_identity",
                    "status": "warn",
                    "message": "User identity: missing (no user logged in)",
                },
            ],
            "ok": True,
        }

        result = self._run_diagnose(payload)

        self.assertNotIn('"name": "cli_update"', result.stdout)
        self.assertIn('"name": "user_identity"', result.stdout)

    def test_diagnose_keeps_real_new_base_cli_update_warning(self):
        payload = {
            "checks": [
                {
                    "name": "cli_version",
                    "status": "pass",
                    "message": "1.0.89-codex-feishu.2",
                },
                {
                    "name": "cli_update",
                    "status": "warn",
                    "message": "1.0.89-codex-feishu.2 → 1.0.90 available",
                },
            ],
            "ok": True,
        }

        result = self._run_diagnose(payload)

        self.assertIn('"name": "cli_update"', result.stdout)
        self.assertIn("1.0.90 available", result.stdout)


class ReleaseVersionTests(unittest.TestCase):
    def test_release_version_and_build_are_unique(self):
        with (ROOT / "Resources/Info.plist").open("rb") as handle:
            info = plistlib.load(handle)
        self.assertEqual(info["CFBundleShortVersionString"], "1.9.4")
        self.assertEqual(info["CFBundleVersion"], "40")
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        release_notes = (ROOT / "RELEASE_NOTES.md").read_text(encoding="utf-8")
        self.assertIn("1.9.4 (build 40)", readme)
        self.assertIn("1.9.4 (build 40", release_notes)


class AppUpdaterSafetyTests(unittest.TestCase):
    def test_helper_verifies_universal_binary_with_valid_lipo_order(self):
        helper = (ROOT / "Resources/bridge/app_update.sh").read_text(encoding="utf-8")
        self.assertIn(
            'lipo "${staged_app}/Contents/MacOS/CodexFeishuBridge" '
            "-verify_arch arm64 x86_64",
            helper,
        )

    def test_helper_refuses_destination_outside_applications(self):
        helper = ROOT / "Resources/bridge/app_update.sh"
        with tempfile.TemporaryDirectory() as directory:
            environment = os.environ.copy()
            environment["HOME"] = directory
            result = subprocess.run(
                [str(helper), "/tmp/not-an-app", "/tmp/Codex Feishu Bridge.app", "1", "9.9.9"],
                text=True,
                capture_output=True,
                env=environment,
            )
        self.assertEqual(result.returncode, 2)

    def test_app_checks_destination_and_all_pending_queues_before_update(self):
        source = (ROOT / "Sources/CodexFeishuBridgeApp/main.swift").read_text(encoding="utf-8")
        self.assertIn('URL(fileURLWithPath: "/Applications/Codex 飞书桥接.app")', source)
        self.assertIn("health.pendingInputs == 0", source)
        self.assertIn("health.pendingDeliveries == 0", source)
        self.assertIn("health.pendingTaskCreations == 0", source)
        self.assertIn('appendingPathComponent("app_update.sh")', source)

    def test_configuration_sheet_has_one_scroll_region_and_labeled_user_cards(self):
        source = (ROOT / "Sources/CodexFeishuBridgeApp/main.swift").read_text(encoding="utf-8")
        configuration = source.split("private struct ConfigurationView", 1)[1].split(
            "private struct DiagnosisView",
            1,
        )[0]

        self.assertEqual(configuration.count("ScrollView {"), 1)
        self.assertNotIn("Form {", configuration)
        self.assertIn('Text("桥接配置")', configuration)
        self.assertIn('"备注名"', configuration)
        self.assertIn('"用户 open_id"', configuration)
        self.assertIn('"允许项目"', configuration)
        self.assertIn('"当前 Task"', configuration)
        self.assertIn('"额度用量"', configuration)
        self.assertIn('"接续桌面 Task"', configuration)
        self.assertIn('config["current_task_menu_event_key"] = currentTaskEventKey', source)
        self.assertIn("六个机器人菜单 Event Key 都不能为空", source)
        self.assertIn("六个机器人菜单 Event Key 不能重复", source)
        self.assertIn('config["desktop_sync_menu_event_key"] = desktopSyncEventKey', source)
        self.assertIn('Text("保存后，正在运行的桥接会自动重启并保留当前 Task。")', configuration)


if __name__ == "__main__":
    unittest.main()

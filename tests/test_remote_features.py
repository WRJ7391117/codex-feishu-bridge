import importlib.util
import json
import os
from pathlib import Path
import subprocess
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
        spec = importlib.util.spec_from_file_location("bridge_remote_test", BRIDGE_PATH)
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


class RemoteFeatureTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        self.temporary = tempfile.TemporaryDirectory()
        self.bridge.STATE_PATH = Path(self.temporary.name) / "state.json"
        self.bridge.LOG_PATH = Path(self.temporary.name) / "bridge.log"

    def tearDown(self):
        self.temporary.cleanup()

    def test_save_state_restricts_state_file_permissions(self):
        self.bridge.STATE_PATH.write_text("{}\n", encoding="utf-8")
        self.bridge.STATE_PATH.chmod(0o644)

        self.bridge.save_state({"queued_turns": []})

        self.assertEqual(self.bridge.STATE_PATH.stat().st_mode & 0o777, 0o600)

    def test_global_run_limit_queues_different_tasks(self):
        self.bridge.MAX_CONCURRENT_RUNS = 2
        runs = [
            self.bridge.new_run(
                "ou_admin",
                "oc_test",
                f"om_{index}",
                {"id": f"task-{index}", "title": f"Task {index}", "project": "deepori"},
                [],
                [],
            )
            for index in range(3)
        ]

        self.assertTrue(self.bridge.claim_active_run(runs[0]))
        self.assertTrue(self.bridge.claim_active_run(runs[1]))
        self.assertFalse(self.bridge.claim_active_run(runs[2]))

    def test_same_task_never_runs_twice_even_below_global_limit(self):
        self.bridge.MAX_CONCURRENT_RUNS = 3
        task = {"id": "task-a", "title": "Home", "project": "deepori"}
        first = self.bridge.new_run("ou_admin", "oc_test", "om_1", task, [], [])
        second = self.bridge.new_run("ou_admin", "oc_test", "om_2", task, [], [])

        self.assertTrue(self.bridge.claim_active_run(first))
        self.assertFalse(self.bridge.claim_active_run(second))

    def tasks(self):
        return [
            {"id": "task-a", "title": "Home", "project": "deepori"},
            {"id": "task-b", "title": "Site", "project": "deepori"},
            {"id": "task-c", "title": "Paper", "project": "thesis"},
        ]

    def test_task_card_filters_by_project_without_truncating_project_tasks(self):
        card = self.bridge.build_task_card(self.tasks(), "task-a", "deepori")
        selectors = {
            item.get("name"): item
            for item in card["body"]["elements"]
            if item.get("tag") == "select_static"
        }

        self.assertEqual(selectors["project_selector"]["initial_option"], "deepori")
        self.assertEqual(
            [option["value"] for option in selectors["project_selector"]["options"]],
            ["deepori", "thesis"],
        )
        self.assertEqual(
            [option["value"] for option in selectors["task_selector"]["options"]],
            ["task-a", "task-b"],
        )

    def test_task_card_defaults_to_first_project_without_an_all_projects_option(self):
        card = self.bridge.build_task_card(self.tasks(), None, "__all__")
        selectors = {
            item.get("name"): item
            for item in card["body"]["elements"]
            if item.get("tag") == "select_static"
        }

        self.assertEqual(selectors["project_selector"]["initial_option"], "deepori")
        self.assertEqual(
            [option["value"] for option in selectors["task_selector"]["options"]],
            ["task-a", "task-b"],
        )

    def test_task_card_paginates_large_project_without_ten_item_truncation(self):
        tasks = [
            {"id": f"task-{index}", "title": f"Task {index}", "project": "deepori"}
            for index in range(55)
        ]

        first = self.bridge.build_task_card(tasks, None, "deepori", page=0)
        first_selector = next(
            item for item in first["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual(len(first_selector["options"]), 50)
        self.assertTrue(any(
            item.get("tag") == "button"
            and item.get("text", {}).get("content") == "下一页"
            for item in first["body"]["elements"]
        ))

        second = self.bridge.build_task_card(tasks, None, "deepori", page=1)
        second_selector = next(
            item for item in second["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual(len(second_selector["options"]), 5)

    def test_task_card_search_filters_title_and_can_be_cleared(self):
        card = self.bridge.build_task_card(
            self.tasks(),
            "task-a",
            "deepori",
            search_query="site",
        )
        selector = next(
            item for item in card["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual([item["value"] for item in selector["options"]], ["task-b"])
        self.assertTrue(any(
            item.get("tag") == "button"
            and item.get("text", {}).get("content") == "清除搜索"
            for item in card["body"]["elements"]
        ))

    def test_task_card_only_contains_task_selection_controls(self):
        card = self.bridge.build_task_card(self.tasks(), "task-a", "deepori")
        button_labels = {
            item.get("text", {}).get("content")
            for item in card["body"]["elements"]
            if item.get("tag") == "button"
        }

        self.assertNotIn("新建 Task", button_labels)
        self.assertNotIn("归档当前 Task…", button_labels)

    def test_new_task_menu_card_only_lists_authorized_desktop_projects(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
            {"id": "project-2", "name": "thesis", "root": "/tmp/thesis"},
        ]
        sent = []
        self.bridge.send_card = lambda user_id, card, kind: (
            sent.append((user_id, card, kind)) or True,
            "oc_test",
        )

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-new-task-menu",
                "event_key": "new_task",
                "operator_id": "ou_admin",
            }
        )

        self.assertEqual(len(sent), 1)
        selector = next(
            item
            for item in sent[0][1]["body"]["elements"]
            if item.get("name") == "new_task_project_selector"
        )
        self.assertEqual(
            [option["value"] for option in selector["options"]],
            ["deepori"],
        )

    def test_new_task_card_project_selection_updates_creation_target(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori", "thesis"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
            {"id": "project-2", "name": "thesis", "root": "/tmp/thesis"},
        ]
        original = self.bridge.build_new_task_card(
            ["deepori", "thesis"],
            "deepori",
        )
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-new-task-project",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "new_task_project_selector",
                "option": "thesis",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        updated = self.bridge.update_card.call_args.args[1]
        button = next(
            item
            for item in updated["body"]["elements"]
            if item.get("tag") == "button"
        )
        payload = button["behaviors"][0]["value"]
        self.assertEqual(payload, {"action": "new_task", "project": "thesis"})

    def test_new_task_card_confirmation_waits_for_title(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"},
        ]
        self.bridge.reply = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-new-task-confirm",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_new_task_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "new_task", "project": "deepori"}
                ),
            }
        )

        self.assertEqual(
            self.bridge.load_state()["pending_task_creations"]["ou_admin"],
            "deepori",
        )
        self.assertIn("请发送 Task 标题", self.bridge.reply.call_args.args[1])

    def test_archive_task_menu_sends_confirmation_for_current_task(self):
        task = self.tasks()[0]
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: None
        sent = []
        self.bridge.send_card = lambda user_id, card, kind: (
            sent.append((user_id, card, kind)) or True,
            "oc_test",
        )

        self.bridge.handle_menu_event(
            {
                "event_id": "evt-archive-task-menu",
                "event_key": "archive_task",
                "operator_id": "ou_admin",
            }
        )

        self.assertEqual(len(sent), 1)
        card = sent[0][1]
        self.assertEqual(card["header"]["subtitle"]["content"], "deepori · Home")
        button = next(
            item for item in card["body"]["elements"]
            if item.get("tag") == "button"
        )
        self.assertEqual(
            button["behaviors"][0]["value"],
            {"action": "archive_task", "task_id": "task-a"},
        )
        self.assertIn("confirm", button)

    def test_archive_callback_clears_selection_only_after_success(self):
        task = self.tasks()[0]
        self.bridge.STATE_PATH.write_text(
            json.dumps({"selected": {"ou_admin": "task-a"}}),
            encoding="utf-8",
        )
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: None
        self.bridge.archive_codex_task = mock.Mock()
        self.bridge.reply = mock.Mock(return_value=True)
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-archive-confirm",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_archive_card",
                "token": "token-test",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "archive_task", "task_id": "task-a"}
                ),
            }
        )

        self.bridge.archive_codex_task.assert_called_once_with("ou_admin", task)
        self.assertNotIn(
            "ou_admin",
            self.bridge.load_state().get("selected", {}),
        )
        completed = self.bridge.update_card.call_args.args[1]
        self.assertEqual(completed["header"]["text_tag_list"][0]["text"]["content"], "已归档")

    def test_archive_callback_blocks_an_active_task(self):
        task = self.tasks()[0]
        self.bridge.selected_task = lambda user_id, state: task
        self.bridge.active_run_for_task = lambda task_id: {"run_id": "run-1"}
        self.bridge.archive_codex_task = mock.Mock()
        self.bridge.reply = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-archive-busy",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_archive_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "archive_task", "task_id": "task-a"}
                ),
            }
        )

        self.bridge.archive_codex_task.assert_not_called()
        self.assertIn("正在运行", self.bridge.reply.call_args.args[1])

    def test_task_management_menus_ignore_unauthorized_users(self):
        self.bridge.send_card = mock.Mock(return_value=(True, "oc_test"))

        for index, event_key in enumerate(("select_task", "new_task", "archive_task")):
            self.bridge.handle_menu_event(
                {
                    "event_id": f"evt-unauthorized-{index}",
                    "event_key": event_key,
                    "operator_id": "ou_unknown",
                }
            )

        self.bridge.send_card.assert_not_called()

    def test_create_task_uses_authorized_desktop_project_and_names_thread(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        self.bridge.desktop_projects = lambda: [
            {"id": "project-1", "name": "deepori", "root": "/tmp/deepori"}
        ]
        task_id = "019ff634-60a0-7c22-a011-111111111111"
        self.bridge.codex_app_server_requests = mock.Mock(
            side_effect=[
                [{"thread": {"id": task_id}}],
                [{}],
            ]
        )

        task = self.bridge.create_codex_task("ou_admin", "deepori", " Ori Home ")

        self.assertEqual(
            task,
            {"id": task_id, "title": "Ori Home", "project": "deepori"},
        )
        first_request = self.bridge.codex_app_server_requests.call_args_list[0].args[0]
        self.assertEqual(first_request[0][0], "thread/start")
        self.assertEqual(first_request[0][1]["cwd"], "/tmp/deepori")
        second_request = self.bridge.codex_app_server_requests.call_args_list[1].args[0]
        self.assertEqual(
            second_request,
            [("thread/name/set", {"threadId": task_id, "name": "Ori Home"})],
        )

    def test_create_task_rejects_project_outside_user_scope(self):
        self.bridge.ALLOWED_USERS["ou_admin"] = {"deepori"}
        with self.assertRaisesRegex(RuntimeError, "没有.*权限"):
            self.bridge.create_codex_task("ou_admin", "thesis", "Paper")

    def test_project_selector_updates_same_card_and_persists_filter(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(tasks, "task-a")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-project",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "action_name": "project_selector",
                "option": "thesis",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["last_projects"]["ou_admin"], "thesis")
        updated = self.bridge.update_card.call_args.args[1]
        selector = next(
            item
            for item in updated["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual([option["value"] for option in selector["options"]], ["task-c"])

    def test_project_selector_is_inferred_when_feishu_omits_action_name(self):
        tasks = self.tasks()
        original = self.bridge.build_task_card(tasks, "task-a")
        self.bridge.recent_tasks = lambda user_id: tasks
        self.bridge.update_card = mock.Mock(return_value=True)

        self.bridge.handle_card_event(
            {
                "event_id": "evt-project-without-name",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "select_static",
                "option": "thesis",
                "token": "token-test",
                "card_content": json.dumps(original),
            }
        )

        state = self.bridge.load_state()
        self.assertEqual(state["last_projects"]["ou_admin"], "thesis")
        updated = self.bridge.update_card.call_args.args[1]
        selector = next(
            item
            for item in updated["body"]["elements"]
            if item.get("name") == "task_selector"
        )
        self.assertEqual([option["value"] for option in selector["options"]], ["task-c"])

    def test_file_and_audio_are_native_desktop_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            document = Path(directory) / "report.pdf"
            audio = Path(directory) / "note.m4a"
            document.write_bytes(b"pdf")
            audio.write_bytes(b"audio")
            attachments = [
                {"path": str(document), "label": "report.pdf", "kind": "file"},
                {"path": str(audio), "label": "note.m4a", "kind": "audio"},
            ]

            items = self.bridge.codex_turn_input("分析", [], attachments)
            context = self.bridge.codex_attachments(attachments)

        self.assertEqual(
            items[1],
            {"type": "mention", "name": "report.pdf", "path": str(document.resolve())},
        )
        self.assertEqual(items[2], {"type": "localAudio", "path": str(audio.resolve())})
        self.assertEqual(context[0]["fsPath"], str(document.resolve()))

    def test_downloaded_file_is_confined_and_normalized(self):
        root = Path(self.temporary.name)
        downloaded = root / "downloaded"
        downloaded.write_bytes(b"content")
        attachment, error = self.bridge.downloaded_file_path(
            json.dumps({"ok": True, "data": {"saved_path": str(downloaded)}}),
            root,
            1,
            "报告.pdf",
            "file",
        )

        self.assertEqual(error, "")
        self.assertEqual(attachment["kind"], "file")
        self.assertTrue(Path(attachment["path"]).is_file())
        self.assertEqual(Path(attachment["path"]).suffix, ".pdf")

    def test_file_resource_is_downloaded_as_bot_without_forcing_extension_loss(self):
        root = Path(self.temporary.name)

        def complete(command, **kwargs):
            downloaded = root / "原始报告.pdf"
            downloaded.write_bytes(b"content")
            return subprocess.CompletedProcess(
                command,
                0,
                json.dumps({"ok": True, "data": {"saved_path": str(downloaded)}}),
                "",
            )

        with mock.patch.object(self.bridge.subprocess, "run", side_effect=complete) as run:
            attachment, error = self.bridge.download_input_file(
                "om_test",
                "file_v3_test",
                root,
                1,
                "原始报告.pdf",
                "file",
            )

        self.assertEqual(error, "")
        command = run.call_args.args[0]
        self.assertNotIn("--output", command)
        self.assertEqual(command[command.index("--file-key") + 1], "file_v3_test")
        self.assertEqual(command[command.index("--as") + 1], "bot")
        self.assertEqual(attachment["kind"], "file")

    def test_message_handler_returns_while_task_runs(self):
        gate = threading.Event()
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.reply_card_message = lambda *args, **kwargs: (True, "om_progress")
        self.bridge.patch_card = lambda *args, **kwargs: True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True

        def run(*args, **kwargs):
            gate.wait(2)
            return True, "完成", []

        self.bridge.run_codex = run
        started = time.monotonic()
        self.bridge.handle_message_event(
            {
                "chat_id": "oc_test",
                "chat_type": "p2p",
                "sender_id": "ou_admin",
                "sender_type": "user",
                "message_id": "om_test",
                "message_type": "text",
                "content": "继续",
            }
        )
        elapsed = time.monotonic() - started

        self.assertLess(elapsed, 0.5)
        self.assertIsNotNone(self.bridge.active_run_for_task("task-a"))
        gate.set()

    def test_busy_task_queues_message_and_runs_it_next(self):
        gate = threading.Event()
        calls = []
        cards = []
        card_ids = iter(("om_progress_1", "om_progress_2"))
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.task_by_id = lambda task_id, user_id: self.tasks()[0]
        self.bridge.reply_card_message = lambda message_id, card, kind: (
            cards.append(card) or True,
            next(card_ids),
        )
        self.bridge.patch_card = lambda *args, **kwargs: True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True

        def run(task_id, prompt, **kwargs):
            calls.append(prompt)
            if len(calls) == 1:
                gate.wait(2)
            return True, f"完成：{prompt}", []

        self.bridge.run_codex = run
        base_event = {
            "chat_id": "oc_test",
            "chat_type": "p2p",
            "sender_id": "ou_admin",
            "sender_type": "user",
            "message_type": "text",
        }
        self.bridge.handle_message_event(
            {**base_event, "message_id": "om_first", "content": "第一条"}
        )
        self.bridge.handle_message_event(
            {**base_event, "message_id": "om_second", "content": "第二条"}
        )

        state = self.bridge.load_state()
        self.assertEqual(len(state["pending_inputs"]), 1)
        self.assertEqual(calls, ["第一条"])
        self.assertEqual(cards[1]["header"]["text_tag_list"][0]["text"]["content"], "已排队")
        gate.set()
        for _ in range(200):
            if calls == ["第一条", "第二条"] and not self.bridge.active_run_for_task("task-a"):
                break
            time.sleep(0.01)

        self.assertEqual(calls, ["第一条", "第二条"])
        self.assertEqual(self.bridge.load_state().get("pending_inputs"), [])

    def test_queued_message_can_be_canceled_from_its_card(self):
        entry = {
            "queue_id": "queue-1",
            "user_id": "ou_admin",
            "chat_id": "oc_test",
            "source_message_id": "om_source",
            "task": self.tasks()[0],
            "content": "稍后执行",
            "image_keys": [],
            "file_keys": [],
            "raw_content": '{"text":"稍后执行"}',
            "message_type": "text",
            "created_at": time.time(),
            "available_at": 0,
            "ready": True,
            "progress_message_id": "om_queue_card",
        }
        self.assertTrue(self.bridge.enqueue_pending_input(entry)[0])
        patched = []
        self.bridge.patch_card = lambda message_id, card: patched.append(card) or True

        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "evt-cancel-queue",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "message_id": "om_queue_card",
                "action_tag": "button",
                "action_value": json.dumps(
                    {"action": "cancel_queued_input", "queue_id": "queue-1"}
                ),
            }
        )

        self.assertEqual(self.bridge.load_state().get("pending_inputs"), [])
        self.assertEqual(patched[0]["header"]["text_tag_list"][0]["text"]["content"], "已取消")

    def test_desktop_busy_response_converts_direct_message_to_queue(self):
        self.bridge.selected_task = lambda user_id, state: self.tasks()[0]
        self.bridge.reply_card_message = lambda *args, **kwargs: (True, "om_progress")
        patched = []
        self.bridge.patch_card = lambda message_id, card: patched.append(card) or True
        self.bridge.reply = lambda *args, **kwargs: True
        self.bridge.reply_or_queue = lambda *args, **kwargs: True
        self.bridge.run_codex = lambda *args, **kwargs: (
            False,
            "当前 task 正在运行，请稍后重试。",
            [],
        )

        self.bridge.handle_message_event(
            {
                "chat_id": "oc_test",
                "chat_type": "p2p",
                "sender_id": "ou_admin",
                "sender_type": "user",
                "message_type": "text",
                "message_id": "om_busy",
                "content": "排队执行",
            }
        )
        for _ in range(100):
            if self.bridge.load_state().get("pending_inputs"):
                break
            time.sleep(0.01)

        queued = self.bridge.load_state()["pending_inputs"]
        self.assertEqual(len(queued), 1)
        self.assertEqual(queued[0]["content"], "排队执行")
        self.assertEqual(patched[-1]["header"]["text_tag_list"][0]["text"]["content"], "已排队")

    def test_progress_card_patch_retries_a_transient_failure(self):
        failure = subprocess.CompletedProcess(
            ["lark-cli"],
            4,
            json.dumps(
                {
                    "ok": False,
                    "error": {"type": "network", "subtype": "temporary"},
                }
            ),
            "",
        )
        success = subprocess.CompletedProcess(
            ["lark-cli"], 0, json.dumps({"ok": True}), ""
        )
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            side_effect=(failure, success),
        ) as run, mock.patch.object(self.bridge.time, "sleep"):
            patched = self.bridge.patch_card(
                "om_progress",
                self.bridge.build_task_card(self.tasks(), "task-a", "deepori"),
            )

        self.assertTrue(patched)
        self.assertEqual(run.call_count, 2)

    def test_stop_button_uses_confirmed_desktop_interrupt_protocol(self):
        run = {
            "run_id": "run-1",
            "user_id": "ou_admin",
            "chat_id": "oc_test",
            "source_message_id": "om_source",
            "task": self.tasks()[0],
            "turn_id": "turn-1",
            "status": "运行中",
            "outcome": "running",
            "started_at": time.time(),
            "attachment_count": 0,
            "cancel_event": threading.Event(),
            "cancel_confirmed": threading.Event(),
            "ipc_connection": object(),
            "ipc_send_lock": threading.Lock(),
            "ipc_pending_lock": threading.RLock(),
            "ipc_pending": {},
        }
        self.bridge.register_active_run(run)
        self.bridge.patch_card = lambda *args, **kwargs: True
        called = []

        def interrupt(run):
            called.append((run["task"]["id"], run["turn_id"]))
            return True

        self.bridge.interrupt_desktop_turn = interrupt
        self.bridge.handle_card_event(
            {
                "type": "card.action.trigger",
                "event_id": "evt-stop",
                "operator_id": "ou_admin",
                "chat_id": "oc_test",
                "action_tag": "button",
                "action_value": json.dumps({"action": "stop_run", "run_id": "run-1"}),
            }
        )
        for _ in range(100):
            if called:
                break
            time.sleep(0.01)

        self.assertTrue(run["cancel_event"].is_set())
        self.assertTrue(run["cancel_confirmed"].is_set())
        self.assertEqual(called, [("task-a", "turn-1")])

    def test_stop_button_is_secondary_and_requires_confirmation(self):
        card = self.bridge.build_run_card(
            {
                "run_id": "run-1",
                "task": self.tasks()[0],
                "status": "运行中",
                "outcome": "running",
                "started_at": time.time(),
                "attachment_count": 0,
            }
        )
        button = next(
            item for item in card["body"]["elements"] if item.get("tag") == "button"
        )

        self.assertEqual(button["text"]["content"], "停止运行…")
        self.assertEqual(button["type"], "default")
        self.assertNotIn("width", button)
        self.assertIn("confirm", button)
        self.assertEqual(button["confirm"]["title"]["content"], "确认停止当前运行？")

    def test_approval_decisions_use_desktop_protocol(self):
        calls = []
        self.bridge.send_run_ipc_request = lambda run, method, version, params: calls.append(
            (method, version, params)
        ) or {"resultType": "success"}
        run = {"task": self.tasks()[0]}

        self.assertTrue(
            self.bridge.respond_desktop_approval(
                run,
                {"type": "command", "request_id": "req-1"},
                True,
            )
        )
        self.assertEqual(calls[0][0], "thread-follower-command-approval-decision")
        self.assertEqual(calls[0][1], 1)
        self.assertEqual(calls[0][2]["decision"], "accept")

    def test_interrupt_uses_expected_turn_guard(self):
        calls = []
        self.bridge.send_run_ipc_request = lambda run, method, version, params: calls.append(
            (method, version, params)
        ) or {"resultType": "success"}
        run = {"task": self.tasks()[0], "turn_id": "turn-1"}

        self.assertTrue(self.bridge.interrupt_desktop_turn(run))
        self.assertEqual(calls[0][0], "thread-follower-interrupt-turn")
        self.assertEqual(calls[0][1], 4)
        self.assertEqual(calls[0][2]["mode"], "user-stop")
        self.assertEqual(calls[0][2]["expectedTurnId"], "turn-1")

    def test_state_updates_from_workers_do_not_overwrite_each_other(self):
        barrier = threading.Barrier(3)

        def queue_reply():
            barrier.wait()
            self.bridge.queue_pending_reply("om_test", "结果", "final", "网络失败")

        def remember_turn():
            barrier.wait()
            self.bridge.remember_bridge_turn("turn-1")

        workers = [
            threading.Thread(target=queue_reply),
            threading.Thread(target=remember_turn),
        ]
        for worker in workers:
            worker.start()
        barrier.wait()
        for worker in workers:
            worker.join()

        state = self.bridge.load_state()
        self.assertEqual(state["bridge_turns"], ["turn-1"])
        self.assertEqual(state["pending_replies"][0]["message_id"], "om_test")

    def test_progress_card_patch_uses_message_patch_api(self):
        completed = subprocess.CompletedProcess([], 0, '{"ok":true}', "")
        with mock.patch.object(self.bridge.subprocess, "run", return_value=completed) as run:
            self.assertTrue(self.bridge.patch_card("om_progress", {"schema": "2.0"}))

        command = run.call_args.args[0]
        self.assertEqual(command[command.index("--message-id") + 1], "om_progress")
        payload = json.loads(command[command.index("--data") + 1])
        self.assertEqual(json.loads(payload["content"])["schema"], "2.0")

    def test_follower_actions_share_the_turn_owner_connection(self):
        client, server = self.bridge.socket.socketpair()
        rollout = Path(self.temporary.name) / "rollout.jsonl"
        rollout.write_text("", encoding="utf-8")
        run = {
            "task": self.tasks()[0],
            "ipc_connection": client,
            "ipc_client_id": "owner-client",
            "ipc_send_lock": threading.Lock(),
            "ipc_pending_lock": threading.RLock(),
            "ipc_pending": {},
        }
        result = []

        def request():
            result.append(
                self.bridge.send_run_ipc_request(
                    run,
                    "thread-follower-interrupt-turn",
                    4,
                    {
                        "conversationId": "task-a",
                        "mode": "user-stop",
                        "expectedTurnId": "turn-1",
                    },
                    2000,
                )
            )

        def desktop():
            request_frame = self.bridge.receive_ipc_message(server)
            self.bridge.send_ipc_message(
                server,
                {
                    "type": "response",
                    "requestId": request_frame["requestId"],
                    "resultType": "success",
                },
            )
            with rollout.open("a", encoding="utf-8") as handle:
                handle.write(
                    json.dumps(
                        {
                            "type": "event_msg",
                            "payload": {
                                "type": "task_complete",
                                "turn_id": "turn-1",
                                "last_agent_message": "完成",
                            },
                        }
                    )
                    + "\n"
                )

        requester = threading.Thread(target=request)
        desktop_side = threading.Thread(target=desktop)
        requester.start()
        desktop_side.start()
        try:
            success, message, _ = self.bridge.wait_for_desktop_turn(
                rollout,
                0,
                "turn-1",
                ipc_connection=client,
                client_id="owner-client",
                on_ipc_response=lambda response: self.bridge.complete_run_ipc_response(
                    run,
                    response,
                ),
            )
        finally:
            requester.join()
            desktop_side.join()
            client.close()
            server.close()

        self.assertTrue(success)
        self.assertEqual(message, "完成")
        self.assertEqual(result[0]["resultType"], "success")

    def test_following_requests_complete_history_for_desktop_read_only_view(self):
        client, server = self.bridge.socket.socketpair()
        try:
            self.bridge.begin_desktop_following(client, "bridge-client", "task-a")

            following = self.bridge.receive_ipc_message(server)
            history = self.bridge.receive_ipc_message(server)
        finally:
            client.close()
            server.close()

        self.assertEqual(following["method"], "thread-stream-following-changed")
        self.assertTrue(following["params"]["following"])
        self.assertEqual(history["method"], "thread-follower-load-complete-history")
        self.assertEqual(history["params"]["conversationId"], "task-a")


if __name__ == "__main__":
    unittest.main()

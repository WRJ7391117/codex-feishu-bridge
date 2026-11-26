import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
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
        spec = importlib.util.spec_from_file_location("bridge_runtime_test", BRIDGE_PATH)
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


class RuntimeCompatibilityTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.bridge.STATE_PATH = Path(directory.name) / "state.json"
        self.bridge.log = mock.Mock()

    def test_corrupt_or_insecure_bridge_state_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.STATE_PATH = Path(directory) / "state.json"
            self.bridge.STATE_PATH.write_text('{"selected":', encoding="utf-8")
            self.bridge.STATE_PATH.chmod(0o600)
            original = self.bridge.STATE_PATH.read_bytes()

            with self.assertRaises(RuntimeError):
                self.bridge.load_state()
            self.assertEqual(self.bridge.STATE_PATH.read_bytes(), original)

            self.bridge.STATE_PATH.write_text("{}\n", encoding="utf-8")
            self.bridge.STATE_PATH.chmod(0o644)
            with self.assertRaises(RuntimeError):
                self.bridge.load_state()

    def test_missing_bridge_state_still_uses_initial_empty_state(self):
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.STATE_PATH = Path(directory) / "state.json"
            self.assertEqual(
                self.bridge.load_state(),
                {
                    "selected": {},
                    "last_lists": {},
                    "authorized_chats": {},
                    "processed": [],
                    "bridge_turns": [],
                },
            )

    def test_any_consumer_exit_requests_parent_restart(self):
        running = mock.Mock()
        running.poll.return_value = None
        clean_exit = mock.Mock()
        clean_exit.poll.return_value = 0
        failed_exit = mock.Mock()
        failed_exit.poll.return_value = 7

        self.assertIsNone(self.bridge.event_consumer_exit_code([running, running]))
        for position in range(3):
            consumers = [running, running, running]
            consumers[position] = clean_exit
            self.assertEqual(
                self.bridge.event_consumer_exit_code(consumers),
                1,
            )
        self.assertEqual(
            self.bridge.event_consumer_exit_code([running, failed_exit]),
            7,
        )

    def executable(self, directory: str, name: str) -> str:
        path = Path(directory) / name
        path.write_text("#!/bin/sh\n", encoding="utf-8")
        path.chmod(0o755)
        return str(path)

    def rollout(self, directory: str, cli_version: str = "0.148.0") -> Path:
        path = Path(directory) / "rollout.jsonl"
        path.write_text(
            json.dumps(
                {
                    "type": "session_meta",
                    "payload": {"cli_version": cli_version},
                }
            )
            + "\n",
            encoding="utf-8",
        )
        return path

    def test_preferred_desktop_path_wins_over_path_lookup(self):
        with tempfile.TemporaryDirectory() as directory:
            desktop = self.executable(directory, "desktop-codex")
            with mock.patch.object(self.bridge.shutil, "which", return_value="/path/codex"):
                selected = self.bridge.find_executable(
                    "codex_cli_path",
                    ("codex",),
                    (desktop,),
                    prefer_paths=True,
                )
        self.assertEqual(selected, desktop)

    def test_explicit_configuration_wins_over_desktop_path(self):
        with tempfile.TemporaryDirectory() as directory:
            configured = self.executable(directory, "configured-codex")
            desktop = self.executable(directory, "desktop-codex")
            self.bridge.CONFIG = {"codex_cli_path": configured}
            selected = self.bridge.find_executable(
                "codex_cli_path",
                ("codex",),
                (desktop,),
                prefer_paths=True,
            )
        self.assertEqual(selected, configured)

    def test_codex_cli_search_paths_include_current_desktop_bundle_layout(self):
        self.assertIn(
            "/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex",
            self.bridge.CODEX_CLI_SEARCH_PATHS,
        )
        self.assertIn(
            "/Applications/Codex.app/Contents/Resources/codex-cli/bin/codex",
            self.bridge.CODEX_CLI_SEARCH_PATHS,
        )

    def test_missing_cached_codex_cli_path_is_resolved_again(self):
        with tempfile.TemporaryDirectory() as directory:
            replacement = self.executable(directory, "replacement-codex")
            self.bridge.CODEX_CLI = str(Path(directory) / "removed-codex")
            with mock.patch.object(
                self.bridge,
                "find_executable",
                return_value=replacement,
            ) as find_executable:
                selected = self.bridge.codex_cli_executable()

        self.assertEqual(selected, replacement)
        self.assertEqual(self.bridge.CODEX_CLI, replacement)
        find_executable.assert_called_once_with(
            "codex_cli_path",
            ("codex",),
            self.bridge.CODEX_CLI_SEARCH_PATHS,
            prefer_paths=True,
        )

    def test_version_tuple_accepts_cli_output_with_prerelease(self):
        self.assertEqual(
            self.bridge.version_tuple("codex-cli 0.148.0-alpha.21"),
            (0, 148, 0),
        )

    def test_older_cli_is_blocked(self):
        with tempfile.TemporaryDirectory() as directory:
            rollout = self.rollout(directory, "0.148.0-alpha.21")
            with mock.patch.object(
                self.bridge,
                "executable_version",
                return_value="codex-cli 0.135.0",
            ):
                allowed, message = self.bridge.cli_resume_preflight(rollout)
        self.assertFalse(allowed)
        self.assertIn("版本低于", message)

    def test_same_or_newer_cli_is_allowed(self):
        for version in ("codex-cli 0.148.0", "codex-cli 0.149.0"):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as directory:
                rollout = self.rollout(directory, "0.148.0-alpha.21")
                with mock.patch.object(
                    self.bridge,
                    "executable_version",
                    return_value=version,
                ):
                    self.assertEqual(
                        self.bridge.cli_resume_preflight(rollout),
                        (True, ""),
                    )

    def test_missing_or_invalid_session_metadata_is_blocked(self):
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "missing.jsonl"
            invalid = Path(directory) / "invalid.jsonl"
            missing.write_text("", encoding="utf-8")
            invalid.write_text("[]\n", encoding="utf-8")
            for rollout in (missing, invalid):
                with self.subTest(rollout=rollout):
                    allowed, message = self.bridge.cli_resume_preflight(rollout)
                    self.assertFalse(allowed)
                    self.assertIn("无需删除或重新选择", message)

    def test_run_codex_does_not_silently_invoke_cli(self):
        self.bridge.run_codex_via_desktop = lambda *args, **kwargs: (
            "unavailable",
            "no-client-found",
            [],
        )
        with mock.patch.object(self.bridge.subprocess, "Popen") as popen:
            with self.assertRaises(self.bridge.DesktopUnavailableError) as raised:
                self.bridge.run_codex("task-id", "hello")
        self.assertEqual(raised.exception.reason, "no-client-found")
        self.assertIn("尚未提交", str(raised.exception))
        popen.assert_not_called()

    def test_stale_desktop_socket_is_not_reported_as_connected(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        connection.connect.side_effect = ConnectionRefusedError(61, "Connection refused")
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.DESKTOP_IPC_SOCKET = Path(directory) / "ipc.sock"
            self.bridge.DESKTOP_IPC_SOCKET.touch()
            with mock.patch.object(self.bridge.socket, "socket", return_value=connection):
                status = self.bridge.desktop_connection_status()

        self.assertEqual(status, {"available": False, "reason": "ipc-not-listening"})

    def test_desktop_diagnostic_requires_successful_handshake(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.DESKTOP_IPC_SOCKET = Path(directory) / "ipc.sock"
            self.bridge.DESKTOP_IPC_SOCKET.touch()
            with mock.patch.object(
                self.bridge.socket, "socket", return_value=connection
            ), mock.patch.object(
                self.bridge, "initialize_desktop_connection", return_value="client"
            ) as initialize:
                status = self.bridge.desktop_connection_status()

        self.assertEqual(status, {"available": True, "reason": ""})
        initialize.assert_called_once_with(connection)

    def test_desktop_diagnostic_rejects_failed_handshake(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.DESKTOP_IPC_SOCKET = Path(directory) / "ipc.sock"
            self.bridge.DESKTOP_IPC_SOCKET.touch()
            with mock.patch.object(
                self.bridge.socket, "socket", return_value=connection
            ), mock.patch.object(
                self.bridge, "initialize_desktop_connection", side_effect=RuntimeError
            ):
                status = self.bridge.desktop_connection_status()

        self.assertEqual(status, {"available": False, "reason": "ipc-initialize-failed"})

    def test_diagnostic_reports_disconnected_desktop_separately_from_local_setup(self):
        with mock.patch.object(
            self.bridge,
            "desktop_connection_status",
            return_value={"available": False, "reason": "ipc-not-listening"},
            create=True,
        ) as probe:
            static = self.bridge.diagnostic_report()
            probe.assert_not_called()
            with mock.patch.object(self.bridge, "codex_app_server_client", side_effect=RuntimeError):
                report = self.bridge.diagnostic_report(check_desktop=True)

        self.assertNotIn("codex_connection", static["checks"])
        self.assertFalse(report["ok"])
        self.assertFalse(report["checks"]["codex_connection"])
        self.assertEqual(report["desktop"]["reason"], "ipc-not-listening")

    def test_connection_refusal_does_not_submit_or_offer_pointless_automatic_retries(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        connection.connect.side_effect = ConnectionRefusedError(61, "Connection refused")
        self.bridge.rollout_path_for_task = lambda thread_id: None
        self.bridge.database_task_exists = lambda thread_id: True
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.DESKTOP_IPC_SOCKET = Path(directory) / "ipc.sock"
            self.bridge.DESKTOP_IPC_SOCKET.touch()
            with mock.patch.object(
                self.bridge.socket, "socket", return_value=connection
            ), mock.patch.object(
                self.bridge, "send_ipc_message"
            ) as send, mock.patch.object(self.bridge.time, "sleep") as sleep:
                result = self.bridge.run_codex_via_desktop("task-id", "hello")

        self.assertEqual(result, ("unavailable", "ipc-not-listening", []))
        connection.connect.assert_called_once()
        send.assert_not_called()
        sleep.assert_not_called()

    def test_connection_error_after_submission_never_becomes_retryable(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        self.bridge.rollout_path_for_task = lambda thread_id: None
        self.bridge.database_task_exists = lambda thread_id: True
        self.bridge.task_working_directory = lambda thread_id: ""
        with tempfile.TemporaryDirectory() as directory:
            self.bridge.DESKTOP_IPC_SOCKET = Path(directory) / "ipc.sock"
            self.bridge.DESKTOP_IPC_SOCKET.touch()
            with mock.patch.object(
                self.bridge.socket, "socket", return_value=connection
            ), mock.patch.object(
                self.bridge, "initialize_desktop_connection", return_value="client"
            ), mock.patch.object(
                self.bridge, "send_ipc_message", side_effect=ConnectionRefusedError
            ), mock.patch.object(self.bridge.time, "sleep") as sleep:
                result = self.bridge.run_codex("task-id", "hello")

        self.assertFalse(result[0])
        self.assertIn("不会再次提交", result[1])
        connection.connect.assert_called_once()
        sleep.assert_not_called()

    def test_no_client_found_activates_task_then_submits_once(self):
        self.bridge.run_codex_via_desktop = mock.Mock(
            side_effect=(
                ("unavailable", "no-client-found", []),
                ("completed", "done", []),
            )
        )
        self.bridge.activate_desktop_task = mock.Mock(return_value=True)

        with mock.patch.object(self.bridge.time, "sleep"):
            result = self.bridge.run_codex(
                "00000000-0000-0000-0000-000000000001",
                "hello",
            )

        self.assertEqual(result, (True, "done", []))
        self.assertEqual(self.bridge.run_codex_via_desktop.call_count, 2)
        self.bridge.activate_desktop_task.assert_called_once_with(
            "00000000-0000-0000-0000-000000000001"
        )

    def test_failed_task_activation_falls_back_to_manual_choice(self):
        self.bridge.run_codex_via_desktop = mock.Mock(
            return_value=("unavailable", "no-client-found", [])
        )
        self.bridge.activate_desktop_task = mock.Mock(return_value=False)

        with self.assertRaises(self.bridge.DesktopUnavailableError) as raised:
            self.bridge.run_codex(
                "00000000-0000-0000-0000-000000000001",
                "hello",
            )

        self.assertEqual(raised.exception.reason, "no-client-found")
        self.bridge.run_codex_via_desktop.assert_called_once()
        self.bridge.activate_desktop_task.assert_called_once()

    def test_task_activation_uses_deep_link_and_restores_previous_app(self):
        completed = subprocess.CompletedProcess([], 0, "com.apple.finder\n", "")
        opened = subprocess.CompletedProcess([], 0, "", "")
        restored = subprocess.CompletedProcess([], 0, "", "")
        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            side_effect=(completed, opened, restored),
        ) as run, mock.patch.object(self.bridge.time, "sleep") as sleep:
            activated = self.bridge.activate_desktop_task(
                "00000000-0000-0000-0000-000000000001"
            )

        self.assertTrue(activated)
        self.assertEqual(
            run.call_args_list[1].args[0],
            [
                "/usr/bin/open",
                "-g",
                "codex://threads/00000000-0000-0000-0000-000000000001",
            ],
        )
        self.assertEqual(
            run.call_args_list[2].args[0],
            ["/usr/bin/open", "-b", "com.apple.finder"],
        )
        sleep.assert_called_once()

    def test_explicit_cli_fallback_skips_desktop(self):
        self.bridge.run_codex_via_desktop = mock.Mock()
        self.bridge.run_codex_via_app_server = mock.Mock(return_value=(False, "blocked", []))

        success, message, images = self.bridge.run_codex(
            "task-id",
            "hello",
            use_cli_fallback=True,
        )

        self.assertFalse(success)
        self.assertEqual(message, "blocked")
        self.assertEqual(images, [])
        self.bridge.run_codex_via_desktop.assert_not_called()

    def test_event_lane_preserves_same_user_order(self):
        handled = []
        completed = threading.Event()

        def handle(event):
            handled.append(event["sequence"])
            if len(handled) == 2:
                completed.set()

        with mock.patch.object(self.bridge, "dispatch_event", side_effect=handle), mock.patch.object(
            self.bridge,
            "acknowledge_workflow_decision_inbox",
        ):
            self.bridge.submit_event({"operator_id": "ou_same", "sequence": 1})
            self.bridge.submit_event({"operator_id": "ou_same", "sequence": 2})
            self.assertTrue(completed.wait(1))

        self.assertEqual(handled, [1, 2])

    def test_latest_ui_intent_supersedes_an_older_matching_control(self):
        first = {
            "type": "card.action.trigger",
            "operator_id": "ou_same",
            "message_id": "om_card",
            "action_tag": "select_static",
            "action_name": "project_selector",
        }
        second = dict(first)

        self.bridge.register_ui_intent(first)
        self.bridge.register_ui_intent(second)

        self.assertFalse(self.bridge.ui_intent_is_current(first))
        self.assertTrue(self.bridge.ui_intent_is_current(second))

    def test_project_and_task_selectors_have_independent_intents(self):
        project = {
            "type": "card.action.trigger",
            "operator_id": "ou_same",
            "message_id": "om_card",
            "action_tag": "select_static",
            "action_name": "project_selector",
        }
        task = {**project, "action_name": "task_selector"}

        self.bridge.register_ui_intent(project)
        self.bridge.register_ui_intent(task)

        self.assertTrue(self.bridge.ui_intent_is_current(project))
        self.assertTrue(self.bridge.ui_intent_is_current(task))

    def test_slow_user_lane_does_not_block_another_user(self):
        slow_started = threading.Event()
        release_slow = threading.Event()
        fast_completed = threading.Event()

        def handle(event):
            if event["operator_id"] == "ou_slow":
                slow_started.set()
                release_slow.wait(1)
            else:
                fast_completed.set()

        with mock.patch.object(self.bridge, "dispatch_event", side_effect=handle), mock.patch.object(
            self.bridge,
            "acknowledge_workflow_decision_inbox",
        ):
            self.bridge.submit_event({"operator_id": "ou_slow"})
            self.assertTrue(slow_started.wait(1))
            self.bridge.submit_event({"operator_id": "ou_fast"})
            self.assertTrue(fast_completed.wait(0.5))
            release_slow.set()


if __name__ == "__main__":
    unittest.main()

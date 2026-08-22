import importlib.util
import json
import os
from pathlib import Path
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

    def test_thread_store_failure_has_actionable_message(self):
        message = self.bridge.codex_resume_failure_message(
            "thread-store internal error: failed to read thread"
        )
        self.assertIn("打开 Codex Desktop", message)
        self.assertIn("无需重新选择", message)

    def test_run_codex_does_not_invoke_incompatible_cli(self):
        self.bridge.run_codex_via_desktop = lambda *args, **kwargs: (
            "unavailable",
            "",
            [],
        )
        self.bridge.rollout_path_for_task = lambda thread_id: None
        with mock.patch.object(self.bridge.subprocess, "run") as run:
            success, message, images = self.bridge.run_codex("task-id", "hello")
        self.assertFalse(success)
        self.assertIn("打开 Codex Desktop", message)
        self.assertEqual(images, [])
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()

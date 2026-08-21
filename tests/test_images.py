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
        spec = importlib.util.spec_from_file_location("bridge_images_test", BRIDGE_PATH)
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


class ImageReplyTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()

    def test_extracts_local_and_remote_markdown_images(self):
        with tempfile.TemporaryDirectory() as directory:
            local = Path(directory) / "image with space.png"
            local.write_bytes(b"png")
            text = (
                f"本地 ![预览](<{local}>)\n"
                "远程 ![图](https://example.com/a.jpg?size=2)\n"
                f"重复 ![图](<{local}>)"
            )

            clean, images = self.bridge.extract_result_images(text)

            self.assertEqual(clean.count("图片见下方"), 3)
            self.assertEqual(
                images,
                [str(local.resolve()), "https://example.com/a.jpg?size=2"],
            )

    def test_missing_or_unsupported_local_image_is_not_sent(self):
        clean, images = self.bridge.extract_result_images(
            "![缺失](/tmp/does-not-exist.png) ![文本](/tmp/readme.txt)"
        )

        self.assertEqual(clean, "图片不可用 图片不可用")
        self.assertEqual(images, [])

    def test_zero_image_limit_disables_sending(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "generated.png"
            image.write_bytes(b"png")
            self.bridge.MAX_RESULT_IMAGES = 0

            clean, images = self.bridge.prepare_result_images("完成", [str(image)])

            self.assertEqual(clean, "完成")
            self.assertEqual(images, [])

    def test_rollout_image_event_does_not_need_turn_id(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "generated.png"
            image.write_bytes(b"png")
            rollout = Path(directory) / "rollout.jsonl"
            records = [
                {
                    "type": "event_msg",
                    "payload": {
                        "type": "image_generation_end",
                        "saved_path": str(image),
                        "status": "completed",
                    },
                },
                {
                    "type": "event_msg",
                    "payload": {
                        "type": "task_complete",
                        "turn_id": "turn-1",
                        "last_agent_message": "完成",
                    },
                },
            ]
            rollout.write_text(
                "".join(json.dumps(record) + "\n" for record in records),
                encoding="utf-8",
            )

            success, message, images = self.bridge.wait_for_desktop_turn(
                rollout,
                0,
                "turn-1",
            )

            self.assertTrue(success)
            self.assertEqual(message, "完成")
            self.assertEqual(images, [str(image.resolve())])

    def test_local_image_reply_uses_relative_path_and_parent_cwd(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "photo.png"
            image.write_bytes(b"png")
            completed = subprocess.CompletedProcess([], 0, '{"ok":true}', "")

            with mock.patch.object(
                self.bridge.subprocess,
                "run",
                return_value=completed,
            ) as run:
                self.assertTrue(self.bridge.reply_image("om_test", str(image), 1))

            command = run.call_args.args[0]
            self.assertEqual(command[command.index("--image") + 1], "./photo.png")
            self.assertEqual(run.call_args.kwargs["cwd"], image.parent.resolve())

    def test_remote_image_reply_passes_url_directly(self):
        completed = subprocess.CompletedProcess([], 0, '{"ok":true}', "")
        url = "https://example.com/photo.webp"

        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=completed,
        ) as run:
            self.assertTrue(self.bridge.reply_image("om_test", url, 2))

        command = run.call_args.args[0]
        self.assertEqual(command[command.index("--image") + 1], url)
        self.assertIsNone(run.call_args.kwargs["cwd"])


if __name__ == "__main__":
    unittest.main()

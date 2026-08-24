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

    def test_extracts_only_supported_local_result_files(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "测试报告.pdf"
            report.write_bytes(b"pdf")
            text = (
                f"报告：[下载](<{report}>)\n"
                "网页：[说明](https://example.com/help)\n"
                "缺失：[文件](/tmp/missing.docx)"
            )

            clean, files = self.bridge.prepare_result_files(text)

            self.assertIn("文件见下方：下载", clean)
            self.assertIn("[说明](https://example.com/help)", clean)
            self.assertIn("[文件](/tmp/missing.docx)", clean)
            self.assertEqual(files, [str(report.resolve())])

    def test_local_file_reply_uses_relative_path_and_parent_cwd(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "report.xlsx"
            report.write_bytes(b"xlsx")
            completed = subprocess.CompletedProcess([], 0, '{"ok":true}', "")

            with mock.patch.object(
                self.bridge.subprocess,
                "run",
                return_value=completed,
            ) as run:
                self.assertTrue(self.bridge.reply_file("om_test", str(report), 1))

            command = run.call_args.args[0]
            self.assertEqual(command[command.index("--file") + 1], "./report.xlsx")
            self.assertEqual(run.call_args.kwargs["cwd"], report.parent.resolve())

    def test_extracts_input_image_keys_from_event_formats(self):
        self.assertEqual(
            self.bridge.input_image_keys('{"image_key":"img_v3_abc-123"}'),
            ["img_v3_abc-123"],
        )
        self.assertEqual(
            self.bridge.input_image_keys(
                "说明 ![Image](img_v3_first) 和 [Image: img_v3_second] "
                "以及重复 ![图](img_v3_first)"
            ),
            ["img_v3_first", "img_v3_second"],
        )

    def test_input_prompt_removes_resource_markers(self):
        keys = ["img_v3_first"]
        self.assertEqual(
            self.bridge.input_prompt("请分析 ![Image](img_v3_first)", keys),
            "请分析",
        )
        self.assertEqual(
            self.bridge.input_prompt('{"image_key":"img_v3_first"}', keys),
            "用户从飞书发送了以下图片。",
        )

    def test_downloaded_image_is_validated_and_normalized(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            downloaded = root / "download-1"
            downloaded.write_bytes(b"\x89PNG\r\n\x1a\n" + b"test")
            stdout = json.dumps(
                {"ok": True, "data": {"saved_path": "download-1"}}
            )

            image, error = self.bridge.downloaded_image_path(stdout, root, 1)

            self.assertEqual(error, "")
            self.assertEqual(image, (root / "input-1.png").resolve())
            self.assertTrue(image.is_file())

    def test_downloaded_image_cannot_escape_temporary_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            outside = root.parent / "outside.png"
            outside.write_bytes(b"\x89PNG\r\n\x1a\n" + b"test")
            try:
                image, error = self.bridge.downloaded_image_path(
                    json.dumps(
                        {"ok": True, "data": {"saved_path": str(outside)}}
                    ),
                    root,
                    1,
                )
            finally:
                outside.unlink(missing_ok=True)

            self.assertIsNone(image)
            self.assertIn("不安全", error)

    def test_download_input_image_uses_matching_message_and_bot_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def complete(command, **kwargs):
                output = root / command[command.index("--output") + 1]
                output.write_bytes(b"\xff\xd8\xff" + b"test")
                return subprocess.CompletedProcess(
                    command,
                    0,
                    json.dumps(
                        {"ok": True, "data": {"saved_path": str(output)}}
                    ),
                    "",
                )

            with mock.patch.object(
                self.bridge.subprocess,
                "run",
                side_effect=complete,
            ) as run:
                image, error = self.bridge.download_input_image(
                    "om_test",
                    "img_v3_test",
                    root,
                    1,
                )

            self.assertEqual(error, "")
            self.assertEqual(image, (root / "input-1.jpg").resolve())
            command = run.call_args.args[0]
            self.assertEqual(command[command.index("--message-id") + 1], "om_test")
            self.assertEqual(command[command.index("--file-key") + 1], "img_v3_test")
            self.assertEqual(command[command.index("--as") + 1], "bot")
            self.assertEqual(run.call_args.kwargs["cwd"], root)

    def test_non_retryable_download_permission_error_is_not_retried(self):
        failure = subprocess.CompletedProcess(
            [],
            1,
            "",
            json.dumps(
                {
                    "ok": False,
                    "error": {
                        "type": "authorization",
                        "subtype": "missing_scope",
                        "missing_scopes": ["message-resource-read"],
                    },
                }
            ),
        )
        with tempfile.TemporaryDirectory() as directory, mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=failure,
        ) as run:
            image, error = self.bridge.download_input_image(
                "om_test",
                "img_v3_test",
                Path(directory),
                1,
            )

        self.assertIsNone(image)
        self.assertIn("权限", error)
        self.assertEqual(run.call_count, 1)

    def test_codex_desktop_input_contains_local_images(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "input.png"
            image.write_bytes(b"png")

            items = self.bridge.codex_turn_input("请分析", [str(image)])

            self.assertEqual(items[0]["type"], "text")
            self.assertEqual(
                items[1],
                {"type": "localImage", "path": str(image.resolve())},
            )

    def test_codex_cli_fallback_receives_image_argument(self):
        with tempfile.TemporaryDirectory() as directory:
            image = Path(directory) / "input.png"
            image.write_bytes(b"png")
            self.bridge.run_codex_via_desktop = lambda *args, **kwargs: (
                "unavailable",
                "",
                [],
            )
            self.bridge.rollout_path_for_task = lambda thread_id: None
            self.bridge.cli_resume_preflight = lambda rollout: (True, "")

            class Process:
                returncode = 0

                def __init__(self, command):
                    self.command = command

                def communicate(self, **kwargs):
                    output = Path(
                        self.command[
                            self.command.index("--output-last-message") + 1
                        ]
                    )
                    output.write_text("完成", encoding="utf-8")
                    return "", ""

                def terminate(self):
                    pass

                def kill(self):
                    pass

            with mock.patch.object(
                self.bridge.subprocess,
                "Popen",
                side_effect=lambda command, **kwargs: Process(command),
            ) as popen:
                success, message, images = self.bridge.run_codex(
                    "task-id",
                    "请分析",
                    input_images=[str(image)],
                    use_cli_fallback=True,
                )

            self.assertTrue(success)
            self.assertEqual(message, "完成")
            self.assertEqual(images, [])
            command = popen.call_args.args[0]
            self.assertEqual(command[command.index("--image") + 1], str(image.resolve()))

    def test_image_event_reaches_selected_task_and_temporary_file_is_cleaned(self):
        self.bridge.ALLOWED_USERS.clear()
        self.bridge.ALLOWED_USERS["ou_admin"] = {"*"}
        self.bridge.load_state = lambda: {}
        self.bridge.save_state = lambda state: None
        self.bridge.selected_task = lambda user_id, state: {
            "id": "task-id",
            "title": "Task",
            "project": "Project",
        }
        replies = []
        self.bridge.reply = lambda message_id, text, kind: replies.append(
            (kind, text)
        ) or True
        self.bridge.reply_or_queue = self.bridge.reply
        self.bridge.reply_card_message = lambda *args, **kwargs: (True, "om_progress")
        self.bridge.patch_card = lambda *args, **kwargs: True
        downloaded = []

        def download(message_id, image_key, directory, index):
            path = directory / "input-1.png"
            path.write_bytes(b"\x89PNG\r\n\x1a\n" + b"test")
            downloaded.append(path)
            return path, ""

        def run(thread_id, prompt, **kwargs):
            self.assertEqual(thread_id, "task-id")
            self.assertEqual(prompt, "用户从飞书发送了以下图片。")
            self.assertEqual(kwargs["input_images"], [str(downloaded[0])])
            self.assertTrue(downloaded[0].is_file())
            kwargs["on_started"]("正在运行")
            return True, "图片已收到", []

        self.bridge.download_input_image = download
        self.bridge.run_codex = run

        self.bridge.handle_message_event(
            {
                "chat_id": "oc_test",
                "chat_type": "p2p",
                "sender_id": "ou_admin",
                "sender_type": "user",
                "message_id": "om_test",
                "message_type": "image",
                "content": '{"image_key":"img_v3_test"}',
            }
        )

        for _ in range(100):
            if any(kind == "final" for kind, _ in replies):
                break
            self.bridge.time.sleep(0.01)

        self.assertEqual([kind for kind, _ in replies], ["final"])
        self.assertIn("状态：已完成", replies[-1][1])
        self.assertFalse(downloaded[0].exists())


if __name__ == "__main__":
    unittest.main()

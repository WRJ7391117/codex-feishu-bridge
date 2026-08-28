import importlib.util
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
        spec = importlib.util.spec_from_file_location("bridge_audio_test", BRIDGE_PATH)
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


class AudioReplyTests(unittest.TestCase):
    def setUp(self):
        self.bridge = load_bridge()
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.bridge.STATE_PATH = self.root / "state.json"
        self.bridge.LOG_PATH = self.root / "bridge.log"

    def tearDown(self):
        self.temporary.cleanup()

    def audio(self, name: str, content: bytes = b"audio") -> Path:
        path = self.root / name
        path.write_bytes(content)
        return path

    def test_audio_is_extracted_before_document_files_without_duplication(self):
        opus = self.audio("voice.opus", b"OggS\x00OpusHead\x01")
        mp3 = self.audio("music.mp3")
        report = self.audio("report.pdf", b"pdf")
        text = (
            f"![语音](<{opus}>) [音乐](<{mp3}>) "
            f"[重复](<{opus}>) [报告](<{report}>)"
        )

        allowed_roots = (self.root.resolve(),)
        clean, audio_files = self.bridge.prepare_result_audio(text, allowed_roots)
        clean, files = self.bridge.prepare_result_files(clean, allowed_roots)

        self.assertEqual(audio_files, [str(opus.resolve()), str(mp3.resolve())])
        self.assertEqual(files, [str(report.resolve())])
        self.assertEqual(clean.count("音频见下方"), 2)
        self.assertIn("音频附件见下方：音乐", clean)
        self.assertIn("文件见下方：报告", clean)

    def test_audio_limit_and_validation_are_enforced(self):
        first = self.audio("first.wav", b"1234")
        second = self.audio("second.m4a", b"1234")
        empty = self.audio("empty.mp3", b"")
        self.bridge.MAX_RESULT_AUDIO = 1
        self.bridge.MAX_RESULT_FILE_BYTES = 4

        clean, audio_files = self.bridge.prepare_result_audio(
            f"[一](<{first}>) [二](<{second}>) [空](<{empty}>) "
            "[相对](voice.opus) [不支持](/tmp/audio.exe)",
            (self.root.resolve(),),
        )

        self.assertEqual(audio_files, [str(first.resolve())])
        self.assertNotIn(str(first), clean)
        self.assertIn(str(second), clean)
        self.assertIn("voice.opus", clean)

    def test_native_audio_reply_uses_audio_flag_relative_path_and_unique_key(self):
        opus = self.audio("voice.opus", b"OggS\x00OpusHead\x01")
        completed = subprocess.CompletedProcess([], 0, '{"ok":true}', "")

        with mock.patch.object(
            self.bridge.subprocess,
            "run",
            return_value=completed,
        ) as run:
            self.assertTrue(self.bridge.reply_audio("om_test", str(opus), 2))

        command = run.call_args.args[0]
        self.assertEqual(command[command.index("--audio") + 1], "./voice.opus")
        self.assertNotIn("--file", command)
        self.assertEqual(run.call_args.kwargs["cwd"], opus.parent.resolve())
        key = command[command.index("--idempotency-key") + 1]
        self.assertEqual(key, self.bridge.idempotency_key("om_test", "audio-2"))

    def test_ogg_vorbis_is_sent_as_file_instead_of_native_audio(self):
        vorbis = self.audio("music.ogg", b"OggS\x00vorbis")
        self.bridge.reply_file = mock.Mock(return_value=True)

        self.assertTrue(self.bridge.reply_result_audio("om_test", str(vorbis), 1))

        self.bridge.reply_file.assert_called_once_with(
            "om_test",
            str(vorbis),
            1,
            kind_prefix="audio-file",
        )

    def test_non_native_audio_is_sent_as_attachment_with_audio_file_key(self):
        mp3 = self.audio("music.mp3")
        self.bridge.reply_file = mock.Mock(return_value=True)

        self.assertTrue(self.bridge.reply_result_audio("om_test", str(mp3), 1))

        self.bridge.reply_file.assert_called_once_with(
            "om_test",
            str(mp3),
            1,
            kind_prefix="audio-file",
        )

    def test_failed_audio_is_spooled_and_retried_by_audio_path(self):
        opus = self.audio("voice.opus")

        self.assertTrue(
            self.bridge.queue_pending_audio(
                "om_test",
                str(opus),
                1,
                "飞书 API 网络连接失败",
                now=100,
            )
        )
        pending = self.bridge.load_state()["pending_replies"]
        spooled = Path(pending[0]["file"])
        self.assertEqual(pending[0]["operation"], "audio_reply")
        self.assertNotEqual(spooled, opus)
        opus.unlink()

        with mock.patch.object(
            self.bridge,
            "reply_result_audio",
            return_value=True,
        ) as send:
            self.assertTrue(self.bridge.retry_pending_replies(now=115))

        send.assert_called_once_with("om_test", str(spooled), 1)
        self.assertEqual(self.bridge.load_state()["pending_replies"], [])
        self.assertFalse(spooled.exists())

    def test_resource_delivery_queues_audio_independently_from_files(self):
        self.bridge.reply_image = mock.Mock(return_value=True)
        self.bridge.reply_result_audio = mock.Mock(return_value=False)
        self.bridge.reply_file = mock.Mock(return_value=True)
        self.bridge.queue_pending_audio = mock.Mock(return_value=True)

        failures = self.bridge.deliver_result_resources(
            "om_test",
            ["/tmp/image.png"],
            ["/tmp/voice.opus"],
            ["/tmp/report.pdf"],
        )

        self.assertEqual(failures, (0, 1, 0))
        self.bridge.queue_pending_audio.assert_called_once_with(
            "om_test",
            "/tmp/voice.opus",
            1,
            "飞书 API 调用失败",
        )


if __name__ == "__main__":
    unittest.main()

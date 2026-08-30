import json
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Sources/PromLightHelper/main.c"


class PromLightHelperTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.helper = Path(cls.temporary.name) / "promlight-helper"
        sdk = subprocess.run(
            ["/usr/bin/xcrun", "--sdk", "macosx", "--show-sdk-path"],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        subprocess.run(
            [
                "/usr/bin/xcrun",
                "clang",
                "-Wall",
                "-Wextra",
                "-Werror",
                "-O2",
                "-isysroot",
                sdk,
                str(SOURCE),
                "-framework",
                "IOKit",
                "-framework",
                "CoreFoundation",
                "-o",
                str(cls.helper),
            ],
            check=True,
        )

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def run_helper(self, *arguments):
        return subprocess.run(
            [str(self.helper), *arguments],
            check=False,
            capture_output=True,
            text=True,
        )

    def test_four_task_states_compile_to_fixed_5e5e_frames(self):
        prefixes = {
            "idle": "5e5e0604010101fffc",
            "running": "5e5e0604020101ffff",
            "human_gate": "5e5e0604020201fffc",
            "error": "5e5e0604040201fffa",
        }
        for state, prefix in prefixes.items():
            with self.subTest(state=state):
                result = self.run_helper("frame", state)
                self.assertEqual(result.returncode, 0)
                self.assertEqual(result.stdout.strip(), prefix.ljust(128, "0"))

    def test_list_always_returns_valid_local_inventory_json(self):
        result = self.run_helper("list")
        self.assertIn(result.returncode, (0, 2))
        payload = json.loads(result.stdout)
        self.assertIsInstance(payload.get("devices"), list)
        if payload.get("ok"):
            self.assertEqual(payload.get("helper_version"), "1")

    def test_packaging_builds_and_installs_a_universal_helper(self):
        build = (ROOT / "scripts/build-app.sh").read_text(encoding="utf-8")
        installer = (ROOT / "Resources/bridge/install.sh").read_text(encoding="utf-8")
        app_source = (ROOT / "Sources/CodexFeishuBridgeApp/main.swift").read_text(
            encoding="utf-8"
        )
        self.assertIn("promlight-helper-arm64", build)
        self.assertIn("promlight-helper-x86_64", build)
        self.assertIn('promlight-helper" -verify_arch arm64 x86_64', build)
        self.assertIn('"${support_dir}/promlight-helper" 755', installer)
        self.assertIn('"promlight-helper": "promlight-helper"', app_source)

    def test_helper_uses_promlight_hid_report_id_two(self):
        source = SOURCE.read_text(encoding="utf-8")
        self.assertIn("#define REPORT_ID 2", source)
        self.assertIn("kIOHIDReportTypeOutput, REPORT_ID", source)


if __name__ == "__main__":
    unittest.main()

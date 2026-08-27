import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
APP_SOURCE = ROOT / "Sources/CodexFeishuBridgeApp/main.swift"
PRODUCT_BOUNDARY = ROOT / "docs/PRODUCTIZATION.md"
CONFIG_EXAMPLE = ROOT / "Resources/bridge/config.example.json"
UNINSTALLER = ROOT / "Resources/bridge/uninstall.sh"


class ProductOnboardingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = APP_SOURCE.read_text(encoding="utf-8")

    def test_product_boundary_uses_local_byoa_architecture(self):
        text = PRODUCT_BOUNDARY.read_text(encoding="utf-8")
        self.assertIn("BYOA (bring your own app)", text)
        self.assertIn("no Roger, DeepOri, or shared cloud relay", text)
        self.assertIn("secret never appears in process arguments", text)

    def test_secret_is_passed_to_lark_cli_through_stdin(self):
        configure = self.source.split("func configureLarkProfile", 1)[1].split(
            "func checkLarkProfile", 1
        )[0]
        self.assertIn('"--app-secret-stdin"', configure)
        self.assertIn("standardInput: appSecret", configure)
        self.assertIn("redacting: appSecret", configure)
        self.assertNotIn('"--app-secret", appSecret', configure)
        self.assertNotIn('"--app-secret",', configure)

    def test_first_launch_opens_connection_setup(self):
        launch = self.source.split("func applicationDidFinishLaunching", 1)[1]
        self.assertIn("if !model.hasConfiguredUsers", launch)
        self.assertIn("self?.model.prepareConnectionSetup()", launch)

    def test_setup_shows_required_events_and_menu_keys(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationView", 1
        )[0]
        for value in (
            "im.message.receive_v1",
            "application.bot.menu_v6",
            "card.action.trigger",
            "current_task",
            "select_task",
            "new_task",
            "archive_task",
            "codex_usage",
            "sync_desktop",
            "sync_desktop_switch",
        ):
            self.assertIn(value, setup)
        self.assertIn("不会默认开放全部项目", setup)

    def test_first_user_discovery_is_bounded_and_does_not_grant_projects(self):
        discovery = self.source.split("func discoverFeishuUser", 1)[1].split(
            "func isRunning", 1
        )[0]
        self.assertIn('"--max-events", "1"', discovery)
        self.assertIn('"--timeout", "2m"', discovery)
        self.assertIn('sender.hasPrefix("ou_")', discovery)
        authorization = self.source.split("func continueToUserAuthorization", 1)[1].split(
            "func prepareConfiguration", 1
        )[0]
        self.assertIn('projects: ""', authorization)
        self.assertNotIn('projects: "*"', authorization)

    def test_project_authorization_reads_codex_sidebar_without_default_wildcard(self):
        reader = self.source.split("func codexProjectNames", 1)[1].split(
            "func pendingAccessRequests", 1
        )[0]
        self.assertIn('state["local-projects"]', reader)
        configuration = self.source.split("private struct ConfigurationView", 1)[1].split(
            "private struct DiagnosisView", 1
        )[0]
        self.assertIn("从 Codex 左侧栏选择", configuration)
        self.assertIn("全部项目仍需手动输入 *", configuration)

    def test_example_config_uses_minimum_project_access_and_all_menu_keys(self):
        config = json.loads(CONFIG_EXAMPLE.read_text(encoding="utf-8"))
        self.assertEqual(config["allowed_users"][0]["allowed_projects"], [])
        self.assertEqual(
            config["desktop_sync_switch_menu_event_key"], "sync_desktop_switch"
        )

    def test_keep_data_uninstall_removes_runtime_but_preserves_local_state(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            support = home / "Library/Application Support/Codex Feishu Bridge"
            state = home / ".codex/feishu-bridge"
            support.mkdir(parents=True)
            state.mkdir(parents=True)
            (support / "config.json").write_text(
                json.dumps({"lark_profile": "codex-notify"}), encoding="utf-8"
            )
            (support / "bridge.py").write_text("runtime", encoding="utf-8")
            (support / "control.sh").write_text("runtime", encoding="utf-8")
            (state / "state.json").write_text("{}", encoding="utf-8")
            result = subprocess.run(
                ["/bin/zsh", str(UNINSTALLER), "--keep-data"],
                env={
                    **os.environ,
                    "HOME": str(home),
                    "CODEX_FEISHU_LAUNCHD_LABEL": (
                        f"com.deepori.codex-feishu-bridge.tests.{os.getpid()}"
                    ),
                },
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue((support / "config.json").is_file())
            self.assertTrue((state / "state.json").is_file())
            self.assertFalse((support / "bridge.py").exists())
            self.assertFalse((support / "control.sh").exists())

    def test_purge_requires_exact_stdin_confirmation_before_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            support = home / "Library/Application Support/Codex Feishu Bridge"
            support.mkdir(parents=True)
            config = support / "config.json"
            config.write_text("{}", encoding="utf-8")
            result = subprocess.run(
                ["/bin/zsh", str(UNINSTALLER), "--purge"],
                input="cancel\n",
                env={
                    **os.environ,
                    "HOME": str(home),
                    "CODEX_FEISHU_LAUNCHD_LABEL": (
                        f"com.deepori.codex-feishu-bridge.tests.{os.getpid()}"
                    ),
                },
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 2)
            self.assertTrue(config.is_file())

    def test_uninstaller_refuses_production_launchagent_with_overridden_home(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run(
                ["/bin/zsh", str(UNINSTALLER), "--keep-data"],
                env={**os.environ, "HOME": directory},
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 2)
            self.assertIn("overridden HOME and production label", result.stderr)

    def test_uninstaller_is_packaged_and_app_confirms_keep_data_mode(self):
        installer = (ROOT / "Resources/bridge/install.sh").read_text(encoding="utf-8")
        self.assertIn('"${resource_dir}/uninstall.sh"', installer)
        self.assertIn('"${support_dir}/uninstall.sh" 755', installer)
        self.assertIn('run(script.path, ["--keep-data"])', self.source)
        self.assertIn("移除服务并保留数据", self.source)


if __name__ == "__main__":
    unittest.main()

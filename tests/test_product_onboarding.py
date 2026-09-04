import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
APP_SOURCE = ROOT / "Sources/CodexFeishuBridgeApp/main.swift"
BOT_SETUP_FLOW = ROOT / "Sources/CodexFeishuBridgeApp/BotSetupFlow.swift"
PRODUCT_BOUNDARY = ROOT / "docs/PRODUCTIZATION.md"
CONFIG_EXAMPLE = ROOT / "Resources/bridge/config.example.json"
UNINSTALLER = ROOT / "Resources/bridge/uninstall.sh"
BRIDGE_SOURCE = ROOT / "Resources/bridge/feishu_codex_bridge.py"
INSTALLER = ROOT / "Resources/bridge/install.sh"
BUILD_SCRIPT = ROOT / "scripts/build-app.sh"
LOCAL_INSTALLER = ROOT / "scripts/install-local.sh"
PUBLIC_INSTALLER = ROOT / "skills/codex-feishu-bridge/scripts/install-latest.sh"
README = ROOT / "README.md"
INFO_PLIST = ROOT / "Resources/Info.plist"
SETUP_SKILL = ROOT / "skills/deepori-bridge-setup/SKILL.md"
SETUP_SKILL_AGENT = ROOT / "skills/deepori-bridge-setup/agents/openai.yaml"
BRIDGE_SKILL = ROOT / "skills/codex-feishu-bridge/SKILL.md"
BRIDGE_TROUBLESHOOTING = ROOT / "skills/codex-feishu-bridge/references/troubleshooting.md"
RELEASE_WORKFLOW = ROOT / ".github/workflows/release.yml"
APPCAST_SCRIPT = ROOT / "scripts/generate-appcast.sh"
PACKAGE_MANIFEST = ROOT / "Package.swift"


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

    def test_first_launch_checks_bot_without_opening_assistant(self):
        launch = self.source.split("func applicationDidFinishLaunching", 1)[1].split(
            "func applicationShouldHandleReopen", 1
        )[0]
        self.assertIn("self?.model.refreshBotConnection()", launch)
        self.assertNotIn("prepareConnectionSetup()", launch)

    def test_existing_profile_is_checked_independently_of_users(self):
        setup = self.source.split("func refreshBotConnection", 1)[1].split(
            "func prepareBotSetup", 1
        )[0]
        self.assertIn("setupUsesExistingProfile = true", setup)
        self.assertNotIn("hasConfiguredUsers", setup)
        self.assertIn("checkExistingProfile()", setup)

    def test_home_page_owns_bot_credentials_before_first_connection(self):
        main = self.source.split("private struct MainView", 1)[1].split(
            "private struct BotSetupView", 1
        )[0]
        bot_setup = self.source.split("private struct BotSetupView", 1)[1].split(
            "private enum ConnectionSetupPath", 1
        )[0]
        self.assertIn('GroupBox("飞书 Bot")', main)
        self.assertIn('Button(model.setupPassed ? "管理 Bot" : "添加 Bot")', main)
        self.assertIn('Text("App ID")', bot_setup)
        self.assertIn('SecureField("请输入 App Secret"', bot_setup)
        self.assertIn("model.completeBotSetup()", bot_setup)

    def test_bot_setup_flow_executes_expected_state_transitions(self):
        harness = r'''
@main
struct BotSetupFlowTestRunner {
    static func main() {
        var empty = BotSetupFlow()
        empty.present(continueToConnectionSetup: true, currentlyVerified: false)
        precondition(!empty.cancel(currentlyVerified: false))
        precondition(!empty.shouldContinueToConnectionSetup)

        var cancelledEdit = BotSetupFlow()
        cancelledEdit.present(
            continueToConnectionSetup: false,
            currentlyVerified: true
        )
        cancelledEdit.beginCredentialReconfiguration()
        precondition(cancelledEdit.cancel(currentlyVerified: false))
        precondition(!cancelledEdit.isEditingCredentials)

        var failedNewCredentials = BotSetupFlow()
        failedNewCredentials.present(
            continueToConnectionSetup: false,
            currentlyVerified: true
        )
        failedNewCredentials.beginCredentialReconfiguration()
        failedNewCredentials.recordCredentialWriteSucceeded()
        precondition(!failedNewCredentials.cancel(currentlyVerified: false))

        var retry = BotSetupFlow()
        retry.present(continueToConnectionSetup: true, currentlyVerified: false)
        retry.beginCredentialReconfiguration()
        retry.recordCredentialWriteSucceeded()
        precondition(retry.complete(currentlyVerified: true))
        precondition(!retry.shouldContinueToConnectionSetup)

        var blocked = BotSetupFlow()
        blocked.present(continueToConnectionSetup: true, currentlyVerified: false)
        precondition(!blocked.complete(currentlyVerified: false))
        precondition(blocked.shouldContinueToConnectionSetup)
    }
}
'''
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory)
            main = temporary / "BotSetupFlowTestRunner.swift"
            executable = temporary / "bot-setup-flow-tests"
            main.write_text(harness, encoding="utf-8")
            compiled = subprocess.run(
                ["swiftc", str(BOT_SETUP_FLOW), str(main), "-o", str(executable)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            executed = subprocess.run(
                [str(executable)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(executed.returncode, 0, executed.stderr)

    def test_manual_setup_summarizes_doctor_output_instead_of_showing_json(self):
        existing = self.source.split("func checkExistingProfile", 1)[1].split(
            "func startCredentialReconfiguration", 1
        )[0]
        configure = self.source.split("func configureProfileAndCheck", 1)[1].split(
            "func recheckProfile", 1
        )[0]
        recheck = self.source.split("func recheckProfile", 1)[1].split(
            "func openDeveloperConsole", 1
        )[0]
        for flow in (existing, configure, recheck):
            self.assertIn("connectionCheckFailureMessage", flow)
            self.assertNotIn("checked.output", flow)
        self.assertNotIn("setupUsesExistingProfile = self.setupPassed", existing)

        summary = self.source.split("private func connectionCheckFailureMessage", 1)[1].split(
            "func openDeveloperConsole", 1
        )[0]
        self.assertIn("JSONSerialization.jsonObject", summary)
        self.assertIn('unsuccessfulCheck("bot_identity")', summary)
        self.assertIn('unsuccessfulCheck("endpoint_open")', summary)
        self.assertIn("无法连接飞书身份服务", summary)
        self.assertLess(
            summary.index('unsuccessfulCheck("endpoint_open")'),
            summary.index('unsuccessfulCheck("bot_identity")'),
        )
        for marker in ("dial tcp", "lookup ", "no such host", "tls"):
            self.assertIn(marker, summary)
        self.assertNotIn("确认飞书应用已启用机器人能力", summary)
        self.assertNotIn("return result.output", summary)

        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationChecklistView", 1
        )[0]
        side_panel = setup.split("private func sidePanelHeader", 1)[1].split(
            "private func instructionRow", 1
        )[0]
        self.assertIn(".lineLimit(6)", side_panel)
        bot_setup = self.source.split("private struct BotSetupView", 1)[1].split(
            "private enum ConnectionSetupPath", 1
        )[0]
        self.assertIn("if model.setupUsesExistingProfile", bot_setup)
        self.assertIn('Button(model.isConfiguringProfile ? "正在检查…" : "重新检查")', bot_setup)
        self.assertIn("当前 Bot 需要检查", bot_setup)
        self.assertNotIn(".frame(width: 1120, height: 760)", setup)
        self.assertIn("minWidth: 820", setup)
        self.assertIn("idealHeight: 760", setup)

    def test_setup_has_explicit_close_and_reconfigure_actions(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationChecklistView", 1
        )[0]
        self.assertIn('.accessibilityLabel("关闭向导")', setup)
        self.assertIn('.help("关闭向导")', setup)
        self.assertIn('Image(systemName: "xmark.circle.fill")', setup)
        self.assertIn(".focusable(false)", setup)
        bot_setup = self.source.split("private struct BotSetupView", 1)[1].split(
            "private enum ConnectionSetupPath", 1
        )[0]
        self.assertIn("当前 Bot 已连接", bot_setup)
        self.assertIn('Button("重新配置凭据"', bot_setup)

    def test_bot_setup_cancel_clears_credentials_and_waits_for_active_check(self):
        dismiss = self.source.split("func dismissBotSetup", 1)[1].split(
            "func completeBotSetup", 1
        )[0]
        self.assertIn('setupAppID = ""', dismiss)
        self.assertIn('setupAppSecret = ""', dismiss)
        bot_setup = self.source.split("private struct BotSetupView", 1)[1].split(
            "private enum ConnectionSetupPath", 1
        )[0]
        cancel = bot_setup.split('Button("取消")', 1)[1].split("Spacer()", 1)[0]
        self.assertIn(".disabled(model.isConfiguringProfile)", cancel)
        self.assertIn(".interactiveDismissDisabled(model.isConfiguringProfile)", bot_setup)

    def test_discovered_user_is_appended_without_replacing_existing_users(self):
        authorization = self.source.split("func continueToUserAuthorization", 1)[1].split(
            "func prepareConfiguration", 1
        )[0]
        self.assertIn("draftUsers.contains", authorization)
        self.assertIn("draftUsers.removeAll", authorization)
        self.assertIn("draftUsers.append", authorization)
        self.assertNotIn("draftUsers = [", authorization)

    def test_app_registers_standard_edit_commands_for_copy_and_paste(self):
        menu = self.source.split("private func buildApplicationMenu", 1)[1].split(
            "private func buildStatusItem", 1
        )[0]
        for selector in ("undo:", "NSText.cut", "NSText.copy", "NSText.paste", "NSText.selectAll"):
            self.assertIn(selector, menu)
        self.assertIn("application.mainMenu = mainMenu", menu)

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
            "task_subscriptions",
            "task_settings",
            "compact_task_context",
            "codex_usage",
            "sync_desktop",
            "sync_desktop_switch",
            "promlight",
            "promlight_legend",
        ):
            self.assertIn(value, setup)
        for label in (
            "Task 管理",
            "桌面task",
            "订阅桌面 Task",
            "接续当前 Task",
            "接续其他 Task",
            "模型设置",
            "修改当前 Task 模型",
            "压缩当前 Task 上下文",
            "Codex 额度用量",
            "提示灯",
            "我的提示灯",
            "灯光状态说明",
        ):
            self.assertIn(label, setup)
        self.assertIn("不会默认开放全部项目", setup)

    def test_setup_is_a_bot_ready_two_step_wizard(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationView", 1
        )[0]
        for title in ("配置机器人", "添加使用者"):
            self.assertIn(title, setup)
        for title in ("创建应用", "连接应用"):
            self.assertNotIn(title, setup)
        self.assertIn("共 2 步", setup)
        self.assertIn("switch currentStep", setup)
        self.assertIn("查看完整配置清单", setup)
        self.assertIn("ProductBrand.localPromise", setup)
        for private_name in ("Roger", "DeepOri", "Ori One"):
            if private_name != "DeepOri":
                self.assertNotIn(private_name, setup)

    def test_public_product_name_and_selected_layout_are_visible(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationChecklistView", 1
        )[0]
        self.assertIn('static let name = "DeepOri Bridge"', self.source)
        self.assertIn('static let edition = "for macOS"', self.source)
        self.assertIn('static let systemRequirement = "macOS 13+"', self.source)
        self.assertIn("ProductBrand.name", setup)
        self.assertIn("progressBar", setup)
        self.assertIn("workspace", setup)
        self.assertIn("statusPanel", setup)
        self.assertIn("现在去飞书完成 3 项设置", setup)
        self.assertIn("我已完成，开始检查", setup)
        info = INFO_PLIST.read_text(encoding="utf-8")
        self.assertGreaterEqual(info.count("DeepOri Bridge"), 2)

    def test_first_connection_offers_codex_and_manual_paths(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationChecklistView", 1
        )[0]
        for text in (
            "选择配置方式",
            "让 Codex 帮我配置",
            "我自己手动配置",
            "复制指令并打开 Codex",
            "已复制，可在 Codex 粘贴",
            "改用手动配置",
            "不需要安装飞书插件",
            "使用 Codex 配置",
            "使用手动向导",
            "推荐 · 无需额外安装",
        ):
            self.assertIn(text, setup)
        self.assertNotIn("选择此方式", setup)
        self.assertIn("$deepori-bridge-setup", self.source)
        self.assertIn("prepareCodexAssistedSetup", self.source)
        self.assertIn("installCodexSetupSkill", self.source)

    def test_copy_prompt_opens_codex_through_registered_url(self):
        open_codex = self.source.split("func openCodexDesktop", 1)[1].split(
            "func control", 1
        )[0]
        self.assertIn('URL(string: "codex://")', open_codex)
        self.assertNotIn("urlForApplication", open_codex)

    def test_manual_user_step_uses_plain_user_language(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationChecklistView", 1
        )[0]
        for text in (
            "选择谁可以使用",
            "添加第一个飞书用户",
            "添加飞书用户",
            "管理用户与项目",
            "设置可访问项目",
            "完成设置",
        ):
            self.assertIn(text, setup)
        for old_text in ("授权首位使用者", "启动两分钟识别", "识别新使用者"):
            self.assertNotIn(old_text, setup)

    def test_setup_skill_is_bundled_and_keeps_secrets_out_of_codex(self):
        skill = SETUP_SKILL.read_text(encoding="utf-8")
        agent = SETUP_SKILL_AGENT.read_text(encoding="utf-8")
        build = BUILD_SCRIPT.read_text(encoding="utf-8")
        self.assertIn("name: deepori-bridge-setup", skill)
        self.assertIn("Do not install a Feishu or Lark plugin", skill)
        self.assertIn("Never ask the user to paste an App Secret", skill)
        self.assertIn("Never grant `*` project access", skill)
        self.assertIn("$deepori-bridge-setup", agent)
        self.assertIn('"${project_dir}/skills/deepori-bridge-setup"', build)
        self.assertIn('"${resources_dir}/CodexSkills/deepori-bridge-setup"', build)

    def test_codex_setup_contract_matches_all_twelve_app_menu_items(self):
        app_checklist = self.source.split(
            'checklistSection("机器人菜单 Event Key"', 1
        )[1].split("])", 1)[0]
        editable_fields = self.source.split("private var advancedSettings", 1)[1].split(
            "private var accessRequests", 1
        )[0]
        setup_skill = SETUP_SKILL.read_text(encoding="utf-8")
        bridge_skill = BRIDGE_SKILL.read_text(encoding="utf-8")
        troubleshooting = BRIDGE_TROUBLESHOOTING.read_text(encoding="utf-8")
        bridge_source = " ".join(BRIDGE_SOURCE.read_text(encoding="utf-8").split())
        groups = (
            (
                "Task 管理",
                (
                    ("当前 Task", "current_task", "current_task_menu_event_key", "CURRENT_TASK_MENU_EVENT_KEY"),
                    ("切换 Task", "select_task", "task_menu_event_key", "TASK_MENU_EVENT_KEY"),
                    ("新建 Task", "new_task", "new_task_menu_event_key", "NEW_TASK_MENU_EVENT_KEY"),
                    ("归档当前 Task", "archive_task", "archive_task_menu_event_key", "ARCHIVE_TASK_MENU_EVENT_KEY"),
                ),
            ),
            (
                "桌面task",
                (
                    ("订阅桌面 Task", "task_subscriptions", "task_subscriptions_menu_event_key", "TASK_SUBSCRIPTIONS_MENU_EVENT_KEY"),
                    ("接续当前 Task", "sync_desktop", "desktop_sync_menu_event_key", "DESKTOP_SYNC_MENU_EVENT_KEY"),
                    ("接续其他 Task", "sync_desktop_switch", "desktop_sync_switch_menu_event_key", "DESKTOP_SYNC_SWITCH_MENU_EVENT_KEY"),
                ),
            ),
            (
                "模型设置",
                (
                    ("修改当前 Task 模型", "task_settings", "task_settings_menu_event_key", "TASK_SETTINGS_MENU_EVENT_KEY"),
                    ("压缩当前 Task 上下文", "compact_task_context", "compact_context_menu_event_key", "COMPACT_CONTEXT_MENU_EVENT_KEY"),
                    ("Codex 额度用量", "codex_usage", "usage_menu_event_key", "USAGE_MENU_EVENT_KEY"),
                ),
            ),
            (
                "提示灯",
                (
                    ("我的提示灯", "promlight", "promlight_menu_event_key", "PROMLIGHT_MENU_EVENT_KEY"),
                    ("灯光状态说明", "promlight_legend", "promlight_legend_menu_event_key", "PROMLIGHT_LEGEND_MENU_EVENT_KEY"),
                ),
            ),
        )
        self.assertIn("exactly these four first-level menus", setup_skill)
        self.assertIn("all twelve Bot menu Event Keys", bridge_skill)
        previous_app_group = -1
        previous_skill_group = -1
        previous_editable_item = -1
        config = json.loads(CONFIG_EXAMPLE.read_text(encoding="utf-8"))
        handler = bridge_source.split("event_key not in {", 1)[1].split(
            "} or not authorized_user", 1
        )[0]
        for number, (group, items) in enumerate(groups, start=1):
            app_group = app_checklist.index(f'"一级菜单 · {group}"')
            skill_group = setup_skill.index(f"{number}. `{group}`")
            self.assertGreater(app_group, previous_app_group)
            self.assertGreater(skill_group, previous_skill_group)
            previous_app_group = app_group
            previous_skill_group = skill_group
            self.assertIn(f'“{group}” groups', troubleshooting)
            previous_app_item = app_group
            previous_skill_item = skill_group
            for label, event_key, config_field, constant in items:
                app_item = app_checklist.index(f'"{event_key} · {label}"')
                skill_item = setup_skill.index(f"- `{label}` → `{event_key}`")
                self.assertGreater(app_item, previous_app_item)
                self.assertGreater(skill_item, previous_skill_item)
                editable_item = editable_fields.index(f'placeholder: "{event_key}"')
                self.assertGreater(editable_item, previous_editable_item)
                previous_app_item = app_item
                previous_skill_item = skill_item
                previous_editable_item = editable_item
                self.assertIn(f"`{event_key}`", bridge_skill)
                self.assertEqual(config[config_field], event_key)
                self.assertIn(f'CONFIG.get("{config_field}") or "{event_key}"', bridge_source)
                self.assertIn(constant, handler)
                self.assertIn(f"if event_key == {constant}:", bridge_source)

    def test_profile_persistence_is_shared_by_configure_and_recheck(self):
        configure = self.source.split("func configureProfileAndCheck", 1)[1].split(
            "func recheckProfile", 1
        )[0]
        recheck = self.source.split("func recheckProfile", 1)[1].split(
            "private func persistSetupProfile", 1
        )[0]
        persist = self.source.split("private func persistSetupProfile", 1)[1].split(
            "private func connectionCheckFailureMessage", 1
        )[0]
        self.assertIn("persistSetupProfile(profile)", configure)
        self.assertIn("persistSetupProfile(profile)", recheck)
        self.assertIn('config["lark_profile"] = profile', persist)

    def test_release_workflow_publishes_immutable_signed_drafts(self):
        workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn('gh release view "$GITHUB_REF_NAME"', workflow)
        self.assertIn('gh release create "$GITHUB_REF_NAME"', workflow)
        self.assertIn("MACOS_CODE_SIGN_IDENTITY", workflow)
        self.assertIn("MACOS_CERTIFICATE_P12_BASE64", workflow)
        self.assertIn("APPLE_NOTARY_KEY_P8_BASE64", workflow)
        self.assertIn("notarytool store-credentials", workflow)
        self.assertIn("security import", workflow)
        self.assertIn("security delete-keychain", workflow)
        self.assertNotIn("MACOS_NOTARY_PROFILE", workflow)
        self.assertIn("--draft", workflow)
        self.assertIn("--draft=false", workflow)
        self.assertNotIn("--clobber", workflow)
        self.assertIn('title="DeepOri Bridge ${GITHUB_REF_NAME#v} for macOS"', workflow)

    def test_sparkle_is_pinned_embedded_and_signed_by_release_workflow(self):
        package = PACKAGE_MANIFEST.read_text(encoding="utf-8")
        build = BUILD_SCRIPT.read_text(encoding="utf-8")
        workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")
        appcast = APPCAST_SCRIPT.read_text(encoding="utf-8")
        self.assertIn('exact: "2.9.6"', package)
        self.assertIn('"${frameworks_dir}/Sparkle.framework"', build)
        self.assertIn('@executable_path/../Frameworks', package)
        self.assertNotIn("--deep --sign", build)
        self.assertIn("SPARKLE_PRIVATE_KEY", workflow)
        self.assertIn("dist/appcast.xml", workflow)
        self.assertIn("--ed-key-file -", appcast)
        self.assertIn("SPARKLE_PUBLIC_KEY", appcast)

    def test_app_update_and_local_repair_are_clearly_separated(self):
        management = self.source.split("private var actionsCard", 1)[1].split(
            "private func infoRow", 1
        )[0]
        self.assertIn("App 更新", management)
        self.assertIn("检查更新", management)
        self.assertIn("自动安装 App 更新", management)
        self.assertIn("不定时轮询", management)
        self.assertIn("桥接空闲时安装", management)
        self.assertIn("更新源：签名的 GitHub appcast", management)
        self.assertIn("请连接 VPN", management)
        self.assertIn("高级维护", management)
        advanced = management.split("DisclosureGroup", 1)[1]
        self.assertIn("打开日志", advanced)
        self.assertIn("数据目录", advanced)
        self.assertIn("修复后台服务", management)
        self.assertIn("不访问 GitHub", management)
        self.assertNotIn("安装/更新后台组件", management)

        window_setup = self.source.split("private func createWindowIfNeeded", 1)[1].split(
            "@objc private func showMainWindow", 1
        )[0]
        self.assertIn("newWindow.titleVisibility = .hidden", window_setup)
        self.assertIn("newWindow.titlebarAppearsTransparent = false", window_setup)
        header = self.source.split("private var header", 1)[1].split(
            "private var statusCard", 1
        )[0]
        self.assertIn(".padding(.top, 20)", header)

        update_check = self.source.split("private final class SparkleUpdateCoordinator", 1)[1].split(
            "private final class BridgeViewModel", 1
        )[0]
        self.assertIn("controller.checkForUpdates(nil)", update_check)
        self.assertIn("makeFileSystemObjectSource", update_check)
        self.assertIn("lastUpdateCheckDate", update_check)
        self.assertIn("bridgeActivityCheckMinimumInterval", update_check)
        self.assertNotIn("URLSession", update_check)

    def test_setup_keeps_technical_connection_details_out_of_the_main_path(self):
        setup = self.source.split("private struct ConnectionSetupView", 1)[1].split(
            "private struct ConfigurationChecklistView", 1
        )[0]
        bot_setup = self.source.split("private struct BotSetupView", 1)[1].split(
            "private enum ConnectionSetupPath", 1
        )[0]
        checklist = self.source.split("private struct ConfigurationChecklistView", 1)[1].split(
            "private struct ConfigurationView", 1
        )[0]
        self.assertIn("高级设置", bot_setup)
        self.assertIn("本机连接名称", bot_setup)
        for detail in ("stdin", "lark-cli", "open_id", "card.action.trigger"):
            self.assertNotIn(detail, setup)
        self.assertIn("card.action.trigger", checklist)

    def test_home_explains_login_autostart_and_startup_readiness(self):
        source = self.source
        status_card = source.split("private var statusCard", 1)[1].split(
            "private var healthCard", 1
        )[0]
        control = (ROOT / "Resources/bridge/control.sh").read_text(encoding="utf-8")
        installer = (ROOT / "Resources/bridge/install.sh").read_text(encoding="utf-8")

        self.assertIn("loginAutostartEnabled", source)
        self.assertIn("正在启动", status_card)
        self.assertIn("登录后自动启动已开启", status_card)
        self.assertIn("登录后自动启动已关闭", status_card)
        self.assertIn("立即开启", status_card)
        self.assertIn(".frame(minWidth: 90)", status_card)
        self.assertIn(".fixedSize(horizontal: true, vertical: false)", status_card)
        self.assertNotIn(".focusable(false)", status_card)
        self.assertNotIn(".focusEffectDisabled()", status_card)
        self.assertIn("停止本次运行…", status_card)
        self.assertIn("停止本次桥接运行？", source)
        self.assertIn("登录后自动启动的开关保持不变", source)
        self.assertIn("loginAutostartSection", source)
        self.assertIn("只影响下次登录", source)
        self.assertIn('autostart-status)', control)
        self.assertIn('enable-autostart)', control)
        self.assertIn('disable-autostart)', control)
        self.assertIn('print-disabled "${domain}"', control)
        stop_service = control.split("stop_service()", 1)[1].split("case", 1)[0]
        self.assertNotIn('launchctl disable "${service}"', stop_service)
        self.assertIn('"ProcessType": "Standard"', installer)
        self.assertNotIn('"ProcessType": "Background"', installer)

    def test_first_user_discovery_is_bounded_and_does_not_grant_projects(self):
        discovery = self.source.split("func discoverFeishuUser", 1)[1].split(
            "func isRunning", 1
        )[0]
        self.assertIn('"--max-events", "1"', discovery)
        self.assertIn('"--timeout", "2m"', discovery)
        self.assertIn('sender.hasPrefix("ou_")', discovery)
        self.assertIn('object["sender_type"] as? String == "user"', discovery)
        self.assertIn('object["chat_type"] as? String == "p2p"', discovery)
        self.assertIn('messageText(object["content"]) == challenge', discovery)
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
        self.assertEqual(
            config["task_subscriptions_menu_event_key"], "task_subscriptions"
        )
        self.assertEqual(config["task_settings_menu_event_key"], "task_settings")
        self.assertEqual(
            config["compact_context_menu_event_key"], "compact_task_context"
        )
        self.assertEqual(config["promlight_menu_event_key"], "promlight")
        self.assertEqual(
            config["promlight_legend_menu_event_key"], "promlight_legend"
        )
        self.assertNotIn("workflow_notifications", config)

    def test_installer_refuses_duplicate_menu_event_keys_before_migration(self):
        installer = INSTALLER.read_text(encoding="utf-8")
        self.assertIn("len(set(event_keys)) != len(event_keys)", installer)
        self.assertIn("menu Event Keys must be non-empty and unique", installer)
        self.assertLess(
            installer.index("len(set(event_keys)) != len(event_keys)"),
            installer.index("if not changed:"),
        )

    def test_public_installer_verifies_release_and_refuses_downgrade(self):
        installer = PUBLIC_INSTALLER.read_text(encoding="utf-8")
        for requirement in (
            "api.github.com/repos/${repository}/releases/latest",
            'digest.startswith("sha256:")',
            "/usr/bin/shasum -a 256",
            "CFBundleIdentifier",
            "CFBundleShortVersionString",
            "/usr/bin/codesign --verify --deep --strict",
            "arm64",
            "x86_64",
            "refusing to downgrade the installed App",
            '${destination}.incoming',
            '${destination}.previous',
            "旧版本已恢复",
        ):
            self.assertIn(requirement, installer)

    def test_app_delegates_state_mutation_to_locked_bridge_helper(self):
        updater = self.source.split("func removeAccessRequests", 1)[1].split(
            "private func run", 1
        )[0]
        self.assertIn('appendingPathComponent("feishu_codex_bridge.py")', updater)
        self.assertIn('"--remove-access-requests"', updater)
        self.assertNotIn("payload.write(to: stateURL", updater)

    def test_public_package_excludes_private_extension_components(self):
        build = BUILD_SCRIPT.read_text(encoding="utf-8")
        installer = INSTALLER.read_text(encoding="utf-8")
        runtime_files = self.source.split("let runtimeFiles = [", 1)[1].split(
            "]", 1
        )[0]
        for name in (
            "workflow_notifications.py",
            "workflow_notify.py",
            "workflow_config.py",
        ):
            self.assertIn(f'"${{resources_dir}}/bridge/{name}"', build)
            self.assertNotIn(f'"${{resource_dir}}/{name}"', installer)
            self.assertNotIn(name, runtime_files)
        readme = README.read_text(encoding="utf-8")
        for private_name in ("Ori One", "ori-one-mind", "deepori.cn"):
            self.assertNotIn(private_name, readme)

    def test_bridge_starts_diagnostics_without_private_extension_module(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            bridge = home / "bridge.py"
            config = home / "config.json"
            bridge.write_bytes(BRIDGE_SOURCE.read_bytes())
            config.write_text("{}\n", encoding="utf-8")
            result = subprocess.run(
                [sys.executable, str(bridge), "--diagnose-json"],
                env={
                    **os.environ,
                    "HOME": str(home),
                    "CODEX_FEISHU_BRIDGE_CONFIG": str(config),
                },
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(result.returncode, 1)
            self.assertNotIn("ModuleNotFoundError", result.stderr)

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

    def test_local_installer_updates_runtime_before_copying_the_app(self):
        installer = LOCAL_INSTALLER.read_text(encoding="utf-8")

        self.assertNotIn('if [[ ! -d "${app_source}" ]]', installer)
        self.assertLess(
            installer.index('"${project_dir}/scripts/build-app.sh"'),
            installer.index('"${app_source}/Contents/Resources/bridge/install.sh"'),
        )
        self.assertLess(
            installer.index('"${app_source}/Contents/Resources/bridge/install.sh"'),
            installer.index('/usr/bin/ditto "${app_source}"'),
        )


if __name__ == "__main__":
    unittest.main()

import AppKit
import Foundation
import SwiftUI

private struct CommandResult {
    let status: Int32
    let output: String
}

private final class BridgeController {
    let supportDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/Codex Feishu Bridge", isDirectory: true)

    var configURL: URL { supportDirectory.appendingPathComponent("config.json") }
    var logURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/log/feishu-bridge.log")
    }

    private var bundledBridgeDirectory: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("bridge", isDirectory: true)
    }

    var isInstalled: Bool {
        ["bridge.py", "control.sh", "diagnose.sh", "config.json"].allSatisfy {
            FileManager.default.fileExists(atPath: supportDirectory.appendingPathComponent($0).path)
        }
    }

    @discardableResult
    func install() -> CommandResult {
        guard let script = bundledBridgeDirectory?.appendingPathComponent("install.sh") else {
            return CommandResult(status: 1, output: "安装资源不存在")
        }
        return run(script.path, [])
    }

    func control(_ action: String) -> CommandResult {
        run(supportDirectory.appendingPathComponent("control.sh").path, [action])
    }

    func diagnose() -> CommandResult {
        run(supportDirectory.appendingPathComponent("diagnose.sh").path, [])
    }

    func isRunning() -> Bool {
        control("status").output.trimmingCharacters(in: .whitespacesAndNewlines) == "on"
    }

    func readConfig() -> [String: Any] {
        guard let data = try? Data(contentsOf: configURL),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return [:]
        }
        return object
    }

    func writeConfig(_ config: [String: Any]) throws {
        let data = try JSONSerialization.data(withJSONObject: config, options: [.prettyPrinted, .sortedKeys])
        var payload = data
        payload.append(0x0A)
        try payload.write(to: configURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configURL.path)
    }

    private func run(_ executable: String, _ arguments: [String]) -> CommandResult {
        guard FileManager.default.isExecutableFile(atPath: executable) else {
            return CommandResult(status: 1, output: "找不到可执行文件：\(executable)")
        }
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = pipe
        do {
            try process.run()
            process.waitUntilExit()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            return CommandResult(
                status: process.terminationStatus,
                output: String(data: data, encoding: .utf8) ?? ""
            )
        } catch {
            return CommandResult(status: 1, output: error.localizedDescription)
        }
    }
}

@MainActor
private final class BridgeViewModel: ObservableObject {
    private let bridge: BridgeController

    @Published var isRunning = false
    @Published var profileName = "codex-notify"
    @Published var showConfiguration = false
    @Published var showDiagnosis = false
    @Published var diagnosisPassed = false
    @Published var diagnosisText = ""
    @Published var alertTitle = ""
    @Published var alertMessage: String?

    @Published var draftProfile = "codex-notify"
    @Published var draftSender = ""
    @Published var draftChats = ""
    @Published var draftEventKey = "select_task"

    init(bridge: BridgeController) {
        self.bridge = bridge
        refresh()
    }

    func refresh() {
        isRunning = bridge.isRunning()
        let config = bridge.readConfig()
        profileName = String(describing: config["lark_profile"] ?? "codex-notify")
    }

    func toggleBridge() {
        let action = isRunning ? "stop" : "start"
        let result = bridge.control(action)
        if result.status != 0 {
            presentError(
                title: action == "start" ? "开启失败" : "关闭失败",
                message: result.output
            )
        }
        refresh()
    }

    func prepareConfiguration() {
        let config = bridge.readConfig()
        draftProfile = String(describing: config["lark_profile"] ?? "codex-notify")
        draftSender = String(describing: config["allowed_sender_id"] ?? "")
        draftChats = (config["allowed_chat_ids"] as? [String] ?? []).joined(separator: ",")
        draftEventKey = String(describing: config["task_menu_event_key"] ?? "select_task")
        showConfiguration = true
    }

    func saveConfiguration() {
        let profile = draftProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        let sender = draftSender.trimmingCharacters(in: .whitespacesAndNewlines)
        let eventKey = draftEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !profile.isEmpty else {
            presentError(title: "配置未保存", message: "lark-cli Profile 不能为空。")
            return
        }
        guard sender.hasPrefix("ou_") else {
            presentError(title: "配置未保存", message: "允许的用户 open_id 必须以 ou_ 开头。")
            return
        }
        guard !eventKey.isEmpty else {
            presentError(title: "配置未保存", message: "机器人菜单 Event Key 不能为空。")
            return
        }

        var config = bridge.readConfig()
        config["lark_profile"] = profile
        config["allowed_sender_id"] = sender
        config["allowed_chat_ids"] = draftChats
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        config["task_menu_event_key"] = eventKey
        config["max_prompt_chars"] = config["max_prompt_chars"] ?? 12000
        config["max_reply_chars"] = config["max_reply_chars"] ?? 3000

        do {
            let wasRunning = bridge.isRunning()
            try bridge.writeConfig(config)
            if wasRunning {
                let result = bridge.control("restart")
                if result.status != 0 {
                    presentError(title: "配置已保存，但重启失败", message: result.output)
                    refresh()
                    return
                }
            }
            showConfiguration = false
            refresh()
        } catch {
            presentError(title: "配置未保存", message: error.localizedDescription)
        }
    }

    func runDiagnosis() {
        let result = bridge.diagnose()
        diagnosisPassed = result.status == 0
        diagnosisText = result.output.isEmpty ? "诊断没有返回内容。" : result.output
        showDiagnosis = true
    }

    func installComponents() {
        let result = bridge.install()
        if result.status == 0 {
            alertTitle = "后台组件已更新"
            alertMessage = "原有配置和当前 Task 状态均已保留。"
        } else {
            presentError(title: "更新失败", message: result.output)
        }
        refresh()
    }

    func openLog() {
        let directory = bridge.logURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: bridge.logURL.path) {
            FileManager.default.createFile(atPath: bridge.logURL.path, contents: nil)
        }
        NSWorkspace.shared.open(bridge.logURL)
    }

    func openSupportDirectory() {
        NSWorkspace.shared.open(bridge.supportDirectory)
    }

    var hasConfiguredSender: Bool {
        String(describing: bridge.readConfig()["allowed_sender_id"] ?? "").hasPrefix("ou_")
    }

    private func presentError(title: String, message: String) {
        alertTitle = title
        alertMessage = message.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? "未知错误"
            : message
    }
}

private struct MainView: View {
    @ObservedObject var model: BridgeViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            header
            statusCard
            HStack(alignment: .top, spacing: 16) {
                connectionCard
                actionsCard
            }
            Spacer(minLength: 0)
            Text("关闭此窗口不会关闭桥接。你可以从菜单栏图标再次打开控制中心。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(28)
        .frame(minWidth: 700, idealWidth: 760, minHeight: 500, idealHeight: 540)
        .background(Color(nsColor: .windowBackgroundColor))
        .sheet(isPresented: $model.showConfiguration) {
            ConfigurationView(model: model)
        }
        .sheet(isPresented: $model.showDiagnosis) {
            DiagnosisView(model: model)
        }
        .alert(
            model.alertTitle,
            isPresented: Binding(
                get: { model.alertMessage != nil },
                set: { if !$0 { model.alertMessage = nil } }
            )
        ) {
            Button("好", role: .cancel) {}
        } message: {
            Text(model.alertMessage ?? "")
        }
        .onAppear { model.refresh() }
    }

    private var header: some View {
        HStack(spacing: 16) {
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .frame(width: 64, height: 64)
                .cornerRadius(14)
            VStack(alignment: .leading, spacing: 5) {
                Text("Codex 飞书桥接")
                    .font(.system(size: 26, weight: .semibold))
                Text("通过飞书继续 Mac 上已有的 Codex Task")
                    .font(.system(size: 14))
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Text("v\(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "")")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var statusCard: some View {
        HStack(spacing: 16) {
            ZStack {
                Circle()
                    .fill(model.isRunning ? Color.green.opacity(0.15) : Color.secondary.opacity(0.12))
                    .frame(width: 52, height: 52)
                Image(systemName: model.isRunning ? "checkmark.circle.fill" : "pause.circle.fill")
                    .font(.system(size: 28))
                    .foregroundStyle(model.isRunning ? .green : .secondary)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(model.isRunning ? "桥接已开启" : "桥接已关闭")
                    .font(.title3.weight(.semibold))
                Text(model.isRunning
                     ? "正在监听飞书消息、Task 卡片和机器人菜单"
                     : "飞书消息暂时不会发送到 Codex Desktop")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button(model.isRunning ? "关闭桥接" : "开启桥接") {
                model.toggleBridge()
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .tint(model.isRunning ? .red : .accentColor)
        }
        .padding(18)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color(nsColor: .controlBackgroundColor))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .stroke(Color.primary.opacity(0.08), lineWidth: 1)
        )
    }

    private var connectionCard: some View {
        GroupBox("连接信息") {
            VStack(spacing: 12) {
                infoRow(icon: "person.crop.circle", title: "lark-cli Profile", value: model.profileName)
                Divider()
                infoRow(icon: "dot.radiowaves.left.and.right", title: "飞书事件", value: "3 个监听器")
                Divider()
                infoRow(icon: "sidebar.left", title: "Task 来源", value: "Codex Desktop 左侧栏")
            }
            .padding(.top, 6)
        }
        .frame(maxWidth: .infinity)
    }

    private var actionsCard: some View {
        GroupBox("管理") {
            VStack(spacing: 10) {
                actionButton("配置桥接", icon: "gearshape") { model.prepareConfiguration() }
                actionButton("运行诊断", icon: "stethoscope") { model.runDiagnosis() }
                actionButton("安装/更新后台组件", icon: "arrow.triangle.2.circlepath") {
                    model.installComponents()
                }
                HStack(spacing: 10) {
                    Button("打开日志") { model.openLog() }
                    Button("数据目录") { model.openSupportDirectory() }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.top, 6)
        }
        .frame(maxWidth: .infinity)
    }

    private func infoRow(icon: String, title: String, value: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .foregroundStyle(.secondary)
                .frame(width: 20)
            Text(title)
            Spacer()
            Text(value)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    private func actionButton(_ title: String, icon: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Image(systemName: icon)
                    .frame(width: 18)
                Text(title)
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.vertical, 5)
    }
}

private struct ConfigurationView: View {
    @ObservedObject var model: BridgeViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 5) {
                Text("桥接配置")
                    .font(.title2.weight(.semibold))
                Text("App Secret 由 lark-cli 和 macOS Keychain 管理，不会保存在这里。")
                    .foregroundStyle(.secondary)
            }
            Form {
                TextField("lark-cli Profile", text: $model.draftProfile)
                TextField("允许的用户 open_id", text: $model.draftSender)
                TextField("允许的群 Chat ID", text: $model.draftChats)
                TextField("机器人菜单 Event Key", text: $model.draftEventKey)
            }
            Text("多个群 Chat ID 使用英文逗号分隔；留空时优先使用与 Bot 的单聊。")
                .font(.caption)
                .foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button("取消") { model.showConfiguration = false }
                    .keyboardShortcut(.cancelAction)
                Button("保存") { model.saveConfiguration() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(26)
        .frame(width: 590)
    }
}

private struct DiagnosisView: View {
    @ObservedObject var model: BridgeViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(spacing: 10) {
                Image(systemName: model.diagnosisPassed ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                    .foregroundStyle(model.diagnosisPassed ? .green : .orange)
                    .font(.title2)
                Text(model.diagnosisPassed ? "诊断通过" : "诊断发现问题")
                    .font(.title2.weight(.semibold))
            }
            ScrollView {
                Text(model.diagnosisText)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(12)
            }
            .background(Color(nsColor: .textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 8))
            HStack {
                Spacer()
                Button("关闭") { model.showDiagnosis = false }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 700, height: 500)
    }
}

@MainActor
private final class AppDelegate: NSObject, NSApplicationDelegate {
    private let bridge = BridgeController()
    private lazy var model = BridgeViewModel(bridge: bridge)
    private var window: NSWindow?
    private var statusItem: NSStatusItem!
    private var statusLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    private var startStopItem = NSMenuItem(title: "", action: #selector(toggleBridge), keyEquivalent: "")
    private var statusTimer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildStatusItem()
        if !bridge.isInstalled {
            let installResult = bridge.install()
            if installResult.status != 0 {
                model.alertTitle = "安装后台组件失败"
                model.alertMessage = installResult.output
            }
        }
        createWindowIfNeeded()
        showMainWindow()
        refreshStatus()

        statusTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.refreshStatus()
            }
        }
        if !model.hasConfiguredSender {
            DispatchQueue.main.async { [weak self] in
                self?.model.prepareConfiguration()
            }
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showMainWindow()
        return true
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    private func createWindowIfNeeded() {
        guard window == nil else { return }
        let hostingController = NSHostingController(rootView: MainView(model: model))
        let newWindow = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 760, height: 540),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        newWindow.title = "Codex 飞书桥接"
        newWindow.titlebarAppearsTransparent = true
        newWindow.isReleasedWhenClosed = false
        newWindow.minSize = NSSize(width: 700, height: 500)
        newWindow.contentViewController = hostingController
        newWindow.center()
        newWindow.setFrameAutosaveName("CodexFeishuBridgeMainWindow")
        window = newWindow
    }

    @objc private func showMainWindow() {
        createWindowIfNeeded()
        NSApplication.shared.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        model.refresh()
    }

    private func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let image = NSImage(
            systemSymbolName: "arrow.left.arrow.right.circle",
            accessibilityDescription: "Codex 飞书桥接"
        ) {
            image.isTemplate = true
            statusItem.button?.image = image
        } else {
            statusItem.button?.title = "↔"
        }
        statusItem.button?.toolTip = "Codex 飞书桥接"

        let menu = NSMenu()
        let open = NSMenuItem(title: "打开控制中心", action: #selector(showMainWindow), keyEquivalent: "o")
        open.target = self
        menu.addItem(open)
        menu.addItem(.separator())
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        startStopItem.target = self
        menu.addItem(startStopItem)

        let configure = NSMenuItem(title: "配置…", action: #selector(showConfiguration), keyEquivalent: ",")
        configure.target = self
        menu.addItem(configure)

        let diagnose = NSMenuItem(title: "运行诊断…", action: #selector(showDiagnosis), keyEquivalent: "d")
        diagnose.target = self
        menu.addItem(diagnose)
        menu.addItem(.separator())

        let quit = NSMenuItem(title: "退出 App（桥接保持现状）", action: #selector(quitApp), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        statusItem.menu = menu
    }

    private func refreshStatus() {
        model.refresh()
        statusLine.title = model.isRunning ? "状态：已开启" : "状态：已关闭"
        startStopItem.title = model.isRunning ? "关闭桥接" : "开启桥接"
    }

    @objc private func toggleBridge() {
        model.toggleBridge()
        refreshStatus()
    }

    @objc private func showConfiguration() {
        showMainWindow()
        model.prepareConfiguration()
    }

    @objc private func showDiagnosis() {
        showMainWindow()
        model.runDiagnosis()
    }

    @objc private func quitApp() {
        NSApplication.shared.terminate(nil)
    }
}

MainActor.assumeIsolated {
    let application = NSApplication.shared
    let delegate = AppDelegate()
    application.delegate = delegate
    application.setActivationPolicy(.regular)
    application.run()
}

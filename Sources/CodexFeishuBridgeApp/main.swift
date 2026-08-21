import AppKit
import Foundation

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

private final class AppDelegate: NSObject, NSApplicationDelegate {
    private let bridge = BridgeController()
    private var statusItem: NSStatusItem!
    private var statusLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    private var startStopItem = NSMenuItem(title: "", action: #selector(toggleBridge), keyEquivalent: "")
    private var statusTimer: Timer?

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let image = NSImage(systemSymbolName: "arrow.left.arrow.right.circle", accessibilityDescription: "Codex 飞书桥接") {
            image.isTemplate = true
            statusItem.button?.image = image
        } else {
            statusItem.button?.title = "↔"
        }
        statusItem.button?.toolTip = "Codex 飞书桥接"
        buildMenu()

        let installResult = bridge.install()
        if installResult.status != 0 {
            showError(title: "安装后台组件失败", message: installResult.output)
        }
        refreshStatus()
        statusTimer = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in
            self?.refreshStatus()
        }

        if String(describing: bridge.readConfig()["allowed_sender_id"] ?? "").isEmpty {
            DispatchQueue.main.async { [weak self] in self?.showConfiguration() }
        }
    }

    private func buildMenu() {
        let menu = NSMenu()
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())
        startStopItem.target = self
        menu.addItem(startStopItem)

        let configure = NSMenuItem(title: "配置…", action: #selector(showConfiguration), keyEquivalent: ",")
        configure.target = self
        menu.addItem(configure)

        let install = NSMenuItem(title: "安装/更新后台组件", action: #selector(installComponents), keyEquivalent: "")
        install.target = self
        menu.addItem(install)

        let diagnose = NSMenuItem(title: "运行诊断…", action: #selector(showDiagnosis), keyEquivalent: "d")
        diagnose.target = self
        menu.addItem(diagnose)
        menu.addItem(.separator())

        let openLog = NSMenuItem(title: "打开日志", action: #selector(openLog), keyEquivalent: "")
        openLog.target = self
        menu.addItem(openLog)

        let openDirectory = NSMenuItem(title: "打开数据目录", action: #selector(openSupportDirectory), keyEquivalent: "")
        openDirectory.target = self
        menu.addItem(openDirectory)
        menu.addItem(.separator())

        let quit = NSMenuItem(title: "退出菜单栏（桥接保持现状）", action: #selector(quitApp), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        statusItem.menu = menu
    }

    private func refreshStatus() {
        let running = bridge.isRunning()
        statusLine.title = running ? "状态：已开启" : "状态：已关闭"
        startStopItem.title = running ? "关闭桥接" : "开启桥接"
        statusItem.button?.appearsDisabled = false
    }

    @objc private func toggleBridge() {
        let action = bridge.isRunning() ? "stop" : "start"
        let result = bridge.control(action)
        if result.status != 0 {
            showError(title: action == "start" ? "开启失败" : "关闭失败", message: result.output)
        }
        refreshStatus()
    }

    @objc private func installComponents() {
        let result = bridge.install()
        if result.status == 0 {
            showInfo(title: "后台组件已更新", message: "原有配置和当前 task 状态均已保留。")
        } else {
            showError(title: "更新失败", message: result.output)
        }
        refreshStatus()
    }

    @objc private func showConfiguration() {
        let config = bridge.readConfig()
        let profile = NSTextField(string: String(describing: config["lark_profile"] ?? "codex-notify"))
        let sender = NSTextField(string: String(describing: config["allowed_sender_id"] ?? ""))
        let chats = NSTextField(string: (config["allowed_chat_ids"] as? [String] ?? []).joined(separator: ","))
        let eventKey = NSTextField(string: String(describing: config["task_menu_event_key"] ?? "select_task"))
        [profile, sender, chats, eventKey].forEach { $0.placeholderString = "必填" }
        chats.placeholderString = "可选，多个 Chat ID 用逗号分隔"

        let grid = NSGridView(views: [
            [NSTextField(labelWithString: "lark-cli Profile"), profile],
            [NSTextField(labelWithString: "允许的用户 open_id"), sender],
            [NSTextField(labelWithString: "允许的群 Chat ID"), chats],
            [NSTextField(labelWithString: "机器人菜单 Event Key"), eventKey],
        ])
        grid.column(at: 0).xPlacement = .trailing
        grid.column(at: 1).width = 330
        grid.rowSpacing = 8
        grid.columnSpacing = 10

        let alert = NSAlert()
        alert.messageText = "配置 Codex 飞书桥接"
        alert.informativeText = "App Secret 由 lark-cli Keychain 管理，不会写入这里。用户 open_id 是必填白名单。"
        alert.accessoryView = grid
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        let senderID = sender.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard senderID.hasPrefix("ou_") else {
            showError(title: "配置未保存", message: "允许的用户 open_id 必须以 ou_ 开头。")
            return
        }
        var updated = config
        updated["lark_profile"] = profile.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        updated["allowed_sender_id"] = senderID
        updated["allowed_chat_ids"] = chats.stringValue
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        updated["task_menu_event_key"] = eventKey.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        updated["max_prompt_chars"] = updated["max_prompt_chars"] ?? 12000
        updated["max_reply_chars"] = updated["max_reply_chars"] ?? 3000

        do {
            let wasRunning = bridge.isRunning()
            try bridge.writeConfig(updated)
            if wasRunning {
                let result = bridge.control("restart")
                if result.status != 0 {
                    showError(title: "配置已保存，但重启失败", message: result.output)
                }
            }
        } catch {
            showError(title: "配置未保存", message: error.localizedDescription)
        }
        refreshStatus()
    }

    @objc private func showDiagnosis() {
        let result = bridge.diagnose()
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 620, height: 360))
        let text = NSTextView(frame: scroll.bounds)
        text.isEditable = false
        text.isRichText = false
        text.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        text.string = result.output
        scroll.documentView = text
        scroll.hasVerticalScroller = true
        let alert = NSAlert()
        alert.messageText = result.status == 0 ? "诊断完成" : "诊断发现问题"
        alert.accessoryView = scroll
        alert.addButton(withTitle: "好")
        alert.runModal()
    }

    @objc private func openLog() {
        let directory = bridge.logURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: bridge.logURL.path) {
            FileManager.default.createFile(atPath: bridge.logURL.path, contents: nil)
        }
        NSWorkspace.shared.open(bridge.logURL)
    }

    @objc private func openSupportDirectory() {
        NSWorkspace.shared.open(bridge.supportDirectory)
    }

    @objc private func quitApp() {
        NSApplication.shared.terminate(nil)
    }

    private func showInfo(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .informational
        alert.runModal()
    }

    private func showError(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message.isEmpty ? "未知错误" : message
        alert.alertStyle = .critical
        alert.runModal()
    }
}

private let application = NSApplication.shared
private let delegate = AppDelegate()
application.delegate = delegate
application.setActivationPolicy(.accessory)
application.run()

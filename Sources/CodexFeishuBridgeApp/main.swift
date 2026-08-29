import AppKit
import Combine
import Foundation
import SwiftUI

private enum ProductBrand {
    static let name = "DeepOri Bridge"
    static let edition = "for macOS"
    static let systemRequirement = "macOS 13+"
    static let purpose = "连接 Codex 与飞书"
    static let tagline = "通过飞书继续 Mac 上已有的 Codex Task"
    static let localPromise = "仅在这台 Mac 上运行，Codex 内容不会经过第三方服务器。"
}

private struct CommandResult {
    let status: Int32
    let output: String
}

private struct AuthorizedUserDraft: Identifiable {
    let id: UUID
    var name: String
    var openID: String
    var projects: String

    init(id: UUID = UUID(), name: String = "", openID: String = "", projects: String = "*") {
        self.id = id
        self.name = name
        self.openID = openID
        self.projects = projects
    }
}

private struct AccessRequestDraft: Identifiable {
    var id: String { openID }
    let name: String
    let openID: String
}

private struct CodexUsageItem: Identifiable {
    let id: String
    let name: String
    let windowLabel: String
    let remainingPercent: Int
    let resetsAt: Date?
}

private struct BridgeHealthSnapshot {
    let activeConsumers: Int
    let activeRuns: Int
    let pendingInputs: Int
    let pendingDeliveries: Int
    let pendingTaskCreations: Int
    let maxConcurrentRuns: Int
    let lastFeishuEventAt: Date?
    let codexUsage: [CodexUsageItem]
    let codexUsageUpdatedAt: Date?

    static let empty = BridgeHealthSnapshot(
        activeConsumers: 0,
        activeRuns: 0,
        pendingInputs: 0,
        pendingDeliveries: 0,
        pendingTaskCreations: 0,
        maxConcurrentRuns: 2,
        lastFeishuEventAt: nil,
        codexUsage: [],
        codexUsageUpdatedAt: nil
    )
}

private enum BridgeUpdateError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let text): text
        }
    }
}

private final class BridgeController: @unchecked Sendable {
    let supportDirectory = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/Codex Feishu Bridge", isDirectory: true)

    var configURL: URL { supportDirectory.appendingPathComponent("config.json") }
    var stateURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/feishu-bridge/state.json")
    }
    var runtimeStatusURL: URL {
        stateURL.deletingLastPathComponent().appendingPathComponent("runtime-status.json")
    }
    var logURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/log/feishu-bridge.log")
    }
    var desktopStateURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".codex/.codex-global-state.json")
    }

    private var bundledBridgeDirectory: URL? {
        Bundle.main.resourceURL?.appendingPathComponent("bridge", isDirectory: true)
    }

    var isInstalled: Bool {
        guard FileManager.default.fileExists(atPath: configURL.path),
              let bundledBridgeDirectory else {
            return false
        }
        let runtimeFiles = [
            "feishu_codex_bridge.py": "bridge.py",
            "control.sh": "control.sh",
            "diagnose.sh": "diagnose.sh",
            "uninstall.sh": "uninstall.sh",
            "lark-cli": "lark-cli",
        ]
        return runtimeFiles.allSatisfy { bundledName, installedName in
            let bundled = bundledBridgeDirectory.appendingPathComponent(bundledName)
            let installed = supportDirectory.appendingPathComponent(installedName)
            guard let bundledData = try? Data(contentsOf: bundled),
                  let installedData = try? Data(contentsOf: installed) else {
                return false
            }
            return bundledData == installedData
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

    func uninstallKeepingData() -> CommandResult {
        guard let script = bundledBridgeDirectory?.appendingPathComponent("uninstall.sh") else {
            return CommandResult(status: 1, output: "卸载资源不存在")
        }
        return run(script.path, ["--keep-data"])
    }

    func configureLarkProfile(
        profile: String,
        appID: String,
        appSecret: String
    ) -> CommandResult {
        guard let cli = bundledBridgeDirectory?.appendingPathComponent("lark-cli") else {
            return CommandResult(status: 1, output: "App 内置 lark-cli 不存在")
        }
        return run(
            cli.path,
            [
                "config", "init",
                "--name", profile,
                "--app-id", appID,
                "--app-secret-stdin",
                "--brand", "feishu",
                "--lang", "zh_cn",
            ],
            standardInput: appSecret + "\n",
            redacting: appSecret
        )
    }

    func checkLarkProfile(_ profile: String) -> CommandResult {
        guard let cli = bundledBridgeDirectory?.appendingPathComponent("lark-cli") else {
            return CommandResult(status: 1, output: "App 内置 lark-cli 不存在")
        }
        return run(cli.path, ["--profile", profile, "doctor"])
    }

    func discoverFeishuUser(_ profile: String, challenge: String) -> CommandResult {
        guard let cli = bundledBridgeDirectory?.appendingPathComponent("lark-cli") else {
            return CommandResult(status: 1, output: "App 内置 lark-cli 不存在")
        }
        let result = run(
            cli.path,
            [
                "--profile", profile,
                "event", "consume", "im.message.receive_v1",
                "--as", "bot",
                "--max-events", "1",
                "--timeout", "2m",
            ]
        )
        guard result.status == 0 else {
            return CommandResult(
                status: result.status,
                output: result.output.isEmpty ? "飞书消息监听启动失败。" : result.output
            )
        }
        for line in result.output.split(whereSeparator: \.isNewline) {
            guard let data = String(line).data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(with: data),
                  let sender = findP2PUser(in: object, challenge: challenge) else {
                continue
            }
            return CommandResult(status: 0, output: sender)
        }
        return CommandResult(
            status: 1,
            output: "两分钟内没有收到 Bot 单聊消息。请确认应用已发布并订阅 im.message.receive_v1 后重试。"
        )
    }

    private func findP2PUser(in value: Any, challenge: String) -> String? {
        if let object = value as? [String: Any] {
            if let sender = object["sender_id"] as? String,
               sender.hasPrefix("ou_"),
               object["sender_type"] as? String == "user",
               object["chat_type"] as? String == "p2p",
               messageText(object["content"]) == challenge {
                return sender
            }
            for nested in object.values {
                if let sender = findP2PUser(in: nested, challenge: challenge) {
                    return sender
                }
            }
        } else if let values = value as? [Any] {
            for nested in values {
                if let sender = findP2PUser(in: nested, challenge: challenge) {
                    return sender
                }
            }
        }
        return nil
    }

    private func messageText(_ value: Any?) -> String? {
        if let object = value as? [String: Any], let text = object["text"] as? String {
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        guard let raw = value as? String else { return nil }
        if let data = raw.data(using: .utf8),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let text = object["text"] as? String {
            return text.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return raw.trimmingCharacters(in: .whitespacesAndNewlines)
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

    func codexProjectNames() -> [String] {
        guard let data = try? Data(contentsOf: desktopStateURL),
              let state = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let projects = state["local-projects"] as? [String: Any] else {
            return []
        }
        return Array(
            Set(
                projects.values.compactMap { value in
                    guard let project = value as? [String: Any] else { return nil }
                    let name = String(describing: project["name"] ?? "")
                        .trimmingCharacters(in: .whitespacesAndNewlines)
                    return name.isEmpty ? nil : name
                }
            )
        ).sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    func writeConfig(_ config: [String: Any]) throws {
        let data = try JSONSerialization.data(withJSONObject: config, options: [.prettyPrinted, .sortedKeys])
        var payload = data
        payload.append(0x0A)
        try payload.write(to: configURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: configURL.path)
    }

    func pendingAccessRequests() -> [AccessRequestDraft] {
        guard let data = try? Data(contentsOf: stateURL),
              let state = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let requests = state["access_requests"] as? [[String: Any]] else {
            return []
        }
        return requests.compactMap { request in
            guard let openID = request["open_id"] as? String,
                  openID.hasPrefix("ou_") else {
                return nil
            }
            return AccessRequestDraft(
                name: request["name"] as? String ?? "",
                openID: openID
            )
        }
    }

    func healthSnapshot() -> BridgeHealthSnapshot {
        let state = readJSONObject(at: stateURL)
        let runtime = readJSONObject(at: runtimeStatusURL)
        let pendingInputs = (state["pending_inputs"] as? [Any])?.count ?? 0
        let pendingDeliveries = (state["pending_replies"] as? [Any])?.count ?? 0
        let pendingTaskCreations = (state["pending_task_creations"] as? [String: Any])?.count ?? 0
        let lastEvent = (runtime["last_feishu_event_at"] as? NSNumber)?.doubleValue ?? 0
        let usage = runtime["codex_usage"] as? [String: Any] ?? [:]
        let usageUpdatedAt = (usage["updated_at"] as? NSNumber)?.doubleValue ?? 0
        let usageItems = (usage["buckets"] as? [[String: Any]] ?? []).flatMap { bucket in
            let bucketID = String(describing: bucket["id"] ?? "codex")
            let name = String(describing: bucket["name"] ?? "Codex")
            return (bucket["windows"] as? [[String: Any]] ?? []).enumerated().compactMap {
                (index, window) -> CodexUsageItem? in
                guard let remaining = (window["remaining_percent"] as? NSNumber)?.intValue else {
                    return nil
                }
                let minutes = (window["window_minutes"] as? NSNumber)?.intValue ?? 0
                let reset = (window["resets_at"] as? NSNumber)?.doubleValue ?? 0
                let label: String
                if minutes == 10_080 {
                    label = "每周"
                } else if minutes > 0, minutes.isMultiple(of: 60) {
                    label = "\(minutes / 60) 小时"
                } else if minutes > 0 {
                    label = "\(minutes) 分钟"
                } else {
                    label = "额度"
                }
                return CodexUsageItem(
                    id: "\(bucketID)-\(index)",
                    name: name,
                    windowLabel: label,
                    remainingPercent: min(100, max(0, remaining)),
                    resetsAt: reset > 0 ? Date(timeIntervalSince1970: reset) : nil
                )
            }
        }
        return BridgeHealthSnapshot(
            activeConsumers: (runtime["active_consumers"] as? NSNumber)?.intValue ?? 0,
            activeRuns: (runtime["active_runs"] as? NSNumber)?.intValue ?? 0,
            pendingInputs: pendingInputs,
            pendingDeliveries: pendingDeliveries,
            pendingTaskCreations: pendingTaskCreations,
            maxConcurrentRuns: (runtime["max_concurrent_runs"] as? NSNumber)?.intValue
                ?? (readConfig()["max_concurrent_runs"] as? NSNumber)?.intValue
                ?? 2,
            lastFeishuEventAt: lastEvent > 0 ? Date(timeIntervalSince1970: lastEvent) : nil,
            codexUsage: usageItems,
            codexUsageUpdatedAt: usageUpdatedAt > 0
                ? Date(timeIntervalSince1970: usageUpdatedAt)
                : nil
        )
    }

    func stageAppUpdate(
        downloadedArchive: URL,
        expectedSHA256: String,
        expectedVersion: String
    ) throws -> URL {
        let health = healthSnapshot()
        guard health.pendingInputs == 0,
              health.pendingDeliveries == 0,
              health.pendingTaskCreations == 0 else {
            throw BridgeUpdateError.message("仍有运行队列、待补发结果或新建 Task 请求，请处理完成后再更新。")
        }
        let cleanDigest = expectedSHA256
            .lowercased()
            .replacingOccurrences(of: "sha256:", with: "")
        guard cleanDigest.range(of: "^[0-9a-f]{64}$", options: .regularExpression) != nil else {
            throw BridgeUpdateError.message("GitHub Release 没有提供有效的 SHA-256，已停止更新。")
        }
        let staging = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexFeishuBridgeUpdate-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: false)
        let archive = staging.appendingPathComponent("update.zip")
        try FileManager.default.copyItem(at: downloadedArchive, to: archive)
        let digestResult = run("/usr/bin/shasum", ["-a", "256", archive.path])
        let actualDigest = digestResult.output.split(separator: " ").first.map(String.init) ?? ""
        guard digestResult.status == 0, actualDigest.lowercased() == cleanDigest else {
            throw BridgeUpdateError.message("更新包 SHA-256 校验失败，已停止安装。")
        }
        let expanded = staging.appendingPathComponent("expanded", isDirectory: true)
        try FileManager.default.createDirectory(at: expanded, withIntermediateDirectories: false)
        let extract = run("/usr/bin/ditto", ["-x", "-k", archive.path, expanded.path])
        guard extract.status == 0 else {
            throw BridgeUpdateError.message("无法解压更新包。")
        }
        let app = expanded.appendingPathComponent("Codex 飞书桥接.app", isDirectory: true)
        guard let bundle = Bundle(url: app),
              bundle.bundleIdentifier == "com.deepori.codex-feishu-bridge",
              (bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
                == expectedVersion else {
            throw BridgeUpdateError.message("更新包中的 App 身份或版本不匹配。")
        }
        let signature = run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        guard signature.status == 0 else {
            throw BridgeUpdateError.message("更新包签名验证失败。")
        }
        let architectures = run(
            "/usr/bin/lipo",
            [
                app.appendingPathComponent("Contents/MacOS/CodexFeishuBridge").path,
                "-verify_arch", "arm64", "x86_64",
            ]
        )
        guard architectures.status == 0 else {
            throw BridgeUpdateError.message("更新包不是完整的 Universal App。")
        }
        return app
    }

    func launchAppUpdate(stagedApp: URL, expectedVersion: String) -> CommandResult {
        guard let helper = bundledBridgeDirectory?.appendingPathComponent("app_update.sh") else {
            return CommandResult(status: 1, output: "更新助手不存在")
        }
        let destination = Bundle.main.bundleURL.standardizedFileURL
        let allowedDestinations = [
            URL(fileURLWithPath: "/Applications/Codex 飞书桥接.app").standardizedFileURL,
            FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent("Applications/Codex 飞书桥接.app")
                .standardizedFileURL,
        ]
        guard allowedDestinations.contains(destination),
              FileManager.default.isWritableFile(
                atPath: destination.deletingLastPathComponent().path
              ) else {
            return CommandResult(
                status: 1,
                output: "当前 App 不在可更新的 Applications 目录，或目录不可写。请下载正式安装包后再打开。"
            )
        }
        let process = Process()
        process.executableURL = helper
        process.arguments = [
            stagedApp.path,
            destination.path,
            String(ProcessInfo.processInfo.processIdentifier),
            expectedVersion,
        ]
        do {
            try process.run()
            return CommandResult(status: 0, output: "")
        } catch {
            return CommandResult(status: 1, output: error.localizedDescription)
        }
    }

    private func readJSONObject(at url: URL) -> [String: Any] {
        guard let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return [:]
        }
        return object
    }

    func removeAccessRequests(openIDs: Set<String>) throws {
        guard !openIDs.isEmpty else { return }
        guard let script = bundledBridgeDirectory?.appendingPathComponent("feishu_codex_bridge.py") else {
            throw BridgeUpdateError.message("App 内置桥接脚本不存在。")
        }
        let payload = try JSONSerialization.data(withJSONObject: openIDs.sorted())
        guard let input = String(data: payload, encoding: .utf8) else {
            throw BridgeUpdateError.message("无法编码授权申请更新。")
        }
        let result = run(
            script.path,
            ["--remove-access-requests"],
            standardInput: input,
            redacting: nil
        )
        if result.status != 0 {
            throw BridgeUpdateError.message(
                result.output.isEmpty ? "无法安全更新授权申请。" : result.output
            )
        }
    }

    private func run(_ executable: String, _ arguments: [String]) -> CommandResult {
        run(executable, arguments, standardInput: nil, redacting: nil)
    }

    private func run(
        _ executable: String,
        _ arguments: [String],
        standardInput: String?,
        redacting secret: String?
    ) -> CommandResult {
        guard FileManager.default.isExecutableFile(atPath: executable) else {
            return CommandResult(status: 1, output: "找不到可执行文件：\(executable)")
        }
        let process = Process()
        let pipe = Pipe()
        let inputPipe = Pipe()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = pipe
        if standardInput != nil {
            process.standardInput = inputPipe
        }
        do {
            try process.run()
            if let standardInput {
                inputPipe.fileHandleForWriting.write(Data(standardInput.utf8))
                try? inputPipe.fileHandleForWriting.close()
            }
            process.waitUntilExit()
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            var output = String(data: data, encoding: .utf8) ?? ""
            if let secret, !secret.isEmpty {
                output = output.replacingOccurrences(of: secret, with: "[REDACTED]")
            }
            return CommandResult(
                status: process.terminationStatus,
                output: output
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
    @Published var authorizedUserCount = 0
    @Published var health = BridgeHealthSnapshot.empty
    @Published var pendingAccessRequests: [AccessRequestDraft] = []
    @Published var showConnectionSetup = false
    @Published var showConfiguration = false
    @Published var showDiagnosis = false
    @Published var diagnosisPassed = false
    @Published var diagnosisText = ""
    @Published var alertTitle = ""
    @Published var alertMessage: String?
    @Published var availableVersion = ""
    @Published var updateURL: URL?
    @Published var updateSHA256 = ""
    @Published var isUpdating = false

    @Published var draftProfile = "codex-notify"
    @Published var draftUsers = [AuthorizedUserDraft()]
    @Published var draftChats = ""
    @Published var draftCurrentTaskEventKey = "current_task"
    @Published var draftEventKey = "select_task"
    @Published var draftNewTaskEventKey = "new_task"
    @Published var draftArchiveTaskEventKey = "archive_task"
    @Published var draftUsageEventKey = "codex_usage"
    @Published var draftDesktopSyncEventKey = "sync_desktop"
    @Published var draftDesktopSyncSwitchEventKey = "sync_desktop_switch"
    @Published var draftTaskSubscriptionsEventKey = "task_subscriptions"
    @Published var draftTaskSettingsEventKey = "task_settings"
    @Published var draftCompactContextEventKey = "compact_task_context"
    @Published var draftMaxConcurrentRuns = 2
    @Published var setupProfile = "codex-notify"
    @Published var setupAppID = ""
    @Published var setupAppSecret = ""
    @Published var setupResult = ""
    @Published var setupPassed = false
    @Published var setupUsesExistingProfile = false
    @Published var isConfiguringProfile = false
    @Published var isDiscoveringUser = false
    @Published var discoveredOpenID = ""
    @Published var userDiscoveryResult = ""
    @Published var availableProjects: [String] = []

    init(bridge: BridgeController) {
        self.bridge = bridge
        refresh()
    }

    func refresh() {
        isRunning = bridge.isRunning()
        let config = bridge.readConfig()
        profileName = String(describing: config["lark_profile"] ?? "codex-notify")
        authorizedUserCount = configuredUsers(from: config).count
        pendingAccessRequests = bridge.pendingAccessRequests()
        health = bridge.healthSnapshot()
    }

    func checkForUpdates(manual: Bool = false) {
        guard let url = URL(string: "https://api.github.com/repos/WRJ7391117/codex-feishu-bridge/releases/latest") else {
            return
        }
        var request = URLRequest(url: url)
        request.setValue("Codex-Feishu-Bridge", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: request) { [weak self] data, _, error in
            guard let self else { return }
            guard error == nil,
                  let data,
                  let payload = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let rawTag = payload["tag_name"] as? String else {
                if manual {
                    Task { @MainActor in
                        self.presentError(
                            title: "检查更新失败",
                            message: "无法访问 GitHub Releases。请检查网络；如当前网络无法访问 GitHub，请先连接 VPN 后重试。"
                        )
                    }
                }
                return
            }
            let latest = rawTag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            let current = Bundle.main.object(
                forInfoDictionaryKey: "CFBundleShortVersionString"
            ) as? String ?? "0.0.0"
            let assets = payload["assets"] as? [[String: Any]] ?? []
            let assetURL = assets.first(where: {
                ($0["name"] as? String) == "Codex-Feishu-Bridge-macOS-universal.zip"
            })?["browser_download_url"] as? String
            let asset = assets.first(where: {
                ($0["name"] as? String) == "Codex-Feishu-Bridge-macOS-universal.zip"
            })
            let digest = asset?["digest"] as? String ?? ""
            Task { @MainActor in
                if latest.compare(current, options: .numeric) == .orderedDescending {
                    self.availableVersion = latest
                    self.updateURL = assetURL.flatMap(URL.init(string:))
                    self.updateSHA256 = digest
                    if manual {
                        self.alertTitle = "发现新版本 v\(latest)"
                        self.alertMessage = "已找到经过 SHA-256 标记的 Universal 安装包，可直接下载安装。"
                    }
                } else if manual {
                    self.alertTitle = "已是最新版本"
                    self.alertMessage = "当前版本 v\(current)。"
                }
            }
        }.resume()
    }

    func installUpdate() {
        guard !isUpdating else { return }
        health = bridge.healthSnapshot()
        guard health.activeRuns == 0,
              health.pendingInputs == 0,
              health.pendingDeliveries == 0,
              health.pendingTaskCreations == 0 else {
            presentError(
                title: "暂不能更新",
                message: "仍有运行中的 Task、排队消息、待补发结果或新建 Task 请求。全部处理完成后再更新，避免打断飞书任务。"
            )
            return
        }
        guard let updateURL, !availableVersion.isEmpty, !updateSHA256.isEmpty else {
            checkForUpdates(manual: true)
            return
        }
        isUpdating = true
        let version = availableVersion
        let digest = updateSHA256
        let updater = bridge
        URLSession.shared.downloadTask(with: updateURL) { [weak self] temporaryURL, _, error in
            guard let self else { return }
            guard error == nil, let temporaryURL else {
                Task { @MainActor in
                    self.isUpdating = false
                    self.presentError(
                        title: "更新失败",
                        message: "无法从 GitHub 下载安装包。请检查网络；如当前网络无法访问 GitHub，请先连接 VPN 后重试。"
                    )
                }
                return
            }
            do {
                let staged = try updater.stageAppUpdate(
                    downloadedArchive: temporaryURL,
                    expectedSHA256: digest,
                    expectedVersion: version
                )
                Task { @MainActor in
                    let result = updater.launchAppUpdate(
                        stagedApp: staged,
                        expectedVersion: version
                    )
                    if result.status == 0 {
                        NSApplication.shared.terminate(nil)
                    } else {
                        self.isUpdating = false
                        self.presentError(title: "更新失败", message: result.output)
                    }
                }
            } catch {
                Task { @MainActor in
                    self.isUpdating = false
                    self.presentError(title: "更新失败", message: error.localizedDescription)
                }
            }
        }.resume()
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

    func prepareConnectionSetup() {
        let configuredProfile = String(
            describing: bridge.readConfig()["lark_profile"] ?? "codex-notify"
        )
        setupProfile = configuredProfile
        setupAppID = ""
        setupAppSecret = ""
        setupUsesExistingProfile = hasConfiguredUsers
        setupResult = setupUsesExistingProfile ? "正在检查现有连接…" : ""
        setupPassed = false
        discoveredOpenID = ""
        userDiscoveryResult = ""
        showConnectionSetup = true
        if setupUsesExistingProfile {
            checkExistingProfile()
        }
    }

    func checkExistingProfile() {
        guard !isConfiguringProfile else { return }
        let profile = setupProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !profile.isEmpty else {
            setupUsesExistingProfile = false
            setupResult = "现有连接名称为空，请重新配置凭证。"
            return
        }
        isConfiguringProfile = true
        let controller = bridge
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let checked = controller.checkLarkProfile(profile)
            Task { @MainActor in
                guard let self else { return }
                self.isConfiguringProfile = false
                self.setupPassed = checked.status == 0
                self.setupUsesExistingProfile = self.setupPassed
                self.setupResult = self.setupPassed
                    ? "现有连接已通过 Bot 身份与飞书网络检查，无需重新输入 App ID 或 App Secret。"
                    : (checked.output.isEmpty ? "现有连接检查失败，请重新配置凭证。" : checked.output)
            }
        }
    }

    func startCredentialReconfiguration() {
        setupUsesExistingProfile = false
        setupPassed = false
        setupResult = "请输入新的 App ID 和 App Secret。"
    }

    func configureProfileAndCheck() {
        guard !isConfiguringProfile else { return }
        let profile = setupProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        let appID = setupAppID.trimmingCharacters(in: .whitespacesAndNewlines)
        let appSecret = setupAppSecret
        guard !profile.isEmpty else {
            presentError(title: "无法保存连接", message: "Profile 名称不能为空。")
            return
        }
        guard appID.hasPrefix("cli_") else {
            presentError(title: "无法保存连接", message: "App ID 应以 cli_ 开头。")
            return
        }
        guard !appSecret.isEmpty else {
            presentError(title: "无法保存连接", message: "App Secret 不能为空。")
            return
        }
        setupUsesExistingProfile = false
        isConfiguringProfile = true
        setupResult = "正在写入 macOS Keychain 并检查 Bot 连接…"
        setupPassed = false
        let controller = bridge
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let configured = controller.configureLarkProfile(
                profile: profile,
                appID: appID,
                appSecret: appSecret
            )
            let checked = configured.status == 0
                ? controller.checkLarkProfile(profile)
                : configured
            Task { @MainActor in
                guard let self else { return }
                self.setupAppSecret = ""
                self.isConfiguringProfile = false
                self.setupPassed = configured.status == 0 && checked.status == 0
                self.setupResult = self.setupPassed
                    ? "连接信息已安全保存，Bot 身份与飞书网络检查通过。"
                    : (checked.output.isEmpty ? "连接检查失败，请核对 App ID、App Secret 和飞书应用状态。" : checked.output)
                if self.setupPassed {
                    var config = controller.readConfig()
                    config["lark_profile"] = profile
                    do {
                        try controller.writeConfig(config)
                        self.profileName = profile
                    } catch {
                        self.setupPassed = false
                        self.setupResult = "Profile 已创建，但桥接配置未保存：\(error.localizedDescription)"
                    }
                }
            }
        }
    }

    func recheckProfile() {
        guard !isConfiguringProfile else { return }
        let profile = setupProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !profile.isEmpty else {
            presentError(title: "无法检查连接", message: "Profile 名称不能为空。")
            return
        }
        isConfiguringProfile = true
        setupResult = "正在检查 Bot 身份与飞书网络…"
        let controller = bridge
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let checked = controller.checkLarkProfile(profile)
            Task { @MainActor in
                guard let self else { return }
                self.isConfiguringProfile = false
                self.setupPassed = checked.status == 0
                self.setupResult = checked.status == 0
                    ? "Bot 身份与飞书网络检查通过。"
                    : (checked.output.isEmpty ? "连接检查失败。" : checked.output)
            }
        }
    }

    func openDeveloperConsole() {
        if let url = URL(string: "https://open.feishu.cn/app") {
            NSWorkspace.shared.open(url)
        }
    }

    func discoverFirstUser() {
        guard !isDiscoveringUser else { return }
        let profile = setupProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        guard setupPassed, !profile.isEmpty else {
            presentError(title: "暂不能识别用户", message: "请先保存并通过飞书连接检查。")
            return
        }
        isDiscoveringUser = true
        discoveredOpenID = ""
        let challenge = String(format: "%06d", Int.random(in: 0...999_999))
        userDiscoveryResult = "监听已启动。请在两分钟内用机主飞书账号单聊 Bot，并发送验证码：\(challenge)"
        let controller = bridge
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let result = controller.discoverFeishuUser(profile, challenge: challenge)
            Task { @MainActor in
                guard let self else { return }
                self.isDiscoveringUser = false
                if result.status == 0, result.output.hasPrefix("ou_") {
                    self.discoveredOpenID = result.output
                    self.userDiscoveryResult = "已识别首位用户。继续后仍需明确选择可访问项目。"
                } else {
                    self.userDiscoveryResult = result.output.isEmpty
                        ? "未识别到飞书用户，请检查事件订阅后重试。"
                        : result.output
                }
            }
        }
    }

    func continueToUserAuthorization() {
        showConnectionSetup = false
        prepareConfiguration()
        if discoveredOpenID.hasPrefix("ou_") {
            draftUsers = [
                AuthorizedUserDraft(
                    name: "机主",
                    openID: discoveredOpenID,
                    projects: ""
                )
            ]
        }
    }

    func prepareConfiguration() {
        let config = bridge.readConfig()
        availableProjects = bridge.codexProjectNames()
        pendingAccessRequests = bridge.pendingAccessRequests()
        draftProfile = String(describing: config["lark_profile"] ?? "codex-notify")
        draftUsers = configuredUsers(from: config)
        if draftUsers.isEmpty {
            draftUsers = [AuthorizedUserDraft()]
        }
        draftChats = (config["allowed_chat_ids"] as? [String] ?? []).joined(separator: ",")
        draftCurrentTaskEventKey = String(
            describing: config["current_task_menu_event_key"] ?? "current_task"
        )
        draftEventKey = String(describing: config["task_menu_event_key"] ?? "select_task")
        draftNewTaskEventKey = String(
            describing: config["new_task_menu_event_key"] ?? "new_task"
        )
        draftArchiveTaskEventKey = String(
            describing: config["archive_task_menu_event_key"] ?? "archive_task"
        )
        draftUsageEventKey = String(
            describing: config["usage_menu_event_key"] ?? "codex_usage"
        )
        draftDesktopSyncEventKey = String(
            describing: config["desktop_sync_menu_event_key"] ?? "sync_desktop"
        )
        draftDesktopSyncSwitchEventKey = String(
            describing: config["desktop_sync_switch_menu_event_key"] ?? "sync_desktop_switch"
        )
        draftTaskSubscriptionsEventKey = String(
            describing: config["task_subscriptions_menu_event_key"] ?? "task_subscriptions"
        )
        draftTaskSettingsEventKey = String(
            describing: config["task_settings_menu_event_key"] ?? "task_settings"
        )
        draftCompactContextEventKey = String(
            describing: config["compact_context_menu_event_key"] ?? "compact_task_context"
        )
        draftMaxConcurrentRuns = min(
            8,
            max(1, (config["max_concurrent_runs"] as? NSNumber)?.intValue ?? 2)
        )
        showConfiguration = true
    }

    func saveConfiguration() {
        let profile = draftProfile.trimmingCharacters(in: .whitespacesAndNewlines)
        let currentTaskEventKey = draftCurrentTaskEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let eventKey = draftEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let newTaskEventKey = draftNewTaskEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let archiveTaskEventKey = draftArchiveTaskEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let usageEventKey = draftUsageEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let desktopSyncEventKey = draftDesktopSyncEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let desktopSyncSwitchEventKey = draftDesktopSyncSwitchEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let taskSubscriptionsEventKey = draftTaskSubscriptionsEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let taskSettingsEventKey = draftTaskSettingsEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        let compactContextEventKey = draftCompactContextEventKey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !profile.isEmpty else {
            presentError(title: "配置未保存", message: "lark-cli Profile 不能为空。")
            return
        }
        let users = draftUsers.enumerated().map { index, user in
            (
                name: user.name.trimmingCharacters(in: .whitespacesAndNewlines),
                openID: user.openID.trimmingCharacters(in: .whitespacesAndNewlines),
                projects: user.projects
                    .split(whereSeparator: { $0 == "," || $0 == "，" })
                    .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                    .filter { !$0.isEmpty },
                index: index
            )
        }
        guard !users.isEmpty else {
            presentError(title: "配置未保存", message: "至少需要保留一个授权用户。")
            return
        }
        if let invalidUser = users.first(where: { !$0.openID.hasPrefix("ou_") }) {
            presentError(
                title: "配置未保存",
                message: "第 \(invalidUser.index + 1) 个用户的 open_id 必须以 ou_ 开头。"
            )
            return
        }
        let openIDs = users.map(\.openID)
        guard Set(openIDs).count == openIDs.count else {
            presentError(title: "配置未保存", message: "用户 open_id 不能重复。")
            return
        }
        if let emptyProjects = users.first(where: { $0.projects.isEmpty }) {
            presentError(
                title: "配置未保存",
                message: "第 \(emptyProjects.index + 1) 个用户至少需要一个允许项目；全部项目请填写 *。"
            )
            return
        }
        saveConfiguration(
            profile: profile,
            users: users,
            currentTaskEventKey: currentTaskEventKey,
            eventKey: eventKey,
            newTaskEventKey: newTaskEventKey,
            archiveTaskEventKey: archiveTaskEventKey,
            usageEventKey: usageEventKey,
            desktopSyncEventKey: desktopSyncEventKey,
            desktopSyncSwitchEventKey: desktopSyncSwitchEventKey,
            taskSubscriptionsEventKey: taskSubscriptionsEventKey,
            taskSettingsEventKey: taskSettingsEventKey,
            compactContextEventKey: compactContextEventKey
        )
    }

    private func saveConfiguration(
        profile: String,
        users: [(name: String, openID: String, projects: [String], index: Int)],
        currentTaskEventKey: String,
        eventKey: String,
        newTaskEventKey: String,
        archiveTaskEventKey: String,
        usageEventKey: String,
        desktopSyncEventKey: String,
        desktopSyncSwitchEventKey: String,
        taskSubscriptionsEventKey: String,
        taskSettingsEventKey: String,
        compactContextEventKey: String
    ) {
        let menuEventKeys = [
            currentTaskEventKey,
            eventKey,
            newTaskEventKey,
            archiveTaskEventKey,
            usageEventKey,
            desktopSyncEventKey,
            desktopSyncSwitchEventKey,
            taskSubscriptionsEventKey,
            taskSettingsEventKey,
            compactContextEventKey,
        ]
        guard menuEventKeys.allSatisfy({ !$0.isEmpty }) else {
            presentError(title: "配置未保存", message: "十个机器人菜单 Event Key 都不能为空。")
            return
        }
        guard Set(menuEventKeys).count == menuEventKeys.count else {
            presentError(title: "配置未保存", message: "十个机器人菜单 Event Key 不能重复。")
            return
        }

        var config = bridge.readConfig()
        config["lark_profile"] = profile
        config["allowed_users"] = users.map { user in
            [
                "name": user.name.isEmpty ? "用户 \(user.index + 1)" : user.name,
                "open_id": user.openID,
                "allowed_projects": user.projects,
            ] as [String: Any]
        }
        config["allowed_sender_id"] = users[0].openID
        config["allowed_chat_ids"] = draftChats
            .split(separator: ",")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        config["current_task_menu_event_key"] = currentTaskEventKey
        config["task_menu_event_key"] = eventKey
        config["new_task_menu_event_key"] = newTaskEventKey
        config["archive_task_menu_event_key"] = archiveTaskEventKey
        config["usage_menu_event_key"] = usageEventKey
        config["desktop_sync_menu_event_key"] = desktopSyncEventKey
        config["desktop_sync_switch_menu_event_key"] = desktopSyncSwitchEventKey
        config["task_subscriptions_menu_event_key"] = taskSubscriptionsEventKey
        config["task_settings_menu_event_key"] = taskSettingsEventKey
        config["compact_context_menu_event_key"] = compactContextEventKey
        config["max_concurrent_runs"] = min(8, max(1, draftMaxConcurrentRuns))
        config["max_prompt_chars"] = config["max_prompt_chars"] ?? 12000
        config["max_reply_chars"] = config["max_reply_chars"] ?? 3000

        do {
            let wasRunning = bridge.isRunning()
            try bridge.writeConfig(config)
            try bridge.removeAccessRequests(openIDs: Set(users.map(\.openID)))
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
        health = bridge.healthSnapshot()
        guard health.activeRuns == 0,
              health.pendingInputs == 0,
              health.pendingDeliveries == 0,
              health.pendingTaskCreations == 0 else {
            presentError(
                title: "暂不能修复后台服务",
                message: "仍有运行中的 Task 或飞书工作等待处理。全部完成后再修复，现有消息不会被打断。"
            )
            return
        }
        let result = bridge.install()
        if result.status == 0 {
            alertTitle = "后台服务已修复"
            alertMessage = "原有配置和当前 Task 状态均已保留。"
        } else {
            presentError(title: "修复失败", message: result.output)
        }
        refresh()
    }

    func prepareUninstall() {
        health = bridge.healthSnapshot()
        guard health.pendingInputs == 0,
              health.pendingDeliveries == 0,
              health.pendingTaskCreations == 0 else {
            presentError(
                title: "暂不能卸载",
                message: "仍有排队消息、待补发结果或新建 Task 请求。全部处理完成后再卸载。"
            )
            return
        }
        let confirmation = NSAlert()
        confirmation.messageText = "移除后台桥接服务？"
        confirmation.informativeText = "后台服务和运行组件会被移除；飞书 Profile、授权配置、Task 状态和日志会保留，便于以后恢复。App 本身仍需由你移到废纸篓。"
        confirmation.alertStyle = .warning
        confirmation.addButton(withTitle: "移除服务并保留数据")
        confirmation.addButton(withTitle: "取消")
        guard confirmation.runModal() == .alertFirstButtonReturn else { return }
        let result = bridge.uninstallKeepingData()
        if result.status == 0 {
            alertTitle = "后台服务已移除"
            alertMessage = "本机配置和 Task 状态已保留。现在可以退出 App，并按需把 App 移到废纸篓。"
        } else {
            presentError(title: "卸载失败", message: result.output)
        }
        refresh()
    }

    func openLog() {
        let directory = bridge.logURL.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
        if !FileManager.default.fileExists(atPath: bridge.logURL.path) {
            FileManager.default.createFile(atPath: bridge.logURL.path, contents: nil)
        }
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: bridge.logURL.path)
        NSWorkspace.shared.open(bridge.logURL)
    }

    func openSupportDirectory() {
        NSWorkspace.shared.open(bridge.supportDirectory)
    }

    var hasConfiguredUsers: Bool {
        !configuredUsers(from: bridge.readConfig()).isEmpty
    }

    func addUser() {
        draftUsers.append(AuthorizedUserDraft())
    }

    func removeUser(id: UUID) {
        guard draftUsers.count > 1 else { return }
        draftUsers.removeAll { $0.id == id }
    }

    func prepareAccessRequest(_ request: AccessRequestDraft) {
        if !draftUsers.contains(where: { $0.openID == request.openID }) {
            draftUsers.append(
                AuthorizedUserDraft(
                    name: request.name,
                    openID: request.openID,
                    projects: ""
                )
            )
        }
    }

    func denyAccessRequest(_ request: AccessRequestDraft) {
        do {
            try bridge.removeAccessRequests(openIDs: [request.openID])
            pendingAccessRequests = bridge.pendingAccessRequests()
        } catch {
            presentError(title: "申请未处理", message: error.localizedDescription)
        }
    }

    private func configuredUsers(from config: [String: Any]) -> [AuthorizedUserDraft] {
        if let users = config["allowed_users"] as? [[String: Any]] {
            return users.compactMap { user in
                guard let openID = user["open_id"] as? String,
                      openID.hasPrefix("ou_"),
                      let projects = user["allowed_projects"] as? [String],
                      !projects.isEmpty else {
                    return nil
                }
                return AuthorizedUserDraft(
                    name: user["name"] as? String ?? "",
                    openID: openID,
                    projects: projects.joined(separator: ", ")
                )
            }
        }
        let legacySender = String(describing: config["allowed_sender_id"] ?? "")
        return legacySender.hasPrefix("ou_")
            ? [AuthorizedUserDraft(name: "现有用户", openID: legacySender, projects: "*")]
            : []
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
    private let refreshTimer = Timer.publish(every: 2, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            header
            statusCard
            healthCard
            usageCard
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
        .frame(minWidth: 760, idealWidth: 820, minHeight: 760, idealHeight: 800)
        .background(Color(nsColor: .windowBackgroundColor))
        .sheet(isPresented: $model.showConfiguration) {
            ConfigurationView(model: model)
        }
        .sheet(isPresented: $model.showConnectionSetup) {
            ConnectionSetupView(model: model)
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
        .onAppear {
            model.refresh()
            model.checkForUpdates()
        }
        .onReceive(refreshTimer) { _ in
            model.refresh()
        }
    }

    private var header: some View {
        HStack(spacing: 16) {
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable()
                .frame(width: 64, height: 64)
                .cornerRadius(14)
            VStack(alignment: .leading, spacing: 5) {
                Text(ProductBrand.name)
                    .font(.system(size: 26, weight: .semibold))
                Text("\(ProductBrand.tagline) · \(ProductBrand.edition)")
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
                Text(model.isRunning ? "\(ProductBrand.name) 已开启" : "\(ProductBrand.name) 已关闭")
                    .font(.title3.weight(.semibold))
                Text(model.isRunning
                     ? "事件监听 \(model.health.activeConsumers)/3 · 运行 \(model.health.activeRuns)/\(model.health.maxConcurrentRuns)"
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

    private var healthCard: some View {
        GroupBox("运行看板") {
            HStack(spacing: 0) {
                metric("事件消费者", "\(model.health.activeConsumers)/3", "dot.radiowaves.left.and.right")
                Divider().frame(height: 46)
                metric("运行 Task", "\(model.health.activeRuns)/\(model.health.maxConcurrentRuns)", "play.circle")
                Divider().frame(height: 46)
                metric("排队消息", "\(model.health.pendingInputs)", "text.line.first.and.arrowtriangle.forward")
                Divider().frame(height: 46)
                metric("待补发", "\(model.health.pendingDeliveries)", "arrow.clockwise")
                Divider().frame(height: 46)
                metric("最近飞书事件", lastEventText, "clock")
            }
            .padding(.vertical, 6)
        }
    }

    private var usageCard: some View {
        GroupBox("Codex 用量") {
            if model.health.codexUsage.isEmpty {
                HStack(spacing: 8) {
                    Image(systemName: "gauge.with.dots.needle.0percent")
                        .foregroundStyle(.secondary)
                    Text("暂无额度数据；桥接连接 Codex 后会自动刷新。")
                        .foregroundStyle(.secondary)
                    Spacer()
                }
                .padding(.vertical, 6)
            } else {
                VStack(spacing: 10) {
                    ForEach(model.health.codexUsage) { item in
                        HStack(spacing: 12) {
                            Text("\(item.name) · \(item.windowLabel)")
                                .frame(width: 190, alignment: .leading)
                                .lineLimit(1)
                            ProgressView(value: Double(item.remainingPercent), total: 100)
                            Text("剩余 \(item.remainingPercent)%")
                                .font(.headline.monospacedDigit())
                                .foregroundStyle(usageColor(item.remainingPercent))
                                .frame(width: 82, alignment: .trailing)
                            Text(resetText(item.resetsAt))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .frame(width: 118, alignment: .trailing)
                        }
                    }
                }
                .padding(.vertical, 6)
            }
        }
    }

    private func usageColor(_ remaining: Int) -> Color {
        remaining <= 10 ? .red : remaining <= 30 ? .orange : .green
    }

    private func resetText(_ date: Date?) -> String {
        guard let date else { return "重置时间未知" }
        return date.formatted(
            Date.FormatStyle(date: .numeric, time: .shortened)
        ) + " 重置"
    }

    private var lastEventText: String {
        guard let date = model.health.lastFeishuEventAt else { return "暂无" }
        return date.formatted(date: .omitted, time: .shortened)
    }

    private func metric(_ title: String, _ value: String, _ icon: String) -> some View {
        VStack(spacing: 5) {
            Image(systemName: icon).foregroundStyle(.secondary)
            Text(value).font(.headline.monospacedDigit())
            Text(title).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
    }

    private var connectionCard: some View {
        GroupBox("连接信息") {
            VStack(spacing: 12) {
                infoRow(icon: "person.crop.circle", title: "lark-cli Profile", value: model.profileName)
                Divider()
                infoRow(icon: "person.2", title: "授权用户", value: "\(model.authorizedUserCount) 位")
                Divider()
                infoRow(
                    icon: "person.badge.clock",
                    title: "待审批申请",
                    value: "\(model.pendingAccessRequests.count) 条"
                )
                Divider()
                infoRow(
                    icon: "dot.radiowaves.left.and.right",
                    title: "飞书事件",
                    value: "\(model.health.activeConsumers)/3 正常"
                )
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
                actionButton("首次连接向导", icon: "link.badge.plus") {
                    model.prepareConnectionSetup()
                }
                actionButton("配置桥接", icon: "gearshape") { model.prepareConfiguration() }
                actionButton("运行诊断", icon: "stethoscope") { model.runDiagnosis() }
                actionButton(
                    model.isUpdating
                        ? "正在下载并验证更新…"
                        : model.availableVersion.isEmpty
                        ? "检查 App 更新"
                        : "安装 App 更新 v\(model.availableVersion)",
                    icon: "arrow.down.app"
                ) {
                    if model.availableVersion.isEmpty {
                        model.checkForUpdates(manual: true)
                    } else {
                        model.installUpdate()
                    }
                }
                Text("更新包从 GitHub 获取。若当前网络无法访问 GitHub，请先连接 VPN。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                HStack(spacing: 10) {
                    Button("打开日志") { model.openLog() }
                    Button("数据目录") { model.openSupportDirectory() }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                Divider()
                DisclosureGroup {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("修复使用当前 App 内置的组件，不访问 GitHub；原有配置和 Task 状态会保留。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        actionButton("修复后台服务", icon: "wrench.and.screwdriver") {
                            model.installComponents()
                        }
                        Divider()
                        Button("移除后台服务…", role: .destructive) {
                            model.prepareUninstall()
                        }
                        .buttonStyle(.plain)
                    }
                    .padding(.top, 8)
                } label: {
                    Label("高级维护", systemImage: "gearshape.2")
                }
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

private struct ConnectionSetupView: View {
    @ObservedObject var model: BridgeViewModel
    @State private var currentStep = 1
    @State private var showAdvancedSettings = false
    @State private var showConfigurationChecklist = false
    @State private var confirmedSteps = Set<Int>()

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            progressBar
                .padding(.horizontal, 34)
                .padding(.vertical, 22)
            workspace
                .padding(.horizontal, 28)
                .padding(.bottom, 22)
            Divider()
            footer
        }
        .frame(width: 1120, height: 760)
        .background(Color(nsColor: .windowBackgroundColor))
        .sheet(isPresented: $showConfigurationChecklist) {
            ConfigurationChecklistView(model: model)
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            Image(nsImage: NSApp.applicationIconImage)
                .resizable()
                .scaledToFit()
                .frame(width: 48, height: 48)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 8) {
                    Text(ProductBrand.name)
                        .font(.title3.weight(.semibold))
                    Text(ProductBrand.edition)
                        .font(.caption.weight(.medium))
                        .foregroundStyle(Color.accentColor)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(Color.accentColor.opacity(0.10))
                        .clipShape(Capsule())
                    Text(ProductBrand.systemRequirement)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Text("\(ProductBrand.purpose) · \(ProductBrand.localPromise)")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button("关闭向导", systemImage: "xmark") {
                model.showConnectionSetup = false
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 28)
        .padding(.vertical, 18)
    }

    private var progressBar: some View {
        HStack(spacing: 0) {
            ForEach(1...4, id: \.self) { step in
                progressStep(step)
                if step < 4 {
                    Rectangle()
                        .fill(step < currentStep ? Color.green.opacity(0.55) : Color.secondary.opacity(0.22))
                        .frame(height: 1)
                        .padding(.horizontal, 14)
                }
            }
        }
    }

    private func progressStep(_ step: Int) -> some View {
        let titles = ["创建应用", "连接应用", "配置机器人", "授权用户"]
        let complete = step < currentStep || (step == 2 && model.setupPassed && currentStep > 2)
        let current = step == currentStep
        let available = step <= currentStep || step == 2

        return Button {
            currentStep = step
        } label: {
            HStack(spacing: 10) {
                Image(systemName: complete ? "checkmark.circle.fill" : "\(step).circle")
                    .font(.system(size: 27, weight: .medium))
                    .foregroundStyle(complete ? Color.green : (current ? Color.accentColor : Color.secondary))
                VStack(alignment: .leading, spacing: 2) {
                    Text(titles[step - 1])
                        .font(.callout.weight(current ? .semibold : .medium))
                        .foregroundStyle(current ? Color.primary : Color.secondary)
                    Text(complete ? "已完成" : (current ? "进行中" : "待开始"))
                        .font(.caption)
                        .foregroundStyle(current ? Color.accentColor : Color.secondary)
                }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!available)
    }

    private var workspace: some View {
        HStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    mainPanel
                }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(30)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()

            VStack(alignment: .leading, spacing: 18) {
                statusPanel
            }
                .frame(width: 330)
                .frame(maxHeight: .infinity, alignment: .topLeading)
                .padding(28)
        }
        .background(Color(nsColor: .controlBackgroundColor).opacity(0.34))
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 16, style: .continuous)
                .stroke(Color.primary.opacity(0.10), lineWidth: 1)
        }
    }

    @ViewBuilder
    private var mainPanel: some View {
        switch currentStep {
        case 1:
            sectionHeader(
                "现在去飞书完成 2 项准备",
                "\(ProductBrand.name) 需要一个由你管理的企业自建应用。"
            )
            instructionRow(
                icon: "plus.app.fill",
                title: "创建企业自建应用",
                detail: "应用名称和图标可以按团队习惯设置。"
            )
            Divider()
            instructionRow(
                icon: "person.badge.shield.checkmark.fill",
                title: "确认你有应用管理权限",
                detail: "后续需要开启机器人、添加事件并发布版本。"
            )
            Divider()
            HStack(spacing: 18) {
                Button("打开飞书开发者后台", systemImage: "arrow.up.right.square") {
                    model.openDeveloperConsole()
                }
                Button("查看完整配置清单", systemImage: "list.bullet.clipboard") {
                    showConfigurationChecklist = true
                }
            }
            .buttonStyle(.link)

        case 2:
            sectionHeader(
                "连接你的飞书应用",
                "优先使用本机已保存的连接；只有更换应用时才需要重新输入凭证。"
            )
            if model.setupUsesExistingProfile {
                VStack(alignment: .leading, spacing: 12) {
                    Label(
                        model.isConfiguringProfile ? "正在检查现有连接" : "现有连接已可用",
                        systemImage: model.isConfiguringProfile ? "clock" : "checkmark.circle.fill"
                    )
                        .font(.headline)
                        .foregroundStyle(model.isConfiguringProfile ? Color.secondary : Color.green)
                    Text("本机连接“\(model.setupProfile)”已保存于 macOS 钥匙串，无需再次输入 App ID 或 App Secret。")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                    Button("重新配置凭证") {
                        model.startCredentialReconfiguration()
                    }
                    .buttonStyle(.bordered)
                }
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.green.opacity(0.08))
                .clipShape(RoundedRectangle(cornerRadius: 12))
            } else {
                HStack {
                    Spacer()
                    Button("App ID 在哪里？", systemImage: "arrow.up.right.square") {
                        model.openDeveloperConsole()
                    }
                    .buttonStyle(.link)
                }

                setupField("App ID", placeholder: "cli_...", text: $model.setupAppID)
                VStack(alignment: .leading, spacing: 6) {
                    Text("App Secret")
                        .font(.callout.weight(.medium))
                    SecureField("请输入 App Secret", text: $model.setupAppSecret)
                        .textFieldStyle(.roundedBorder)
                        .font(.system(.body, design: .monospaced))
                    Label("App Secret 安全存入 macOS 钥匙串", systemImage: "lock.fill")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Divider().padding(.vertical, 4)
                DisclosureGroup("高级设置", isExpanded: $showAdvancedSettings) {
                    VStack(alignment: .leading, spacing: 6) {
                        setupField(
                            "本机连接名称",
                            placeholder: "codex-notify",
                            text: $model.setupProfile
                        )
                        Text("仅用于在这台 Mac 上区分多个飞书应用，通常无需修改。")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .padding(.top, 8)
                }
            }

        case 3:
            sectionHeader(
                "现在去飞书完成 3 项设置",
                "这三项共同决定机器人能否收发消息、响应卡片和显示菜单。"
            )
            instructionRow(
                icon: "message.fill",
                title: "1. 开启机器人能力",
                detail: "在“添加应用能力”中添加机器人。"
            )
            Divider()
            instructionRow(
                icon: "bubble.left.and.bubble.right.fill",
                title: "2. 添加消息、卡片与菜单事件",
                detail: "按完整配置清单添加三个事件消费者和菜单 Event Key。"
            )
            Divider()
            instructionRow(
                icon: "square.and.arrow.up.fill",
                title: "3. 创建并发布应用版本",
                detail: "发布后设置才会生效，菜单通常会在几分钟内刷新。"
            )
            Divider()
            HStack(spacing: 10) {
                Button("打开飞书开发者后台", systemImage: "arrow.up.right.square") {
                    model.openDeveloperConsole()
                }
                Button("查看完整配置清单", systemImage: "list.bullet.clipboard") {
                    showConfigurationChecklist = true
                }
            }
            .buttonStyle(.link)

        default:
            sectionHeader(
                "授权首位使用者",
                "用需要使用 \(ProductBrand.name) 的飞书账号单聊机器人，再完成项目授权。"
            )
            instructionRow(
                icon: "person.crop.circle.badge.plus",
                title: "1. 启动两分钟识别",
                detail: "\(ProductBrand.name) 会生成一次性验证码并等待一条新的机器人单聊消息。"
            )
            Divider()
            instructionRow(
                icon: "ellipsis.message.fill",
                title: "2. 在飞书发送验证码",
                detail: "只接受这次显示的验证码，不会把普通历史消息识别为授权请求。"
            )
        }
    }

    @ViewBuilder
    private var statusPanel: some View {
        switch currentStep {
        case 1:
            sidePanelHeader(
                icon: confirmedSteps.contains(1) ? "checkmark.shield.fill" : "shield",
                title: confirmedSteps.contains(1) ? "准备已确认" : "完成后继续",
                detail: confirmedSteps.contains(1)
                    ? "可以进入下一步，连接刚刚创建的飞书应用。"
                    : "完成左侧两项准备后，在这里确认。"
            )
            Spacer()
            Button(confirmedSteps.contains(1) ? "已完成" : "我已完成") {
                confirmedSteps.insert(1)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(confirmedSteps.contains(1))

        case 2:
            connectionStatusPanel
            Spacer()
            if !model.setupUsesExistingProfile {
                if !model.setupResult.isEmpty && !model.setupPassed {
                    Button("重新检查") { model.recheckProfile() }
                        .disabled(model.isConfiguringProfile)
                }
                Button(model.isConfiguringProfile ? "正在检查…" : "保存并检查连接") {
                    model.configureProfileAndCheck()
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(model.isConfiguringProfile)
            }

        case 3:
            sidePanelHeader(
                icon: consoleCheckPassed ? "checkmark.shield.fill" : "checkmark.shield",
                title: consoleCheckPassed ? "基础连接检查通过" : "完成后检查",
                detail: consoleCheckPassed
                    ? "\(ProductBrand.name) 已确认 Bot 身份和飞书网络可用。权限、事件与版本仍以开放平台显示为准。"
                    : "检查 Bot 身份和飞书网络，并保留一份清晰的人工核对边界。"
            )
            Spacer()
            Button(
                model.isConfiguringProfile
                    ? "正在检查…"
                    : (consoleCheckPassed ? "重新检查" : "我已完成，开始检查")
            ) {
                confirmedSteps.insert(3)
                model.recheckProfile()
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(model.isConfiguringProfile)

        default:
            sidePanelHeader(
                icon: authorizationStatusIcon,
                title: authorizationStatusTitle,
                detail: authorizationStatusDetail
            )
            Spacer()
            Button(
                model.isDiscoveringUser
                    ? "正在等待飞书消息…"
                    : (model.hasConfiguredUsers ? "识别新使用者" : "开始识别使用者")
            ) {
                model.discoverFirstUser()
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .disabled(!model.setupPassed || model.isDiscoveringUser)

            Text("识别成功后仍需明确选择可访问的 Codex 项目，不会默认开放全部项目。")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var connectionStatusPanel: some View {
        let icon = model.isConfiguringProfile
            ? "clock"
            : (model.setupPassed ? "checkmark.shield.fill" : "bolt.horizontal.circle")
        let title = model.setupPassed
            ? "连接检查通过"
            : (model.isConfiguringProfile ? "正在检查连接" : "等待检查")
        let detail = model.setupResult.isEmpty
            ? "保存后，\(ProductBrand.name) 会自动验证 Bot 身份和飞书网络。"
            : model.setupResult

        return sidePanelHeader(icon: icon, title: title, detail: detail)
    }

    private var consoleCheckPassed: Bool {
        confirmedSteps.contains(3) && model.setupPassed && !model.isConfiguringProfile
    }

    private var authorizationStatusIcon: String {
        if !model.discoveredOpenID.isEmpty || model.hasConfiguredUsers {
            return "person.crop.circle.badge.checkmark"
        }
        return "person.badge.clock"
    }

    private var authorizationStatusTitle: String {
        if !model.discoveredOpenID.isEmpty {
            return "使用者已识别"
        }
        return model.hasConfiguredUsers ? "已有授权用户" : "等待识别"
    }

    private var authorizationStatusDetail: String {
        if !model.userDiscoveryResult.isEmpty {
            return model.userDiscoveryResult
        }
        if model.hasConfiguredUsers {
            return "可以直接打开授权设置；需要添加其他用户时，再启动一次识别。"
        }
        return "启动识别后，请按这里显示的提示到飞书发送验证码。"
    }

    private var footer: some View {
        HStack {
            if currentStep > 1 {
                Button("返回") { currentStep -= 1 }
            } else {
                Button("稍后设置") { model.showConnectionSetup = false }
                    .keyboardShortcut(.cancelAction)
            }
            Spacer()
            Text("第 \(currentStep) 步，共 4 步")
                .font(.callout)
                .foregroundStyle(.secondary)
            Spacer()
            Button(primaryFooterTitle) { advance() }
                .buttonStyle(.borderedProminent)
                .disabled(!canAdvance)
                .frame(minWidth: 110)
        }
        .padding(.horizontal, 28)
        .padding(.vertical, 16)
    }

    private var primaryFooterTitle: String {
        currentStep == 4 ? "配置授权" : "继续"
    }

    private var canAdvance: Bool {
        switch currentStep {
        case 1:
            return confirmedSteps.contains(1)
        case 2:
            return model.setupPassed
        case 3:
            return consoleCheckPassed
        case 4:
            return model.setupPassed
                && (model.hasConfiguredUsers || !model.discoveredOpenID.isEmpty)
        default:
            return false
        }
    }

    private func advance() {
        if currentStep == 4 {
            model.continueToUserAuthorization()
        } else {
            currentStep += 1
        }
    }

    private func sectionHeader(_ title: String, _ subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title)
                .font(.title2.weight(.semibold))
            Text(subtitle)
                .font(.body)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.bottom, 6)
    }

    private func sidePanelHeader(icon: String, title: String, detail: String) -> some View {
        VStack(alignment: .center, spacing: 16) {
            Image(systemName: icon)
                .font(.system(size: 52, weight: .light))
                .foregroundStyle(title.contains("通过") || title.contains("已") ? Color.green : Color.accentColor)
                .frame(height: 64)
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.title3.weight(.semibold))
                    .frame(maxWidth: .infinity, alignment: .center)
                Text(detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: .infinity)
    }

    private func instructionRow(icon: String, title: String, detail: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            ZStack {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(Color.accentColor.opacity(0.10))
                    .frame(width: 46, height: 46)
                Image(systemName: icon)
                    .font(.title3)
                    .foregroundStyle(Color.accentColor)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(.headline)
                Text(detail)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.vertical, 8)
    }

    private func setupField(
        _ title: String,
        placeholder: String,
        text: Binding<String>
    ) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.callout.weight(.medium))
            TextField(placeholder, text: text)
                .textFieldStyle(.roundedBorder)
                .font(.system(.body, design: .monospaced))
        }
    }
}

private struct ConfigurationChecklistView: View {
    @ObservedObject var model: BridgeViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text("完整配置清单")
                        .font(.title2.weight(.semibold))
                    Text("在飞书开放平台逐项核对；完成后创建并发布应用版本。")
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Button("完成") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(24)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    checklistSection("机器人权限", items: [
                        "接收单聊消息、读取消息及消息资源",
                        "发送和更新消息、上传图片与文件（含 im:resource）",
                    ])
                    checklistSection("长连接事件与卡片回调", items: [
                        "im.message.receive_v1",
                        "application.bot.menu_v6",
                        "card.action.trigger",
                    ])
                    checklistSection("机器人菜单 Event Key", items: [
                        "一级菜单 · Task 管理",
                        "current_task · 当前 Task",
                        "select_task · 切换 Task",
                        "new_task · 新建 Task",
                        "archive_task · 归档当前 Task",
                        "一级菜单 · 管理桌面 Task",
                        "task_subscriptions · 订阅桌面 Task",
                        "sync_desktop · 接续当前 Task",
                        "sync_desktop_switch · 接续其他 Task",
                        "一级菜单 · 模型设置",
                        "task_settings · 修改当前 Task 模型",
                        "compact_task_context · 压缩当前 Task 上下文",
                        "codex_usage · Codex 额度用量",
                    ])
                    Divider()
                    Button("打开飞书开放平台", systemImage: "arrow.up.right.square") {
                        model.openDeveloperConsole()
                    }
                    .buttonStyle(.borderedProminent)
                }
                .padding(24)
            }
        }
        .frame(width: 620, height: 560)
    }

    private func checklistSection(_ title: String, items: [String]) -> some View {
        GroupBox(title) {
            VStack(alignment: .leading, spacing: 9) {
                ForEach(items, id: \.self) { item in
                    Label(item, systemImage: "circle")
                        .font(.callout)
                        .textSelection(.enabled)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, 6)
        }
    }
}

private struct ConfigurationView: View {
    @ObservedObject var model: BridgeViewModel
    @State private var showAdvancedSettings = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    connectionSettings
                    advancedSettings
                    accessRequests
                    authorizedUsers
                    Text("项目名必须与 Codex Desktop 左侧栏完全一致。多个群 Chat ID 使用英文逗号分隔；留空时优先使用与 Bot 的单聊。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(22)
            }
            Divider()
            footer
        }
        .frame(width: 720, height: 560)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text("桥接配置")
                .font(.title2.weight(.semibold))
            Text("App Secret 由 lark-cli 和 macOS Keychain 管理，不会保存在这里。")
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 24)
        .padding(.vertical, 18)
    }

    private var connectionSettings: some View {
        GroupBox("连接设置") {
            VStack(alignment: .leading, spacing: 5) {
                configurationField(
                    "lark-cli Profile",
                    placeholder: "例如 codex-notify",
                    text: $model.draftProfile,
                    monospaced: true
                )
                configurationField(
                    "允许的群 Chat ID",
                    placeholder: "可选；多个 ID 使用英文逗号分隔",
                    text: $model.draftChats,
                    monospaced: true
                )
                Divider().padding(.vertical, 5)
                Stepper(
                    "最多同时运行 \(model.draftMaxConcurrentRuns) 个 Task",
                    value: $model.draftMaxConcurrentRuns,
                    in: 1...8
                )
                Text("不同 Task 可并行；同一 Task 始终按顺序执行。提高并发会增加内存和 CPU 占用。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            .padding(.top, 6)
        }
    }

    private var advancedSettings: some View {
        GroupBox {
            DisclosureGroup("机器人菜单 Event Key", isExpanded: $showAdvancedSettings) {
                VStack(alignment: .leading, spacing: 10) {
                    configurationField(
                        "当前 Task",
                        placeholder: "current_task",
                        text: $model.draftCurrentTaskEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "切换 Task",
                        placeholder: "select_task",
                        text: $model.draftEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "新建 Task",
                        placeholder: "new_task",
                        text: $model.draftNewTaskEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "归档当前 Task",
                        placeholder: "archive_task",
                        text: $model.draftArchiveTaskEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "修改当前 Task 模型",
                        placeholder: "task_settings",
                        text: $model.draftTaskSettingsEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "压缩当前 Task 上下文",
                        placeholder: "compact_task_context",
                        text: $model.draftCompactContextEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "Codex 额度用量",
                        placeholder: "codex_usage",
                        text: $model.draftUsageEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "接续当前 Task",
                        placeholder: "sync_desktop",
                        text: $model.draftDesktopSyncEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "接续其他 Task",
                        placeholder: "sync_desktop_switch",
                        text: $model.draftDesktopSyncSwitchEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "订阅桌面 Task",
                        placeholder: "task_subscriptions",
                        text: $model.draftTaskSubscriptionsEventKey,
                        monospaced: true
                    )
                }
                .padding(.top, 10)
            }
            .padding(.vertical, 4)
        }
    }

    @ViewBuilder
    private var accessRequests: some View {
        if !model.pendingAccessRequests.isEmpty {
            GroupBox("待审批访问申请") {
                VStack(alignment: .leading, spacing: 10) {
                    ForEach(model.pendingAccessRequests) { request in
                        HStack(spacing: 10) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(request.name.isEmpty ? "飞书用户" : request.name)
                                Text(request.openID)
                                    .font(.caption.monospaced())
                                    .foregroundStyle(.secondary)
                                    .textSelection(.enabled)
                            }
                            Spacer()
                            Button("拒绝", role: .destructive) {
                                model.denyAccessRequest(request)
                            }
                            Button("配置授权") {
                                model.prepareAccessRequest(request)
                            }
                            .buttonStyle(.borderedProminent)
                        }
                        .padding(10)
                        .background(Color.orange.opacity(0.08))
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                    }
                    Text("配置授权后仍需填写明确项目并保存；留空不会获得权限。")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .padding(.top, 6)
            }
        }
    }

    private var authorizedUsers: some View {
        GroupBox {
            VStack(spacing: 12) {
                ForEach($model.draftUsers) { $user in
                    VStack(alignment: .leading, spacing: 10) {
                        HStack {
                            Text(user.name.isEmpty ? "未命名用户" : user.name)
                                .font(.headline)
                            Spacer()
                            Button(role: .destructive) {
                                model.removeUser(id: user.id)
                            } label: {
                                Label("删除", systemImage: "trash")
                            }
                            .buttonStyle(.borderless)
                            .disabled(model.draftUsers.count == 1)
                            .help(
                                model.draftUsers.count == 1
                                    ? "至少保留一位授权用户"
                                    : "删除这位用户"
                            )
                        }
                        configurationField(
                            "备注名",
                            placeholder: "用于本机辨认，不会发送给 Codex",
                            text: $user.name
                        )
                        configurationField(
                            "用户 open_id",
                            placeholder: "ou_...",
                            text: $user.openID,
                            monospaced: true
                        )
                        configurationField(
                            "允许项目",
                            placeholder: "英文逗号分隔；* 表示全部项目",
                            text: $user.projects
                        )
                        if !model.availableProjects.isEmpty {
                            Menu("从 Codex 左侧栏选择") {
                                ForEach(model.availableProjects, id: \.self) { project in
                                    Button {
                                        toggleProject(project, projects: $user.projects)
                                    } label: {
                                        if selectedProjects(user.projects).contains(project) {
                                            Label(project, systemImage: "checkmark")
                                        } else {
                                            Text(project)
                                        }
                                    }
                                }
                            }
                            Text("已读取这台 Mac 的 Codex 项目；可多选。全部项目仍需手动输入 *，避免误授权。")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(14)
                    .background(Color(nsColor: .controlBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 10))
                    .overlay(
                        RoundedRectangle(cornerRadius: 10)
                            .stroke(Color.primary.opacity(0.08), lineWidth: 1)
                    )
                }
            }
            .padding(.top, 6)
        } label: {
            HStack {
                Text("授权用户")
                Spacer()
                Button("添加用户", systemImage: "plus") { model.addUser() }
            }
        }
    }

    private var footer: some View {
        HStack {
            Text("保存后，正在运行的桥接会自动重启并保留当前 Task。")
                .font(.caption)
                .foregroundStyle(.secondary)
            Spacer()
            Button("取消") { model.showConfiguration = false }
                .keyboardShortcut(.cancelAction)
            Button("保存") { model.saveConfiguration() }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 14)
    }

    private func configurationField(
        _ title: String,
        placeholder: String,
        text: Binding<String>,
        monospaced: Bool = false
    ) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title)
                .font(.caption.weight(.medium))
                .foregroundStyle(.secondary)
            TextField(placeholder, text: text)
                .textFieldStyle(.roundedBorder)
                .font(monospaced ? .system(.body, design: .monospaced) : .body)
        }
    }

    private func selectedProjects(_ value: String) -> Set<String> {
        Set(
            value
                .split(whereSeparator: { $0 == "," || $0 == "，" })
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty }
        )
    }

    private func toggleProject(_ project: String, projects: Binding<String>) {
        var selected = selectedProjects(projects.wrappedValue)
        if selected.contains("*") {
            selected = [project]
        } else if selected.contains(project) {
            selected.remove(project)
        } else {
            selected.insert(project)
        }
        projects.wrappedValue = selected
            .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
            .joined(separator: ", ")
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
        buildApplicationMenu()
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
        if !model.hasConfiguredUsers {
            DispatchQueue.main.async { [weak self] in
                self?.model.prepareConnectionSetup()
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
        newWindow.title = ProductBrand.name
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

    private func buildApplicationMenu() {
        let application = NSApplication.shared
        let mainMenu = NSMenu()

        let appMenuItem = NSMenuItem()
        let appMenu = NSMenu(title: ProductBrand.name)
        appMenu.addItem(
            withTitle: "关于 \(ProductBrand.name)",
            action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)),
            keyEquivalent: ""
        )
        appMenu.addItem(.separator())
        appMenu.addItem(
            withTitle: "退出 \(ProductBrand.name)",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        appMenuItem.submenu = appMenu
        mainMenu.addItem(appMenuItem)

        let editMenuItem = NSMenuItem()
        let editMenu = NSMenu(title: "编辑")
        editMenu.addItem(
            withTitle: "撤销",
            action: Selector(("undo:")),
            keyEquivalent: "z"
        )
        let redoItem = editMenu.addItem(
            withTitle: "重做",
            action: Selector(("redo:")),
            keyEquivalent: "z"
        )
        redoItem.keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(.separator())
        editMenu.addItem(
            withTitle: "剪切",
            action: #selector(NSText.cut(_:)),
            keyEquivalent: "x"
        )
        editMenu.addItem(
            withTitle: "复制",
            action: #selector(NSText.copy(_:)),
            keyEquivalent: "c"
        )
        editMenu.addItem(
            withTitle: "粘贴",
            action: #selector(NSText.paste(_:)),
            keyEquivalent: "v"
        )
        editMenu.addItem(
            withTitle: "全选",
            action: #selector(NSText.selectAll(_:)),
            keyEquivalent: "a"
        )
        editMenuItem.submenu = editMenu
        mainMenu.addItem(editMenuItem)

        application.mainMenu = mainMenu
    }

    private func buildStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let image = NSImage(
            systemSymbolName: "arrow.left.arrow.right.circle",
            accessibilityDescription: ProductBrand.name
        ) {
            image.isTemplate = true
            statusItem.button?.image = image
        } else {
            statusItem.button?.title = "↔"
        }
        statusItem.button?.toolTip = ProductBrand.name

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

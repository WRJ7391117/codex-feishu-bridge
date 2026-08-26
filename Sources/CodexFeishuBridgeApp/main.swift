import AppKit
import Combine
import Foundation
import SwiftUI

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
            "workflow_notifications.py": "workflow_notifications.py",
            "workflow_notify.py": "workflow-notify",
            "workflow_config.py": "workflow-config",
            "control.sh": "control.sh",
            "diagnose.sh": "diagnose.sh",
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
            ["-verify_arch", "arm64", "x86_64", app.appendingPathComponent("Contents/MacOS/CodexFeishuBridge").path]
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
        guard !openIDs.isEmpty,
              let data = try? Data(contentsOf: stateURL),
              var state = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let requests = state["access_requests"] as? [[String: Any]] else {
            return
        }
        state["access_requests"] = requests.filter { request in
            guard let openID = request["open_id"] as? String else { return false }
            return !openIDs.contains(openID)
        }
        let encoded = try JSONSerialization.data(withJSONObject: state, options: [.prettyPrinted, .sortedKeys])
        var payload = encoded
        payload.append(0x0A)
        try FileManager.default.createDirectory(
            at: stateURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try payload.write(to: stateURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: stateURL.path)
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
    @Published var authorizedUserCount = 0
    @Published var health = BridgeHealthSnapshot.empty
    @Published var pendingAccessRequests: [AccessRequestDraft] = []
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
    @Published var draftMaxConcurrentRuns = 2

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
                            message: "无法读取 GitHub Releases，请稍后重试。"
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
        guard health.pendingInputs == 0,
              health.pendingDeliveries == 0,
              health.pendingTaskCreations == 0 else {
            presentError(
                title: "暂不能更新",
                message: "仍有排队消息、待补发结果或新建 Task 请求。全部处理完成后再更新，避免打断飞书任务。"
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
                    self.presentError(title: "更新失败", message: "下载安装包失败，请稍后重试。")
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

    func prepareConfiguration() {
        let config = bridge.readConfig()
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
            desktopSyncEventKey: desktopSyncEventKey
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
        desktopSyncEventKey: String
    ) {
        let menuEventKeys = [
            currentTaskEventKey,
            eventKey,
            newTaskEventKey,
            archiveTaskEventKey,
            usageEventKey,
            desktopSyncEventKey,
        ]
        guard menuEventKeys.allSatisfy({ !$0.isEmpty }) else {
            presentError(title: "配置未保存", message: "六个机器人菜单 Event Key 都不能为空。")
            return
        }
        guard Set(menuEventKeys).count == menuEventKeys.count else {
            presentError(title: "配置未保存", message: "六个机器人菜单 Event Key 不能重复。")
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
        guard health.pendingInputs == 0,
              health.pendingDeliveries == 0,
              health.pendingTaskCreations == 0 else {
            presentError(
                title: "暂不能更新后台组件",
                message: "仍有飞书工作等待处理。队列清零后再更新，现有消息不会被打断。"
            )
            return
        }
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
                actionButton("配置桥接", icon: "gearshape") { model.prepareConfiguration() }
                actionButton("运行诊断", icon: "stethoscope") { model.runDiagnosis() }
                actionButton("安装/更新后台组件", icon: "arrow.triangle.2.circlepath") {
                    model.installComponents()
                }
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
                        "选择 Task",
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
                        "额度用量",
                        placeholder: "codex_usage",
                        text: $model.draftUsageEventKey,
                        monospaced: true
                    )
                    configurationField(
                        "接续桌面",
                        placeholder: "sync_desktop",
                        text: $model.draftDesktopSyncEventKey,
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
        if !model.hasConfiguredUsers {
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

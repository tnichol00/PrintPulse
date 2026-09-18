import Foundation
import CoreFoundation

struct CloudSession: Codable, Equatable {
    var id = UUID().uuidString
    let token: String
    let username: String
    let email: String
    let china: Bool
}

enum PrintState: String, Codable {
    case printing = "Printing", preparing = "Preparing", paused = "Paused"
    case idle = "Idle", completed = "Completed", error = "Failed", offline = "Offline", unknown = "Waiting"
    var active: Bool { [.printing, .preparing, .paused].contains(self) }
    static func parse(_ value: String) -> Self {
        switch value.uppercased() {
        case "RUNNING", "PRINTING": return .printing
        case "PREPARE", "PREPARING", "SLICING", "INIT": return .preparing
        case "PAUSE", "PAUSED": return .paused
        case "FINISH", "SUCCESS", "COMPLETED": return .completed
        case "FAILED", "ERROR": return .error
        case "IDLE": return .idle
        case "OFFLINE": return .offline
        default: return .unknown
        }
    }
}

func text(_ object: [String: Any], _ key: String) -> String? {
    if let value = object[key] as? String { return value }
    if let value = object[key] as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID() { return value.stringValue }
    return nil
}

struct PrinterSnapshot: Codable, Identifiable, Equatable {
    let id: String
    var name: String
    var model = "Bambu Lab"
    var job = ""
    var taskID = ""
    var state: PrintState = .unknown
    var lastOnlineState: PrintState = .unknown
    var progress: Int?
    var layer: Int?
    var totalLayers: Int?
    var remainingMinutes: Int?
    var estimatedFinish: Date?
    var reportedAt: Date?
    var thumbnail: Data?

    mutating func apply(_ report: [String: Any], now: Date = Date()) {
        let task = text(report, "subtask_id")
        let newTask = task != nil && task != "" && task != "0" && task != taskID
        var title = text(report, "subtask_name")
        if title?.isEmpty != false { title = (newTask || job.isEmpty) ? text(report, "gcode_file") : nil }
        if newTask || (taskID.isEmpty && !job.isEmpty && title != nil && title != job) {
            clearJob()
        }
        if let task { taskID = task }
        if let title { job = title }
        func number(_ key: String) -> Int? { text(report, key).flatMap(Int.init) }
        if let value = number("mc_percent") { progress = min(100, max(0, value)) }
        if let value = number("layer_num") { layer = max(0, value) }
        if let value = number("total_layer_num") { totalLayers = max(0, value) }
        if let value = number("mc_remaining_time") {
            remainingMinutes = min(525_600, max(0, value))
            estimatedFinish = now.addingTimeInterval(Double(remainingMinutes!) * 60)
        }
        if let value = text(report, "gcode_state") { state = .parse(value) }
        else if state == .offline { state = lastOnlineState }
        if state == .completed { progress = 100; remainingMinutes = 0 }
        if state == .idle { clearJob() }
        lastOnlineState = state
        reportedAt = now
    }

    mutating func setOnline(_ online: Bool) {
        if !online && state != .offline { lastOnlineState = state; state = .offline }
        else if online && state == .offline { state = lastOnlineState }
    }

    private mutating func clearJob() {
        job = ""; taskID = ""; progress = nil; layer = nil; totalLayers = nil
        remainingMinutes = nil; estimatedFinish = nil; thumbnail = nil
    }

    var jobLabel: String { job.isEmpty ? (state == .idle ? "Ready for your next print" : "Waiting for printer") : job }
    var percentage: String { progress.map { "\($0)%" } ?? "—" }
    var layerLabel: String {
        guard let layer, let totalLayers, totalLayers > 0 else { return "Layer —" }
        return "Layer \(layer) / \(totalLayers)"
    }
    var timeLabel: String {
        guard state.active, let remainingMinutes else { return "Time —" }
        return remainingMinutes >= 60 ? "\(remainingMinutes / 60)h \(remainingMinutes % 60)m" : "\(remainingMinutes)m"
    }
}

struct WidgetSnapshot: Codable {
    var sessionID: String
    var fetchedAt: Date
    var printers: [PrinterSnapshot]
    var message: String
}

enum PreviewSelection {
    static func cover(in tasks: [[String: Any]], printer: PrinterSnapshot) -> URL? {
        guard !printer.taskID.isEmpty, printer.taskID != "0" else { return nil }
        let task = tasks.first {
            text($0, "deviceId") == printer.id && (text($0, "id") ?? text($0, "taskId")) == printer.taskID
        }
        guard let task, let cover = text(task, "cover"), let url = URL(string: cover), allowed(url) else { return nil }
        return url
    }
    static func allowed(_ url: URL) -> Bool {
        guard url.scheme == "https", let host = url.host?.lowercased(), url.user == nil, url.password == nil,
              url.port == nil || url.port == 443 else { return false }
        return host == "or-cloud-model-prod.s3.dualstack.us-west-2.amazonaws.com" ||
            ["bambulab.com", "bambulab.cn", "bblmw.com", "makerworld.com"].contains { host == $0 || host.hasSuffix("." + $0) }
    }
}

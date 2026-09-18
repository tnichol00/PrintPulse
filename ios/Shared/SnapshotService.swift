import Foundation
import CocoaMQTT
import ImageIO
import UniformTypeIdentifiers

// A widget gets a short execution window. Connect for a bounded snapshot only;
// do not pretend a widget can keep a permanent MQTT connection alive.
@MainActor
final class TelemetrySnapshot {
    private var client: CocoaMQTT?
    private var continuation: CheckedContinuation<[String: [String: Any]], Error>?
    private var deadline: Task<Void, Never>?
    private var reports: [String: [String: Any]] = [:]
    private var expected: Set<String> = []

    func read(session: CloudSession, ids: [String]) async throws -> [String: [String: Any]] {
        expected = Set(ids.filter { !$0.isEmpty && !$0.contains(where: { "/+#".contains($0) }) })
        guard !expected.isEmpty else { return [:] }
        return try await withTaskCancellationHandler(operation: {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                let mqtt = CocoaMQTT(clientID: "PrintPulse_iOS_" + UUID().uuidString, host: session.china ? "cn.mqtt.bambulab.com" : "us.mqtt.bambulab.com", port: 8883)
                self.client = mqtt
                mqtt.username = session.username
                mqtt.password = session.token
                mqtt.enableSSL = true
                mqtt.allowUntrustCACertificate = false
                mqtt.cleanSession = true
                mqtt.autoReconnect = false
                mqtt.backgroundOnSocket = false
                mqtt.keepAlive = 30
                mqtt.logLevel = .off
                mqtt.delegateQueue = .main
                mqtt.didConnectAck = { [weak self] client, ack in
                    guard let self, self.continuation != nil else { return }
                    guard ack == .accept else {
                        self.finish(.failure(ack == .notAuthorized || ack == .badUsernameOrPassword ? CloudError.expired : CloudError.unavailable)); return
                    }
                    for id in self.expected { client.subscribe("device/\(id)/report", qos: .qos0) }
                }
                mqtt.didSubscribeTopics = { [weak self] client, success, failed in
                    guard let self, self.continuation != nil else { return }
                    if !failed.isEmpty { self.finish(.failure(CloudError.unavailable)); return }
                    for case let topic as String in success.allKeys {
                        let parts = topic.split(separator: "/")
                        guard parts.count == 3, self.expected.contains(String(parts[1])) else { continue }
                        client.publish("device/\(parts[1])/request", withString: "{\"pushing\":{\"sequence_id\":\"0\",\"command\":\"pushall\",\"version\":1,\"push_target\":1}}", qos: .qos0)
                    }
                }
                mqtt.didReceiveMessage = { [weak self] _, message, _ in
                    guard let self, self.continuation != nil, message.payload.count <= 2_000_000 else { return }
                    let topic = message.topic.split(separator: "/")
                    guard topic.count == 3, topic[0] == "device", topic[2] == "report", self.expected.contains(String(topic[1])),
                          let json = try? JSONSerialization.jsonObject(with: Data(message.payload)) as? [String: Any],
                          let report = json["print"] as? [String: Any], !report.isEmpty else { return }
                    let id = String(topic[1])
                    self.reports[id, default: [:]].merge(report) { _, new in new }
                    // Wait for a status snapshot, not just a temperature delta.
                    if self.expected.allSatisfy({ self.reports[$0]?["gcode_state"] != nil }) { self.finish(.success(self.reports)) }
                }
                mqtt.didDisconnect = { [weak self] _, _ in
                    guard let self else { return }
                    self.finish(self.reports.isEmpty ? .failure(CloudError.unavailable) : .success(self.reports))
                }
                deadline = Task { [weak self] in
                    try? await Task.sleep(for: .seconds(8))
                    guard !Task.isCancelled, let self else { return }
                    self.finish(self.reports.isEmpty ? .failure(CloudError.unavailable) : .success(self.reports))
                }
                if !mqtt.connect(timeout: 5) { finish(.failure(CloudError.unavailable)) }
            }
        }, onCancel: { Task { @MainActor in self.finish(.failure(CancellationError())) } })
    }
    private func finish(_ result: Result<[String: [String: Any]], Error>) {
        guard let continuation else { return }
        self.continuation = nil
        deadline?.cancel(); deadline = nil
        client?.didDisconnect = { _, _ in }
        client?.disconnect(); client = nil
        continuation.resume(with: result)
    }
}

private final class NoImageRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

enum PreviewLoader {
    static func load(_ url: URL) async -> Data? {
        guard PreviewSelection.allowed(url) else { return nil }
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 3
        config.timeoutIntervalForResource = 4
        let session = URLSession(configuration: config, delegate: NoImageRedirects(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        do {
            // This separate session never has Bambu account authorization headers/cookies.
            let (stream, response) = try await session.bytes(from: url)
            try BambuAPI.validate(response)
            guard response.expectedContentLength <= 2_000_000 else { return nil }
            var data = Data()
            for try await byte in stream {
                guard data.count < 2_000_000 else { return nil }
                data.append(byte)
            }
            guard let source = CGImageSourceCreateWithData(data as CFData, nil),
                  let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [kCGImageSourceCreateThumbnailFromImageAlways: true, kCGImageSourceThumbnailMaxPixelSize: 160, kCGImageSourceCreateThumbnailWithTransform: true] as CFDictionary) else { return nil }
            let result = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(result, UTType.png.identifier as CFString, 1, nil) else { return nil }
            CGImageDestinationAddImage(destination, image, nil)
            return CGImageDestinationFinalize(destination) ? result as Data : nil
        } catch { return nil }
    }
}

@MainActor
enum SnapshotService {
    static func refresh(session: CloudSession) async throws -> WidgetSnapshot {
        let storage = SharedStore()
        let previous = storage.load(sessionID: session.id)
        let api = BambuAPI()
        let devices = try await api.devices(session)
        try Task.checkCancellation()
        var printers = devices.compactMap { device -> PrinterSnapshot? in
            guard let id = text(device, "dev_id"), !id.isEmpty, !id.contains(where: { "/+#".contains($0) }) else { return nil }
            var printer = previous?.printers.first(where: { $0.id == id }) ?? PrinterSnapshot(id: id, name: "Bambu printer")
            printer.name = text(device, "name") ?? text(device, "dev_product_name") ?? "Bambu printer"
            printer.model = text(device, "dev_product_name") ?? "Bambu Lab"
            if let online = device["online"] as? Bool { printer.setOnline(online) }
            return printer
        }
        async let metadata = try? api.tasks(session)
        let ids = printers.filter { $0.state != .offline }.map(\.id)
        var message = printers.isEmpty ? "No printers on this account" : "Updated from Bambu Cloud"
        do {
            let updates = try await TelemetrySnapshot().read(session: session, ids: ids)
            for index in printers.indices {
                if let report = updates[printers[index].id], report["gcode_state"] != nil {
                    printers[index].apply(report)
                }
            }
            if !ids.isEmpty && !ids.allSatisfy({ updates[$0]?["gcode_state"] != nil }) { message = "Some printers have last-known data" }
        } catch CloudError.expired { throw CloudError.expired }
        catch is CancellationError { throw CancellationError() }
        catch { message = "Cloud disconnected · Last-known data" }
        let tasks = await metadata ?? []
        // Bound time and widget memory: at most six compact previews, fetched concurrently.
        let needed = printers.indices.filter { printers[$0].thumbnail == nil && !storage.hiddenPrinters.contains(printers[$0].id) }.prefix(6)
        await withTaskGroup(of: (Int, Data?).self) { group in
            for index in needed {
                if let url = PreviewSelection.cover(in: tasks, printer: printers[index]) {
                    group.addTask { (index, await PreviewLoader.load(url)) }
                }
            }
            for await (index, data) in group { printers[index].thumbnail = data }
        }
        try Task.checkCancellation()
        guard try SessionStore().load()?.id == session.id else { throw CancellationError() }
        let snapshot = WidgetSnapshot(sessionID: session.id, fetchedAt: Date(), printers: printers, message: message)
        try storage.save(snapshot)
        return snapshot
    }
}

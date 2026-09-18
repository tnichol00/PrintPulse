import SwiftUI
import WidgetKit
import AppIntents

struct PrinterChoice: AppEntity {
    var id: String
    var name: String
    static var typeDisplayRepresentation: TypeDisplayRepresentation = "Printer"
    static var defaultQuery = PrinterQuery()
    var displayRepresentation: DisplayRepresentation { DisplayRepresentation(title: "\(name)") }
}

struct PrinterQuery: EntityQuery {
    func entities(for identifiers: [String]) async throws -> [PrinterChoice] {
        try await suggestedEntities().filter { identifiers.contains($0.id) }
    }
    func suggestedEntities() async throws -> [PrinterChoice] {
        guard let session = try? SessionStore().load() else { return [] }
        let storage = SharedStore()
        return (storage.load(sessionID: session.id)?.printers ?? []).filter { !storage.hiddenPrinters.contains($0.id) }.map { PrinterChoice(id: $0.id, name: $0.name) }
    }
}

struct WidgetOptions: WidgetConfigurationIntent {
    static var title: LocalizedStringResource = "PrintPulse"
    static var description = IntentDescription("Choose a printer, or leave this empty to show your visible printers.")
    @Parameter(title: "Printer") var printer: PrinterChoice?
}

struct RefreshPrinters: AppIntent {
    static var title: LocalizedStringResource = "Refresh printer status"
    static var openAppWhenRun = false
    func perform() async throws -> some IntentResult {
        if let session = try? SessionStore().load() { _ = try? await SnapshotService.refresh(session: session) }
        WidgetCenter.shared.reloadTimelines(ofKind: "PrintPulseWidget")
        return .result()
    }
}

struct PrintEntry: TimelineEntry {
    let date: Date
    let printers: [PrinterSnapshot]
    let message: String
    var signedIn = true
    var stale = false
}

struct PrintProvider: AppIntentTimelineProvider {
    func placeholder(in context: Context) -> PrintEntry {
        // Gallery preview only; no samples are returned by a production timeline.
        var sample = PrinterSnapshot(id: "preview", name: "Bambu Lab A1")
        sample.apply(["subtask_id": "preview", "subtask_name": "Your next print", "gcode_state": "RUNNING", "mc_percent": 52, "layer_num": 162, "total_layer_num": 311, "mc_remaining_time": 48])
        return PrintEntry(date: Date(), printers: [sample], message: "Preview")
    }
    func snapshot(for configuration: WidgetOptions, in context: Context) async -> PrintEntry {
        if context.isPreview { return placeholder(in: context) }
        return await entry(configuration, refresh: false)
    }
    func timeline(for configuration: WidgetOptions, in context: Context) async -> Timeline<PrintEntry> {
        let current = await entry(configuration, refresh: true)
        // iOS may delay this request. Never extrapolate a percentage or completion.
        let next = Date().addingTimeInterval(15 * 60)
        let stale = PrintEntry(date: next, printers: current.printers, message: "Last-known status", signedIn: current.signedIn, stale: true)
        return Timeline(entries: [current, stale], policy: .after(next))
    }
    @MainActor private func entry(_ configuration: WidgetOptions, refresh: Bool) async -> PrintEntry {
        do {
            guard let session = try SessionStore().load() else { return PrintEntry(date: Date(), printers: [], message: "Open PrintPulse to sign in", signedIn: false) }
            let storage = SharedStore()
            var snapshot = storage.load(sessionID: session.id)
            var errorMessage: String?
            if refresh && (snapshot == nil || Date().timeIntervalSince(snapshot!.fetchedAt) > 45) {
                do { snapshot = try await SnapshotService.refresh(session: session) }
                catch CloudError.expired { errorMessage = CloudError.expired.localizedDescription }
                catch { errorMessage = "Last-known status · Tap refresh to retry" }
            }
            guard try SessionStore().load()?.id == session.id else { return PrintEntry(date: Date(), printers: [], message: "Open PrintPulse to sign in", signedIn: false) }
            let printers = (snapshot?.printers ?? []).filter {
                !storage.hiddenPrinters.contains($0.id) && (configuration.printer == nil || configuration.printer?.id == $0.id)
            }
            let message = errorMessage ?? (printers.isEmpty ? "Choose a visible printer in Settings" : snapshot?.message ?? "Open PrintPulse to refresh")
            return PrintEntry(date: Date(), printers: printers, message: message, stale: errorMessage != nil || message.contains("Last-known") || message.contains("last-known"))
        } catch {
            return PrintEntry(date: Date(), printers: [], message: "Unlock your iPhone and open PrintPulse", signedIn: false)
        }
    }
}

struct PrintWidgetView: View {
    @Environment(\.widgetFamily) private var family
    var entry: PrintEntry
    var body: some View {
        VStack(alignment: .leading, spacing: family == .systemSmall ? 5 : 6) {
            HStack(spacing: 5) {
                Image("BrandIcon").resizable().frame(width: 18, height: 18).clipShape(RoundedRectangle(cornerRadius: 4))
                Text("PrintPulse").font(.caption.weight(.semibold))
                Spacer(minLength: 0)
                if entry.signedIn {
                    Button(intent: RefreshPrinters()) { Image(systemName: "arrow.clockwise").font(.caption.weight(.semibold)) }
                        .buttonStyle(.plain).accessibilityLabel("Refresh printer status")
                }
            }
            if entry.printers.isEmpty {
                Spacer(minLength: 0)
                Image(systemName: entry.signedIn ? "printer" : "person.crop.circle.badge.checkmark").font(.title2).foregroundStyle(.secondary)
                Text(entry.message).font(.caption).foregroundStyle(.secondary)
                Spacer(minLength: 0)
            } else {
                ForEach(Array(entry.printers.prefix(family == .systemLarge ? 3 : 1))) { printer in
                    PrinterWidgetView(printer: printer, compact: family == .systemSmall, dense: family == .systemLarge)
                    if family == .systemLarge && printer.id != entry.printers.prefix(3).last?.id { Divider() }
                }
                if family != .systemSmall {
                    Spacer(minLength: 0)
                    HStack {
                        Text(entry.stale ? entry.message : "Updates are scheduled by iOS")
                        Spacer(minLength: 0)
                        if entry.printers.count > (family == .systemLarge ? 3 : 1) { Text("+\(entry.printers.count - (family == .systemLarge ? 3 : 1)) printers") }
                    }.font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1)
                }
            }
        }
        .containerBackground(for: .widget) { Color(.systemBackground) }
        .widgetURL(URL(string: "printpulse://settings"))
        .privacySensitive()
    }
}

@main
struct PrintPulseWidget: Widget {
    var body: some WidgetConfiguration {
        AppIntentConfiguration(kind: "PrintPulseWidget", intent: WidgetOptions.self, provider: PrintProvider()) { entry in
            PrintWidgetView(entry: entry)
        }
        .configurationDisplayName("PrintPulse")
        .description("Print progress, layers, time remaining, and the current print preview.")
        .supportedFamilies([.systemSmall, .systemMedium, .systemLarge])
    }
}

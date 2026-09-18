import SwiftUI

struct PrinterWidgetView: View {
    let printer: PrinterSnapshot
    var compact = false
    var dense = false
    var accent: Color {
        switch printer.state {
        case .error: return .red
        case .paused: return .orange
        case .printing, .preparing, .completed: return Color(red: 0, green: 0.65, blue: 0.24)
        default: return .secondary
        }
    }
    var body: some View {
        if compact {
            VStack(alignment: .leading, spacing: 3) {
                Text(printer.name).font(.system(size: 11, weight: .semibold)).lineLimit(1)
                HStack {
                    preview.frame(width: 32, height: 32)
                    Spacer(minLength: 3)
                    Text(printer.percentage).font(.system(size: 26, weight: .semibold, design: .rounded)).foregroundStyle(accent).minimumScaleFactor(0.65)
                }
                ProgressView(value: Double(printer.progress ?? 0), total: 100).tint(accent)
                Text(printer.jobLabel).font(.system(size: 10)).lineLimit(1)
                HStack { Text(printer.state.rawValue).foregroundStyle(accent); Spacer(minLength: 2); Text(printer.timeLabel) }.font(.system(size: 10)).lineLimit(1)
                freshness
            }
        } else {
            VStack(alignment: .leading, spacing: dense ? 4 : 6) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(printer.name).font(dense ? .caption.weight(.semibold) : .subheadline.weight(.semibold)).lineLimit(1)
                    Spacer(minLength: 0)
                    Text(printer.jobLabel).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                }
                HStack(spacing: dense ? 8 : 12) {
                    preview.frame(width: dense ? 40 : 53, height: dense ? 40 : 53)
                    VStack(alignment: .leading, spacing: dense ? 3 : 5) {
                        HStack(alignment: .firstTextBaseline) {
                            Text(printer.percentage).font(.system(size: dense ? 22 : 27, weight: .semibold, design: .rounded))
                            Spacer(minLength: 2)
                            Text(printer.state.rawValue).font(.caption)
                        }.foregroundStyle(accent)
                        ProgressView(value: Double(printer.progress ?? 0), total: 100).tint(accent)
                        HStack(spacing: 5) {
                            Text(printer.layerLabel)
                            Spacer(minLength: 0)
                            Text(printer.timeLabel)
                            if printer.state == .printing, let eta = printer.estimatedFinish {
                                Text("·"); Text(eta, style: .time)
                            }
                        }.font(.system(size: 10)).foregroundStyle(.secondary).lineLimit(1).minimumScaleFactor(0.8)
                    }
                }
                freshness
            }
        }
    }
    @ViewBuilder private var preview: some View {
        // Mutually exclusive: a transparent real image never has the placeholder beneath it.
        if let data = printer.thumbnail, let image = UIImage(data: data) {
            Image(uiImage: image).resizable().scaledToFit()
        } else {
            RoundedRectangle(cornerRadius: 9).fill(Color.secondary.opacity(0.07))
                .overlay(Image(systemName: "cube.transparent").font(.system(size: 28, weight: .light)).foregroundStyle(.secondary))
        }
    }
    private var freshness: some View {
        HStack(spacing: 3) {
            if let date = printer.reportedAt {
                Text("As of"); Text(date, style: .time)
            } else { Text("Waiting for a status report") }
        }.font(.system(size: 9)).foregroundStyle(.secondary).lineLimit(1)
    }
}

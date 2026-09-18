import XCTest
import SwiftUI
@testable import PrintPulse

final class PrintPulseTests: XCTestCase {
    func testPartialReportPreservesJobAndProgress() {
        var printer = PrinterSnapshot(id: "A", name: "A1")
        printer.apply(["subtask_id": "1", "subtask_name": "Box", "gcode_state": "RUNNING", "mc_percent": 42, "layer_num": 15])
        printer.apply(["gcode_file": "Metadata/plate_1.gcode", "nozzle_temper": 210])
        XCTAssertEqual(printer.job, "Box")
        XCTAssertEqual(printer.progress, 42)
        XCTAssertEqual(printer.layer, 15)
    }
    func testNewPrintClearsOldPreviewAndProgress() {
        var printer = PrinterSnapshot(id: "A", name: "A1")
        printer.apply(["subtask_id": "1", "subtask_name": "Old", "mc_percent": 90])
        printer.thumbnail = Data([1, 2])
        printer.apply(["subtask_id": "2", "gcode_state": "PREPARE"])
        XCTAssertNil(printer.thumbnail)
        XCTAssertNil(printer.progress)
        XCTAssertEqual(printer.job, "")
    }
    func testPreviewRequiresPrinterAndTaskIdentity() {
        var printer = PrinterSnapshot(id: "A", name: "A1")
        printer.apply(["subtask_id": "current"])
        let tasks: [[String: Any]] = [
            ["deviceId": "A", "id": "old", "cover": "https://cdn.bblmw.com/old.png"],
            ["deviceId": "B", "id": "current", "cover": "https://cdn.bblmw.com/other.png"],
            ["deviceId": "A", "id": "current", "cover": "https://cdn.bblmw.com/correct.png"]]
        XCTAssertEqual(PreviewSelection.cover(in: tasks, printer: printer)?.lastPathComponent, "correct.png")
        XCTAssertNil(PreviewSelection.cover(in: [["deviceId": "A", "id": "old", "taskId": "current", "cover": "https://cdn.bblmw.com/incorrect.png"]], printer: printer))
        printer.apply(["subtask_id": "new"])
        XCTAssertNil(PreviewSelection.cover(in: tasks, printer: printer))
    }
    func testPreviewRejectsUntrustedHosts() {
        for value in ["http://cdn.bblmw.com/a", "https://bblmw.com.evil.invalid/a", "https://127.0.0.1/a", "https://other.s3.dualstack.us-west-2.amazonaws.com/a", "https://cdn.bblmw.com:444/a", "https://user:password@cdn.bblmw.com/a"] {
            XCTAssertFalse(PreviewSelection.allowed(URL(string: value)!))
        }
        XCTAssertTrue(PreviewSelection.allowed(URL(string: "https://or-cloud-model-prod.s3.dualstack.us-west-2.amazonaws.com/a")!))
    }
    func testOfflineKeepsLastKnownValuesAndIdleClearsThem() {
        var printer = PrinterSnapshot(id: "A", name: "A1")
        printer.apply(["gcode_state": "RUNNING", "mc_percent": 50, "mc_remaining_time": 12])
        printer.setOnline(false)
        XCTAssertEqual(printer.progress, 50)
        XCTAssertEqual(printer.timeLabel, "Time —")
        printer.setOnline(true)
        XCTAssertEqual(printer.state, .printing)
        printer.apply(["gcode_state": "IDLE"])
        XCTAssertNil(printer.progress)
        XCTAssertNil(printer.estimatedFinish)
    }
    func testValuesAreBoundedAndBadTypesIgnored() {
        var printer = PrinterSnapshot(id: "A", name: "A1")
        printer.apply(["mc_percent": "120", "layer_num": -8, "mc_remaining_time": -3])
        XCTAssertEqual(printer.progress, 100)
        XCTAssertEqual(printer.layer, 0)
        XCTAssertEqual(printer.remainingMinutes, 0)
        printer.apply(["mc_percent": true, "layer_num": NSNull()])
        XCTAssertEqual(printer.progress, 100)
        XCTAssertEqual(printer.layer, 0)
    }
    func testPrintersUpdateIndependently() {
        var printers = [PrinterSnapshot(id: "A", name: "A1"), PrinterSnapshot(id: "B", name: "P1S")]
        printers[0].apply(["mc_percent": 20])
        printers[1].apply(["mc_percent": 80])
        XCTAssertEqual(printers[0].progress, 20)
        XCTAssertEqual(printers[1].progress, 80)
    }
    func testSignedOutCacheIsBoundToSession() throws {
        let snapshot = WidgetSnapshot(sessionID: "fixture-session", fetchedAt: Date(), printers: [], message: "fixture")
        let data = try JSONEncoder().encode(snapshot)
        let restored = try JSONDecoder().decode(WidgetSnapshot.self, from: data)
        XCTAssertEqual(restored.sessionID, "fixture-session")
        XCTAssertFalse(String(data: data, encoding: .utf8)!.contains("token"))
    }
    func testHTTPFailuresAreActionableAndSanitized() {
        for status in [401, 403, 429, 500] {
            XCTAssertThrowsError(try BambuAPI.validate(HTTPURLResponse(url: URL(string: "https://api.bambulab.com")!, statusCode: status, httpVersion: nil, headerFields: nil)!))
        }
    }
    @MainActor func testWidgetCardRendersWithAndWithoutTransparentPreview() throws {
        var printer = PrinterSnapshot(id: "fixture", name: "Bambu Lab A1")
        printer.apply(["subtask_id": "fixture", "subtask_name": "Sample bracket", "gcode_state": "RUNNING", "mc_percent": 52, "layer_num": 162, "total_layer_num": 311, "mc_remaining_time": 48])
        for preview in [false, true] {
            if preview {
                printer.thumbnail = UIGraphicsImageRenderer(size: CGSize(width: 64, height: 64)).image { context in
                    UIColor.darkGray.setFill(); context.fill(CGRect(x: 16, y: 16, width: 32, height: 32))
                }.pngData()
            }
            for compact in [false, true] {
                let width: CGFloat = compact ? 140 : 320
                let renderer = ImageRenderer(content: PrinterWidgetView(printer: printer, compact: compact).padding(12).frame(width: width + 24).background(Color.white).environment(\.colorScheme, .light))
                renderer.scale = 2
                let image = try XCTUnwrap(renderer.uiImage)
                if compact { XCTAssertLessThanOrEqual(image.size.height, 128, "Small card must leave room for the widget header and margins") }
                let attachment = XCTAttachment(image: image)
                attachment.name = "widget-\(compact ? "small" : "medium")-\(preview ? "preview" : "placeholder")"
                attachment.lifetime = .keepAlways; add(attachment)
            }
        }
    }
    @MainActor func testThreePrinterLayoutFitsLargeWidget() throws {
        var printer = PrinterSnapshot(id: "fixture", name: "Bambu Lab A1")
        printer.apply(["subtask_name": "A long model name for the widget", "gcode_state": "RUNNING", "mc_percent": 52, "layer_num": 162, "total_layer_num": 311, "mc_remaining_time": 48])
        let cards = VStack(spacing: 6) {
            HStack { Image("BrandIcon").resizable().frame(width: 18, height: 18); Text("PrintPulse").font(.caption.weight(.semibold)); Spacer() }
            ForEach(0..<3) { index in
                PrinterWidgetView(printer: printer, dense: true)
                if index < 2 { Divider() }
            }
            Text("Updates are scheduled by iOS").font(.system(size: 9)).foregroundStyle(.secondary)
        }.frame(width: 306).padding(16).background(Color.white).environment(\.colorScheme, .light)
        let renderer = ImageRenderer(content: cards)
        renderer.scale = 2
        let image = try XCTUnwrap(renderer.uiImage)
        XCTAssertLessThanOrEqual(image.size.height, 354, "Three cards overflow the large widget")
        let attachment = XCTAttachment(image: image); attachment.name = "widget-large-three-printers"; attachment.lifetime = .keepAlways; add(attachment)
    }
}

import SwiftUI
import WidgetKit

@main
struct PrintPulseApp: App {
    @StateObject private var settings = SettingsModel()
    @Environment(\.scenePhase) private var phase
    var body: some Scene {
        WindowGroup {
            SettingsView(model: settings)
                .task { settings.restore() }
                .onChange(of: phase) { _, phase in
                    if phase == .active { settings.refreshIfNeeded() }
                }
        }
    }
}

@MainActor
final class SettingsModel: ObservableObject {
    @Published var session: CloudSession?
    @Published var email = ""
    @Published var secret = ""
    @Published var china = false
    @Published var useCode = false
    @Published var busy = false
    @Published var message = ""
    @Published var printers: [PrinterSnapshot] = []
    @Published var hidden: Set<String> = []
    @Published var updated: Date?
    private var challenge: String?
    private var generation = UUID()
    private var task: Task<Void, Never>?
    private let api = BambuAPI()
    private var restored = false

    var secretLabel: String { challenge != nil ? "Authenticator code" : useCode ? "Email verification code" : "Password" }
    func restore() {
        guard !restored else { return }; restored = true
        do {
            session = try SessionStore().load()
            hidden = SharedStore().hiddenPrinters
            if let session, let snapshot = SharedStore().load(sessionID: session.id) {
                printers = snapshot.printers; updated = snapshot.fetchedAt
            }
            refreshIfNeeded()
        } catch { message = error.localizedDescription }
    }
    func resetChallenge() { challenge = nil; secret = ""; message = "" }
    func signIn() {
        let address = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard address.contains("@"), !secret.isEmpty else { message = "Enter your email and \(secretLabel.lowercased())."; return }
        let epoch = generation
        let value = secret; let code = useCode; let region = china; let key = challenge
        busy = true; message = "Signing in…"
        task = Task {
            do {
                let result: LoginResult
                if let key { result = .session(try await api.verifyAuthenticator(key: key, code: value, email: address, china: region)) }
                else { result = try await api.login(email: address, secret: value, code: code, china: region) }
                guard epoch == generation, !Task.isCancelled else { return }
                secret = ""
                switch result {
                case .session(let account):
                    try SessionStore().save(account)
                    SharedStore().clear(); hidden = []; printers = []; updated = nil
                    session = account; challenge = nil; message = "Signed in"; busy = false
                    WidgetCenter.shared.reloadAllTimelines()
                    refresh()
                case .emailCode: challenge = nil; useCode = true; message = "Enter the verification code Bambu emailed you."; busy = false
                case .authenticator(let key): challenge = key; message = "Enter the code from your authenticator."; busy = false
                }
            } catch {
                guard epoch == generation else { return }
                secret = ""; busy = false; message = safeMessage(error)
            }
        }
    }
    func sendCode() {
        let address = email.trimmingCharacters(in: .whitespacesAndNewlines)
        guard address.contains("@") else { message = "Enter your Bambu account email first."; return }
        let epoch = generation; let region = china
        busy = true; challenge = nil; secret = ""
        task = Task {
            do {
                try await api.sendCode(email: address, china: region)
                guard epoch == generation, !Task.isCancelled else { return }
                useCode = true; message = "Check your email for a sign-in code."; busy = false
            } catch { if epoch == generation { busy = false; message = safeMessage(error) } }
        }
    }
    func refreshIfNeeded() {
        if session != nil && !busy && (updated == nil || Date().timeIntervalSince(updated!) > 60) { refresh() }
    }
    func refresh() {
        guard let session, !busy else { return }
        let epoch = generation
        busy = true; message = "Updating widgets…"
        task = Task {
            do {
                let snapshot = try await SnapshotService.refresh(session: session)
                guard epoch == generation, !Task.isCancelled else { return }
                printers = snapshot.printers; updated = snapshot.fetchedAt; message = snapshot.message
            } catch { if epoch == generation { message = safeMessage(error) } }
            guard epoch == generation else { return }
            busy = false
            WidgetCenter.shared.reloadAllTimelines()
        }
    }
    func setVisible(_ id: String, visible: Bool) {
        if visible { hidden.remove(id) } else { hidden.insert(id) }
        SharedStore().hiddenPrinters = hidden
        WidgetCenter.shared.reloadAllTimelines()
    }
    func signOut() {
        do {
            try SessionStore().clear()
            generation = UUID(); task?.cancel(); task = nil
            SharedStore().clear(); session = nil; secret = ""; challenge = nil
            printers = []; hidden = []; updated = nil; busy = false; message = "Signed out"
            WidgetCenter.shared.reloadAllTimelines()
        } catch { message = error.localizedDescription }
    }
    private func safeMessage(_ error: Error) -> String {
        if error is CloudError || error is LocalError { return error.localizedDescription }
        return "Could not reach Bambu Cloud. Check your connection and try again."
    }
}

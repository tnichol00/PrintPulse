import SwiftUI

struct SettingsView: View {
    @ObservedObject var model: SettingsModel
    @State private var confirmSignOut = false
    var body: some View {
        NavigationStack {
            Form {
                Section {
                    HStack(spacing: 14) {
                        Image("BrandIcon").resizable().frame(width: 48, height: 48).clipShape(RoundedRectangle(cornerRadius: 11))
                        VStack(alignment: .leading, spacing: 4) {
                            Text("Your printers, at a glance").font(.headline)
                            Text("Print progress lives in your widgets.").font(.subheadline).foregroundStyle(.secondary)
                        }
                    }.padding(.vertical, 6)
                }
                if let account = model.session {
                    Section("Bambu account") {
                        LabeledContent("Signed in", value: account.email)
                        LabeledContent("Region", value: account.china ? "Mainland China" : "Global")
                        Button("Sign out", role: .destructive) { confirmSignOut = true }
                    }
                    Section {
                        Button { model.refresh() } label: {
                            HStack { Label("Refresh widgets", systemImage: "arrow.clockwise"); Spacer(); if model.busy { ProgressView() } }
                        }.disabled(model.busy)
                        if let updated = model.updated { LabeledContent("Last refresh") { Text(updated, style: .time).foregroundStyle(.secondary) } }
                    } header: { Text("Widget updates") } footer: {
                        Text("iOS schedules widget updates. They may be delayed. Each printer shows when its status was received; progress is never estimated between updates.")
                    }
                    if !model.printers.isEmpty {
                        Section("Show in widgets") {
                            ForEach(model.printers) { printer in
                                Toggle(printer.name, isOn: Binding(get: { !model.hidden.contains(printer.id) }, set: { model.setVisible(printer.id, visible: $0) }))
                            }
                        }
                    }
                } else {
                    Section("Sign in to Bambu Lab") {
                        Picker("Region", selection: $model.china) { Text("Global").tag(false); Text("Mainland China").tag(true) }
                            .onChange(of: model.china) { _, _ in model.resetChallenge() }
                        TextField("Email", text: $model.email).keyboardType(.emailAddress).textContentType(.username).textInputAutocapitalization(.never).autocorrectionDisabled()
                            .onChange(of: model.email) { _, _ in model.resetChallenge() }
                        Toggle("Use an email code", isOn: $model.useCode).onChange(of: model.useCode) { _, _ in model.resetChallenge() }
                        SecureField(model.secretLabel, text: $model.secret).textContentType(model.useCode ? .oneTimeCode : .password)
                        Button("Email me a code") { model.sendCode() }
                        Button { model.signIn() } label: { HStack { Text("Sign in").bold(); Spacer(); if model.busy { ProgressView() } } }
                    }.disabled(model.busy)
                }
                if !model.message.isEmpty { Section { Text(model.message).font(.callout).foregroundStyle(.secondary).accessibilityIdentifier("accountStatus") } }
                Section("Add your widget") {
                    Text("Touch and hold your Home Screen, choose Edit → Add Widget, then search for PrintPulse.")
                    Text("Small and medium widgets show one printer. A large widget shows up to three. Touch and hold a widget and choose Edit Widget to select a particular printer.").foregroundStyle(.secondary)
                }
                Section("About") {
                    LabeledContent("Version", value: "1.0.0")
                    NavigationLink("Open-source notices") {
                        ScrollView {
                            Text(notices).font(.footnote).frame(maxWidth: .infinity, alignment: .leading).padding()
                        }.navigationTitle("Notices").navigationBarTitleDisplayMode(.inline)
                    }
                    Text("An independent companion for Bambu Cloud. Your session is saved securely in the iPhone Keychain. Sign out removes local account data and widget previews.").font(.footnote).foregroundStyle(.secondary)
                }
            }
            .navigationTitle("PrintPulse Settings")
            .confirmationDialog("Sign out and clear widget data?", isPresented: $confirmSignOut, titleVisibility: .visible) {
                Button("Sign out", role: .destructive) { model.signOut() }
            }
        }.tint(Color(red: 0, green: 0.40, blue: 0.75))
    }
    private var notices: String {
        guard let url = Bundle.main.url(forResource: "ThirdPartyNotices", withExtension: "txt") else { return "Dependency notices are unavailable." }
        return (try? String(contentsOf: url, encoding: .utf8)) ?? "Dependency notices are unavailable."
    }
}

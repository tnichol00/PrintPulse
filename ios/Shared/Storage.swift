import Foundation
import Security

enum LocalError: LocalizedError {
    case keychain, sharedContainer
    var errorDescription: String? {
        switch self {
        case .keychain: return "Secure account storage is unavailable. Unlock your iPhone and try again."
        case .sharedContainer: return "Widget storage is unavailable. This build needs matching Apple App Group permissions."
        }
    }
}

struct SessionStore {
    private var query: [String: Any] {
        var result: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "PrintPulse.BambuSession", kSecAttrAccount as String: "current"]
        if let group = Bundle.main.object(forInfoDictionaryKey: "SharedKeychainGroup") as? String, !group.isEmpty {
            result[kSecAttrAccessGroup as String] = group
        }
        return result
    }
    func load() throws -> CloudSession? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data,
              let session = try? JSONDecoder().decode(CloudSession.self, from: data) else { throw LocalError.keychain }
        return session
    }
    func save(_ session: CloudSession) throws {
        let data = try JSONEncoder().encode(session)
        let attributes: [String: Any] = [kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            status = SecItemAdd(query.merging(attributes) { _, new in new } as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw LocalError.keychain }
    }
    func clear() throws {
        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw LocalError.keychain }
    }
}

struct SharedStore {
    static var group: String { Bundle.main.object(forInfoDictionaryKey: "SharedAppGroup") as? String ?? "group.com.tnichol00.PrintPulse" }
    var defaults: UserDefaults { UserDefaults(suiteName: Self.group) ?? .standard }
    var hiddenPrinters: Set<String> {
        get { Set(defaults.stringArray(forKey: "hiddenPrinters") ?? []) }
        nonmutating set { defaults.set(Array(newValue), forKey: "hiddenPrinters") }
    }
    private var url: URL? { FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: Self.group)?.appendingPathComponent("widget-snapshot.json") }
    func load(sessionID: String) -> WidgetSnapshot? {
        guard let url, let data = try? Data(contentsOf: url),
              let snapshot = try? JSONDecoder().decode(WidgetSnapshot.self, from: data), snapshot.sessionID == sessionID else { return nil }
        return snapshot
    }
    func save(_ snapshot: WidgetSnapshot) throws {
        guard let url else { throw LocalError.sharedContainer }
        guard try SessionStore().load()?.id == snapshot.sessionID else { return }
        let data = try JSONEncoder().encode(snapshot)
        try data.write(to: url, options: [.atomic, .completeFileProtectionUntilFirstUserAuthentication])
    }
    func clear() {
        if let url { try? FileManager.default.removeItem(at: url) }
        defaults.removeObject(forKey: "hiddenPrinters")
    }
}

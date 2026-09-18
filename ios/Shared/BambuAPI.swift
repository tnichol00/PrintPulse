import Foundation

enum CloudError: LocalizedError {
    case expired, rejected, limited, unavailable, malformed, verification
    var errorDescription: String? {
        switch self {
        case .expired: return "Session expired. Open PrintPulse and sign in again."
        case .rejected: return "Bambu rejected this request. Check your sign-in details or try email-code sign-in."
        case .limited: return "Bambu is limiting requests. Wait a few minutes before retrying."
        case .unavailable: return "Bambu Cloud is unavailable. Showing the last update."
        case .malformed: return "Bambu returned an unexpected response. Please try again later."
        case .verification: return "Bambu requires browser verification. Try email-code sign-in."
        }
    }
}

enum LoginResult {
    case session(CloudSession), emailCode, authenticator(String)
}

final class BambuAPI {
    private let network: URLSession
    init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 10
        config.timeoutIntervalForResource = 15
        config.httpAdditionalHeaders = ["User-Agent": "PrintPulse-iOS/1.0", "Accept": "application/json"]
        network = URLSession(configuration: config)
    }
    deinit { network.invalidateAndCancel() }
    static func root(_ china: Bool) -> String { china ? "https://api.bambulab.cn" : "https://api.bambulab.com" }
    private func request(_ url: String, body: [String: String]? = nil, token: String? = nil, timeout: TimeInterval = 10) async throws -> [String: Any] {
        var request = URLRequest(url: URL(string: url)!)
        request.timeoutInterval = timeout
        if let body {
            request.httpMethod = "POST"
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        if let token { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        let (data, response) = try await network.data(for: request)
        try Self.validate(response)
        guard data.count <= 5_000_000, let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { throw CloudError.malformed }
        return json
    }
    static func validate(_ response: URLResponse) throws {
        guard let response = response as? HTTPURLResponse else { throw CloudError.unavailable }
        switch response.statusCode {
        case 200..<300: break
        case 401: throw CloudError.expired
        case 403: throw CloudError.verification
        case 429: throw CloudError.limited
        case 400..<500: throw CloudError.rejected
        default: throw CloudError.unavailable
        }
    }
    func login(email: String, secret: String, code: Bool, china: Bool) async throws -> LoginResult {
        let body = code ? ["account": email, "code": secret] : ["account": email, "password": secret, "apiError": ""]
        let result = try await request(Self.root(china) + "/v1/user-service/user/login", body: body)
        if let token = text(result, "accessToken"), !token.isEmpty { return .session(try await makeSession(token: token, email: email, china: china)) }
        if text(result, "loginType") == "verifyCode" { return .emailCode }
        if text(result, "loginType") == "tfa", let key = text(result, "tfaKey") { return .authenticator(key) }
        throw CloudError.rejected
    }
    func sendCode(email: String, china: Bool) async throws {
        _ = try await request(Self.root(china) + "/v1/user-service/user/sendemail/code", body: ["email": email, "type": "codeLogin"])
    }
    func verifyAuthenticator(key: String, code: String, email: String, china: Bool) async throws -> CloudSession {
        let root = china ? "https://bambulab.cn" : "https://bambulab.com"
        var req = URLRequest(url: URL(string: root + "/api/sign-in/tfa")!)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try JSONSerialization.data(withJSONObject: ["tfaKey": key, "tfaCode": code])
        let (_, response) = try await network.data(for: req)
        try Self.validate(response)
        guard let token = network.configuration.httpCookieStorage?.cookies(for: URL(string: root)!)?.first(where: { $0.name == "token" })?.value else { throw CloudError.verification }
        return try await makeSession(token: token, email: email, china: china)
    }
    private func makeSession(token: String, email: String, china: Bool) async throws -> CloudSession {
        let preference = try await request(Self.root(china) + "/v1/design-user-service/my/preference", token: token)
        guard let uid = text(preference, "uid"), !uid.isEmpty else { throw CloudError.malformed }
        return CloudSession(token: token, username: "u_" + uid, email: email, china: china)
    }
    func devices(_ session: CloudSession) async throws -> [[String: Any]] {
        let result = try await request(Self.root(session.china) + "/v1/iot-service/api/user/bind", token: session.token, timeout: 5)
        guard let devices = result["devices"] as? [[String: Any]] else { throw CloudError.malformed }
        return devices
    }
    func tasks(_ session: CloudSession) async throws -> [[String: Any]] {
        let result = try await request(Self.root(session.china) + "/v1/user-service/my/tasks?limit=100", token: session.token, timeout: 4)
        return result["hits"] as? [[String: Any]] ?? []
    }
}

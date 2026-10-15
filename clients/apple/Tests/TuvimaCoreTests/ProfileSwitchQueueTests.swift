import XCTest
@testable import TuvimaCore

/// Serves canned answers to the API client, so the profile switch can be exercised without a server.
final class StubURLProtocol: URLProtocol {
    /// Answers a request with a status code and a JSON body.
    static var handler: ((URLRequest) -> (status: Int, body: String))?
    static var seen: [URLRequest] = []

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.seen.append(request)
        guard let handler = Self.handler, let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
            return
        }
        let result = handler(request)
        let response = HTTPURLResponse(
            url: url,
            statusCode: result.status,
            httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json"]
        )!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(result.body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

final class MemoryTokenStore: TokenStoring, @unchecked Sendable {
    private var tokens: [URL: ClientToken] = [:]

    func save(_ token: ClientToken, for origin: URL) throws { tokens[origin] = token }
    func load(for origin: URL) -> ClientToken? { tokens[origin] }
    func clear(for origin: URL) { tokens[origin] = nil }
}

/// The Listen root (what CarPlay shows first) loads, and its queue follows the person after a profile switch.
final class ProfileSwitchQueueTests: XCTestCase {
    private let mary = UUID(uuidString: "33333333-3333-3333-3333-333333333333")!
    private let jim = UUID(uuidString: "44444444-4444-4444-4444-444444444444")!
    private let device = UUID(uuidString: "22222222-2222-2222-2222-222222222222")!
    private let origin = URL(string: "http://localhost:5016")!

    override func setUp() {
        super.setUp()
        StubURLProtocol.seen = []
        StubURLProtocol.handler = { [self] request in answer(request) }
    }

    override func tearDown() {
        StubURLProtocol.handler = nil
        super.tearDown()
    }

    func testListenRootAndQueueFollowAProfileSwitch() async throws {
        let (api, store) = try makeClient()

        // Mary, before the switch.
        let rootBefore = try await api.browse(lane: "listen")
        XCTAssertEqual(rootBefore.key, "listen")
        let before = try await api.playerQueue()
        XCTAssertEqual(before.map { $0.title }, ["Mary Song"])

        // Switch to Jim with his PIN.
        let result = await api.switchProfile(profileID: jim, pin: "1234")
        guard case let .switched(token) = result else { return XCTFail("The switch should work, got \(result)") }
        XCTAssertEqual(token.profileID, jim)
        XCTAssertEqual(token.deviceID, device)
        XCTAssertEqual(store.load(for: origin)?.accessToken, "token-jim")

        // The same client now answers for Jim: the Listen root loads and the queue is his.
        let rootAfter = try await api.browse(lane: "listen")
        XCTAssertEqual(rootAfter.key, "listen")
        let after = try await api.playerQueue()
        XCTAssertEqual(after.map { $0.title }, ["Jim Book"])
        XCTAssertEqual(after.first?.mediaType, "Audiobooks")

        // Requests before the switch used Mary's token, requests after it used Jim's.
        let queueCalls = StubURLProtocol.seen.filter { $0.url?.path == "/api/v1/player/state" }
        XCTAssertEqual(queueCalls.map { $0.value(forHTTPHeaderField: "Authorization") }, ["Bearer token-mary", "Bearer token-jim"])
    }

    func testEachRefusalIsItsOwnResult() async throws {
        let (api, _) = try makeClient()
        let cases: [(Int, String)] = [
            (428, "pin_required"), (403, "profile_not_allowed"), (409, "already_active_profile"), (429, "pin_locked"),
        ]
        var seen: [String] = []
        for (status, code) in cases {
            StubURLProtocol.handler = { request in
                if request.url?.path == "/api/v1/devices/current/switch-profile" {
                    return (status, "{\"error\":\"\(code)\"}")
                }
                return (404, "{}")
            }
            switch await api.switchProfile(profileID: jim) {
            case .pinRequired: seen.append("pin")
            case .notAllowed: seen.append("notAllowed")
            case .alreadyActive: seen.append("alreadyActive")
            case .locked: seen.append("locked")
            default: seen.append("other")
            }
        }
        XCTAssertEqual(seen, ["pin", "notAllowed", "alreadyActive", "locked"])
        // A refused switch leaves Mary's credentials in place.
        let token = await api.currentToken
        XCTAssertEqual(token?.accessToken, "token-mary")
    }

    func testPhotoRoutesExplainAForbiddenDevice() async throws {
        let (api, _) = try makeClient()
        StubURLProtocol.handler = { _ in (403, "{\"error\":\"insufficient_scope\"}") }
        do {
            _ = try await api.viewTimeline()
            XCTFail("A 403 should throw")
        } catch let error as TuvimaClientError {
            guard case .photoAccessDenied = error else { return XCTFail("Expected photoAccessDenied, got \(error)") }
            XCTAssertEqual(error.errorDescription, photoAccessDeniedMessage)
        }
    }

    // MARK: Helpers

    private func makeClient() throws -> (TuvimaAPIClient, MemoryTokenStore) {
        let store = MemoryTokenStore()
        try store.save(
            ClientToken(
                accessToken: "token-mary",
                refreshToken: "refresh-mary",
                expiresAt: Date().addingTimeInterval(3600),
                scope: "library.read playback.read",
                deviceID: device,
                profileID: mary
            ),
            for: origin
        )
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        let api = try TuvimaAPIClient(
            serverAddress: origin.absoluteString,
            clientVersion: "test",
            deviceName: "Test phone",
            store: store,
            session: URLSession(configuration: configuration)
        )
        return (api, store)
    }

    /// A tiny fake server: Mary and Jim each have their own queue, picked by the bearer token.
    private func answer(_ request: URLRequest) -> (status: Int, body: String) {
        let bearer = request.value(forHTTPHeaderField: "Authorization") ?? ""
        switch (request.httpMethod ?? "GET", request.url?.path ?? "") {
        case ("POST", "/api/v1/devices/current/switch-profile"):
            return (200, """
            {"access_token":"token-jim","token_type":"Bearer","expires_in":600,"refresh_token":"refresh-jim",
             "scope":"library.read playback.read","device_id":"\(device.uuidString)","profile_id":"\(jim.uuidString)"}
            """)
        case ("GET", "/api/v1/player/state"):
            if bearer == "Bearer token-jim" {
                return (200, queue(title: "Jim Book", mediaType: "Audiobooks"))
            }
            return (200, queue(title: "Mary Song", mediaType: "Music"))
        case ("GET", "/api/v1/display/browse"):
            return (200, "{\"key\":\"listen\",\"title\":\"Listen\",\"subtitle\":null,\"shelves\":[]}")
        default:
            return (404, "{}")
        }
    }

    private func queue(title: String, mediaType: String) -> String {
        """
        {"queue":[{"assetId":"\(UUID().uuidString)","mediaType":"\(mediaType)","title":"\(title)","subtitle":null}]}
        """
    }
}

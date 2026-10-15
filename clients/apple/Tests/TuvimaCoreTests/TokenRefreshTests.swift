import XCTest
@testable import TuvimaCore

/// Counts how often something happened, from any thread.
private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    func increment() { lock.lock(); value += 1; lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return value }
}

/// Token refresh behaviour against a fake server: one refresh at a time, and only the exact `invalid_grant` answer signs
/// the device out. They need no device or server, but they are not part of any target yet (see clients/apple/README.md for
/// the project.yml stanza that turns them on), so CI does not run them.
final class TokenRefreshTests: XCTestCase {
    private let mary = UUID(uuidString: "33333333-3333-3333-3333-333333333333")!
    private let device = UUID(uuidString: "22222222-2222-2222-2222-222222222222")!
    private let origin = URL(string: "http://localhost:5016")!

    override func setUp() {
        super.setUp()
        StubURLProtocol.seen = []
    }

    override func tearDown() {
        StubURLProtocol.handler = nil
        super.tearDown()
    }

    func testConcurrentCallsShareOneRefresh() async throws {
        let refreshes = Counter()
        StubURLProtocol.handler = { [self] request in
            if request.url?.path == "/api/v1/oauth/token" {
                refreshes.increment()
                return (200, tokenBody(access: "fresh", refresh: "rotated"))
            }
            return answer(request)
        }
        let (api, store, _) = try makeClient(expired: true)

        async let first = api.browse(lane: "listen")
        async let second = api.browse(lane: "listen")
        async let third = api.browse(lane: "listen")
        _ = try await (first, second, third)

        XCTAssertEqual(refreshes.count, 1)
        XCTAssertEqual(store.load(for: origin)?.refreshToken, "rotated")
    }

    func testInvalidGrantSignsTheDeviceOut() async throws {
        StubURLProtocol.handler = { [self] request in
            if request.url?.path == "/api/v1/oauth/token" { return (400, "{\"error\":\"invalid_grant\"}") }
            return answer(request)
        }
        let (api, store, revoked) = try makeClient(expired: true)

        do {
            _ = try await api.browse(lane: "listen")
            XCTFail("A revoked grant should throw")
        } catch let error as TuvimaClientError {
            guard case .sessionRevoked = error else { return XCTFail("Expected sessionRevoked, got \(error)") }
        }
        XCTAssertEqual(revoked.count, 1)
        XCTAssertNil(store.load(for: origin))
        let paired = await api.isPaired
        XCTAssertFalse(paired)
    }

    func testAProxyRefusalDoesNotSignTheDeviceOut() async throws {
        for (status, body) in [(403, "<html>Forbidden</html>"), (401, "<html>Unauthorized</html>"), (400, "{\"error\":\"invalid_request\"}")] {
            StubURLProtocol.handler = { [self] request in
                if request.url?.path == "/api/v1/oauth/token" { return (status, body) }
                return answer(request)
            }
            let (api, store, revoked) = try makeClient(expired: true)
            do {
                _ = try await api.browse(lane: "listen")
                XCTFail("A refused refresh should throw")
            } catch let error as TuvimaClientError {
                guard case .http = error else { return XCTFail("Expected an HTTP error, got \(error)") }
            }
            XCTAssertEqual(revoked.count, 0)
            XCTAssertNotNil(store.load(for: origin))
            let paired = await api.isPaired
            XCTAssertTrue(paired)
        }
    }

    // MARK: Helpers

    private func makeClient(expired: Bool) throws -> (TuvimaAPIClient, MemoryTokenStore, Counter) {
        let store = MemoryTokenStore()
        try store.save(
            ClientToken(
                accessToken: "token-mary",
                refreshToken: "refresh-mary",
                expiresAt: Date().addingTimeInterval(expired ? -60 : 3600),
                scope: "library.read playback.read",
                deviceID: device,
                profileID: mary
            ),
            for: origin
        )
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubURLProtocol.self]
        let revoked = Counter()
        let api = try TuvimaAPIClient(
            serverAddress: origin.absoluteString,
            clientVersion: "test",
            deviceName: "Test phone",
            store: store,
            session: URLSession(configuration: configuration),
            onSessionRevoked: { revoked.increment() }
        )
        return (api, store, revoked)
    }

    private func tokenBody(access: String, refresh: String) -> String {
        """
        {"access_token":"\(access)","token_type":"Bearer","expires_in":600,"refresh_token":"\(refresh)",
         "scope":"library.read playback.read","device_id":"\(device.uuidString)","profile_id":"\(mary.uuidString)"}
        """
    }

    private func answer(_ request: URLRequest) -> (status: Int, body: String) {
        switch request.url?.path ?? "" {
        case "/.well-known/tuvima":
            return (200, """
            {"server_name":"Test","api_base_url":"http://localhost:5016/api/v1","supported_api_versions":["1"],
             "device_authorization_endpoint":"/api/v1/oauth/device","token_endpoint":"/api/v1/oauth/token",
             "verification_uri":"http://localhost:5016/pair","capabilities":[]}
            """)
        case "/api/v1/display/browse":
            return (200, "{\"key\":\"listen\",\"title\":\"Listen\",\"subtitle\":null,\"shelves\":[]}")
        default:
            return (404, "{}")
        }
    }
}

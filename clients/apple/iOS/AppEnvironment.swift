import Foundation
import TuvimaCore
import UIKit

struct NativeDetailLink: Equatable {
    let entityType: String
    let id: UUID
}

@MainActor
final class AppEnvironment: ObservableObject {
    static let shared = AppEnvironment()

    @Published private(set) var api: TuvimaAPIClient?
    @Published private(set) var pendingDetail: NativeDetailLink?
    /// Bumped after a profile switch so the Home, Libraries, Search and Downloads screens load again for the new person.
    @Published private(set) var sessionRevision = 0
    let playback = PlaybackCoordinator()
    let downloads = OfflineDownloadManager()

    private init() {
        playback.attachOffline(downloads)
        if let address = UserDefaults.standard.string(forKey: "tuvima.server") {
            try? connect(address)
        }
    }

    var serverAddress: String { UserDefaults.standard.string(forKey: "tuvima.server") ?? "" }

    @discardableResult
    func connect(_ address: String) throws -> TuvimaAPIClient {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"
        let value = try TuvimaAPIClient(
            serverAddress: address,
            clientVersion: version,
            deviceName: UIDevice.current.name
        )
        UserDefaults.standard.set(address, forKey: "tuvima.server")
        api = value
        playback.attach(value)
        downloads.attach(value)
        return value
    }

    /// Forgets the server on this device without telling it.
    func disconnect() {
        let current = api
        Task { await current?.signOut() }
        downloads.clearAll()
        api = nil
        UserDefaults.standard.removeObject(forKey: "tuvima.server")
    }

    /// Removes this device on the server, then forgets it here. If the server cannot be reached the device is
    /// still signed out locally and can be removed from the Dashboard's device list.
    func signOut() async {
        playback.pause()
        let current = api
        if let current {
            try? await current.revokeCurrentDevice()
            await current.signOut()
        }
        downloads.clearAll()
        api = nil
        UserDefaults.standard.removeObject(forKey: "tuvima.server")
    }

    /// Called after the app switched to another person: stops playback and reloads the screens.
    func profileSwitched() {
        playback.pause()
        downloads.profileChanged()
        sessionRevision += 1
    }

    func handleDeepLink(_ url: URL) {
        guard url.scheme == "tuvima" else { return }
        if url.host == "play", let id = url.pathComponents.dropFirst().first.flatMap(UUID.init(uuidString:)) {
            playback.play(assetID: id, title: "Tuvima Library")
        } else if url.host == "details" {
            let parts = Array(url.pathComponents.dropFirst())
            if parts.count >= 2, let id = UUID(uuidString: parts[1]) {
                pendingDetail = NativeDetailLink(entityType: parts[0], id: id)
            }
        }
    }

    func consumePendingDetail() -> NativeDetailLink? {
        defer { pendingDetail = nil }
        return pendingDetail
    }
}

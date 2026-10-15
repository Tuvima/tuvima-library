import SwiftUI
import TuvimaCore

/// What the app asks for at pairing. Kept outside the main-actor model so views can read the key directly.
enum PairingSettings {
    /// Stored on the device: whether the person ticked "Allow access to my photos and videos" on the server screen.
    static let photosKey = "tuvima.requestPhotos"

    /// The app's own scopes. The photo scopes are added only when the person opts in.
    static let baseScopes =
        "library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write downloads.read downloads.write"

    static var requestedScopes: String {
        PhotoScopes.requested(
            baseScopes: baseScopes,
            includePhotos: UserDefaults.standard.bool(forKey: photosKey)
        )
    }
}

/// Connecting and pairing: from the server address to a paired device, before the tabs appear.
@MainActor
final class ConnectionModel: ObservableObject {
    enum Phase {
        case server
        case loading(String)
        case pairing(PairingSession)
        case ready
        case error(String)
    }

    @Published var phase: Phase = .server
    private var pollTask: Task<Void, Never>?

    func start(environment: AppEnvironment) {
        guard let api = environment.api else { return }
        connect(api)
    }

    func connect(_ address: String, environment: AppEnvironment) {
        do { connect(try environment.connect(address)) }
        catch { phase = .error(error.localizedDescription) }
    }

    func cancelPairing() {
        pollTask?.cancel()
        phase = .server
    }

    private func connect(_ api: TuvimaAPIClient) {
        pollTask?.cancel()
        phase = .loading("Connecting…")
        pollTask = Task {
            do {
                _ = try await api.discoverServer()
                if await api.isPaired {
                    phase = .ready
                } else {
                    let pairing = try await api.beginPairing(scopes: PairingSettings.requestedScopes)
                    phase = .pairing(pairing)
                    await poll(api, pairing)
                }
            } catch {
                if !isCancellation(error) { phase = .error(error.localizedDescription) }
            }
        }
    }

    private func poll(_ api: TuvimaAPIClient, _ pairing: PairingSession) async {
        var interval = pairing.interval
        let deadline = Date().addingTimeInterval(TimeInterval(pairing.expiresIn))
        while Date() < deadline {
            try? await Task.sleep(for: .seconds(interval))
            if Task.isCancelled { return }
            do {
                switch try await api.pollPairing(pairing) {
                case .authorized:
                    phase = .ready
                    return
                case .pending(let next):
                    interval = next
                case let .failed(_, description):
                    phase = .error(description ?? "Pairing failed.")
                    return
                }
            } catch {
                if !isCancellation(error) { phase = .error(error.localizedDescription) }
                return
            }
        }
        phase = .error("The pairing code expired.")
    }
}

import Foundation

/// Which person's session a piece of background work belongs to. `epoch` counts every profile change (and sign-out) on
/// the client, so a switch away and back again during one run is still seen as a change.
public struct ProfileBinding: Equatable, Sendable {
    public let profileID: UUID
    public let epoch: Int

    public init(profileID: UUID, epoch: Int) {
        self.profileID = profileID
        self.epoch = epoch
    }
}

/// Work started inside `ProfileScope.$bound.withValue(binding) { ... }` is bound to that profile: every authenticated
/// client call made inside refuses to run (it throws `TuvimaClientError.profileChanged`) once the client's current
/// profile no longer matches, so one run never sends a request with a different person's token.
public enum ProfileScope {
    @TaskLocal public static var bound: ProfileBinding?
}

/// Binds one sync (or download) run to the person who was using the app when it started. The run checks `isBound` before
/// each network call and again before it applies the answer; when it is false the run stops and marks, removes or
/// purges nothing, so one person's records never reach (or get cleaned against) another person's list.
public struct SyncGuard: Equatable, Sendable {
    public let binding: ProfileBinding

    public var profileID: UUID { binding.profileID }

    /// Binds to whoever is signed in now; nil when nobody is.
    public static func start(current: ProfileBinding?) -> SyncGuard? {
        current.map { SyncGuard(binding: $0) }
    }

    /// Binds to `expectedProfile` when that person is still the one signed in; nil when another profile (or nobody) is.
    public static func start(expectedProfile: UUID, current: ProfileBinding?) -> SyncGuard? {
        guard let current, current.profileID == expectedProfile else { return nil }
        return SyncGuard(binding: current)
    }

    /// True while the same person, in the same session, is still signed in.
    public func isBound(current: ProfileBinding?) -> Bool { current == binding }
}

public enum RefreshFailureKind: Equatable, Sendable {
    /// The Engine no longer accepts this device's grant: sign out and pair again.
    case revoked
    /// A network error or a server problem: keep the sign-in and try again later.
    case transient
}

/// Tracks profile switches so a refresh refusal caused by one is not mistaken for a revocation. The Engine's switch revokes
/// every token of the device, so a refresh sent with the previous profile's refresh token just before or after a switch is
/// refused with `invalid_grant` even though the device is fine. While a switch is pending, or when one started or finished
/// during a refresh attempt (the generation changed), the refusal is transient.
public struct SwitchTracker: Equatable, Sendable {
    public private(set) var pending = 0
    public private(set) var generation = 0

    public init() {}

    /// A profile switch request is about to be sent.
    public mutating func begin() {
        pending += 1
        generation += 1
    }

    /// The switch ended (stored, refused or failed).
    public mutating func finish() {
        pending = max(0, pending - 1)
        generation += 1
    }

    /// True when a refusal of a refresh that began at `capturedGeneration` may be a real revocation (no switch involved).
    public func mayTreatRefusalAsRevoked(capturedGeneration: Int) -> Bool {
        pending == 0 && generation == capturedGeneration
    }
}

public enum SessionLogic {
    /// Sorts a failed token refresh. The Engine answers every failure at the token endpoint with 400, and every real
    /// revocation carries the OAuth error `invalid_grant`, so only that exact code (parsed from the answer's JSON body)
    /// means the device was revoked or its grant changed. Any other answer (a 400 without the code, a 401 or 403 from a
    /// proxy or host filter, an HTML page, 408, 429, 5xx) and no answer at all (`statusCode` nil) is transient and must
    /// never sign the person out or delete their downloads.
    public static func classifyRefreshFailure(statusCode: Int?, errorCode: String?) -> RefreshFailureKind {
        errorCode == "invalid_grant" ? .revoked : .transient
    }
}

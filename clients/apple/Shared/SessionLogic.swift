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

public enum SessionLogic {
    /// Sorts a failed token refresh. `statusCode` is nil when no answer arrived (no connection, timeout). `invalid_grant`,
    /// or a 400, 401 or 403 answer to the refresh, means the device was revoked or its grant changed. Anything else (no
    /// answer, 408, 429, 5xx) is transient and must never sign the person out.
    public static func classifyRefreshFailure(statusCode: Int?, errorCode: String?) -> RefreshFailureKind {
        if errorCode?.lowercased() == "invalid_grant" { return .revoked }
        if let statusCode, [400, 401, 403].contains(statusCode) { return .revoked }
        return .transient
    }
}

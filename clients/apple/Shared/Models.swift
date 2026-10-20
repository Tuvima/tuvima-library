import Foundation

public struct Discovery: Decodable, Sendable {
    public let serverName: String
    public let apiBaseURL: String
    public let supportedAPIVersions: [String]
    public let deviceAuthorizationEndpoint: String
    public let tokenEndpoint: String
    public let verificationURI: String
    public let capabilities: [String]

    enum CodingKeys: String, CodingKey {
        case serverName = "server_name"
        case apiBaseURL = "api_base_url"
        case supportedAPIVersions = "supported_api_versions"
        case deviceAuthorizationEndpoint = "device_authorization_endpoint"
        case tokenEndpoint = "token_endpoint"
        case verificationURI = "verification_uri"
        case capabilities
    }
}

public struct PairingSession: Decodable, Sendable {
    public let deviceCode: String
    public let userCode: String
    public let verificationURI: String
    public let verificationURIComplete: String
    public let expiresIn: Int
    public let interval: Int

    enum CodingKeys: String, CodingKey {
        case deviceCode = "device_code"
        case userCode = "user_code"
        case verificationURI = "verification_uri"
        case verificationURIComplete = "verification_uri_complete"
        case expiresIn = "expires_in"
        case interval
    }
}

public struct ClientToken: Codable, Sendable {
    public let accessToken: String
    public let refreshToken: String
    public let expiresAt: Date
    public let scope: String
    public let deviceID: UUID
    public let profileID: UUID

    public var expiresSoon: Bool { expiresAt.timeIntervalSinceNow < 30 }

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case refreshToken = "refresh_token"
        case expiresAt = "expires_at"
        case scope
        case deviceID = "device_id"
        case profileID = "profile_id"
    }
}

struct TokenResponse: Decodable {
    let accessToken: String
    let refreshToken: String
    let expiresIn: TimeInterval
    let scope: String
    let deviceID: UUID
    let profileID: UUID

    enum CodingKeys: String, CodingKey {
        case accessToken = "access_token"
        case refreshToken = "refresh_token"
        case expiresIn = "expires_in"
        case scope
        case deviceID = "device_id"
        case profileID = "profile_id"
    }
}

public struct DisplayPage: Decodable, Sendable {
    public let key: String
    public let title: String
    public let subtitle: String?
    public let shelves: [DisplayShelf]
}

public struct DisplayShelf: Decodable, Identifiable, Sendable {
    public var id: String { key }
    public let key: String
    public let title: String
    public let subtitle: String?
    public let items: [DisplayCard]
    public let seeAllRoute: String?
}

public struct DisplayCard: Decodable, Identifiable, Sendable {
    public let id: UUID
    public let workId: UUID?
    public let assetId: UUID?
    public let collectionId: UUID?
    public let mediaType: String
    public let title: String
    public let subtitle: String?
    public let facts: [String]
    public let actions: [DisplayAction]
    public let description: String?
    public let continuationState: String?
    public let preferredShape: String?
    public let artwork: DisplayArtwork?
    public let progress: DisplayProgress?
    public let flags: DisplayCardFlags?

    public var playableAssetID: UUID? { actions.compactMap(\.assetId).first ?? assetId }

    /// True when the card plays as audio or video (a readable book or comic is not played).
    public var isPlayable: Bool { flags?.isPlayable ?? actions.contains { $0.type == "playAsset" } }

    /// The file to play, or nil for a card that cannot be played.
    public var playAssetID: UUID? { isPlayable ? playableAssetID : nil }

    /// Artwork path (relative to the server) suited to the card's preferred shape.
    public var artworkPath: String? {
        if preferredShape == "square" {
            return artwork?.squareMediumUrl ?? artwork?.squareUrl ?? artwork?.coverMediumUrl ?? artwork?.coverUrl
        }
        return artwork?.coverMediumUrl ?? artwork?.coverUrl ?? artwork?.coverLargeUrl
            ?? artwork?.squareMediumUrl ?? artwork?.squareUrl
    }
}

public struct DisplayCardFlags: Decodable, Sendable {
    public let isPlayable: Bool?
    public let isReadable: Bool?
}

public struct DisplayArtwork: Decodable, Sendable {
    public let coverUrl: String?
    public let coverMediumUrl: String?
    public let coverLargeUrl: String?
    public let squareUrl: String?
    public let squareMediumUrl: String?
    public let squareLargeUrl: String?
    public let backgroundUrl: String?
}

public struct DisplayProgress: Decodable, Sendable {
    public let percent: Double
    public let label: String?
}

public struct DisplayAction: Decodable, Sendable {
    public let type: String
    public let label: String
    public let workId: UUID?
    public let assetId: UUID?
    public let collectionId: UUID?
    public let webUrl: String?
}

public struct SearchResponse: Decodable, Sendable {
    public let query: String
    public let sections: [SearchSection]
}

public struct SearchSection: Decodable, Identifiable, Sendable {
    public var id: String { key }
    public let key: String
    public let title: String
    public let results: [SearchResult]
}

public struct SearchResult: Decodable, Identifiable, Sendable {
    public let id: UUID
    public let entityType: String
    public let mediaType: String?
    public let title: String
    public let subtitle: String?
    public let description: String?
    public let detailRoute: String

    public var detailTarget: (entityType: String, id: UUID)? { detailTargetFromRoute(detailRoute) }
}

public struct DetailPage: Decodable, Sendable {
    public let id: String?
    public let title: String
    public let subtitle: String?
    public let tagline: String?
    public let description: String?
    public let facts: DetailFacts?
    public let artwork: DetailArtwork?
    public let progress: DetailProgress?
    public let previewContributors: [DetailCredit]?
    public let ownedFormats: [DetailEdition]?

    /// Portrait-first artwork path, relative to the server.
    public var posterPath: String? {
        artwork?.posterUrl ?? artwork?.coverUrl ?? artwork?.heroArtwork?.largeUrl
            ?? artwork?.heroArtwork?.url ?? artwork?.backdropUrl
    }

    /// True when the person has started this work and has not finished it.
    public var isInProgress: Bool {
        guard let percent = progress?.percent else { return false }
        return percent > 0 && percent < 99.5
    }

    /// Cast for the page: the credited contributors when there are any, otherwise the plain actor names.
    public var cast: [DetailCastMember] {
        var seen = Set<String>()
        var people: [DetailCastMember] = []
        for credit in previewContributors ?? [] where !credit.displayName.isEmpty {
            if seen.insert(credit.displayName).inserted {
                let role = (credit.characterName?.isEmpty == false) ? credit.characterName : credit.primaryRole
                people.append(DetailCastMember(name: credit.displayName, role: role))
            }
        }
        if people.isEmpty {
            for name in facts?.actors ?? [] where seen.insert(name).inserted {
                people.append(DetailCastMember(name: name, role: nil))
            }
        }
        return Array(people.prefix(24))
    }
}

public struct DetailCastMember: Identifiable, Sendable {
    public var id: String { name }
    public let name: String
    public let role: String?
}

public struct DetailFacts: Decodable, Sendable {
    public let year: String?
    public let rating: String?
    public let contentRating: String?
    public let runtime: String?
    public let duration: String?
    public let genres: [String]?
    public let actors: [String]?

    public var summary: String {
        [year, rating, contentRating, runtime, duration]
            .compactMap { $0?.isEmpty == false ? $0 : nil }
            .reduce(into: [String]()) { if !$0.contains($1) { $0.append($1) } }
            .joined(separator: " · ")
    }
}

public struct DetailArtwork: Decodable, Sendable {
    public let posterUrl: String?
    public let coverUrl: String?
    public let backdropUrl: String?
    public let heroArtwork: DetailHeroArtwork?
}

public struct DetailHeroArtwork: Decodable, Sendable {
    public let url: String?
    public let largeUrl: String?
}

public struct DetailProgress: Decodable, Sendable {
    public let percent: Double?
}

public struct DetailCredit: Decodable, Sendable {
    public let displayName: String
    public let characterName: String?
    public let primaryRole: String?
}

public struct DetailEdition: Decodable, Sendable {
    public let editionID: String?
    public let editionTitle: String?
    public let displayName: String?
    public let fileFormat: String?
    public let runtime: String?
    public let publisher: String?

    enum CodingKeys: String, CodingKey {
        case editionID = "id"
        case editionTitle, displayName, fileFormat, runtime, publisher
    }

    public var title: String { editionTitle ?? displayName ?? "Edition" }

    public var details: String? {
        let line = [fileFormat, runtime, publisher].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        return line.isEmpty ? nil : line
    }
}

public struct PlaybackManifest: Decodable, Sendable {
    public let assetId: UUID
    public let recommendedDelivery: String
    public let directPlaySupported: Bool
    public let directStreamUrl: String?
    public let hlsUrl: String?
    public let hlsStatus: String?
    public let hlsExpiresAt: String?
    public let resume: PlaybackResume?
    public let offlineVariants: [OfflineVariant]
    public let warnings: [String]
}

public struct PlaybackResume: Decodable, Sendable {
    public let positionSeconds: Double
    public let durationSeconds: Double?
    public let completed: Bool
}

public struct OfflineVariant: Decodable, Sendable {
    public let id: UUID
    public let assetId: UUID
    public let status: String
    public let downloadUrl: String?
    public let fileSizeBytes: Int64?
    public let profileKey: String?
    public let expiresAt: String?

    /// When the Engine will remove this prepared copy; nil when it never does.
    public var expiresAtDate: Date? { OfflineLogic.parseIso(expiresAt) }
}

public struct PlayerQueueItem: Decodable, Sendable {
    public let assetId: UUID?
    public let mediaType: String
    public let title: String
    public let subtitle: String?
}

struct PlayerState: Decodable {
    let queue: [PlayerQueueItem]
}

public enum PairingPoll: Sendable {
    case authorized(ClientToken)
    case pending(interval: Int)
    case failed(code: String, description: String?)
}

struct OAuthError: Decodable {
    let error: String
    let errorDescription: String?
    let interval: Int?

    enum CodingKeys: String, CodingKey {
        case error
        case errorDescription = "error_description"
        case interval
    }
}

public enum TuvimaClientError: LocalizedError {
    case invalidServer
    case insecureRemoteServer
    case unsupportedAPI
    case notPaired
    case http(Int, String)
    case noPlayableDelivery
    case invalidIdentifier
    case photoAccessDenied
    /// Work bound to one profile found another profile (or nobody) signed in.
    case profileChanged
    /// The Engine refused to refresh the sign-in because the device was revoked or its grant changed.
    case sessionRevoked

    public var errorDescription: String? {
        switch self {
        case .invalidServer: "Enter a valid Library Dashboard address."
        case .insecureRemoteServer: "Remote Library connections require HTTPS."
        case .unsupportedAPI: "This server does not advertise API v1."
        case .notPaired: "This device has not been paired."
        case let .http(status, _): "Library returned HTTP \(status)."
        case .noPlayableDelivery: "The server did not provide a playable delivery."
        case .invalidIdentifier: "That item could not be opened."
        case .photoAccessDenied: photoAccessDeniedMessage
        case .profileChanged: "The profile in use changed while this was running."
        case .sessionRevoked: "This device was signed out by the server. Connect it again."
        }
    }
}

import Foundation

// Types behind offline downloads: the records the phone keeps, the shapes the Engine answers with, and the
// pieces the pure rules in OfflineLogic.swift work on. Nothing here touches the network, the disk or UIKit.
// The device-download, next-episodes and progress routes use snake_case; the client decodes them with
// `.convertFromSnakeCase`, so the property names are the camelCase forms (`stored_at` becomes `storedAt`).

/// Where one download is in its life. Only `complete` has a playable file.
public enum DownloadState: String, Codable, Sendable {
    case queued, preparing, downloading, paused, complete, failed
}

/// One title the phone holds, or is getting, for offline use. `fileName` is relative to the person's offline folder
/// (an absolute path would go stale when the app is updated).
public struct DownloadRecord: Codable, Identifiable, Equatable, Sendable {
    public var id: UUID { assetId }
    public var assetId: UUID
    public var variantId: UUID?
    public var title: String
    public var subtitle: String?
    public var artworkPath: String?
    public var hasLocalArtwork: Bool
    public var entityType: String
    public var showId: UUID?
    public var profileKey: String
    public var state: DownloadState
    public var bytesDone: Int64
    public var totalBytes: Int64
    public var storedAt: Date?
    public var expiresAt: Date?
    public var fileName: String?
    public var reportedToServer: Bool
    public var variantRequested: Bool
    public var error: String?
    public var queuedAt: Date

    public init(
        assetId: UUID,
        variantId: UUID? = nil,
        title: String,
        subtitle: String? = nil,
        artworkPath: String? = nil,
        hasLocalArtwork: Bool = false,
        entityType: String,
        showId: UUID? = nil,
        profileKey: String,
        state: DownloadState = .queued,
        bytesDone: Int64 = 0,
        totalBytes: Int64 = 0,
        storedAt: Date? = nil,
        expiresAt: Date? = nil,
        fileName: String? = nil,
        reportedToServer: Bool = false,
        variantRequested: Bool = false,
        error: String? = nil,
        queuedAt: Date = Date()
    ) {
        self.assetId = assetId
        self.variantId = variantId
        self.title = title
        self.subtitle = subtitle
        self.artworkPath = artworkPath
        self.hasLocalArtwork = hasLocalArtwork
        self.entityType = entityType
        self.showId = showId
        self.profileKey = profileKey
        self.state = state
        self.bytesDone = bytesDone
        self.totalBytes = totalBytes
        self.storedAt = storedAt
        self.expiresAt = expiresAt
        self.fileName = fileName
        self.reportedToServer = reportedToServer
        self.variantRequested = variantRequested
        self.error = error
        self.queuedAt = queuedAt
    }

    public var isComplete: Bool { state == .complete }

    /// True while the download is waiting, preparing or transferring (not paused, failed or finished).
    public var isActive: Bool { state == .queued || state == .preparing || state == .downloading }
}

/// One row of the Engine's device download list (`GET /api/v1/devices/current/downloads`).
public struct ServerDownloadEntry: Equatable, Sendable {
    public let assetId: UUID
    public let variantId: UUID
    public let storedBytes: Int64
    public let storedAt: Date?
    public let expiresAt: Date?

    public init(assetId: UUID, variantId: UUID, storedBytes: Int64, storedAt: Date?, expiresAt: Date?) {
        self.assetId = assetId
        self.variantId = variantId
        self.storedBytes = storedBytes
        self.storedAt = storedAt
        self.expiresAt = expiresAt
    }
}

struct DeviceDownloadWire: Decodable {
    let assetId: UUID
    let variantId: UUID
    let storedBytes: Int64?
    let storedAt: String?
    let expiresAt: String?

    var entry: ServerDownloadEntry {
        ServerDownloadEntry(
            assetId: assetId,
            variantId: variantId,
            storedBytes: storedBytes ?? 0,
            storedAt: OfflineLogic.parseIso(storedAt),
            expiresAt: OfflineLogic.parseIso(expiresAt)
        )
    }
}

struct DeviceDownloadsResponse: Decodable {
    let downloads: [DeviceDownloadWire]
}

/// One owned episode that follows the one in play (`GET /api/v1/details/show/{id}/next-episodes`).
public struct NextEpisode: Decodable, Identifiable, Sendable {
    public var id: UUID { assetId }
    public let workId: UUID?
    public let assetId: UUID
    public let title: String?
    public let seasonNumber: Int?
    public let episodeNumber: Int?
    public let stillUrl: String?
}

public struct NextEpisodesResponse: Decodable, Sendable {
    public let showId: UUID?
    public let afterAssetId: UUID?
    public let episodes: [NextEpisode]
}

/// Progress recorded on the phone while playing a downloaded file. `pending` is true until it has been replayed to
/// the Engine; `revision` is the last server revision seen for the asset (0 when none).
public struct OfflineProgressEntry: Codable, Equatable, Sendable {
    public var assetId: UUID
    public var positionSeconds: Double
    public var durationSeconds: Double?
    public var progressPct: Double
    public var recordedAt: Date
    public var pending: Bool
    public var revision: Int64

    public init(
        assetId: UUID,
        positionSeconds: Double,
        durationSeconds: Double?,
        progressPct: Double,
        recordedAt: Date,
        pending: Bool,
        revision: Int64
    ) {
        self.assetId = assetId
        self.positionSeconds = positionSeconds
        self.durationSeconds = durationSeconds
        self.progressPct = progressPct
        self.recordedAt = recordedAt
        self.pending = pending
        self.revision = revision
    }
}

/// The state the Engine answers with after a progress read or write.
public struct ServerProgress: Equatable, Sendable {
    public let revision: Int64
    public let progressPct: Double
    public let lastAccessed: Date?
    public let positionSeconds: Double?

    public init(revision: Int64, progressPct: Double, lastAccessed: Date?, positionSeconds: Double?) {
        self.revision = revision
        self.progressPct = progressPct
        self.lastAccessed = lastAccessed
        self.positionSeconds = positionSeconds
    }
}

struct ServerProgressWire: Decodable {
    let revision: Int64?
    let progressPct: Double?
    let lastAccessed: String?
    let extendedProperties: [String: String]?

    var progress: ServerProgress {
        // `.convertFromSnakeCase` also rewrites dictionary keys, so the position may arrive under either spelling.
        let position = extendedProperties?["position_seconds"] ?? extendedProperties?["positionSeconds"]
        return ServerProgress(
            revision: revision ?? 0,
            progressPct: progressPct ?? 0,
            lastAccessed: OfflineLogic.parseIso(lastAccessed),
            positionSeconds: position.flatMap { Double($0) }
        )
    }
}

/// What the phone must tell the Engine's download list about a variant.
public enum ReportKind: String, Codable, Sendable { case store, delete }

public struct PendingReport: Codable, Equatable, Sendable {
    public let kind: ReportKind
    public let assetId: UUID
    public let variantId: UUID
    public let storedBytes: Int64
    public let storedAt: Date

    public init(kind: ReportKind, assetId: UUID, variantId: UUID, storedBytes: Int64, storedAt: Date) {
        self.kind = kind
        self.assetId = assetId
        self.variantId = variantId
        self.storedBytes = storedBytes
        self.storedAt = storedAt
    }
}

/// What to do with a queued report after the Engine answered: finished, try again later, or give up on it.
public enum ReportOutcome: Equatable, Sendable { case done, retry, drop }

/// Which kind of title a download is, which decides the quality choices. `excluded` titles (books, comics and
/// photos) are not offered here.
public enum DownloadKind: Equatable, Sendable { case video, audio, unknown, excluded }

public struct QualityChoice: Identifiable, Equatable, Sendable {
    public var id: String { key }
    public let key: String
    public let label: String

    public init(key: String, label: String) {
        self.key = key
        self.label = label
    }
}

public struct ExpiryResult: Equatable, Sendable {
    public let keep: [DownloadRecord]
    public let purge: [DownloadRecord]

    public init(keep: [DownloadRecord], purge: [DownloadRecord]) {
        self.keep = keep
        self.purge = purge
    }
}

/// Everything the phone keeps for one person: their downloads, the reports still owed to the Engine and the
/// progress recorded offline.
public struct OfflineState: Codable, Equatable, Sendable {
    public var records: [DownloadRecord]
    public var reports: [PendingReport]
    public var progress: [OfflineProgressEntry]

    public init(records: [DownloadRecord] = [], reports: [PendingReport] = [], progress: [OfflineProgressEntry] = []) {
        self.records = records
        self.reports = reports
        self.progress = progress
    }
}

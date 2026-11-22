import Foundation

// Wire shapes for "Find another" lyrics and subtitle results (Find another on TV, WP7). They mirror
// `src/MediaEngine.Contracts/Playback/TextTrackResponses.cs`, which uses snake_case names. Like the other app models
// (see AppModels.swift) the client decodes them with `.convertFromSnakeCase`, so each property is the camelCase form of
// the Engine's name (`source_id` becomes `sourceId`) and the request is written back in snake_case. Anything the Engine
// may leave out is optional so an absent field never fails a whole window. No views: a tvOS target and the iPhone app
// both build their screens on these.

/// Which kind of text a window searches for. The raw value is what the Engine's `kind` query and body fields take.
public enum TextTrackKind: String, Codable, Sendable, CaseIterable {
    case lyrics
    case subtitles
}

/// One result a source offers for an item's lyrics or subtitles.
public struct TextTrackAlternative: Codable, Sendable, Equatable {
    public let provider: String
    public let providerName: String
    public let sourceId: String
    public let language: String
    public let isTimed: Bool
    public let confidence: Double
    public let durationMatchScore: Double?
    public let isHearingImpaired: Bool
    public let releaseName: String?
    public let preview: String?
    public let isCurrent: Bool
    /// The source's own name for the song, its artist and album (lyrics).
    public let title: String?
    public let artist: String?
    public let album: String?
    /// The length the source gives the result (lyrics), and the length of the item it is for (the song, or the film).
    public let lengthSeconds: Double?
    public let itemLengthSeconds: Double?
    /// Up to six lyric lines, or the first three spoken lines of a subtitle file with when each appears. Empty for a
    /// subtitle row that has not been read yet.
    public let previewLines: [TextTrackPreviewLine]
    /// Where a subtitle file's last line comes, once it has been read.
    public let endsAtSeconds: Double?
    /// `Fits` or `Off`; nil when either length is unknown.
    public let lengthCheck: String?
    /// Seconds the result is longer (positive) or shorter (negative) than the song, or the file's end against the film.
    public let lengthDifferenceSeconds: Double?

    public init(
        provider: String,
        providerName: String = "",
        sourceId: String,
        language: String = "und",
        isTimed: Bool = false,
        confidence: Double = 0,
        durationMatchScore: Double? = nil,
        isHearingImpaired: Bool = false,
        releaseName: String? = nil,
        preview: String? = nil,
        isCurrent: Bool = false,
        title: String? = nil,
        artist: String? = nil,
        album: String? = nil,
        lengthSeconds: Double? = nil,
        itemLengthSeconds: Double? = nil,
        previewLines: [TextTrackPreviewLine] = [],
        endsAtSeconds: Double? = nil,
        lengthCheck: String? = nil,
        lengthDifferenceSeconds: Double? = nil
    ) {
        self.provider = provider
        self.providerName = providerName
        self.sourceId = sourceId
        self.language = language
        self.isTimed = isTimed
        self.confidence = confidence
        self.durationMatchScore = durationMatchScore
        self.isHearingImpaired = isHearingImpaired
        self.releaseName = releaseName
        self.preview = preview
        self.isCurrent = isCurrent
        self.title = title
        self.artist = artist
        self.album = album
        self.lengthSeconds = lengthSeconds
        self.itemLengthSeconds = itemLengthSeconds
        self.previewLines = previewLines
        self.endsAtSeconds = endsAtSeconds
        self.lengthCheck = lengthCheck
        self.lengthDifferenceSeconds = lengthDifferenceSeconds
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        provider = try container.decodeIfPresent(String.self, forKey: .provider) ?? ""
        providerName = try container.decodeIfPresent(String.self, forKey: .providerName) ?? ""
        sourceId = try container.decodeIfPresent(String.self, forKey: .sourceId) ?? ""
        language = try container.decodeIfPresent(String.self, forKey: .language) ?? "und"
        isTimed = try container.decodeIfPresent(Bool.self, forKey: .isTimed) ?? false
        confidence = try container.decodeIfPresent(Double.self, forKey: .confidence) ?? 0
        durationMatchScore = try container.decodeIfPresent(Double.self, forKey: .durationMatchScore)
        isHearingImpaired = try container.decodeIfPresent(Bool.self, forKey: .isHearingImpaired) ?? false
        releaseName = try container.decodeIfPresent(String.self, forKey: .releaseName)
        preview = try container.decodeIfPresent(String.self, forKey: .preview)
        isCurrent = try container.decodeIfPresent(Bool.self, forKey: .isCurrent) ?? false
        title = try container.decodeIfPresent(String.self, forKey: .title)
        artist = try container.decodeIfPresent(String.self, forKey: .artist)
        album = try container.decodeIfPresent(String.self, forKey: .album)
        lengthSeconds = try container.decodeIfPresent(Double.self, forKey: .lengthSeconds)
        itemLengthSeconds = try container.decodeIfPresent(Double.self, forKey: .itemLengthSeconds)
        previewLines = try container.decodeIfPresent([TextTrackPreviewLine].self, forKey: .previewLines) ?? []
        endsAtSeconds = try container.decodeIfPresent(Double.self, forKey: .endsAtSeconds)
        lengthCheck = try container.decodeIfPresent(String.self, forKey: .lengthCheck)
        lengthDifferenceSeconds = try container.decodeIfPresent(Double.self, forKey: .lengthDifferenceSeconds)
    }

    /// The key that names this result inside one list: the source and its own id for the result.
    public var key: String { "\(provider):\(sourceId)" }
}

/// One line of a result's preview. `atSeconds` is set for subtitles only.
public struct TextTrackPreviewLine: Codable, Sendable, Equatable {
    public let atSeconds: Double?
    public let text: String

    public init(atSeconds: Double? = nil, text: String) {
        self.atSeconds = atSeconds
        self.text = text
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        atSeconds = try container.decodeIfPresent(Double.self, forKey: .atSeconds)
        text = try container.decodeIfPresent(String.self, forKey: .text) ?? ""
    }
}

/// Answer to the list call. The list is asked of the sources live and is never stored.
public struct TextTrackAlternativesResponse: Codable, Sendable, Equatable {
    public let assetId: UUID
    public let kind: String
    /// `Found`, `NoneFound`, `NoSourceOn`, `NotAllowed`, `Blocked`, `Unavailable`, `AssetMissing`, `Unsupported`; for
    /// subtitles also `IdentityUnverified`, `SourceKeyRequired`, `LimitReached` and `LanguageUnknown`.
    public let status: String
    public let message: String
    public let items: [TextTrackAlternative]
    /// The display name of the source the status is about (for example `SubDL`), when there is one.
    public let sourceName: String?
    /// The language code that was searched, for subtitles.
    public let language: String?

    public init(
        assetId: UUID,
        kind: String,
        status: String,
        message: String = "",
        items: [TextTrackAlternative] = [],
        sourceName: String? = nil,
        language: String? = nil
    ) {
        self.assetId = assetId
        self.kind = kind
        self.status = status
        self.message = message
        self.items = items
        self.sourceName = sourceName
        self.language = language
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        assetId = try container.decode(UUID.self, forKey: .assetId)
        kind = try container.decodeIfPresent(String.self, forKey: .kind) ?? ""
        status = try container.decodeIfPresent(String.self, forKey: .status) ?? ""
        message = try container.decodeIfPresent(String.self, forKey: .message) ?? ""
        items = try container.decodeIfPresent([TextTrackAlternative].self, forKey: .items) ?? []
        sourceName = try container.decodeIfPresent(String.self, forKey: .sourceName)
        language = try container.decodeIfPresent(String.self, forKey: .language)
    }
}

/// Answer to the read-one-result call: one result read on request.
public struct TextTrackAlternativePreviewResponse: Codable, Sendable, Equatable {
    public let assetId: UUID
    /// `Found`, `NotFound`, `Unavailable` or a source status such as `LimitReached`.
    public let status: String
    public let item: TextTrackAlternative?

    public init(assetId: UUID, status: String, item: TextTrackAlternative? = nil) {
        self.assetId = assetId
        self.status = status
        self.item = item
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        assetId = try container.decode(UUID.self, forKey: .assetId)
        status = try container.decodeIfPresent(String.self, forKey: .status) ?? ""
        item = try container.decodeIfPresent(TextTrackAlternative.self, forKey: .item)
    }
}

/// Answer to "is a source switched on for this item", from settings alone (no source is contacted).
public struct TextTrackSourcesResponse: Codable, Sendable, Equatable {
    public let assetId: UUID
    public let kind: String
    /// `Ready`, `NoSourceOn`, `SourceKeyRequired`, `Blocked`, `NotAllowed`, `Unsupported` or `AssetMissing`.
    public let status: String

    public init(assetId: UUID, kind: String, status: String) {
        self.assetId = assetId
        self.kind = kind
        self.status = status
    }

    /// True when the footer row "Find subtitles…" (or "Find lyrics") should be shown: only `Ready`.
    public var isReady: Bool { status == "Ready" }
}

/// Body of the use-this call. Sent in snake_case; `language` is left out when nil.
public struct ChooseTextTrackAlternativeRequest: Codable, Sendable, Equatable {
    public let kind: TextTrackKind
    public let provider: String
    public let sourceId: String
    /// For subtitles, the language name or code the list was searched in, so the same result is found again.
    public let language: String?

    public init(kind: TextTrackKind, provider: String, sourceId: String, language: String? = nil) {
        self.kind = kind
        self.provider = provider
        self.sourceId = sourceId
        self.language = language
    }

    /// The JSON body, with the Engine's snake_case names (`kind`, `provider`, `source_id`, `language`).
    public var jsonObject: [String: Any] {
        var body: [String: Any] = [
            "kind": kind.rawValue,
            "provider": provider,
            "source_id": sourceId,
        ]
        if let language, !language.isEmpty { body["language"] = language }
        return body
    }
}

/// Answer to the use-this call.
public struct TextTrackChoiceResponse: Codable, Sendable, Equatable {
    public let assetId: UUID
    /// `Updated` when the result is now in use; anything else is a refusal that `message` explains.
    public let status: String
    public let message: String
    /// The track now in use, or nil when none is.
    public let trackId: UUID?

    public init(assetId: UUID, status: String, message: String = "", trackId: UUID? = nil) {
        self.assetId = assetId
        self.status = status
        self.message = message
        self.trackId = trackId
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        assetId = try container.decode(UUID.self, forKey: .assetId)
        status = try container.decodeIfPresent(String.self, forKey: .status) ?? ""
        message = try container.decodeIfPresent(String.self, forKey: .message) ?? ""
        trackId = try container.decodeIfPresent(UUID.self, forKey: .trackId)
    }

    public var isUpdated: Bool { status == "Updated" }
}

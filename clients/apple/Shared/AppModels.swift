import Foundation

// Wire shapes for the app screens. The photo, Collections and device routes use snake_case; the client decodes
// them with `.convertFromSnakeCase`, so the property names here are the camelCase forms of the Engine's names
// (`next_cursor` becomes `nextCursor`). The display and details models stay camelCase on the wire. Anything the
// Engine may leave out is optional so an absent field never fails a whole screen.

// MARK: Paged answers

public struct PagedList<Element: Decodable & Sendable>: Decodable, Sendable {
    public let items: [Element]
    public let totalCount: Int?
    public let nextCursor: String?
    public let hasMore: Bool?

    /// True when the Engine says there is more and gave the cursor to ask for it.
    public var canLoadMore: Bool { (hasMore ?? false) && nextCursor != nil }
}

// MARK: Library browse

public struct BrowseFacets: Decodable, Sendable {
    public let genres: [String]?
    public let creators: [String]?
    public let years: [String]?
}

struct BrowseResponse: Decodable {
    let shelves: [DisplayShelf]?
    let catalog: [DisplayCard]?
    let totalCount: Int?
    let facets: BrowseFacets?

    var page: BrowsePage {
        let catalogCards = catalog ?? []
        let cards = catalogCards.isEmpty ? (shelves ?? []).flatMap { $0.items } : catalogCards
        return BrowsePage(
            cards: cards,
            totalCount: totalCount ?? cards.count,
            genres: facets?.genres ?? [],
            creators: facets?.creators ?? [],
            years: facets?.years ?? []
        )
    }
}

/// The cards of one browse page plus the sort and filter values the Engine reports for it.
public struct BrowsePage: Sendable {
    public let cards: [DisplayCard]
    public let totalCount: Int
    public let genres: [String]
    public let creators: [String]
    public let years: [String]
}

// MARK: Photos (View)

public struct ViewItem: Decodable, Identifiable, Hashable, Sendable {
    public let id: UUID
    public let mediaKind: String
    public let title: String?
    public let fileName: String?
    public let mimeType: String?
    public let capturedAt: String?
    public let width: Int?
    public let height: Int?
    public let durationSeconds: Double?
    public let favorite: Bool?
    public let locationName: String?

    public init(
        id: UUID,
        mediaKind: String,
        title: String? = nil,
        fileName: String? = nil,
        mimeType: String? = nil,
        capturedAt: String? = nil,
        width: Int? = nil,
        height: Int? = nil,
        durationSeconds: Double? = nil,
        favorite: Bool? = nil,
        locationName: String? = nil
    ) {
        self.id = id
        self.mediaKind = mediaKind
        self.title = title
        self.fileName = fileName
        self.mimeType = mimeType
        self.capturedAt = capturedAt
        self.width = width
        self.height = height
        self.durationSeconds = durationSeconds
        self.favorite = favorite
        self.locationName = locationName
    }

    public var isVideo: Bool { mediaKind.lowercased() == "video" }

    /// The title if there is one, otherwise the file name.
    public var displayTitle: String {
        if let title, !title.isEmpty { return title }
        if let fileName, !fileName.isEmpty { return fileName }
        return "Untitled"
    }
}

public struct ViewTimelineMonth: Decodable, Identifiable, Hashable, Sendable {
    public var id: Int { year * 100 + month }
    public let year: Int
    public let month: Int
    public let items: [ViewItem]

    public init(year: Int, month: Int, items: [ViewItem]) {
        self.year = year
        self.month = month
        self.items = items
    }

    enum CodingKeys: String, CodingKey { case year, month, items }
}

public struct ViewTimelinePage: Decodable, Sendable {
    public let months: [ViewTimelineMonth]
    public let nextCursor: String?
    public let hasMore: Bool?
}

public struct ViewItemPage: Decodable, Sendable {
    public let items: [ViewItem]
    public let nextCursor: String?
    public let hasMore: Bool?
}

public struct ViewGallery: Decodable, Identifiable, Hashable, Sendable {
    public let id: UUID
    public let name: String
    public let description: String?
    public let itemCount: Int?
    public let coverItemId: UUID?
}

public struct ViewGalleries: Decodable, Sendable {
    public let owned: [ViewGallery]
    public let sharedWithYou: [ViewGallery]
}

// MARK: Collections and Universes

public struct CollectionArtwork: Decodable, Sendable {
    public let coverUrl: String?
}

/// One entry of the Collections catalogue; `groupType` says what it is (show, series, collection, list).
public struct CollectionEntry: Decodable, Identifiable, Sendable {
    public let id: UUID
    public let name: String
    public let description: String?
    public let groupType: String?
    public let collectionType: String?
    public let systemKey: String?
    public let universeQid: String?
    public let itemCount: Int?
    public let artworkItems: [CollectionArtwork]?

    /// The Wikidata QID when the Engine carries one for this entry (`universe_qid`, else a QID-shaped `system_key`).
    public var resolvedUniverseQid: String? {
        if let universeQid, ClientRoutes.isQid(universeQid) { return universeQid.trimmingCharacters(in: .whitespaces) }
        if let systemKey, ClientRoutes.isQid(systemKey) { return systemKey.trimmingCharacters(in: .whitespaces) }
        return nil
    }

    /// What the person calls this group, by its group type.
    public var kindLabel: String { groupTypeLabel(groupType ?? "collection") }

    public var coverPaths: [String] { (artworkItems ?? []).compactMap { $0.coverUrl } }
}

public struct CollectionItem: Decodable, Identifiable, Sendable {
    public let id: UUID
    public let workId: UUID?
    public let title: String
    public let creator: String?
    public let mediaType: String?
    public let coverUrl: String?
    public let detailRoute: String?

    /// The details route's (entity type, id) pair, or a work fallback.
    public var detailTarget: (entityType: String, id: UUID) {
        detailTargetFromRoute(detailRoute) ?? ("work", workId ?? id)
    }
}

/// One Series (or show) of a Universe. `itemCount` and `shelf` are the numbered run; `alsoInThisWorld` holds the
/// Series' unnumbered items. Older Engines leave the also fields out, which reads as no also row.
public struct UniverseShelf: Decodable, Identifiable, Sendable {
    public var id: String { shelf.key }
    public let collectionId: UUID?
    public let groupType: String?
    public let itemCount: Int?
    public let shelf: DisplayShelf
    public let alsoInThisWorld: DisplayShelf?
    public let alsoInThisWorldCount: Int

    enum CodingKeys: String, CodingKey {
        case collectionId, groupType, itemCount, shelf, alsoInThisWorld, alsoInThisWorldCount
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        collectionId = try container.decodeIfPresent(UUID.self, forKey: .collectionId)
        groupType = try container.decodeIfPresent(String.self, forKey: .groupType)
        itemCount = try container.decodeIfPresent(Int.self, forKey: .itemCount)
        shelf = try container.decode(DisplayShelf.self, forKey: .shelf)
        alsoInThisWorld = try container.decodeIfPresent(DisplayShelf.self, forKey: .alsoInThisWorld)
        alsoInThisWorldCount = try container.decodeIfPresent(Int.self, forKey: .alsoInThisWorldCount) ?? 0
    }
}

/// A character a cast member plays in a Universe.
public struct UniverseCharacter: Decodable, Sendable {
    public let qid: String
    public let label: String
}

/// One person in a Universe's Cast with the characters they play there.
public struct UniverseCastMember: Decodable, Identifiable, Sendable {
    public var id: UUID { personId }
    public let personId: UUID
    public let name: String
    public let headshotUrl: String?
    public let characters: [UniverseCharacter]
    public let workCount: Int

    enum CodingKeys: String, CodingKey { case personId, name, headshotUrl, characters, workCount }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        personId = try container.decode(UUID.self, forKey: .personId)
        name = try container.decode(String.self, forKey: .name)
        headshotUrl = try container.decodeIfPresent(String.self, forKey: .headshotUrl)
        characters = try container.decodeIfPresent([UniverseCharacter].self, forKey: .characters) ?? []
        workCount = try container.decodeIfPresent(Int.self, forKey: .workCount) ?? 0
    }
}

/// A work in a Universe that is an adaptation of another (`relationship` is lower case, such as "based on").
public struct UniverseAdaptation: Decodable, Identifiable, Sendable {
    public var id: String { "\(qid)|\(relationship)|\(sourceQid)" }
    public let qid: String
    public let label: String
    public let mediaType: String?
    public let relationship: String
    public let sourceQid: String
    public let sourceLabel: String

    enum CodingKeys: String, CodingKey { case qid, label, mediaType, relationship, sourceQid, sourceLabel }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        qid = try container.decode(String.self, forKey: .qid)
        label = try container.decode(String.self, forKey: .label)
        mediaType = try container.decodeIfPresent(String.self, forKey: .mediaType)
        relationship = try container.decodeIfPresent(String.self, forKey: .relationship) ?? ""
        sourceQid = try container.decodeIfPresent(String.self, forKey: .sourceQid) ?? ""
        sourceLabel = try container.decodeIfPresent(String.self, forKey: .sourceLabel) ?? ""
    }
}

public struct UniverseDetail: Decodable, Sendable {
    public let qid: String
    public let name: String
    public let description: String?
    public let itemCount: Int?
    public let seriesCount: Int
    public let mediaTypes: [String]
    public let shelves: [UniverseShelf]
    public let cast: [UniverseCastMember]
    public let adaptations: [UniverseAdaptation]

    enum CodingKeys: String, CodingKey {
        case qid, name, description, itemCount, seriesCount, mediaTypes, shelves, cast, adaptations
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        qid = try container.decode(String.self, forKey: .qid)
        name = try container.decode(String.self, forKey: .name)
        description = try container.decodeIfPresent(String.self, forKey: .description)
        itemCount = try container.decodeIfPresent(Int.self, forKey: .itemCount)
        seriesCount = try container.decodeIfPresent(Int.self, forKey: .seriesCount) ?? 0
        mediaTypes = try container.decodeIfPresent([String].self, forKey: .mediaTypes) ?? []
        shelves = try container.decode([UniverseShelf].self, forKey: .shelves)
        cast = try container.decodeIfPresent([UniverseCastMember].self, forKey: .cast) ?? []
        adaptations = try container.decodeIfPresent([UniverseAdaptation].self, forKey: .adaptations) ?? []
    }
}

// MARK: Profiles

/// One person this paired app may switch to.
public struct DeviceProfile: Decodable, Identifiable, Hashable, Sendable {
    public var id: UUID { profileId }
    public let profileId: UUID
    public let displayName: String
    public let avatarUrl: String?
    public let isKids: Bool
    public let requiresPin: Bool
    public let isActive: Bool
}

struct DeviceProfilesResponse: Decodable {
    let profiles: [DeviceProfile]
}

/// The answer to a profile switch. Every refusal the Engine can give has its own case.
public enum SwitchProfileResult: Sendable {
    /// The switch worked; the client now holds the new person's tokens.
    case switched(ClientToken)
    /// HTTP 428: a PIN is needed, or the PIN given was wrong.
    case pinRequired(message: String?)
    /// HTTP 403: the account has no grant for that person, or this device may not switch.
    case notAllowed(message: String?)
    /// HTTP 409: the chosen person is already the one in use.
    case alreadyActive(message: String?)
    /// HTTP 429: too many wrong PINs, or too many switches in a minute.
    case locked(message: String?)
    /// Anything else, with the Engine's own error code when it gave one.
    case failed(statusCode: Int, code: String, message: String?)
}

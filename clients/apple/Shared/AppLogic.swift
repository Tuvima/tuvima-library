import Foundation

// Pure, network-free logic for the app screens: routes, sort and filter queries, photo timeline merging,
// profile-switch results, and the words shown for each. Kept in the shared framework so a unit-test target can
// reach it without a device or a server.

/// Shown when the Engine refuses a photo request because the device was not given the photo scopes.
public let photoAccessDeniedMessage =
    "Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it"

/// Percent-encodes everything except the RFC 3986 unreserved characters, so a value is safe inside a query string.
public func percentEncode(_ value: String) -> String {
    let unreserved = CharacterSet(
        charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"
    )
    return value.addingPercentEncoding(withAllowedCharacters: unreserved) ?? value
}

/// What a browse page asks for: the same sort, filters and window the Dashboard's library pages send to
/// `/api/v1/display/browse`.
public struct BrowseQuery: Equatable, Sendable {
    public var lane: String?
    public var mediaType: String?
    public var grouping: String?
    public var search: String?
    public var genres: [String]
    public var creators: [String]
    public var years: [String]
    public var status: String?
    public var sort: String?
    public var offset: Int
    public var limit: Int

    public init(
        lane: String? = nil,
        mediaType: String? = nil,
        grouping: String? = nil,
        search: String? = nil,
        genres: [String] = [],
        creators: [String] = [],
        years: [String] = [],
        status: String? = nil,
        sort: String? = nil,
        offset: Int = 0,
        limit: Int = 48
    ) {
        self.lane = lane
        self.mediaType = mediaType
        self.grouping = grouping
        self.search = search
        self.genres = genres
        self.creators = creators
        self.years = years
        self.status = status
        self.sort = sort
        self.offset = offset
        self.limit = limit
    }

    /// True when a filter (not just the sort) narrows the result.
    public var hasFilters: Bool {
        !genres.isEmpty || !creators.isEmpty || !years.isEmpty || !(status ?? "").isEmpty
    }
}

/// Every API v1 path the app screens use, built in one place so the paths stay inside `/api/v1` and can be
/// checked without a network or a device.
public enum ClientRoutes {
    private static let api = "/api/v1"

    /// Thumbnail edge lengths the Engine accepts; any other size answers 400.
    public static let thumbnailSizes: [Int] = [160, 320, 640]

    /// Preview edge lengths the Engine accepts.
    public static let previewSizes: [Int] = [1280, 2048]

    public static let deviceProfiles = "\(api)/devices/current/profiles"
    public static let switchProfile = "\(api)/devices/current/switch-profile"
    public static let viewGalleries = "\(api)/view/galleries"

    /// True for a Wikidata QID such as `Q4242`.
    public static func isQid(_ value: String?) -> Bool {
        guard let value else { return false }
        let text = value.trimmingCharacters(in: .whitespaces)
        guard text.count >= 2, text.count <= 16, text.first == "Q" else { return false }
        return text.dropFirst().allSatisfy { $0.isASCII && $0.isNumber }
    }

    public static func home() -> String { "\(api)/display/home?includeCatalog=false" }

    public static func continueAll(limit: Int = 24) -> String {
        "\(api)/display/continue?limit=\(clamp(limit, 1, 100))&includeCatalog=false"
    }

    public static func group(_ collectionID: UUID) -> String {
        "\(api)/display/groups/\(collectionID.uuidString.lowercased())?includeCatalog=false"
    }

    public static func browse(_ query: BrowseQuery) -> String {
        var parts: [String] = []
        if let value = query.lane, !value.isEmpty { parts.append("lane=\(percentEncode(value))") }
        if let value = query.mediaType, !value.isEmpty { parts.append("mediaType=\(percentEncode(value))") }
        if let value = query.grouping, !value.isEmpty { parts.append("grouping=\(percentEncode(value))") }
        if let value = query.search, !value.isEmpty { parts.append("search=\(percentEncode(value))") }
        if !query.genres.isEmpty { parts.append("genres=\(percentEncode(query.genres.joined(separator: ",")))") }
        if !query.creators.isEmpty { parts.append("creator=\(percentEncode(query.creators.joined(separator: ",")))") }
        if !query.years.isEmpty { parts.append("year=\(percentEncode(query.years.joined(separator: ",")))") }
        if let value = query.status, !value.isEmpty { parts.append("status=\(percentEncode(value))") }
        if let value = query.sort, !value.isEmpty { parts.append("sort=\(percentEncode(value))") }
        parts.append("offset=\(max(0, query.offset))")
        parts.append("limit=\(clamp(query.limit, 1, 200))")
        parts.append("includeCatalog=true")
        return "\(api)/display/browse?\(parts.joined(separator: "&"))"
    }

    public static func details(entityType: String, id: UUID) -> String {
        "\(api)/details/\(percentEncode(entityType))/\(id.uuidString.lowercased())"
    }

    public static func encodeJob(_ assetID: UUID) -> String {
        "\(api)/playback/\(assetID.uuidString.lowercased())/encode"
    }

    public static func revokeDevice(_ deviceID: UUID) -> String {
        "\(api)/devices/\(deviceID.uuidString.lowercased())"
    }

    // MARK: Offline downloads

    public static let deviceDownloads = "\(api)/devices/current/downloads"

    public static func deviceDownload(_ variantID: UUID) -> String {
        "\(deviceDownloads)/\(variantID.uuidString.lowercased())"
    }

    /// The prepared file of an offline variant (bearer token, `downloads.read`, supports ranges).
    public static func offlineFile(_ assetID: UUID, _ variantID: UUID) -> String {
        "\(api)/playback/\(assetID.uuidString.lowercased())/offline/\(variantID.uuidString.lowercased())"
    }

    public static func progress(_ assetID: UUID) -> String { "\(api)/progress/\(assetID.uuidString.lowercased())" }

    /// The owned episodes after the episode file `afterAssetID`; the Engine clamps `count` to 1-10.
    public static func nextEpisodes(showID: UUID, afterAssetID: UUID, count: Int = 3) -> String {
        "\(api)/details/show/\(showID.uuidString.lowercased())/next-episodes"
            + "?after=\(afterAssetID.uuidString.lowercased())&count=\(clamp(count, 1, 10))"
    }

    public static func viewTimeline(cursor: String? = nil, limit: Int = 100) -> String {
        paged("\(api)/view/timeline", cursor: cursor, limit: clamp(limit, 1, 200))
    }

    public static func viewGalleryItems(_ galleryID: UUID, cursor: String? = nil, limit: Int = 100) -> String {
        paged("\(api)/view/galleries/\(galleryID.uuidString.lowercased())/items", cursor: cursor, limit: clamp(limit, 1, 200))
    }

    public static func viewItem(_ id: UUID) -> String { "\(api)/view/items/\(id.uuidString.lowercased())" }

    public static func viewThumbnail(_ id: UUID, size: Int = 320) -> String {
        "\(viewItem(id))/thumbnail?size=\(nearestSize(size, allowed: thumbnailSizes))"
    }

    public static func viewPreview(_ id: UUID, size: Int = 2048) -> String {
        "\(viewItem(id))/preview?size=\(nearestSize(size, allowed: previewSizes))"
    }

    public static func viewOriginal(_ id: UUID) -> String { "\(viewItem(id))/original" }

    public static func collections(cursor: String? = nil, limit: Int = 24) -> String {
        paged("\(api)/collections", cursor: cursor, limit: clamp(limit, 1, 250))
    }

    public static func collectionItems(_ id: UUID, cursor: String? = nil, limit: Int = 24) -> String {
        paged("\(api)/collections/\(id.uuidString.lowercased())/items", cursor: cursor, limit: clamp(limit, 1, 250))
    }

    /// The Universe route only accepts a Wikidata QID such as `Q4242`; anything else yields nil.
    public static func universe(_ qid: String) -> String? {
        let value = qid.trimmingCharacters(in: .whitespaces)
        return isQid(value) ? "\(api)/universe/\(value)" : nil
    }

    /// The smallest allowed size that is at least `requested`, or the largest allowed size.
    public static func nearestSize(_ requested: Int, allowed: [Int]) -> Int {
        allowed.first(where: { $0 >= requested }) ?? (allowed.last ?? requested)
    }

    private static func paged(_ path: String, cursor: String?, limit: Int) -> String {
        var parts: [String] = []
        if let cursor, !cursor.isEmpty { parts.append("cursor=\(percentEncode(cursor))") }
        parts.append("limit=\(limit)")
        return "\(path)?\(parts.joined(separator: "&"))"
    }

    private static func clamp(_ value: Int, _ low: Int, _ high: Int) -> Int { min(max(value, low), high) }
}

// MARK: Details routes

/// `/details/work/{id}?context=watch` becomes ("work", id). A route without a `details` segment falls back to its
/// last two segments; nil when no id can be read.
public func detailTargetFromRoute(_ route: String?) -> (entityType: String, id: UUID)? {
    guard let route else { return nil }
    let pathAndQuery = route.components(separatedBy: "?")
    let path = pathAndQuery.first ?? route
    let parts = path.split(separator: "/").map { String($0) }
    if let index = parts.firstIndex(of: "details"), parts.count >= index + 3, let id = UUID(uuidString: parts[index + 2]) {
        return (parts[index + 1], id)
    }
    guard parts.count >= 2, let id = UUID(uuidString: parts[parts.count - 1]) else { return nil }
    return (parts[parts.count - 2], id)
}

/// Entity type for a card's media type when the card carries no details route.
public func detailEntityType(forMediaType mediaType: String) -> String {
    let value = mediaType.lowercased()
    if value.contains("movie") { return "movie" }
    if value.contains("episode") { return "tvEpisode" }
    if value.contains("tv") { return "tvShow" }
    if value.contains("audiobook") { return "audiobook" }
    if value.contains("book") { return "book" }
    if value.contains("comic") { return "comicIssue" }
    if value.contains("music") { return "musicAlbum" }
    return "work"
}

/// Where a card opens: the details route of its own action, else its collection, else its work.
public func cardDetailTarget(_ card: DisplayCard) -> (entityType: String, id: UUID) {
    for action in card.actions {
        if let target = detailTargetFromRoute(action.webUrl) { return target }
    }
    if let collectionID = card.collectionId { return ("collection", collectionID) }
    return (detailEntityType(forMediaType: card.mediaType), card.workId ?? card.id)
}

/// The label for a group type; unknown types read as a collection.
public func groupTypeLabel(_ groupType: String) -> String {
    switch groupType.lowercased() {
    case "show": return "Show"
    case "series": return "Series"
    case "list": return "List"
    case "universe": return "Universe"
    default: return "Collection"
    }
}

/// Work id to the file that plays it, for the playable cards that name both.
public func playableAssets(byWork cards: [DisplayCard]) -> [UUID: UUID] {
    var result: [UUID: UUID] = [:]
    for card in cards {
        if let work = card.workId, let asset = card.playAssetID { result[work] = asset }
    }
    return result
}

// MARK: Photo timeline

private let monthNames = [
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
]

public func monthLabel(year: Int, month: Int) -> String {
    month >= 1 && month <= 12 ? "\(monthNames[month - 1]) \(year)" : "\(year)"
}

/// Adds a page to the months already loaded. A month can continue on the next page, so a bucket with the same
/// year and month is merged into the earlier one, keeping item order and dropping repeats.
public func mergeTimelineMonths(
    existing: [ViewTimelineMonth],
    incoming: [ViewTimelineMonth]
) -> [ViewTimelineMonth] {
    var result = existing
    for month in incoming {
        if let index = result.firstIndex(where: { $0.year == month.year && $0.month == month.month }) {
            let known = Set(result[index].items.map { $0.id })
            let extra = month.items.filter { !known.contains($0.id) }
            result[index] = ViewTimelineMonth(
                year: month.year,
                month: month.month,
                items: result[index].items + extra
            )
        } else {
            result.append(month)
        }
    }
    return result
}

/// Photo scopes: requested only when the person opts in, and never part of the default scopes.
public enum PhotoScopes {
    public static let all = ["view.personal.read", "view.originals.read", "view.galleries.read"]

    /// `baseScopes` as they are, plus the photo scopes only when the person opted in.
    public static func requested(baseScopes: String, includePhotos: Bool) -> String {
        guard includePhotos else { return baseScopes }
        let existing = baseScopes.split(separator: " ").map(String.init)
        return (existing + all.filter { !existing.contains($0) }).joined(separator: " ")
    }

    /// True when a token's space-delimited scope string holds the scope that opens the photo timeline.
    public static func grantedIn(_ tokenScope: String) -> Bool {
        tokenScope.split(separator: " ").contains("view.personal.read")
    }
}

// MARK: Profile switching

/// Turns a switch-profile refusal into its typed result by HTTP status; the error code is only used for `.failed`.
public func classifySwitchFailure(statusCode: Int, code: String?, description: String?) -> SwitchProfileResult {
    switch statusCode {
    case 428: return .pinRequired(message: description)
    case 403: return .notAllowed(message: description)
    case 409: return .alreadyActive(message: description)
    case 429: return .locked(message: description)
    default: return .failed(statusCode: statusCode, code: code ?? "request_failed", message: description)
    }
}

/// What the person is told after a switch attempt; nil when the switch worked and nothing needs saying.
public func profileSwitchMessage(_ result: SwitchProfileResult, pinWasSent: Bool) -> String? {
    switch result {
    case .switched:
        return nil
    case .pinRequired:
        return pinWasSent ? "That PIN was not accepted. Try again." : "Enter the PIN for this profile."
    case .notAllowed:
        return "This account is not allowed to use that profile."
    case .alreadyActive:
        return "That profile is already in use."
    case let .locked(message):
        return message ?? "Too many attempts. Wait a few minutes and try again."
    case let .failed(statusCode, _, message):
        return message ?? "Switching profile failed (HTTP \(statusCode))."
    }
}

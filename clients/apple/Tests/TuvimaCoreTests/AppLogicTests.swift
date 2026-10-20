import XCTest
@testable import TuvimaCore

/// Pure-logic and wire-shape tests for the app screens. They need no device or server. They are not part of any
/// target yet: see clients/apple/README.md for the project.yml stanza that turns them on.
final class AppLogicTests: XCTestCase {
    private let id = UUID(uuidString: "6f0c2a3e-0000-0000-0000-000000000001")!

    // MARK: Routes

    func testBrowseRouteMatchesTheDashboardsOptions() {
        let query = BrowseQuery(
            lane: "watch",
            mediaType: "Movies",
            grouping: "shows",
            search: "dune & co",
            genres: ["Sci-Fi", "Drama"],
            creators: ["Frank Herbert"],
            years: ["2021"],
            status: "in-progress",
            sort: "title",
            offset: 48,
            limit: 48
        )
        XCTAssertEqual(
            ClientRoutes.browse(query),
            "/api/v1/display/browse?lane=watch&mediaType=Movies&grouping=shows&search=dune%20%26%20co"
                + "&genres=Sci-Fi%2CDrama&creator=Frank%20Herbert&year=2021&status=in-progress&sort=title"
                + "&offset=48&limit=48&includeCatalog=true"
        )
    }

    func testBrowseRouteClampsTheWindow() {
        let route = ClientRoutes.browse(BrowseQuery(lane: "listen", offset: -5, limit: 9999))
        XCTAssertTrue(route.contains("offset=0"))
        XCTAssertTrue(route.contains("limit=200"))
    }

    func testViewSizesSnapToTheSizesTheEngineAccepts() {
        XCTAssertTrue(ClientRoutes.viewThumbnail(id, size: 200).hasSuffix("/thumbnail?size=320"))
        XCTAssertTrue(ClientRoutes.viewThumbnail(id, size: 9999).hasSuffix("/thumbnail?size=640"))
        XCTAssertTrue(ClientRoutes.viewPreview(id, size: 1000).hasSuffix("/preview?size=1280"))
        XCTAssertTrue(ClientRoutes.viewPreview(id).hasSuffix("/preview?size=2048"))
    }

    func testUniverseRouteOnlyAcceptsAQid() {
        XCTAssertEqual(ClientRoutes.universe("Q4242"), "/api/v1/universe/Q4242")
        XCTAssertNil(ClientRoutes.universe("universe-one"))
        XCTAssertNil(ClientRoutes.universe("Q"))
        XCTAssertNil(ClientRoutes.universe("../etc"))
    }

    func testPagedRoutesCarryTheCursorUntouched() {
        XCTAssertEqual(ClientRoutes.collections(), "/api/v1/collections?limit=24")
        XCTAssertEqual(
            ClientRoutes.collectionItems(id, cursor: "abc=", limit: 10),
            "/api/v1/collections/6f0c2a3e-0000-0000-0000-000000000001/items?cursor=abc%3D&limit=10"
        )
        XCTAssertEqual(ClientRoutes.viewTimeline(limit: 500), "/api/v1/view/timeline?limit=200")
    }

    func testDetailTargetFromRoute() {
        let target = detailTargetFromRoute("/details/work/6f0c2a3e-0000-0000-0000-000000000001?context=watch")
        XCTAssertEqual(target?.entityType, "work")
        XCTAssertEqual(target?.id, id)
        XCTAssertNil(detailTargetFromRoute("/watch/player/not-an-id"))
        XCTAssertNil(detailTargetFromRoute(nil))
    }

    func testGroupTypeLabels() {
        XCTAssertEqual(groupTypeLabel("show"), "Show")
        XCTAssertEqual(groupTypeLabel("series"), "Series")
        XCTAssertEqual(groupTypeLabel("list"), "List")
        XCTAssertEqual(groupTypeLabel("universe"), "Universe")
        XCTAssertEqual(groupTypeLabel("anything-else"), "Collection")
    }

    // MARK: Photos

    func testPhotoScopesAreOnlyRequestedWhenTheUserOptsIn() {
        let base = "library.read playback.read"
        XCTAssertEqual(PhotoScopes.requested(baseScopes: base, includePhotos: false), base)
        let withPhotos = PhotoScopes.requested(baseScopes: base, includePhotos: true)
        for scope in PhotoScopes.all { XCTAssertTrue(withPhotos.split(separator: " ").contains(Substring(scope))) }
        XCTAssertFalse(PhotoScopes.grantedIn(base))
        XCTAssertTrue(PhotoScopes.grantedIn(withPhotos))
    }

    func testTimelineMonthsMergeAcrossPagesWithoutRepeats() throws {
        let first = try item("aaaaaaaa-0000-4000-8000-000000000001")
        let second = try item("aaaaaaaa-0000-4000-8000-000000000002")
        let third = try item("aaaaaaaa-0000-4000-8000-000000000003")
        let page1 = [ViewTimelineMonth(year: 2026, month: 3, items: [first, second])]
        let page2 = [
            ViewTimelineMonth(year: 2026, month: 3, items: [second, third]),
            ViewTimelineMonth(year: 2026, month: 2, items: [first]),
        ]
        let merged = mergeTimelineMonths(existing: page1, incoming: page2)
        XCTAssertEqual(merged.map { $0.id }, [202603, 202602])
        XCTAssertEqual(merged[0].items.map { $0.id }, [first.id, second.id, third.id])
    }

    func testMonthLabel() {
        XCTAssertEqual(monthLabel(year: 2026, month: 3), "March 2026")
        XCTAssertEqual(monthLabel(year: 2026, month: 0), "2026")
    }

    func testPhotoRefusalReadsAsTheDocumentedMessage() {
        XCTAssertEqual(
            TuvimaClientError.photoAccessDenied.errorDescription,
            "Photo access was not allowed for this device. Re-pair and tick photos and videos to enable it"
        )
    }

    // MARK: Profile switch results

    func testSwitchFailuresAreTypedByStatus() {
        guard case .pinRequired = classifySwitchFailure(statusCode: 428, code: "pin_required", description: nil) else {
            return XCTFail("428 should need a PIN")
        }
        guard case .notAllowed = classifySwitchFailure(statusCode: 403, code: "profile_not_allowed", description: nil) else {
            return XCTFail("403 should be not allowed")
        }
        guard case .alreadyActive = classifySwitchFailure(statusCode: 409, code: "already_active_profile", description: nil) else {
            return XCTFail("409 should be already active")
        }
        guard case .locked = classifySwitchFailure(statusCode: 429, code: "pin_locked", description: nil) else {
            return XCTFail("429 should be locked")
        }
        guard case let .failed(status, code, _) = classifySwitchFailure(statusCode: 500, code: nil, description: nil) else {
            return XCTFail("500 should be a plain failure")
        }
        XCTAssertEqual(status, 500)
        XCTAssertEqual(code, "request_failed")
    }

    func testProfileSwitchMessages() {
        XCTAssertEqual(
            profileSwitchMessage(.alreadyActive(message: nil), pinWasSent: false),
            "That profile is already in use."
        )
        XCTAssertEqual(
            profileSwitchMessage(.pinRequired(message: nil), pinWasSent: true),
            "That PIN was not accepted. Try again."
        )
        XCTAssertEqual(
            profileSwitchMessage(.pinRequired(message: nil), pinWasSent: false),
            "Enter the PIN for this profile."
        )
    }

    // MARK: Wire fixtures (tests/fixtures/native-client-v1)

    func testViewTimelineFixtureDecodes() throws {
        let page = try decode(ViewTimelinePage.self, fixture: "view-timeline")
        XCTAssertEqual(page.months.count, 2)
        XCTAssertEqual(page.months[0].year, 2026)
        XCTAssertEqual(page.months[0].month, 3)
        XCTAssertEqual(page.months[0].items.count, 2)
        XCTAssertTrue(page.months[0].items[0].isVideo)
        XCTAssertEqual(page.months[0].items[0].durationSeconds, 12.5)
        XCTAssertEqual(page.hasMore, true)
        XCTAssertNotNil(page.nextCursor)
        XCTAssertEqual(page.months[1].items[0].displayTitle, "IMG_0003.jpg")
    }

    func testViewGalleriesFixtureDecodes() throws {
        let galleries = try decode(ViewGalleries.self, fixture: "view-galleries")
        XCTAssertEqual(galleries.owned.first?.name, "Trip")
        XCTAssertEqual(galleries.owned.first?.itemCount, 12)
        XCTAssertNotNil(galleries.owned.first?.coverItemId)
        XCTAssertEqual(galleries.sharedWithYou.first?.name, "For Ana")
        XCTAssertNil(galleries.sharedWithYou.first?.coverItemId)
    }

    func testCollectionsFixturesDecode() throws {
        let catalog = try decode(PagedList<CollectionEntry>.self, fixture: "collections")
        XCTAssertEqual(catalog.items.count, 2)
        XCTAssertEqual(catalog.items[0].name, "Adult Universe")
        XCTAssertEqual(catalog.items[0].kindLabel, "Collection")
        XCTAssertEqual(catalog.items[0].coverPaths.count, 2)
        XCTAssertEqual(catalog.totalCount, 3)
        XCTAssertTrue(catalog.canLoadMore)

        let items = try decode(PagedList<CollectionItem>.self, fixture: "collection-items")
        XCTAssertEqual(items.items.count, 2)
        XCTAssertEqual(items.items[0].detailTarget.entityType, "work")
        XCTAssertEqual(items.items[0].detailTarget.id, items.items[0].workId)
    }

    func testUniverseFixtureDecodes() throws {
        let universe = try decode(UniverseDetail.self, fixture: "universe")
        XCTAssertEqual(universe.qid, "Q4242")
        XCTAssertEqual(universe.shelves.count, 3)
        let shelf = universe.shelves[0]
        XCTAssertEqual(shelf.groupType, "series")
        XCTAssertEqual(shelf.shelf.title, "Series Alpha")
        XCTAssertEqual(shelf.shelf.items.count, 2)
        XCTAssertNil(shelf.alsoInThisWorld)
        XCTAssertEqual(shelf.alsoInThisWorldCount, 0)
        XCTAssertTrue(shelf.shelf.items[0].isPlayable)
        XCTAssertNotNil(shelf.shelf.items[0].playAssetID)
        XCTAssertFalse(universe.shelves[1].shelf.items[0].isPlayable)
        XCTAssertNil(universe.shelves[1].shelf.items[0].playAssetID)

        let gamma = universe.shelves[2]
        XCTAssertEqual(gamma.shelf.title, "Series Gamma")
        XCTAssertEqual(gamma.itemCount, 0)
        XCTAssertTrue(gamma.shelf.items.isEmpty)
        XCTAssertEqual(gamma.alsoInThisWorldCount, 1)
        XCTAssertEqual(gamma.alsoInThisWorld?.title, "Also in this world")
        XCTAssertEqual(gamma.alsoInThisWorld?.items.first?.title, "Gamma Companion")

        XCTAssertEqual(universe.seriesCount, 3)
        XCTAssertEqual(universe.mediaTypes, ["Movie", "Book"])
        XCTAssertEqual(universe.cast.count, 2)
        XCTAssertEqual(universe.cast[0].name, "Ann Actor")
        XCTAssertEqual(universe.cast[0].characters.map { $0.label }, ["Hero"])
        XCTAssertEqual(universe.cast[0].characters.first?.qid, "Q8200")
        XCTAssertEqual(universe.cast[0].workCount, 2)
        XCTAssertNil(universe.cast[0].headshotUrl)
        XCTAssertEqual(universe.cast[1].name, "Ben Narrator")
        XCTAssertTrue(universe.cast[1].characters.isEmpty)
        XCTAssertEqual(universe.adaptations.count, 1)
        XCTAssertEqual(universe.adaptations[0].label, "The Film")
        XCTAssertEqual(universe.adaptations[0].mediaType, "Object")
        XCTAssertEqual(universe.adaptations[0].relationship, "based on")
        XCTAssertEqual(universe.adaptations[0].sourceQid, "Q8101")
        XCTAssertEqual(universe.adaptations[0].sourceLabel, "The Novel")
    }

    func testUniverseWithoutTheNewKeysStillDecodes() throws {
        let json = """
        {"qid":"Q1","name":"Old Shape","item_count":4,
         "shelves":[{"collection_id":"6f0c2a3e-0000-0000-0000-000000000001","group_type":"series","item_count":4,
                     "shelf":{"key":"k","title":"Only Series","items":[]}}]}
        """
        let universe = try decodeJSON(UniverseDetail.self, json)
        XCTAssertEqual(universe.seriesCount, 0)
        XCTAssertTrue(universe.mediaTypes.isEmpty)
        XCTAssertTrue(universe.cast.isEmpty)
        XCTAssertTrue(universe.adaptations.isEmpty)
        XCTAssertNil(universe.shelves[0].alsoInThisWorld)
        XCTAssertEqual(universe.shelves[0].alsoInThisWorldCount, 0)
        XCTAssertEqual(universeTotalsLine(universe), "4 Works")
    }

    func testUniverseTotalsLineCountsSeriesWorksAndFormats() throws {
        let universe = try decode(UniverseDetail.self, fixture: "universe")
        XCTAssertEqual(universeTotalsLine(universe), "3 Series · 5 Works · 2 formats")
    }

    func testUniverseTotalsLineUsesSingularsAndLeavesOutZeroParts() throws {
        let one = try decodeJSON(UniverseDetail.self, universeJSON(seriesCount: 1, itemCount: 1, mediaTypes: ["Book"], groupType: "series"))
        XCTAssertEqual(universeTotalsLine(one), "1 Series · 1 Work · 1 format")
        let none = try decodeJSON(UniverseDetail.self, universeJSON(seriesCount: 0, itemCount: 0, mediaTypes: [], groupType: "series"))
        XCTAssertEqual(universeTotalsLine(none), "")
        let worksOnly = try decodeJSON(UniverseDetail.self, universeJSON(seriesCount: 0, itemCount: 14, mediaTypes: [], groupType: "series"))
        XCTAssertEqual(universeTotalsLine(worksOnly), "14 Works")
    }

    func testUniverseTotalsLineSaysShowsWhenEveryLineIsAShow() throws {
        let oneShow = try decodeJSON(UniverseDetail.self, universeJSON(seriesCount: 1, itemCount: 8, mediaTypes: ["TV"], groupType: "show"))
        XCTAssertEqual(universeTotalsLine(oneShow), "1 show · 8 Works · 1 format")
        let twoShows = try decodeJSON(UniverseDetail.self, universeJSON(seriesCount: 2, itemCount: 8, mediaTypes: ["TV"], groupType: "Show"))
        XCTAssertEqual(universeTotalsLine(twoShows), "2 shows · 8 Works · 1 format")
        let mixed = try decode(UniverseDetail.self, fixture: "universe")
        XCTAssertTrue(universeTotalsLine(mixed).hasPrefix("3 Series"))
    }

    func testCastCharactersLineJoinsCharactersAndIsNilWithoutThem() throws {
        let universe = try decode(UniverseDetail.self, fixture: "universe")
        XCTAssertEqual(castCharactersLine(universe.cast[0]), "as Hero")
        XCTAssertNil(castCharactersLine(universe.cast[1]))
        let two = try decodeJSON(UniverseCastMember.self, """
        {"person_id":"6f0c2a3e-0000-0000-0000-000000000011","name":"Cy","characters":[{"qid":"Q1","label":"Hero"},{"qid":"Q2","label":"Villain"}]}
        """)
        XCTAssertEqual(castCharactersLine(two), "as Hero, Villain")
        XCTAssertEqual(two.workCount, 0)
    }

    func testAdaptationLineCapitalisesTheRelationship() throws {
        let universe = try decode(UniverseDetail.self, fixture: "universe")
        XCTAssertEqual(adaptationLine(universe.adaptations[0]), "The Film — Based on The Novel")
        let blank = try decodeJSON(UniverseAdaptation.self, """
        {"qid":"Q1","label":"The Film","relationship":"","source_qid":"Q2","source_label":"The Novel"}
        """)
        XCTAssertEqual(adaptationLine(blank), "The Film")
    }

    func testUniverseShelfPresentationShowsTheRunAndNeverAZeroCount() throws {
        let universe = try decode(UniverseDetail.self, fixture: "universe")

        let alpha = try XCTUnwrap(universeShelfPresentation(universe.shelves[0]))
        XCTAssertTrue(alpha.showsRun)
        XCTAssertNil(alpha.alsoShelf)
        XCTAssertEqual(alpha.title, "Series Alpha")
        XCTAssertEqual(alpha.kindLine, "Series · 2 items")

        let gamma = try XCTUnwrap(universeShelfPresentation(universe.shelves[2]))
        XCTAssertFalse(gamma.showsRun)
        XCTAssertEqual(gamma.title, "Series Gamma")
        XCTAssertEqual(gamma.alsoShelf?.items.count, 1)
        XCTAssertEqual(gamma.alsoHeading, "Also in this world · 1 item")
        XCTAssertEqual(gamma.kindLine, "Series · Also in this world · 1 item")
        XCTAssertFalse(gamma.kindLine.contains("0"))
    }

    func testUniverseShelfPresentationSkipsASeriesWithNeitherRunNorAlsoItems() throws {
        let empty = try decodeJSON(UniverseShelf.self, """
        {"group_type":"series","item_count":0,"shelf":{"key":"k","title":"Empty","items":[]},"also_in_this_world":null,"also_in_this_world_count":0}
        """)
        XCTAssertNil(universeShelfPresentation(empty))
    }

    func testDeviceProfilesFixtureDecodes() throws {
        let response = try decode(DeviceProfilesResponse.self, fixture: "device-profiles")
        XCTAssertEqual(response.profiles.map { $0.displayName }, ["Mary", "Jim"])
        XCTAssertTrue(response.profiles[0].isActive)
        XCTAssertTrue(response.profiles[1].requiresPin)
        XCTAssertTrue(response.profiles[1].isKids)
    }

    // MARK: Helpers

    private func universeJSON(seriesCount: Int, itemCount: Int, mediaTypes: [String], groupType: String) -> String {
        let formats = mediaTypes.map { "\"\($0)\"" }.joined(separator: ",")
        return """
        {"qid":"Q1","name":"U","item_count":\(itemCount),"series_count":\(seriesCount),"media_types":[\(formats)],
         "shelves":[{"group_type":"\(groupType)","item_count":\(itemCount),"shelf":{"key":"k","title":"S","items":[]}}]}
        """
    }

    private func decodeJSON<T: Decodable>(_ type: T.Type, _ json: String) throws -> T {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(T.self, from: Data(json.utf8))
    }

    private func item(_ id: String) throws -> ViewItem {
        let json = "{\"id\":\"\(id)\",\"media_kind\":\"image\",\"file_name\":\"\(id).jpg\"}"
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(ViewItem.self, from: Data(json.utf8))
    }

    private func decode<T: Decodable>(_ type: T.Type, fixture name: String) throws -> T {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { url = url.deletingLastPathComponent() }
        url = url.appendingPathComponent("tests/fixtures/native-client-v1/\(name).json")
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(T.self, from: Data(contentsOf: url))
    }
}

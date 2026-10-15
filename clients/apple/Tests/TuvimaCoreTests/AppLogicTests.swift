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
        XCTAssertEqual(universe.shelves.count, 2)
        let shelf = universe.shelves[0]
        XCTAssertEqual(shelf.groupType, "series")
        XCTAssertEqual(shelf.shelf.title, "Series Alpha")
        XCTAssertEqual(shelf.shelf.items.count, 2)
        XCTAssertTrue(shelf.shelf.items[0].isPlayable)
        XCTAssertNotNil(shelf.shelf.items[0].playAssetID)
        XCTAssertFalse(universe.shelves[1].shelf.items[0].isPlayable)
        XCTAssertNil(universe.shelves[1].shelf.items[0].playAssetID)
    }

    func testDeviceProfilesFixtureDecodes() throws {
        let response = try decode(DeviceProfilesResponse.self, fixture: "device-profiles")
        XCTAssertEqual(response.profiles.map { $0.displayName }, ["Mary", "Jim"])
        XCTAssertTrue(response.profiles[0].isActive)
        XCTAssertTrue(response.profiles[1].requiresPin)
        XCTAssertTrue(response.profiles[1].isKids)
    }

    // MARK: Helpers

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

import XCTest
@testable import TuvimaCore

/// Pure-logic tests for photos kept on the device: the storage limit with photos, the purge rule, kind handling and the
/// report results. They need no device or server. They are not part of any target yet (see clients/apple/README.md for
/// the project.yml stanza that turns them on), so CI does not run them.
final class PhotoOfflineLogicTests: XCTestCase {
    private let photoA = UUID(uuidString: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")!
    private let photoB = UUID(uuidString: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")!
    private let photoC = UUID(uuidString: "cccccccc-cccc-cccc-cccc-cccccccccccc")!
    private let assetA = UUID(uuidString: "44444444-4444-4444-4444-444444444444")!
    private let variantA = UUID(uuidString: "55555555-5555-5555-5555-555555555555")!
    private let now = Date(timeIntervalSince1970: 1_900_000_000)

    private func photo(
        _ id: UUID,
        state: DownloadState = .complete,
        bytes: Int64 = 500,
        total: Int64 = 500,
        reported: Bool = true,
        fileName: String? = nil
    ) -> DownloadRecord {
        DownloadRecord(
            assetId: id,
            title: "Photo",
            subtitle: "2026-03-02",
            entityType: OfflineLogic.photoEntityType,
            profileKey: OfflineLogic.photoProfileKey,
            state: state,
            bytesDone: bytes,
            totalBytes: total,
            fileName: fileName ?? OfflineLogic.photoFileName(id),
            reportedToServer: reported,
            kind: .photo
        )
    }

    private func variant(_ asset: UUID, variantID: UUID, bytes: Int64 = 100, reported: Bool = true) -> DownloadRecord {
        DownloadRecord(
            assetId: asset,
            variantId: variantID,
            title: "Film",
            entityType: "movie",
            profileKey: OfflineLogic.qualityStandard,
            state: .complete,
            bytesDone: bytes,
            totalBytes: bytes,
            reportedToServer: reported
        )
    }

    private func photoRow(_ id: UUID) -> ServerDownloadEntry {
        ServerDownloadEntry(
            assetId: UUID(uuidString: "00000000-0000-0000-0000-000000000000")!,
            variantId: UUID(uuidString: "00000000-0000-0000-0000-000000000000")!,
            storedBytes: 500,
            storedAt: now,
            expiresAt: nil,
            kind: .photo,
            photoItemId: id
        )
    }

    // MARK: Kinds

    func testTheKindReadsItsWireNameAndAnythingElseIsAVariant() {
        XCTAssertEqual(OfflineKind(wire: "photo"), .photo)
        XCTAssertEqual(OfflineKind(wire: " Photo "), .photo)
        XCTAssertEqual(OfflineKind(wire: "book"), .book)
        XCTAssertEqual(OfflineKind(wire: "variant"), .variant)
        // An older Engine sends no kind, and a kind this app does not know is not a photo or a book.
        XCTAssertEqual(OfflineKind(wire: nil), .variant)
        XCTAssertEqual(OfflineKind(wire: "hologram"), .variant)
        XCTAssertEqual(OfflineKind.photo.rawValue, "photo")
    }

    func testARecordWithoutAKindIsAVariantAndAPhotoRecordIsAPhoto() {
        XCTAssertEqual(variant(assetA, variantID: variantA).itemKind, .variant)
        XCTAssertFalse(variant(assetA, variantID: variantA).isPhoto)
        XCTAssertTrue(photo(photoA).isPhoto)
    }

    func testARecordSavedBeforePhotosStillDecodes() throws {
        // State written by an earlier version has no "kind" and no "itemKind"; it must still load.
        let json = """
        {"records":[{"assetId":"44444444-4444-4444-4444-444444444444","title":"Film","entityType":"movie",
        "profileKey":"mobile-standard","state":"complete","bytesDone":100,"totalBytes":100,"hasLocalArtwork":false,
        "reportedToServer":true,"variantRequested":true,"queuedAt":0}],
        "reports":[{"kind":"store","assetId":"44444444-4444-4444-4444-444444444444",
        "variantId":"55555555-5555-5555-5555-555555555555","storedBytes":100,"storedAt":0}],"progress":[]}
        """
        let state = try JSONDecoder().decode(OfflineState.self, from: Data(json.utf8))
        XCTAssertEqual(state.records.first?.itemKind, .variant)
        XCTAssertEqual(state.reports.first?.item, .variant)
    }

    func testAPhotoStateSurvivesAnEncodeAndDecode() throws {
        var state = OfflineState()
        state.records = [photo(photoA)]
        state.reports = [PendingReport(kind: .store, assetId: photoA, variantId: photoA, storedBytes: 500, storedAt: now, itemKind: .photo)]
        let data = try JSONEncoder().encode(state)
        XCTAssertEqual(try JSONDecoder().decode(OfflineState.self, from: data), state)
    }

    // MARK: Purge rule

    func testAKeptPhotoIsJudgedByItsOwnRowOfTheEnginesList() {
        let local = [
            photo(photoA),
            photo(photoB),
            photo(photoC, reported: false),
            photo(UUID(), state: .downloading),
        ]
        let result = OfflineLogic.reconcileWithServer(local: local, server: [photoRow(photoA)], now: now)

        // A is still listed. B was reported but its row is gone (deleted, or access removed), so it is purged.
        // C was never reported and the last is not finished, so the list cannot judge them.
        XCTAssertEqual(result.purge.map { $0.assetId }, [photoB])
        XCTAssertEqual(result.keep.count, 3)
        XCTAssertNil(result.keep.first { $0.assetId == photoA }?.expiresAt)
    }

    func testAPhotoRowAndAVariantRowNeverStandInForEachOther() {
        let variantRow = ServerDownloadEntry(assetId: photoA, variantId: photoA, storedBytes: 1, storedAt: now, expiresAt: nil)
        XCTAssertEqual(
            OfflineLogic.reconcileWithServer(local: [photo(photoA)], server: [variantRow], now: now).purge.map { $0.assetId },
            [photoA]
        )
        let film = variant(photoA, variantID: photoA)
        XCTAssertEqual(
            OfflineLogic.reconcileWithServer(local: [film], server: [photoRow(photoA)], now: now).purge.map { $0.assetId },
            [photoA]
        )
        XCTAssertTrue(OfflineLogic.serverEntryMatches(photoRow(photoA), photo(photoA)))
        XCTAssertFalse(OfflineLogic.serverEntryMatches(photoRow(photoB), photo(photoA)))
        XCTAssertTrue(OfflineLogic.serverEntryMatches(variantRow, film))
    }

    func testAMixedListKeepsVariantsAndPhotosIndependently() {
        let local = [
            variant(assetA, variantID: variantA),
            variant(photoC, variantID: photoC),
            photo(photoA),
            photo(photoB),
        ]
        let server = [
            ServerDownloadEntry(assetId: assetA, variantId: variantA, storedBytes: 100, storedAt: now, expiresAt: nil),
            photoRow(photoB),
        ]
        let result = OfflineLogic.reconcileWithServer(local: local, server: server, now: now)
        XCTAssertEqual(result.purge.map { $0.assetId }, [photoC, photoA])
        XCTAssertEqual(result.keep.map { $0.assetId }, [assetA, photoB])
    }

    func testABookCopyIsNeverPurgedByTheListBecauseBooksAreNotWiredYet() {
        var book = photo(photoA)
        book.kind = .book
        let result = OfflineLogic.reconcileWithServer(local: [book], server: [], now: now)
        XCTAssertTrue(result.purge.isEmpty)
        XCTAssertEqual(result.keep.count, 1)
        XCTAssertFalse(OfflineLogic.serverEntryMatches(photoRow(photoA), book))
    }

    // MARK: Reports

    func testReportsForAPhotoAndAVariantWithTheSameIdAreKeptApart() {
        let variantStore = PendingReport(kind: .store, assetId: photoA, variantId: photoA, storedBytes: 10, storedAt: now)
        let photoStore = PendingReport(kind: .store, assetId: photoA, variantId: photoA, storedBytes: 20, storedAt: now, itemKind: .photo)
        let queue = OfflineLogic.queueReport(OfflineLogic.queueReport([], variantStore), photoStore)
        XCTAssertEqual(queue.count, 2)

        // Store then delete of the same photo leaves just the delete, and the variant report is untouched.
        let photoDelete = PendingReport(kind: .delete, assetId: photoA, variantId: photoA, storedBytes: 0, storedAt: now, itemKind: .photo)
        XCTAssertEqual(OfflineLogic.queueReport(queue, photoDelete), [variantStore, photoDelete])
    }

    func testAReportMatchesTheRecordOfItsOwnKind() {
        let photoReport = PendingReport(kind: .store, assetId: photoA, variantId: photoA, storedBytes: 500, storedAt: now, itemKind: .photo)
        let variantReport = PendingReport(kind: .store, assetId: assetA, variantId: variantA, storedBytes: 100, storedAt: now)
        XCTAssertTrue(OfflineLogic.reportMatches(photoReport, photo(photoA)))
        XCTAssertFalse(OfflineLogic.reportMatches(photoReport, photo(photoB)))
        XCTAssertFalse(OfflineLogic.reportMatches(photoReport, variant(photoA, variantID: photoA)))
        XCTAssertTrue(OfflineLogic.reportMatches(variantReport, variant(assetA, variantID: variantA)))
        XCTAssertFalse(OfflineLogic.reportMatches(variantReport, photo(assetA)))
    }

    func testAPhotoReportIsDoneOn204AndWhenADeleteFindsNothing() {
        // 204 is what the Engine really answers for a stored or forgotten photo.
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 204), .done)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .delete, statusCode: 204), .done)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .delete, statusCode: 404), .done)
        // A photo the Engine will not take (gone or not visible, list full, no photo access, bad request) is dropped.
        for status in [400, 403, 404, 409] {
            XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: status), .drop)
        }
        // Connection and credential problems are tried again.
        for status in [401, 408, 429, 500, 503] {
            XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: status), .retry)
        }
    }

    // MARK: Storage limit

    func testKeptPhotosCountUnderTheSameStorageLimitAsEveryOtherDownload() {
        let cap: Int64 = 10_000
        let held = [variant(assetA, variantID: variantA, bytes: 6_000), photo(photoA, bytes: 1_000, total: 1_000)]
        XCTAssertEqual(OfflineLogic.committedBytes(held), 7_000)
        XCTAssertTrue(OfflineLogic.canQueue(committedBytes: 7_000, capBytes: cap, estimatedBytes: 3_000))
        XCTAssertFalse(OfflineLogic.canQueue(committedBytes: 7_000, capBytes: cap, estimatedBytes: 3_001))

        // A photo still being fetched holds the larger of what is on disk and what was reserved for it.
        let inFlight = held + [photo(photoB, state: .downloading, bytes: 100, total: 600)]
        XCTAssertEqual(OfflineLogic.committedBytes(inFlight), 7_600)
    }

    func testAGalleryIsKeptNewestFirstUpToTheLimit() {
        let candidates = (0..<10).map { _ in PhotoCandidate(id: UUID(), isVideo: false) }
        // Room for exactly four more previews of 1000 bytes under the limit.
        let plan = OfflineLogic.planPhotoKeep(
            candidates: candidates,
            heldIds: [],
            committedBytes: 6_000,
            capBytes: 10_000,
            listedCount: 0,
            estimatePerPhoto: 1_000
        )
        XCTAssertEqual(plan.toKeep, candidates.prefix(4).map { $0.id })
        XCTAssertEqual(plan.stop, .storageCap)
        XCTAssertEqual(plan.alreadyKept, 0)
    }

    func testAGalleryKeepSkipsVideosPhotosAlreadyHeldAndRepeats() {
        let video = UUID()
        let candidates = [
            PhotoCandidate(id: photoA, isVideo: false),
            PhotoCandidate(id: video, isVideo: true),
            PhotoCandidate(id: photoB, isVideo: false),
            PhotoCandidate(id: photoA, isVideo: false),
            PhotoCandidate(id: photoC, isVideo: false),
        ]
        let plan = OfflineLogic.planPhotoKeep(
            candidates: candidates,
            heldIds: [photoB],
            committedBytes: 0,
            capBytes: 1_000_000_000,
            listedCount: 3,
            estimatePerPhoto: 1_000
        )
        XCTAssertEqual(plan.toKeep, [photoA, photoC])
        XCTAssertEqual(plan.alreadyKept, 1)
        XCTAssertEqual(plan.skippedVideos, 1)
        XCTAssertEqual(plan.stop, .finished)
    }

    func testAGalleryKeepStopsAtTheEnginesListLimitAndAFullLimitKeepsNothing() {
        let candidates = (0..<5).map { _ in PhotoCandidate(id: UUID(), isVideo: false) }
        let listFull = OfflineLogic.planPhotoKeep(
            candidates: candidates,
            heldIds: [],
            committedBytes: 0,
            capBytes: 1_000_000,
            listedCount: 498,
            estimatePerPhoto: 10
        )
        XCTAssertEqual(listFull.toKeep, candidates.prefix(2).map { $0.id })
        XCTAssertEqual(listFull.stop, .listFull)

        let capReached = OfflineLogic.planPhotoKeep(
            candidates: candidates,
            heldIds: [],
            committedBytes: 5_000,
            capBytes: 5_000,
            listedCount: 0,
            estimatePerPhoto: 10
        )
        XCTAssertTrue(capReached.toKeep.isEmpty)
        XCTAssertEqual(capReached.stop, .storageCap)

        // A limit of zero blocks everything, as it does for other downloads.
        let noCap = OfflineLogic.planPhotoKeep(candidates: candidates, heldIds: [], committedBytes: 0, capBytes: 0, listedCount: 0)
        XCTAssertTrue(noCap.toKeep.isEmpty)
        XCTAssertEqual(noCap.stop, .storageCap)
    }

    // MARK: Reading from the device

    func testTheViewerReadsAFinishedLocalCopyFirstAndNeverAnUnfinishedOrMissingOne() {
        let present: (String) -> Bool = { $0 == OfflineLogic.photoFileName(self.photoA) }
        XCTAssertEqual(OfflineLogic.localPhotoFileName(photo(photoA), fileExists: present), OfflineLogic.photoFileName(photoA))
        // The file is gone from the device, the copy is unfinished, there is no record, or it is a video copy.
        XCTAssertNil(OfflineLogic.localPhotoFileName(photo(photoB), fileExists: present))
        XCTAssertNil(OfflineLogic.localPhotoFileName(photo(photoA, state: .downloading), fileExists: present))
        XCTAssertNil(OfflineLogic.localPhotoFileName(nil, fileExists: present))
        var film = variant(photoA, variantID: variantA)
        film.fileName = OfflineLogic.photoFileName(photoA)
        XCTAssertNil(OfflineLogic.localPhotoFileName(film, fileExists: { _ in true }))
    }

    func testAKeptPhotoReadsAsAPreviewAndHasItsOwnRoute() {
        XCTAssertEqual(OfflineLogic.qualityLabel(OfflineLogic.photoProfileKey), "Preview (2048 px)")
        XCTAssertEqual(OfflineLogic.qualityLabel(OfflineLogic.qualityStandard), "Standard (720p)")
        XCTAssertEqual(OfflineLogic.photoPreviewEdge, 2048)
        XCTAssertEqual(
            ClientRoutes.devicePhotoDownload(photoA),
            "/api/v1/devices/current/downloads/photos/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        )
        XCTAssertTrue(ClientRoutes.viewPreview(photoA, size: OfflineLogic.photoPreviewEdge).hasSuffix("/preview?size=2048"))
        // Photos still never get a video or audio quality choice.
        XCTAssertTrue(OfflineLogic.qualityChoices(for: OfflineLogic.downloadKind(forEntityType: "photo")).isEmpty)
    }

    func testTheGalleryMessagesNameTheLimitThatStoppedIt() {
        XCTAssertEqual(OfflineLogic.keepGalleryMessage(found: 0, queued: 0, stop: .finished), "This gallery has no photos to keep.")
        XCTAssertEqual(
            OfflineLogic.keepGalleryMessage(found: 5, queued: 0, stop: .finished),
            "These photos are already kept on this device."
        )
        XCTAssertTrue(OfflineLogic.keepGalleryMessage(found: 5, queued: 3, stop: .storageCap).contains("storage limit"))
        XCTAssertTrue(OfflineLogic.keepGalleryMessage(found: 5, queued: 3, stop: .finished).contains("2048 px"))
        XCTAssertTrue(OfflineLogic.keepGalleryMessage(found: 5, queued: 0, stop: .listFull).hasPrefix("No photos were kept."))
    }
}

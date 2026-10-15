import XCTest
@testable import TuvimaCore

/// Pure-logic and wire-shape tests for offline downloads. They need no device or server. They are not part of any
/// target yet: see clients/apple/README.md for the project.yml stanza that turns them on.
final class OfflineLogicTests: XCTestCase {
    private let assetA = UUID(uuidString: "44444444-4444-4444-4444-444444444444")!
    private let assetB = UUID(uuidString: "66666666-6666-6666-6666-666666666666")!
    private let variantA = UUID(uuidString: "55555555-5555-5555-5555-555555555555")!
    private let variantB = UUID(uuidString: "77777777-7777-7777-7777-777777777777")!
    private let now = Date(timeIntervalSince1970: 1_900_000_000)

    private func record(
        _ asset: UUID,
        variant: UUID? = nil,
        state: DownloadState = .complete,
        bytes: Int64 = 100,
        total: Int64 = 100,
        reported: Bool = false,
        expires: Date? = nil
    ) -> DownloadRecord {
        DownloadRecord(
            assetId: asset,
            variantId: variant,
            title: "Title",
            entityType: "movie",
            profileKey: OfflineLogic.qualityStandard,
            state: state,
            bytesDone: bytes,
            totalBytes: total,
            expiresAt: expires,
            reportedToServer: reported
        )
    }

    private func entry(
        _ asset: UUID,
        position: Double = 60,
        pct: Double = 10,
        at: TimeInterval = 0,
        pending: Bool = true,
        revision: Int64 = 3
    ) -> OfflineProgressEntry {
        OfflineProgressEntry(
            assetId: asset,
            positionSeconds: position,
            durationSeconds: 600,
            progressPct: pct,
            recordedAt: now.addingTimeInterval(at),
            pending: pending,
            revision: revision
        )
    }

    // MARK: Storage cap

    func testCommittedBytesCountsReservedSizeForUnfinishedDownloads() {
        let records = [
            record(assetA, state: .complete, bytes: 500, total: 500),
            record(assetB, state: .downloading, bytes: 100, total: 900),
        ]
        XCTAssertEqual(OfflineLogic.committedBytes(records), 1_400)
    }

    func testCanQueueBlocksAtOrOverTheCap() {
        let cap = OfflineLogic.capBytes(forGB: 10)
        XCTAssertTrue(OfflineLogic.canQueue(committedBytes: 0, capBytes: cap))
        XCTAssertFalse(OfflineLogic.canQueue(committedBytes: cap, capBytes: cap))
        XCTAssertFalse(OfflineLogic.canQueue(committedBytes: cap + 1, capBytes: cap))
        XCTAssertTrue(OfflineLogic.canQueue(committedBytes: cap - 10, capBytes: cap, estimatedBytes: 10))
        XCTAssertFalse(OfflineLogic.canQueue(committedBytes: cap - 10, capBytes: cap, estimatedBytes: 11))
        XCTAssertFalse(OfflineLogic.canQueue(committedBytes: 0, capBytes: 0))
    }

    func testDefaultCapIsTenGigabytes() {
        XCTAssertEqual(OfflineLogic.defaultCapGB, 10)
        XCTAssertEqual(OfflineLogic.capBytes(forGB: 10), 10 * 1_073_741_824)
        XCTAssertTrue(OfflineLogic.capOptionsGB.contains(OfflineLogic.defaultCapGB))
    }

    // MARK: Expiry

    func testReportedDownloadMissingFromTheListIsPurged() {
        let held = record(assetA, variant: variantA, reported: true)
        let result = OfflineLogic.reconcileWithServer(local: [held], server: [], now: now)
        XCTAssertEqual(result.purge.map { $0.assetId }, [assetA])
        XCTAssertTrue(result.keep.isEmpty)
    }

    func testUnreportedDownloadIsNotJudgedByTheList() {
        let held = record(assetA, variant: variantA, reported: false)
        let result = OfflineLogic.reconcileWithServer(local: [held], server: [], now: now)
        XCTAssertTrue(result.purge.isEmpty)
        XCTAssertEqual(result.keep.count, 1)
    }

    func testListedDownloadTakesTheEnginesExpiry() {
        let held = record(assetA, variant: variantA, reported: true)
        let expiry = now.addingTimeInterval(86_400)
        let listed = ServerDownloadEntry(assetId: assetA, variantId: variantA, storedBytes: 100, storedAt: now, expiresAt: expiry)
        let result = OfflineLogic.reconcileWithServer(local: [held], server: [listed], now: now)
        XCTAssertTrue(result.purge.isEmpty)
        XCTAssertEqual(result.keep.first?.expiresAt, expiry)
    }

    func testListedDownloadWhoseExpiryPassedIsPurged() {
        let held = record(assetA, variant: variantA, reported: true)
        let listed = ServerDownloadEntry(assetId: assetA, variantId: variantA, storedBytes: 100, storedAt: now, expiresAt: now.addingTimeInterval(-1))
        let result = OfflineLogic.reconcileWithServer(local: [held], server: [listed], now: now)
        XCTAssertEqual(result.purge.count, 1)
    }

    func testDownloadsInProgressAreNeverPurged() {
        let held = record(assetA, variant: variantA, state: .downloading, reported: true)
        let result = OfflineLogic.reconcileWithServer(local: [held], server: [], now: now)
        XCTAssertTrue(result.purge.isEmpty)
        XCTAssertEqual(result.keep.count, 1)
    }

    func testIsExpiredOnlyForFinishedDownloadsPastTheirExpiry() {
        XCTAssertTrue(OfflineLogic.isExpired(record(assetA, expires: now.addingTimeInterval(-5)), now: now))
        XCTAssertFalse(OfflineLogic.isExpired(record(assetA, expires: now.addingTimeInterval(5)), now: now))
        XCTAssertFalse(OfflineLogic.isExpired(record(assetA, expires: nil), now: now))
        XCTAssertFalse(OfflineLogic.isExpired(record(assetA, state: .paused, expires: now.addingTimeInterval(-5)), now: now))
    }

    // MARK: Reporting to the Engine's list

    func testNoContentIsSuccess() {
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 204), .done)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .delete, statusCode: 204), .done)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 200), .done)
    }

    func testReportOutcomesByStatus() {
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .delete, statusCode: 404), .done)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 404), .drop)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 409), .drop)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 401), .retry)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 429), .retry)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .store, statusCode: 503), .retry)
        XCTAssertEqual(OfflineLogic.classifyReport(kind: .delete, statusCode: 0), .retry)
    }

    func testNewerReportForTheSameVariantReplacesTheOlderOne() {
        let store = PendingReport(kind: .store, assetId: assetA, variantId: variantA, storedBytes: 5, storedAt: now)
        let delete = PendingReport(kind: .delete, assetId: assetA, variantId: variantA, storedBytes: 0, storedAt: now)
        let other = PendingReport(kind: .store, assetId: assetB, variantId: variantB, storedBytes: 5, storedAt: now)
        let queue = OfflineLogic.queueReport(OfflineLogic.queueReport([store], other), delete)
        XCTAssertEqual(queue, [other, delete])
    }

    // MARK: Progress replay

    func testReplayOrderIsOldestFirstAndSkipsSyncedEntries() {
        let late = entry(assetA, at: 300)
        let early = entry(assetB, at: 100)
        let synced = entry(UUID(), at: 50, pending: false)
        XCTAssertEqual(OfflineLogic.replayOrder([late, synced, early]).map { $0.assetId }, [assetB, assetA])
    }

    func testRecordingKeepsTheRevisionLastSeen() {
        let existing = entry(assetA, revision: 7)
        let fresh = entry(assetA, position: 90, revision: 0)
        let merged = OfflineLogic.recordProgress([existing], fresh)
        XCTAssertEqual(merged.count, 1)
        XCTAssertEqual(merged.first?.revision, 7)
        XCTAssertEqual(merged.first?.positionSeconds, 90)
    }

    func testServerWinsWhenItsRecordIsLater() {
        let sent = entry(assetA, position: 60, at: 0)
        let server = ServerProgress(revision: 9, progressPct: 50, lastAccessed: now.addingTimeInterval(120), positionSeconds: 300)
        XCTAssertTrue(OfflineLogic.isServerWon(sent: sent, expectedRevision: 3, server: server))
    }

    func testServerWinsWhenItsRevisionDidNotMove() {
        let sent = entry(assetA, position: 60, at: 0)
        let server = ServerProgress(revision: 3, progressPct: 10, lastAccessed: nil, positionSeconds: 60)
        XCTAssertTrue(OfflineLogic.isServerWon(sent: sent, expectedRevision: 3, server: server))
    }

    func testReplayTheEngineTookIsNotAServerWin() {
        let sent = entry(assetA, position: 60, at: 0)
        let server = ServerProgress(revision: 4, progressPct: 10, lastAccessed: now, positionSeconds: 60)
        XCTAssertFalse(OfflineLogic.isServerWon(sent: sent, expectedRevision: 3, server: server))
    }

    func testTieInTimeComparesThePosition() {
        let sent = entry(assetA, position: 60, at: 0)
        let same = ServerProgress(revision: 4, progressPct: 10, lastAccessed: now, positionSeconds: 60.2)
        let different = ServerProgress(revision: 4, progressPct: 40, lastAccessed: now, positionSeconds: 240)
        XCTAssertFalse(OfflineLogic.isServerWon(sent: sent, expectedRevision: 3, server: same))
        XCTAssertTrue(OfflineLogic.isServerWon(sent: sent, expectedRevision: 3, server: different))
    }

    func testAdoptingServerStateTakesItsPositionTimeAndRevision() {
        let sent = entry(assetA, position: 60, at: 0)
        let server = ServerProgress(revision: 9, progressPct: 50, lastAccessed: now.addingTimeInterval(120), positionSeconds: 300)
        let adopted = OfflineLogic.adoptServerState(sent, server: server, now: now)
        XCTAssertEqual(adopted.positionSeconds, 300)
        XCTAssertEqual(adopted.progressPct, 50)
        XCTAssertEqual(adopted.revision, 9)
        XCTAssertEqual(adopted.recordedAt, now.addingTimeInterval(120))
        XCTAssertFalse(adopted.pending)
    }

    func testAdoptingServerStateEstimatesThePositionFromPercent() {
        let sent = entry(assetA, position: 60, at: 0)
        let server = ServerProgress(revision: 9, progressPct: 50, lastAccessed: nil, positionSeconds: nil)
        XCTAssertEqual(OfflineLogic.adoptServerState(sent, server: server, now: now).positionSeconds, 300)
    }

    func testReplayResultKeepsNewerLocalProgressPending() {
        let sent = entry(assetA, position: 60, at: 0)
        let played = entry(assetA, position: 200, at: 30)
        let answer = OfflineLogic.markReplayed(sent, server: ServerProgress(revision: 4, progressPct: 10, lastAccessed: now, positionSeconds: 60))
        let kept = OfflineLogic.applyReplayResult(current: played, sent: sent, updated: answer)
        XCTAssertTrue(kept.pending)
        XCTAssertEqual(kept.positionSeconds, 200)
        XCTAssertEqual(kept.revision, 4)
        XCTAssertEqual(OfflineLogic.applyReplayResult(current: sent, sent: sent, updated: answer), answer)
    }

    func testServerProgressIsAdoptedOnlyWhenNothingUnsyncedIsHeld() {
        let server = ServerProgress(revision: 8, progressPct: 30, lastAccessed: now.addingTimeInterval(500), positionSeconds: 180)
        XCTAssertTrue(OfflineLogic.shouldAdoptServerProgress(entry: nil, server: server))
        XCTAssertFalse(OfflineLogic.shouldAdoptServerProgress(entry: entry(assetA, pending: true), server: server))
        XCTAssertTrue(OfflineLogic.shouldAdoptServerProgress(entry: entry(assetA, pending: false, revision: 3), server: server))
        let same = ServerProgress(revision: 3, progressPct: 10, lastAccessed: now, positionSeconds: 60)
        XCTAssertFalse(OfflineLogic.shouldAdoptServerProgress(entry: entry(assetA, at: 0, pending: false, revision: 3), server: same))
    }

    func testResumeStartsWhereLeftOffOrAtTheBeginningWhenFinished() {
        XCTAssertEqual(OfflineLogic.resumeSeconds(nil), 0)
        XCTAssertEqual(OfflineLogic.resumeSeconds(entry(assetA, position: 120, pct: 20)), 120)
        XCTAssertEqual(OfflineLogic.resumeSeconds(entry(assetA, position: 590, pct: 98)), 0)
    }

    func testProgressPercentIsClamped() {
        XCTAssertEqual(OfflineLogic.progressPercent(position: 30, duration: 120), 25)
        XCTAssertEqual(OfflineLogic.progressPercent(position: 500, duration: 120), 100)
        XCTAssertEqual(OfflineLogic.progressPercent(position: 30, duration: nil), 0)
    }

    // MARK: Quality and kinds

    func testKindsAndQualityChoices() {
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "movie"), .video)
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "tvEpisode"), .video)
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "audiobook"), .audio)
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "musicAlbum"), .audio)
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "book"), .excluded)
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "comicIssue"), .excluded)
        XCTAssertEqual(OfflineLogic.downloadKind(forEntityType: "work"), .unknown)
        XCTAssertEqual(OfflineLogic.qualityChoices(for: .video).map { $0.key }, ["mobile-small", "mobile-standard"])
        XCTAssertEqual(OfflineLogic.qualityChoices(for: .audio).map { $0.key }, ["audio-mobile"])
        XCTAssertEqual(OfflineLogic.qualityChoices(for: .unknown).count, 3)
        XCTAssertTrue(OfflineLogic.qualityChoices(for: .excluded).isEmpty)
    }

    func testDefaultQualityFitsTheKind() {
        XCTAssertEqual(OfflineLogic.defaultQuality(for: .video, preferred: "mobile-small"), "mobile-small")
        XCTAssertEqual(OfflineLogic.defaultQuality(for: .video, preferred: "audio-mobile"), "mobile-standard")
        XCTAssertEqual(OfflineLogic.defaultQuality(for: .audio, preferred: "mobile-standard"), "audio-mobile")
    }

    func testOnlyShowEntitiesOfferNextEpisodes() {
        XCTAssertTrue(OfflineLogic.isShowEntity("tvShow"))
        XCTAssertFalse(OfflineLogic.isShowEntity("tvEpisode"))
        XCTAssertFalse(OfflineLogic.isShowEntity("movie"))
    }

    func testChooseReadyVariantPrefersTheKnownOneThenTheLongestLived() throws {
        let json = """
        [
          {"id":"55555555-5555-5555-5555-555555555555","assetId":"44444444-4444-4444-4444-444444444444","status":"ready","profileKey":"mobile-standard","expiresAt":"2030-01-10T00:00:00+00:00"},
          {"id":"77777777-7777-7777-7777-777777777777","assetId":"44444444-4444-4444-4444-444444444444","status":"ready","profileKey":"mobile-standard","expiresAt":"2030-01-20T00:00:00+00:00"},
          {"id":"88888888-8888-8888-8888-888888888888","assetId":"44444444-4444-4444-4444-444444444444","status":"pending","profileKey":"mobile-small"}
        ]
        """
        let variants = try JSONDecoder().decode([OfflineVariant].self, from: Data(json.utf8))
        XCTAssertEqual(OfflineLogic.chooseReadyVariant(variants, profileKey: "mobile-standard", knownVariantId: nil)?.id, variantB)
        XCTAssertEqual(OfflineLogic.chooseReadyVariant(variants, profileKey: "mobile-standard", knownVariantId: variantA)?.id, variantA)
        XCTAssertNil(OfflineLogic.chooseReadyVariant(variants, profileKey: "mobile-small", knownVariantId: nil))
    }

    // MARK: Words and times

    func testFormatBytes() {
        XCTAssertEqual(OfflineLogic.formatBytes(512), "512 B")
        XCTAssertEqual(OfflineLogic.formatBytes(2_048), "2 KB")
        XCTAssertEqual(OfflineLogic.formatBytes(734_003_200), "700 MB")
        XCTAssertEqual(OfflineLogic.formatBytes(10 * 1_073_741_824), "10.0 GB")
    }

    func testExpiryLabel() {
        XCTAssertNil(OfflineLogic.expiryLabel(nil, now: now))
        XCTAssertEqual(OfflineLogic.expiryLabel(now.addingTimeInterval(-1), now: now), "Expired")
        XCTAssertEqual(OfflineLogic.expiryLabel(now.addingTimeInterval(12 * 86_400 + 60), now: now), "Expires in 12 days")
        XCTAssertEqual(OfflineLogic.expiryLabel(now.addingTimeInterval(86_400 + 60), now: now), "Expires in 1 day")
        XCTAssertEqual(OfflineLogic.expiryLabel(now.addingTimeInterval(5 * 3_600 + 60), now: now), "Expires in 5 hours")
        XCTAssertEqual(OfflineLogic.expiryLabel(now.addingTimeInterval(600), now: now), "Expires within the hour")
    }

    func testEpisodeLabel() {
        XCTAssertEqual(OfflineLogic.episodeLabel(season: 1, episode: 4), "Season 1, episode 4")
        XCTAssertEqual(OfflineLogic.episodeLabel(season: 0, episode: 2), "Special 2")
        XCTAssertEqual(OfflineLogic.episodeLabel(season: nil, episode: 7), "Episode 7")
        XCTAssertNil(OfflineLogic.episodeLabel(season: 1, episode: nil))
    }

    func testIsoTimesParseWithAndWithoutZoneAndFractions() {
        let base = OfflineLogic.parseIso("2030-01-01T00:05:00+00:00")
        XCTAssertNotNil(base)
        XCTAssertEqual(OfflineLogic.parseIso("2030-01-01T00:05:00Z"), base)
        XCTAssertEqual(OfflineLogic.parseIso("2030-01-01T00:05:00"), base)
        XCTAssertEqual(OfflineLogic.parseIso("2030-01-01T01:05:00+01:00"), base)
        let fractional = OfflineLogic.parseIso("2030-01-01T00:05:00.1234567Z")
        XCTAssertEqual(fractional?.timeIntervalSince(base ?? Date()) ?? 0, 0.123, accuracy: 0.001)
        XCTAssertNil(OfflineLogic.parseIso(nil))
        XCTAssertNil(OfflineLogic.parseIso("  "))
        XCTAssertNil(OfflineLogic.parseIso("not a time"))
    }

    func testIsoFormattingIsUtcWithTrailingZ() {
        let text = OfflineLogic.formatIsoUtc(Date(timeIntervalSince1970: 1_893_456_300))
        XCTAssertTrue(text.hasSuffix("Z"))
        XCTAssertEqual(OfflineLogic.parseIso(text), Date(timeIntervalSince1970: 1_893_456_300))
    }

    func testStatusTextForEachState() {
        var held = record(assetA, variant: variantA, bytes: 734_003_200, total: 734_003_200, expires: now.addingTimeInterval(12 * 86_400 + 60))
        XCTAssertEqual(
            OfflineLogic.statusText(held, bytesDone: held.bytesDone, now: now),
            "Standard (720p) \u{00B7} 700 MB \u{00B7} Expires in 12 days"
        )
        held.state = .downloading
        held.bytesDone = 0
        XCTAssertEqual(
            OfflineLogic.statusText(held, bytesDone: 104_857_600, now: now),
            "Downloading \u{00B7} 100 MB of 700 MB"
        )
        held.state = .failed
        held.error = "Storage limit reached."
        XCTAssertEqual(OfflineLogic.statusText(held, bytesDone: 0, now: now), "Storage limit reached.")
    }

    func testNextEpisodesMessages() {
        XCTAssertEqual(
            OfflineLogic.nextEpisodesMessage(found: 0, queued: 0, overCap: false),
            "There are no more episodes after this one in your library."
        )
        XCTAssertEqual(
            OfflineLogic.nextEpisodesMessage(found: 3, queued: 0, overCap: true),
            "Storage limit reached. Delete a download or raise the limit in Settings."
        )
        XCTAssertEqual(OfflineLogic.nextEpisodesMessage(found: 3, queued: 1, overCap: false), "Started downloading the next episode.")
        XCTAssertEqual(OfflineLogic.nextEpisodesMessage(found: 3, queued: 3, overCap: false), "Started downloading the next 3 episodes.")
    }

    // MARK: Routes

    func testOfflineRoutes() {
        XCTAssertEqual(ClientRoutes.deviceDownloads, "/api/v1/devices/current/downloads")
        XCTAssertEqual(
            ClientRoutes.deviceDownload(variantA),
            "/api/v1/devices/current/downloads/55555555-5555-5555-5555-555555555555"
        )
        XCTAssertEqual(
            ClientRoutes.offlineFile(assetA, variantA),
            "/api/v1/playback/44444444-4444-4444-4444-444444444444/offline/55555555-5555-5555-5555-555555555555"
        )
        XCTAssertEqual(ClientRoutes.progress(assetA), "/api/v1/progress/44444444-4444-4444-4444-444444444444")
        XCTAssertEqual(
            ClientRoutes.nextEpisodes(showID: assetB, afterAssetID: assetA),
            "/api/v1/details/show/66666666-6666-6666-6666-666666666666/next-episodes"
                + "?after=44444444-4444-4444-4444-444444444444&count=3"
        )
        XCTAssertTrue(ClientRoutes.nextEpisodes(showID: assetB, afterAssetID: assetA, count: 99).hasSuffix("count=10"))
    }

    // MARK: Wire fixtures (tests/fixtures/native-client-v1)

    func testDeviceDownloadsFixtureDecodes() throws {
        let response = try decode(DeviceDownloadsResponse.self, fixture: "device-downloads")
        let entries = response.downloads.map { $0.entry }
        XCTAssertEqual(entries.count, 3)
        XCTAssertEqual(entries[0].assetId, assetA)
        XCTAssertEqual(entries[0].variantId, variantA)
        XCTAssertEqual(entries[0].storedBytes, 734_003_200)
        XCTAssertNotNil(entries[0].storedAt)
        XCTAssertNotNil(entries[0].expiresAt)
        XCTAssertNil(entries[1].expiresAt)
        XCTAssertEqual(entries[0].kind, .variant)
        XCTAssertNil(entries[0].photoItemId)
        // The kept photo names its View item, carries the empty ids and never expires.
        XCTAssertEqual(entries[2].kind, .photo)
        XCTAssertEqual(entries[2].photoItemId, UUID(uuidString: "88888888-8888-8888-8888-888888888888"))
        XCTAssertEqual(entries[2].assetId, UUID(uuidString: "00000000-0000-0000-0000-000000000000"))
        XCTAssertNil(entries[2].expiresAt)
    }

    func testNextEpisodesFixtureDecodes() throws {
        let response = try decode(NextEpisodesResponse.self, fixture: "next-episodes")
        XCTAssertEqual(response.afterAssetId, assetA)
        XCTAssertEqual(response.episodes.count, 2)
        XCTAssertEqual(response.episodes[0].title, "The Second Door")
        XCTAssertEqual(response.episodes[0].seasonNumber, 1)
        XCTAssertEqual(response.episodes[0].episodeNumber, 4)
        XCTAssertEqual(response.episodes[0].stillUrl, "/api/v1/display/artwork/fixture/still-small")
        XCTAssertNil(response.episodes[1].stillUrl)
    }

    func testProgressAnswerReadsRevisionTimeAndPosition() throws {
        let json = """
        {"user_id":"11111111-1111-1111-1111-111111111111","asset_id":"44444444-4444-4444-4444-444444444444",
         "content_hash":"abc","progress_pct":42.5,"last_accessed":"2030-01-01T00:05:00.1234567",
         "extended_properties":{"position_seconds":"255.000","playback_state":"paused"},"revision":8}
        """
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        let wire = try decoder.decode(ServerProgressWire.self, from: Data(json.utf8))
        let progress = wire.progress
        XCTAssertEqual(progress.revision, 8)
        XCTAssertEqual(progress.progressPct, 42.5)
        XCTAssertEqual(progress.positionSeconds, 255)
        XCTAssertNotNil(progress.lastAccessed)
    }

    func testOfflineStateSurvivesAnEncodeAndDecode() throws {
        var state = OfflineState()
        state.records = [record(assetA, variant: variantA, reported: true, expires: now)]
        state.reports = [PendingReport(kind: .store, assetId: assetA, variantId: variantA, storedBytes: 100, storedAt: now)]
        state.progress = [entry(assetA)]
        let data = try JSONEncoder().encode(state)
        XCTAssertEqual(try JSONDecoder().decode(OfflineState.self, from: data), state)
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

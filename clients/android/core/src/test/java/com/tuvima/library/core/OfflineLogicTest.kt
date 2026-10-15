package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks the pure offline rules: server-won detection, replay order, the storage cap, expiry and report results. */
class OfflineLogicTest {
    private val now = 1_900_000_000_000L

    private fun record(
        assetId: String,
        variantId: String? = "variant-$assetId",
        state: DownloadState = DownloadState.Complete,
        bytesDone: Long = 100L,
        totalBytes: Long = 100L,
        reported: Boolean = true,
        expiresAtMs: Long? = null,
    ) = DownloadRecord(
        assetId = assetId,
        variantId = variantId,
        title = "Title $assetId",
        subtitle = null,
        artworkUrl = null,
        artworkPath = null,
        entityType = "movie",
        showId = null,
        profileKey = "mobile-standard",
        state = state,
        bytesDone = bytesDone,
        totalBytes = totalBytes,
        storedAtMs = now,
        expiresAtMs = expiresAtMs,
        filePath = "/files/$assetId.mp4",
        reportedToServer = reported,
        variantRequested = true,
        error = null,
        queuedAtMs = now,
    )

    /** A kept photo: the item id is the asset id, there is no variant, and the file is the 2048 px preview. */
    private fun photo(
        itemId: String,
        state: DownloadState = DownloadState.Complete,
        bytesDone: Long = 500L,
        totalBytes: Long = 500L,
        reported: Boolean = true,
    ) = DownloadRecord(
        assetId = itemId,
        variantId = null,
        title = "Photo $itemId",
        subtitle = "2026-03-02",
        artworkUrl = null,
        artworkPath = null,
        entityType = OfflineLogic.PHOTO_ENTITY_TYPE,
        showId = null,
        profileKey = OfflineLogic.PHOTO_PROFILE_KEY,
        state = state,
        bytesDone = bytesDone,
        totalBytes = totalBytes,
        storedAtMs = now,
        expiresAtMs = null,
        filePath = "/files/photos/$itemId.jpg",
        reportedToServer = reported,
        variantRequested = false,
        error = null,
        queuedAtMs = now,
        kind = OfflineKind.Photo,
    )

    private fun photoRow(itemId: String) = ServerDownloadEntry(
        assetId = EMPTY_ID,
        variantId = EMPTY_ID,
        storedBytes = 500L,
        storedAtMs = now,
        expiresAtMs = null,
        kind = OfflineKind.Photo,
        photoItemId = itemId,
    )

    private fun progress(
        assetId: String,
        recordedAtMs: Long,
        position: Double = 600.0,
        pending: Boolean = true,
        revision: Long = 0L,
    ) = OfflineProgressEntry(
        assetId = assetId,
        positionSeconds = position,
        durationSeconds = 6000.0,
        progressPct = position / 6000.0 * 100.0,
        recordedAtMs = recordedAtMs,
        pending = pending,
        revision = revision,
    )

    private fun server(revision: Long, lastAccessedMs: Long?, position: Double?, pct: Double = 10.0) =
        ServerProgress(revision = revision, progressPct = pct, lastAccessedMs = lastAccessedMs, positionSeconds = position)

    // ---- server-won detection ----

    @Test
    fun theEngineTakingTheReplayIsNotServerWon() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        // The Engine stamped the replay with the client time and moved the revision on.
        val answer = server(revision = 4, lastAccessedMs = now, position = 600.0)
        assertFalse(OfflineLogic.isServerWon(sent, expectedRevision = 3, server = answer))
    }

    @Test
    fun aLaterServerRecordIsServerWon() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        val answer = server(revision = 9, lastAccessedMs = now + 60_000, position = 1500.0)
        assertTrue(OfflineLogic.isServerWon(sent, expectedRevision = 3, server = answer))
    }

    @Test
    fun anUnmovedRevisionIsServerWon() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        val answer = server(revision = 3, lastAccessedMs = now, position = 600.0)
        assertTrue(OfflineLogic.isServerWon(sent, expectedRevision = 3, server = answer))
    }

    @Test
    fun aTieInTimeIsServerWonOnlyWhenThePositionDiffers() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        assertTrue(OfflineLogic.isServerWon(sent, 3, server(revision = 8, lastAccessedMs = now, position = 900.0)))
        assertFalse(OfflineLogic.isServerWon(sent, 3, server(revision = 8, lastAccessedMs = now, position = 600.2)))
    }

    @Test
    fun aClampedFutureClientTimeIsNotServerWon() {
        // The Engine treats a client time later than its own clock as its own time, so the stamp comes back earlier.
        val sent = progress("a", recordedAtMs = now + 3_600_000, position = 600.0)
        val answer = server(revision = 4, lastAccessedMs = now, position = 600.0)
        assertFalse(OfflineLogic.isServerWon(sent, 3, answer))
    }

    @Test
    fun withoutAServerPositionThePercentDecides() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        assertFalse(OfflineLogic.isServerWon(sent, 3, server(4, now, position = null, pct = 10.0)))
        assertTrue(OfflineLogic.isServerWon(sent, 3, server(4, now, position = null, pct = 60.0)))
    }

    @Test
    fun adoptingTheServerStateReplacesTheLocalRecord() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        val adopted = OfflineLogic.adoptServerState(sent, server(9, now + 5_000, position = 1500.0, pct = 25.0), now)
        assertEquals(1500.0, adopted.positionSeconds, 0.001)
        assertEquals(25.0, adopted.progressPct, 0.001)
        assertEquals(now + 5_000, adopted.recordedAtMs)
        assertEquals(9L, adopted.revision)
        assertFalse(adopted.pending)
    }

    @Test
    fun adoptingWithoutAServerPositionUsesThePercentOfTheDuration() {
        val sent = progress("a", recordedAtMs = now, position = 600.0)
        val adopted = OfflineLogic.adoptServerState(sent, server(9, null, position = null, pct = 50.0), now)
        assertEquals(3000.0, adopted.positionSeconds, 0.001)
        assertEquals(now, adopted.recordedAtMs)
    }

    // ---- replay ordering ----

    @Test
    fun replayGoesOldestRecordingFirstAndSkipsWhatIsAlreadySynced() {
        val entries = listOf(
            progress("late", recordedAtMs = now + 3_000),
            progress("done", recordedAtMs = now + 500, pending = false),
            progress("early", recordedAtMs = now + 1_000),
            progress("middle", recordedAtMs = now + 2_000),
        )
        assertEquals(listOf("early", "middle", "late"), OfflineLogic.replayOrder(entries).map { it.assetId })
    }

    @Test
    fun recordingProgressTwiceKeepsOneEntryAndTheLastRevision() {
        val first = OfflineLogic.recordProgress(emptyList(), progress("a", now, revision = 7))
        val second = OfflineLogic.recordProgress(first, progress("a", now + 10_000, position = 900.0, revision = 0))
        assertEquals(1, second.size)
        assertEquals(900.0, second.single().positionSeconds, 0.001)
        assertEquals(7L, second.single().revision)
    }

    @Test
    fun aFinishedFileStartsAgainAndAnUnfinishedOneResumes() {
        assertEquals(0L, OfflineLogic.resumeStartMs(null))
        assertEquals(600_000L, OfflineLogic.resumeStartMs(progress("a", now, position = 600.0)))
        assertEquals(0L, OfflineLogic.resumeStartMs(progress("a", now, position = 5900.0)))
    }

    // ---- storage cap ----

    @Test
    fun theCapBlocksNewQueueingOnceReachedOrWhenTheNewFileWouldNotFit() {
        val cap = StorageCap.DEFAULT_BYTES
        assertEquals(10L * 1024 * 1024 * 1024, cap)
        assertTrue(OfflineLogic.canQueue(committedBytes = 1_000L, capBytes = cap))
        assertFalse(OfflineLogic.canQueue(committedBytes = cap, capBytes = cap))
        assertFalse(OfflineLogic.canQueue(committedBytes = cap + 1, capBytes = cap))
        assertTrue(OfflineLogic.canQueue(committedBytes = cap - 500, capBytes = cap, estimatedBytes = 500))
        assertFalse(OfflineLogic.canQueue(committedBytes = cap - 500, capBytes = cap, estimatedBytes = 501))
        assertFalse(OfflineLogic.canQueue(committedBytes = 0L, capBytes = 0L))
    }

    @Test
    fun committedBytesCountFinishedFilesAndTheRestOfUnfinishedOnes() {
        val records = listOf(
            record("done", bytesDone = 400L, totalBytes = 400L),
            record("partial", state = DownloadState.Downloading, bytesDone = 100L, totalBytes = 900L),
            record("waiting", state = DownloadState.Queued, bytesDone = 0L, totalBytes = 0L),
        )
        assertEquals(1_300L, OfflineLogic.committedBytes(records))
    }

    @Test
    fun capOptionsConvertBetweenGigabytesAndBytes() {
        assertEquals(5L * 1024 * 1024 * 1024, StorageCap.bytesForGb(5))
        assertEquals(20, StorageCap.gbFor(StorageCap.bytesForGb(20)))
        assertTrue(10 in StorageCap.optionsGb)
    }

    // ---- expiry ----

    @Test
    fun aDownloadMissingFromTheEngineListIsPurged() {
        val kept = record("kept")
        val gone = record("gone")
        val listing = listOf(ServerDownloadEntry("kept", "variant-kept", 100L, now, null))
        val result = OfflineLogic.reconcileWithServer(listOf(kept, gone), listing, now)
        assertEquals(listOf("gone"), result.purge.map { it.assetId })
        assertEquals(listOf("kept"), result.keep.map { it.assetId })
    }

    @Test
    fun aListedDownloadTakesTheEnginesExpiryAndAPassedExpiryPurges() {
        val soon = record("soon")
        val passed = record("passed")
        val listing = listOf(
            ServerDownloadEntry("soon", "variant-soon", 100L, now, now + 86_400_000L),
            ServerDownloadEntry("passed", "variant-passed", 100L, now, now - 1L),
        )
        val result = OfflineLogic.reconcileWithServer(listOf(soon, passed), listing, now)
        assertEquals(listOf("passed"), result.purge.map { it.assetId })
        assertEquals(now + 86_400_000L, result.keep.single().expiresAtMs)
    }

    @Test
    fun anUnreportedOrUnfinishedDownloadIsNotJudgedByTheEngineList() {
        val unreported = record("new", reported = false)
        val running = record("running", state = DownloadState.Downloading, bytesDone = 10L, totalBytes = 100L, reported = false)
        val result = OfflineLogic.reconcileWithServer(listOf(unreported, running), emptyList(), now)
        assertTrue(result.purge.isEmpty())
        assertEquals(2, result.keep.size)
    }

    @Test
    fun anUnreportedDownloadPastItsOwnExpiryIsPurged() {
        val old = record("old", reported = false, expiresAtMs = now - 1L)
        val result = OfflineLogic.reconcileWithServer(listOf(old), emptyList(), now)
        assertEquals(listOf("old"), result.purge.map { it.assetId })
        assertTrue(OfflineLogic.isExpired(old, now))
        assertFalse(OfflineLogic.isExpired(record("fresh", expiresAtMs = now + 1_000), now))
    }

    // ---- reports to the Engine's download list ----

    @Test
    fun noContentIsSuccessForEveryReport() {
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Store, 204))
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Delete, 204))
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Store, 200))
    }

    @Test
    fun aMissingRowCountsAsDeletedAndRefusalsAreDroppedButOutagesRetry() {
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Delete, 404))
        assertEquals(ReportOutcome.Drop, OfflineLogic.classifyReport(ReportKind.Store, 404))
        assertEquals(ReportOutcome.Drop, OfflineLogic.classifyReport(ReportKind.Store, 409))
        assertEquals(ReportOutcome.Retry, OfflineLogic.classifyReport(ReportKind.Store, 401))
        assertEquals(ReportOutcome.Retry, OfflineLogic.classifyReport(ReportKind.Delete, 503))
        assertEquals(ReportOutcome.Retry, OfflineLogic.classifyReport(ReportKind.Store, 429))
    }

    @Test
    fun aNewerReportForTheSameVariantReplacesTheOlderOne() {
        val store = PendingReport(ReportKind.Store, "a", "v1", 100L, now)
        val delete = PendingReport(ReportKind.Delete, "a", "v1", 0L, now + 1)
        val other = PendingReport(ReportKind.Store, "b", "v2", 50L, now)
        val queue = OfflineLogic.queueReport(OfflineLogic.queueReport(listOf(other), store), delete)
        assertEquals(listOf("v2", "v1"), queue.map { it.variantId })
        assertEquals(ReportKind.Delete, queue.last().kind)
    }

    // ---- kinds, quality, variants ----

    @Test
    fun kindsDecideTheQualityChoices() {
        assertEquals(DownloadKind.Video, OfflineLogic.downloadKindFor("movie"))
        assertEquals(DownloadKind.Video, OfflineLogic.downloadKindFor("tvEpisode"))
        assertEquals(DownloadKind.Audio, OfflineLogic.downloadKindFor("audiobook"))
        assertEquals(DownloadKind.Audio, OfflineLogic.downloadKindFor("musicAlbum"))
        assertEquals(DownloadKind.Excluded, OfflineLogic.downloadKindFor("book"))
        assertEquals(DownloadKind.Excluded, OfflineLogic.downloadKindFor("comicIssue"))
        assertEquals(DownloadKind.Unknown, OfflineLogic.downloadKindFor("work"))

        assertEquals(
            listOf("mobile-small", "mobile-standard"),
            OfflineLogic.qualityChoicesFor(DownloadKind.Video).map { it.key },
        )
        assertEquals(listOf("audio-mobile"), OfflineLogic.qualityChoicesFor(DownloadKind.Audio).map { it.key })
        assertEquals(3, OfflineLogic.qualityChoicesFor(DownloadKind.Unknown).size)
        assertTrue(OfflineLogic.qualityChoicesFor(DownloadKind.Excluded).isEmpty())
    }

    @Test
    fun theDefaultQualityFitsTheKind() {
        assertEquals("mobile-small", OfflineLogic.defaultQualityFor(DownloadKind.Video, "mobile-small"))
        assertEquals("audio-mobile", OfflineLogic.defaultQualityFor(DownloadKind.Audio, "mobile-small"))
        assertEquals("mobile-standard", OfflineLogic.defaultQualityFor(DownloadKind.Video, "audio-mobile"))
    }

    @Test
    fun theKnownVariantIsKeptAndOtherwiseTheLongestLivedReadyOneForTheProfile() {
        val variants = listOf(
            OfflineVariant("pending", "a", "pending", null, null, "mobile-standard", null),
            OfflineVariant("old", "a", "ready", null, 10L, "mobile-standard", 1_000L),
            OfflineVariant("new", "a", "ready", null, 10L, "mobile-standard", 5_000L),
            OfflineVariant("small", "a", "ready", null, 10L, "mobile-small", 9_000L),
        )
        assertEquals("new", OfflineLogic.chooseReadyVariant(variants, "mobile-standard", null)?.id)
        assertEquals("small", OfflineLogic.chooseReadyVariant(variants, "mobile-small", null)?.id)
        assertEquals("old", OfflineLogic.chooseReadyVariant(variants, "mobile-standard", "old")?.id)
        assertNull(OfflineLogic.chooseReadyVariant(variants, "mobile-standard", "pending"))
        assertNull(OfflineLogic.chooseReadyVariant(variants, "audio-mobile", null))
    }

    // ---- words and times ----

    @Test
    fun sizesAndExpiryReadInPlainWords() {
        assertEquals("512 B", OfflineLogic.formatBytes(512))
        assertEquals("700 MB", OfflineLogic.formatBytes(700L * 1024 * 1024))
        assertEquals("1.5 GB", OfflineLogic.formatBytes(1_610_612_736L))
        assertNull(OfflineLogic.expiryLabel(null, now))
        assertEquals("Expired", OfflineLogic.expiryLabel(now - 1, now))
        assertEquals("Expires in 12 days", OfflineLogic.expiryLabel(now + 12L * 86_400_000L + 1_000L, now))
        assertEquals("Expires in 1 day", OfflineLogic.expiryLabel(now + 86_400_000L + 1_000L, now))
        assertEquals("Expires in 5 hours", OfflineLogic.expiryLabel(now + 5L * 3_600_000L + 1_000L, now))
        assertEquals("Season 1, episode 4", OfflineLogic.episodeLabel(1, 4))
        assertEquals("Special 2", OfflineLogic.episodeLabel(0, 2))
        assertNull(OfflineLogic.episodeLabel(null, null))
    }

    @Test
    fun isoTimesRoundTripInUtc() {
        assertEquals(1_893_456_300_000L, OfflineLogic.parseIsoMillis("2030-01-01T00:05:00+00:00"))
        assertEquals(1_893_456_300_000L, OfflineLogic.parseIsoMillis("2030-01-01T00:05:00Z"))
        assertEquals(1_893_456_300_000L, OfflineLogic.parseIsoMillis("2030-01-01T00:05:00"))
        assertEquals(1_893_456_300_123L, OfflineLogic.parseIsoMillis("2030-01-01T00:05:00.1234567Z"))
        assertNull(OfflineLogic.parseIsoMillis(null))
        assertNull(OfflineLogic.parseIsoMillis("not a time"))
        assertEquals("2030-01-01T00:05:00Z", OfflineLogic.formatIsoUtc(1_893_456_300_000L))
        assertEquals("12.500", OfflineLogic.formatSeconds(12.5))
    }

    // ---- photos kept on the phone ----

    @Test
    fun theKindReadsItsWireNameAndAnythingElseIsAVariant() {
        assertEquals(OfflineKind.Photo, OfflineKind.fromWire("photo"))
        assertEquals(OfflineKind.Photo, OfflineKind.fromWire(" Photo "))
        assertEquals(OfflineKind.Book, OfflineKind.fromWire("book"))
        assertEquals(OfflineKind.Variant, OfflineKind.fromWire("variant"))
        // An older Engine sends no kind, and a kind this app does not know is not a photo or a book.
        assertEquals(OfflineKind.Variant, OfflineKind.fromWire(null))
        assertEquals(OfflineKind.Variant, OfflineKind.fromWire("hologram"))
        assertEquals("photo", OfflineKind.Photo.wire)
    }

    @Test
    fun aKeptPhotoIsJudgedByItsOwnRowOfTheEnginesList() {
        val local = listOf(photo("p1"), photo("p2"), photo("p3", reported = false), photo("p4", state = DownloadState.Downloading))
        val result = OfflineLogic.reconcileWithServer(local, listOf(photoRow("p1")), now)

        // p1 is still listed, p2 was reported but its row is gone (deleted, or access removed), so it is purged.
        // p3 was never reported and p4 is not finished, so the list cannot judge them.
        assertEquals(listOf("p2"), result.purge.map { it.assetId })
        assertEquals(listOf("p1", "p3", "p4"), result.keep.map { it.assetId })
        // A photo has no expiry, so listing it leaves it without one.
        assertNull(result.keep.first { it.assetId == "p1" }.expiresAtMs)
    }

    @Test
    fun aPhotoRowAndAVariantRowNeverStandInForEachOther() {
        val variantRow = ServerDownloadEntry("p1", "variant-p1", 100L, now, null)
        // A photo record is not kept alive by a variant row that happens to share its id.
        val photoResult = OfflineLogic.reconcileWithServer(listOf(photo("p1")), listOf(variantRow), now)
        assertEquals(listOf("p1"), photoResult.purge.map { it.assetId })

        // And a variant record is not kept alive by a photo row for the same id.
        val variantResult = OfflineLogic.reconcileWithServer(listOf(record("p1")), listOf(photoRow("p1")), now)
        assertEquals(listOf("p1"), variantResult.purge.map { it.assetId })

        assertTrue(OfflineLogic.serverEntryMatches(photoRow("p1"), photo("p1")))
        assertFalse(OfflineLogic.serverEntryMatches(photoRow("p2"), photo("p1")))
        assertTrue(OfflineLogic.serverEntryMatches(variantRow, record("p1")))
    }

    @Test
    fun aMixedListKeepsVariantsAndPhotosIndependently() {
        val local = listOf(record("a"), record("b"), photo("p1"), photo("p2"))
        val server = listOf(
            ServerDownloadEntry("a", "variant-a", 100L, now, now + 86_400_000L),
            photoRow("p2"),
        )
        val result = OfflineLogic.reconcileWithServer(local, server, now)
        assertEquals(listOf("b", "p1"), result.purge.map { it.assetId })
        assertEquals(listOf("a", "p2"), result.keep.map { it.assetId })
    }

    @Test
    fun aBookCopyIsNeverPurgedByTheListBecauseBooksAreNotWiredYet() {
        val book = photo("b1").copy(kind = OfflineKind.Book, entityType = "book")
        val result = OfflineLogic.reconcileWithServer(listOf(book), emptyList(), now)
        assertTrue(result.purge.isEmpty())
        assertEquals(listOf("b1"), result.keep.map { it.assetId })
        assertFalse(OfflineLogic.serverEntryMatches(photoRow("b1"), book))
    }

    @Test
    fun reportsForAPhotoAndAVariantWithTheSameIdAreKeptApart() {
        val variantStore = PendingReport(ReportKind.Store, "x", "x", 10L, now)
        val photoStore = PendingReport(ReportKind.Store, "x", "x", 20L, now, OfflineKind.Photo)
        val queue = OfflineLogic.queueReport(OfflineLogic.queueReport(emptyList(), variantStore), photoStore)
        assertEquals(2, queue.size)

        // Store then delete of the same photo leaves just the delete, and the variant report is untouched.
        val photoDelete = PendingReport(ReportKind.Delete, "x", "x", 0L, now + 1, OfflineKind.Photo)
        val after = OfflineLogic.queueReport(queue, photoDelete)
        assertEquals(listOf(variantStore, photoDelete), after)
    }

    @Test
    fun aReportMatchesTheRecordOfItsOwnKind() {
        val photoReport = PendingReport(ReportKind.Store, "p1", "p1", 500L, now, OfflineKind.Photo)
        val variantReport = PendingReport(ReportKind.Store, "a", "variant-a", 100L, now)
        assertTrue(OfflineLogic.reportMatches(photoReport, photo("p1")))
        assertFalse(OfflineLogic.reportMatches(photoReport, photo("p2")))
        assertFalse(OfflineLogic.reportMatches(photoReport, record("p1", variantId = "p1")))
        assertTrue(OfflineLogic.reportMatches(variantReport, record("a")))
        assertFalse(OfflineLogic.reportMatches(variantReport, photo("a")))
    }

    @Test
    fun aPhotoReportIsDoneOn204AndWhenADeleteFindsNothing() {
        // 204 is what the Engine really answers for a stored or forgotten photo.
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Store, 204))
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Delete, 204))
        assertEquals(ReportOutcome.Done, OfflineLogic.classifyReport(ReportKind.Delete, 404))
        // A photo the Engine will not take (gone or not visible, list full, no photo access, bad request) is dropped.
        for (status in listOf(400, 403, 404, 409)) {
            assertEquals(ReportOutcome.Drop, OfflineLogic.classifyReport(ReportKind.Store, status))
        }
        // Connection and credential problems are tried again.
        for (status in listOf(401, 408, 429, 500, 503)) {
            assertEquals(ReportOutcome.Retry, OfflineLogic.classifyReport(ReportKind.Store, status))
        }
    }

    @Test
    fun keptPhotosCountUnderTheSameStorageCapAsEveryOtherDownload() {
        val cap = 10_000L
        val held = listOf(record("a", bytesDone = 6_000L, totalBytes = 6_000L), photo("p1", bytesDone = 1_000L, totalBytes = 1_000L))
        assertEquals(7_000L, OfflineLogic.committedBytes(held))
        assertTrue(OfflineLogic.canQueue(OfflineLogic.committedBytes(held), cap, 3_000L))
        assertFalse(OfflineLogic.canQueue(OfflineLogic.committedBytes(held), cap, 3_001L))

        // A photo still being fetched holds the larger of what is on disk and what was reserved for it.
        val inFlight = held + photo("p2", state = DownloadState.Downloading, bytesDone = 100L, totalBytes = 600L)
        assertEquals(7_600L, OfflineLogic.committedBytes(inFlight))
    }

    @Test
    fun aGalleryIsKeptNewestFirstUpToTheCap() {
        val candidates = (1..10).map { PhotoCandidate("p$it", isVideo = false) }
        // Room for exactly four more previews of 1000 bytes under the cap.
        val plan = OfflineLogic.planPhotoKeep(
            candidates, emptySet(), committedBytes = 6_000L, capBytes = 10_000L, listedCount = 0, estimatePerPhoto = 1_000L,
        )
        assertEquals(listOf("p1", "p2", "p3", "p4"), plan.toKeep)
        assertEquals(PhotoKeepStop.StorageCap, plan.stop)
        assertEquals(0, plan.alreadyKept)
    }

    @Test
    fun aGalleryKeepSkipsVideosPhotosAlreadyHeldAndRepeats() {
        val candidates = listOf(
            PhotoCandidate("p1", false),
            PhotoCandidate("v1", true),
            PhotoCandidate("p2", false),
            PhotoCandidate("p1", false),
            PhotoCandidate("p3", false),
        )
        val plan = OfflineLogic.planPhotoKeep(
            candidates, heldIds = setOf("p2"), committedBytes = 0L, capBytes = 1_000_000_000L, listedCount = 3, estimatePerPhoto = 1_000L,
        )
        assertEquals(listOf("p1", "p3"), plan.toKeep)
        assertEquals(1, plan.alreadyKept)
        assertEquals(1, plan.skippedVideos)
        assertEquals(PhotoKeepStop.None, plan.stop)
    }

    @Test
    fun aGalleryKeepStopsAtTheEnginesListLimitAndAFullCapKeepsNothing() {
        val candidates = (1..5).map { PhotoCandidate("p$it", false) }
        val listFull = OfflineLogic.planPhotoKeep(
            candidates, emptySet(), committedBytes = 0L, capBytes = 1_000_000L, listedCount = 498, estimatePerPhoto = 10L,
        )
        assertEquals(listOf("p1", "p2"), listFull.toKeep)
        assertEquals(PhotoKeepStop.ListFull, listFull.stop)

        val capReached = OfflineLogic.planPhotoKeep(
            candidates, emptySet(), committedBytes = 5_000L, capBytes = 5_000L, listedCount = 0, estimatePerPhoto = 10L,
        )
        assertTrue(capReached.toKeep.isEmpty())
        assertEquals(PhotoKeepStop.StorageCap, capReached.stop)

        // A cap of zero (or less) blocks everything, as it does for other downloads.
        val noCap = OfflineLogic.planPhotoKeep(candidates, emptySet(), 0L, 0L, 0)
        assertTrue(noCap.toKeep.isEmpty())
        assertEquals(PhotoKeepStop.StorageCap, noCap.stop)
    }

    @Test
    fun theViewerReadsAFinishedLocalCopyFirstAndNeverAnUnfinishedOrMissingOne() {
        val present = { path: String -> path.endsWith("p1.jpg") }
        assertEquals("/files/photos/p1.jpg", OfflineLogic.localPhotoPath(photo("p1"), present))
        // The file is gone from the phone, the copy is unfinished, there is no record, or it is a video copy.
        assertNull(OfflineLogic.localPhotoPath(photo("p2"), present))
        assertNull(OfflineLogic.localPhotoPath(photo("p1", state = DownloadState.Downloading), present))
        assertNull(OfflineLogic.localPhotoPath(null, present))
        assertNull(OfflineLogic.localPhotoPath(record("p1"), { true }))
    }

    @Test
    fun aKeptPhotoReadsAsAPreviewAndHasItsOwnRoute() {
        assertEquals("Preview (2048 px)", OfflineLogic.qualityLabel(OfflineLogic.PHOTO_PROFILE_KEY))
        assertEquals("Standard (720p)", OfflineLogic.qualityLabel(OfflineLogic.QUALITY_STANDARD))
        assertEquals(2048, OfflineLogic.PHOTO_PREVIEW_EDGE)
        assertEquals("/api/v1/devices/current/downloads/photos/i1", ClientRoutes.devicePhotoDownload("i1"))
        assertEquals("/api/v1/view/items/i1/preview?size=2048", ClientRoutes.viewPreview("i1", OfflineLogic.PHOTO_PREVIEW_EDGE))
        assertTrue(photo("p1").isPhoto)
        assertFalse(record("a").isPhoto)
        // Photos still never get a video or audio quality choice.
        assertTrue(OfflineLogic.qualityChoicesFor(OfflineLogic.downloadKindFor("photo")).isEmpty())
    }

    // ---- paths ----

    @Test
    fun offlineRoutesStayInsideApiV1() {
        assertEquals("/api/v1/devices/current/downloads", ClientRoutes.DEVICE_DOWNLOADS)
        assertEquals("/api/v1/devices/current/downloads/v1", ClientRoutes.deviceDownload("v1"))
        assertEquals("/api/v1/playback/a1/offline/v1", ClientRoutes.offlineFile("a1", "v1"))
        assertEquals("/api/v1/progress/a1", ClientRoutes.progress("a1"))
        assertEquals(
            "/api/v1/details/show/s1/next-episodes?after=a1&count=3",
            ClientRoutes.nextEpisodes("s1", "a1"),
        )
        assertTrue(ClientRoutes.nextEpisodes("s1", "a1", 99).endsWith("count=10"))
    }

    private companion object {
        const val EMPTY_ID = "00000000-0000-0000-0000-000000000000"
    }
}

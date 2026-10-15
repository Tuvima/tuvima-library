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
}

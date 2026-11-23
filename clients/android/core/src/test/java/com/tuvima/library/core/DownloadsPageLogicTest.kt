package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks what the Downloads page groups and says, the Wi-Fi-only wording, free space and the Downloaded mark. */
class DownloadsPageLogicTest {
    private val now = 1_900_000_000_000L
    private val day = 24L * 60L * 60L * 1000L

    private fun record(
        assetId: String,
        state: DownloadState = DownloadState.Complete,
        entityType: String = "movie",
        bytesDone: Long = 2_000_000L,
        totalBytes: Long = 2_000_000L,
        expiresAtMs: Long? = null,
        queuedAtMs: Long = now,
        storedAtMs: Long? = now,
        error: String? = null,
        filePath: String? = "/files/$assetId.mp4",
    ) = DownloadRecord(
        assetId = assetId,
        variantId = "variant-$assetId",
        title = "Title $assetId",
        subtitle = null,
        artworkUrl = null,
        artworkPath = null,
        entityType = entityType,
        showId = null,
        profileKey = OfflineLogic.QUALITY_STANDARD,
        state = state,
        bytesDone = bytesDone,
        totalBytes = totalBytes,
        storedAtMs = storedAtMs,
        expiresAtMs = expiresAtMs,
        filePath = filePath,
        reportedToServer = true,
        variantRequested = true,
        error = error,
        queuedAtMs = queuedAtMs,
    )

    // ---- Sections ----

    @Test
    fun sections_PutsTitlesOnTheirWayFirstThenReadyThenExpiredAndDropsEmptyOnes() {
        val records = listOf(
            record("done-old", storedAtMs = now - 2 * day, queuedAtMs = now - 2 * day),
            record("done-new", storedAtMs = now - day, queuedAtMs = now - day),
            record("going", DownloadState.Downloading, queuedAtMs = now - 5 * day),
            record("expired", expiresAtMs = now - 1),
        )

        val sections = DownloadsPageLogic.sections(records, now)

        assertEquals(listOf(DownloadSection.InProgress, DownloadSection.Ready, DownloadSection.Expired), sections.map { it.section })
        assertEquals(listOf("going"), sections[0].records.map { it.assetId })
        assertEquals(listOf("done-new", "done-old"), sections[1].records.map { it.assetId })
        assertEquals(listOf("expired"), sections[2].records.map { it.assetId })
    }

    @Test
    fun sections_KeepsTitlesOnTheirWayInTheOrderTheyWereAsked() {
        val records = listOf(
            record("second", DownloadState.Queued, queuedAtMs = now + 2),
            record("first", DownloadState.Downloading, queuedAtMs = now + 1),
            record("paused", DownloadState.Paused, queuedAtMs = now + 3),
        )

        val section = DownloadsPageLogic.sections(records, now).single()

        assertEquals(listOf("first", "second", "paused"), section.records.map { it.assetId })
    }

    @Test
    fun sections_ReturnsNothingForAnEmptyList() {
        assertTrue(DownloadsPageLogic.sections(emptyList(), now).isEmpty())
    }

    // ---- Words ----

    @Test
    fun statusLine_NamesWhatAQueuedTitleWaitsForByTheWifiOnlyChoice() {
        val queued = record("q", DownloadState.Queued)

        assertEquals("Waiting for a connection", DownloadsPageLogic.statusLine(queued, now, wifiOnly = false))
        assertEquals("Waiting for Wi-Fi", DownloadsPageLogic.statusLine(queued, now, wifiOnly = true))
    }

    @Test
    fun statusLine_ShowsProgressSizeAndExpiryAndAFailureReason() {
        val going = record("g", DownloadState.Downloading, bytesDone = 1_048_576L, totalBytes = 4_194_304L)
        val done = record("d", expiresAtMs = now + 3 * day, bytesDone = 5_242_880L)
        val failed = record("f", DownloadState.Failed, error = "Not enough free space on this phone. Free some space, then Retry.")

        assertEquals("Downloading 1 MB of 4 MB", DownloadsPageLogic.statusLine(going, now, false))
        assertEquals("5 MB · Standard (720p) · Expires in 3 days", DownloadsPageLogic.statusLine(done, now, false))
        assertTrue(DownloadsPageLogic.statusLine(failed, now, false).startsWith("Not enough free space"))
    }

    @Test
    fun statusLine_ShowsWhyAPausedTitleWaits() {
        val waiting = record("p", DownloadState.Paused, error = "Switch back to this profile to continue.")
        val plain = record("p2", DownloadState.Paused, bytesDone = 1_048_576L, totalBytes = 2_097_152L)

        assertEquals("Switch back to this profile to continue.", DownloadsPageLogic.statusLine(waiting, now, false))
        assertEquals("Paused · 1 MB of 2 MB", DownloadsPageLogic.statusLine(plain, now, false))
    }

    @Test
    fun typeLabel_NamesEachKindInAWord() {
        assertEquals("Movie", DownloadsPageLogic.typeLabel(record("a", entityType = "movie")))
        assertEquals("Episode", DownloadsPageLogic.typeLabel(record("a", entityType = "tvEpisode")))
        assertEquals("Song", DownloadsPageLogic.typeLabel(record("a", entityType = "musicTrack")))
        assertEquals("Audiobook", DownloadsPageLogic.typeLabel(record("a", entityType = "audiobook")))
        assertEquals("Photo", DownloadsPageLogic.typeLabel(record("a", entityType = OfflineLogic.PHOTO_ENTITY_TYPE).copy(kind = OfflineKind.Photo)))
    }

    // ---- Pause all and Resume all ----

    @Test
    fun bulkLabels_AreShownOnlyWhenSomethingCanAct() {
        val mixed = listOf(
            record("a", DownloadState.Downloading),
            record("b", DownloadState.Queued),
            record("c", DownloadState.Paused),
            record("d", DownloadState.Failed),
            record("e"),
        )

        assertEquals("Pause all (2)", DownloadsPageLogic.pauseAllLabel(mixed))
        assertEquals("Resume all (2)", DownloadsPageLogic.resumeAllLabel(mixed))
        assertNull(DownloadsPageLogic.pauseAllLabel(listOf(record("e"))))
        assertNull(DownloadsPageLogic.resumeAllLabel(emptyList()))
    }

    @Test
    fun countLine_SaysHowManyAreReadyAndHowManyAreOnTheWay() {
        val records = listOf(record("a"), record("b"), record("c", DownloadState.Downloading))

        assertEquals("2 ready · 1 on the way", DownloadsPageLogic.countLine(records, now))
        assertNull(DownloadsPageLogic.countLine(emptyList(), now))
    }

    // ---- Offline note ----

    @Test
    fun offlineNote_IsSilentOnlineAndExplainsPlaybackAndSyncOffline() {
        assertNull(DownloadsPageLogic.offlineNote(online = true, hasReady = true))
        assertTrue(DownloadsPageLogic.offlineNote(online = false, hasReady = true)!!.contains("back online"))
        assertTrue(DownloadsPageLogic.offlineNote(online = false, hasReady = false)!!.contains("nothing is downloaded"))
    }

    @Test
    fun removeAllWarning_SaysHowManyAndThatTheServerIsUntouched() {
        assertTrue(DownloadsPageLogic.removeAllWarning(1).startsWith("All 1 download will"))
        assertTrue(DownloadsPageLogic.removeAllWarning(3).contains("3 downloads"))
        assertTrue(DownloadsPageLogic.removeAllWarning(3).contains("server is not changed"))
    }

    // ---- Wi-Fi only and free space ----

    @Test
    fun networkFor_WaitsForAnUnmeteredConnectionOnlyWhenWifiOnlyIsOn() {
        assertEquals(DownloadNetwork.UnmeteredOnly, OfflineStorageLogic.networkFor(true))
        assertEquals(DownloadNetwork.AnyConnection, OfflineStorageLogic.networkFor(false))
    }

    @Test
    fun fitsFreeSpace_KeepsTheReserveFree() {
        val reserve = OfflineStorageLogic.FREE_SPACE_RESERVE_BYTES

        assertTrue(OfflineStorageLogic.fitsFreeSpace(freeBytes = reserve + 1_000, remainingBytes = 1_000))
        assertFalse(OfflineStorageLogic.fitsFreeSpace(freeBytes = reserve + 999, remainingBytes = 1_000))
        // An unknown size needs only the reserve itself.
        assertTrue(OfflineStorageLogic.fitsFreeSpace(freeBytes = reserve, remainingBytes = 0))
        assertFalse(OfflineStorageLogic.fitsFreeSpace(freeBytes = reserve - 1, remainingBytes = 0))
    }

    @Test
    fun isOutOfSpace_RecognisesADiskFullFailure() {
        assertTrue(OfflineStorageLogic.isOutOfSpace("write failed: ENOSPC (No space left on device)"))
        assertFalse(OfflineStorageLogic.isOutOfSpace("connection reset"))
        assertFalse(OfflineStorageLogic.isOutOfSpace(null))
    }

    @Test
    fun usageLine_AddsFreeSpaceWhenKnown() {
        val gb = 1024L * 1024L * 1024L

        assertEquals("Downloads use 2.0 GB of 10.0 GB · 14.0 GB free on this phone", OfflineStorageLogic.usageLine(2 * gb, 10 * gb, 14 * gb))
        assertEquals("Downloads use 2.0 GB of 10.0 GB", OfflineStorageLogic.usageLine(2 * gb, 10 * gb, null))
    }

    @Test
    fun lowSpaceNote_AppearsWhenThePhoneIsNearlyFull() {
        val reserve = OfflineStorageLogic.FREE_SPACE_RESERVE_BYTES

        assertTrue(OfflineStorageLogic.lowSpaceNote(reserve)!!.contains("almost full"))
        assertNull(OfflineStorageLogic.lowSpaceNote(reserve * 3))
        assertNull(OfflineStorageLogic.lowSpaceNote(null))
    }

    @Test
    fun estimateBytes_SizesAVideoByQualityAndAudioByKind() {
        val song = OfflineStorageLogic.estimateBytes("musicTrack", OfflineLogic.QUALITY_AUDIO)
        val book = OfflineStorageLogic.estimateBytes("audiobook", OfflineLogic.QUALITY_AUDIO)
        val small = OfflineStorageLogic.estimateBytes("movie", OfflineLogic.QUALITY_SMALL)
        val standard = OfflineStorageLogic.estimateBytes("movie", OfflineLogic.QUALITY_STANDARD)

        assertTrue(song < book)
        assertTrue(small < standard)
        assertTrue(song > 0)
    }

    // ---- The Downloaded mark ----

    @Test
    fun heldAssetIds_ListsOnlyFinishedUnexpiredFilesThatAreNotPhotos() {
        val records = listOf(
            record("ok"),
            record("going", DownloadState.Downloading),
            record("expired", expiresAtMs = now - 1),
            record("nofile", filePath = null),
            record("photo").copy(kind = OfflineKind.Photo),
        )

        assertEquals(setOf("ok"), DownloadBadgeLogic.heldAssetIds(records, now))
    }

    @Test
    fun showsBadge_IsForSingleFilesOnlyNotShowsAlbumsOrCollections() {
        val held = setOf("a1")

        assertTrue(DownloadBadgeLogic.showsBadge("movie", "a1", held))
        assertTrue(DownloadBadgeLogic.showsBadge("tvepisode", "a1", held))
        assertTrue(DownloadBadgeLogic.showsBadge("audiobook", "a1", held))
        assertFalse(DownloadBadgeLogic.showsBadge("tvshow", "a1", held))
        assertFalse(DownloadBadgeLogic.showsBadge("musicalbum", "a1", held))
        assertFalse(DownloadBadgeLogic.showsBadge("collection", "a1", held))
        assertFalse(DownloadBadgeLogic.showsBadge("movie", "a2", held))
        assertFalse(DownloadBadgeLogic.showsBadge("movie", null, held))
    }
}

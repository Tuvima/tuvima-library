package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks bookmarks added while offline: what is queued, in what order it is sent, and what the offline list shows. */
class OfflineBookmarksTest {
    private fun pending(local: String, position: Double, at: Long, asset: String = "asset-1", work: String = "work-1") =
        PendingBookmark(local, work, asset, position, 3600.0, at)

    private fun bookmark(id: String, position: Double) =
        TvBookmark(id, position, "Label $id", "", null, "Me", isYours = true, inEveryone = false)

    @Test
    fun keepsBookmarks_IsTrueForVideosAndAudiobooksNotMusic() {
        assertTrue(OfflineBookmarkLogic.keepsBookmarks("movie"))
        assertTrue(OfflineBookmarkLogic.keepsBookmarks("tvEpisode"))
        assertTrue(OfflineBookmarkLogic.keepsBookmarks("audiobook"))
        assertFalse(OfflineBookmarkLogic.keepsBookmarks("musicTrack"))
        assertFalse(OfflineBookmarkLogic.keepsBookmarks("photo"))
    }

    @Test
    fun queue_ReplacesAPressAtTheSameMomentOfTheSameFile() {
        val first = pending("a", 100.0, 1)
        val again = pending("b", 100.4, 2)

        val queue = OfflineBookmarkLogic.queue(listOf(first), again)

        assertEquals(listOf("b"), queue.map { it.localId })
    }

    @Test
    fun queue_KeepsBookmarksAtDifferentMomentsOrInDifferentFiles() {
        val queue = OfflineBookmarkLogic.queue(
            OfflineBookmarkLogic.queue(listOf(pending("a", 100.0, 1)), pending("b", 160.0, 2)),
            pending("c", 100.0, 3, asset = "asset-2"),
        )

        assertEquals(listOf("a", "b", "c"), queue.map { it.localId })
    }

    @Test
    fun sendOrder_IsTheOrderTheBookmarksWereMade() {
        val queue = listOf(pending("late", 5.0, 30), pending("early", 90.0, 10), pending("middle", 40.0, 20))

        assertEquals(listOf("early", "middle", "late"), OfflineBookmarkLogic.sendOrder(queue).map { it.localId })
    }

    @Test
    fun classify_SendsAgainWhenOfflineOrBusyAndDropsARefusalThatWillNotChange() {
        assertEquals(ReportOutcome.Done, OfflineBookmarkLogic.classify(200))
        assertEquals(ReportOutcome.Done, OfflineBookmarkLogic.classify(201))
        assertEquals(ReportOutcome.Drop, OfflineBookmarkLogic.classify(404))
        assertEquals(ReportOutcome.Drop, OfflineBookmarkLogic.classify(403))
        assertEquals(ReportOutcome.Retry, OfflineBookmarkLogic.classify(401))
        assertEquals(ReportOutcome.Retry, OfflineBookmarkLogic.classify(429))
        assertEquals(ReportOutcome.Retry, OfflineBookmarkLogic.classify(503))
    }

    @Test
    fun body_IsTheSameOneAnOnlineBookmarkSends() {
        val body = OfflineBookmarkLogic.body(pending("a", 61.26, 1))

        assertEquals("asset-1", body.getString("assetId"))
        assertEquals(61.3, body.getDouble("positionSeconds"), 0.001)
        assertEquals(3600.0, body.getDouble("durationSeconds"), 0.001)
        assertFalse(body.getBoolean("isGlobal"))
    }

    @Test
    fun body_CarriesTheTypedNoteAndOmitsItWhenThereIsNone() {
        val noted = PendingBookmark("a", "work-1", "asset-1", 10.0, 3600.0, 1, "  Watch this  ")

        assertEquals("Watch this", OfflineBookmarkLogic.body(noted).getString("note"))
        assertFalse(OfflineBookmarkLogic.body(pending("b", 10.0, 1)).has("note"))
    }

    @Test
    fun waitingFor_ShowsTheNoteAndMergeWaitingAddsThemToTheEnginesListByPosition() {
        val online = listOf(bookmark("x", 300.0), bookmark("y", 10.0))
        val queue = listOf(PendingBookmark("p", "work-1", "asset-1", 120.0, null, 1, "Good bit"), pending("other", 5.0, 2, work = "work-2"))

        val merged = OfflineBookmarkLogic.mergeWaiting(online, queue, "work-1")

        assertEquals(listOf("y", "offline-p", "x"), merged.map { it.id })
        assertEquals("Good bit", merged[1].note)
        assertEquals(listOf("y", "x"), OfflineBookmarkLogic.mergeWaiting(online, emptyList(), "work-1").map { it.id })
    }

    @Test
    fun forDisplay_AddsTheWaitingBookmarksOfThisTitleByPosition() {
        val cached = listOf(bookmark("x", 300.0), bookmark("y", 10.0))
        val waiting = listOf(pending("p", 120.0, 1), pending("other", 5.0, 2, work = "work-2"))

        val shown = OfflineBookmarkLogic.forDisplay(cached, waiting, "work-1")

        assertEquals(listOf("y", "offline-p", "x"), shown.map { it.id })
        assertEquals(OfflineBookmarkLogic.SAVED_ON_PHONE_LABEL, shown[1].label)
        assertTrue(shown[1].isYours)
    }

    @Test
    fun isWaiting_RecognisesABookmarkThatHasNotReachedTheEngine() {
        assertTrue(OfflineBookmarkLogic.isWaiting("offline-abc"))
        assertFalse(OfflineBookmarkLogic.isWaiting("0f1e2d3c"))
        assertEquals("abc", OfflineBookmarkLogic.localIdOf("offline-abc"))
        assertNull(OfflineBookmarkLogic.localIdOf("0f1e2d3c"))
    }
}

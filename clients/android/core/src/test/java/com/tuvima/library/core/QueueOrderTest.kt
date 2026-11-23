package com.tuvima.library.core

import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class QueueOrderTest {
    private fun ids(count: Int) = (1..count).map { "s$it" }

    private fun track(n: Int) = QueueTrack("s$n", "Song $n")

    @Test
    fun shufflingKeepsEverythingUpToTheSongInPlayAndReordersOnlyWhatFollows() {
        val before = ids(10)

        val after = QueueShuffle.shuffledOrder(before, currentIndex = 3, random = Random(7))

        assertEquals(before.take(4), after.take(4))
        assertEquals(before.drop(4).sorted(), after.drop(4).sorted())
        assertEquals(before.size, after.size)
    }

    @Test
    fun shufflingAShortQueueLeavesItAlone() {
        assertEquals(listOf("a", "b"), QueueShuffle.shuffledOrder(listOf("a", "b"), 0, Random(1)))
        assertEquals(emptyList<String>(), QueueShuffle.shuffledOrder(emptyList(), 0, Random(1)))
    }

    @Test
    fun shuffleOffPutsTheUpcomingSongsBackInTheirOriginalOrder() {
        val original = ids(8)
        val shuffled = QueueShuffle.shuffledOrder(original, currentIndex = 0, random = Random(3))

        val restored = QueueShuffle.restoredOrder(shuffled, currentIndex = 0, original = original)

        assertEquals(original, restored)
    }

    @Test
    fun shuffleOffKeepsAPlayedSongWhereItIsAndPutsAnUnknownSongAfterTheKnownOnes() {
        val original = listOf("a", "b", "c", "d")
        val now = listOf("c", "x", "d", "a", "b")

        val restored = QueueShuffle.restoredOrder(now, currentIndex = 0, original = original)

        assertEquals(listOf("c", "a", "b", "d", "x"), restored)
    }

    @Test
    fun theMovesTurnOneOrderIntoTheOtherEvenWithARepeatedSong() {
        val from = listOf("a", "b", "c", "d", "a")
        val to = listOf("d", "a", "a", "c", "b")

        val moves = QueueShuffle.moves(from, to)

        assertEquals(to, QueueShuffle.apply(from, moves))
        assertTrue(moves.size < from.size)
    }

    @Test
    fun noMovesAreNeededForTheSameOrderOrForDifferentSongs() {
        assertEquals(emptyList<QueueMove>(), QueueShuffle.moves(ids(4), ids(4)))
        assertEquals(emptyList<QueueMove>(), QueueShuffle.moves(listOf("a", "b"), listOf("a", "c")))
        assertEquals(emptyList<QueueMove>(), QueueShuffle.moves(listOf("a"), listOf("a", "b")))
    }

    @Test
    fun aShuffledRequestStartsWithThePointedSongAndHasEveryOtherSongOnce() {
        val request = PlayQueueRequest((1..6).map(::track), startIndex = 2, shuffle = true, sourceName = "Songs")

        val arranged = QueueShuffle.arranged(request, Random(11))

        assertEquals("s3", arranged.tracks.first().assetId)
        assertEquals(0, arranged.startIndex)
        assertEquals(request.tracks.map { it.assetId }.sorted(), arranged.tracks.map { it.assetId }.sorted())
        assertEquals("Songs", arranged.sourceName)
        assertEquals(ids(6), ListenQueue.originalIds(request))
    }

    @Test
    fun aRequestWithShuffleOffIsNotReordered() {
        val request = PlayQueueRequest((1..4).map(::track), startIndex = 1, shuffle = false)

        assertSame(request, QueueShuffle.arranged(request, Random(1)))
    }

    @Test
    fun theShuffleHeaderStartsAtARandomSongWithShuffleOn() {
        val request = PlayQueueRequest((1..50).map(::track))

        val starts = (1..20).map { QueueShuffle.forShuffleHeader(request, Random(it)).startIndex }.toSet()

        assertTrue("every press started at the same song", starts.size > 1)
        assertTrue(QueueShuffle.forShuffleHeader(request, Random(1)).shuffle)
        assertEquals(emptyList<QueueTrack>(), QueueShuffle.forShuffleHeader(PlayQueueRequest(emptyList()), Random(1)).tracks)
    }

    @Test
    fun theQueueRowActionsMoveTheRowWhereTheContractSays() {
        // Song 2 is playing; the row at 5 asks to play next, the row at 3 moves down, the row at 4 moves up.
        assertEquals(QueueMove(5, 3), ListenQueue.moveFor(QueueRowAction.PlayNext, index = 5, current = 2))
        assertEquals(QueueMove(3, 4), ListenQueue.moveFor(QueueRowAction.MoveDown, index = 3, current = 2))
        assertEquals(QueueMove(4, 3), ListenQueue.moveFor(QueueRowAction.MoveUp, index = 4, current = 2))
        assertNull(ListenQueue.moveFor(QueueRowAction.Remove, index = 4, current = 2))
    }

    private fun song(n: Int, asset: String? = "s$n") = DisplayCard(
        id = "w$n", workId = "w$n", assetId = asset, collectionId = null, mediaType = "Music", title = "Song $n", subtitle = "Alpha Band",
        facts = emptyList(), description = null, artworkUrl = null, actions = emptyList(), progressPercent = null,
        groupingType = "work", artist = "Alpha Band", album = "Alpha Songs", duration = "3:10",
    )

    @Test
    fun aPressOnASongInAListPlaysTheListFromThatSong() {
        val cards = (1..5).map { song(it) }

        val request = ListenPlay.fromCards(cards, pressed = cards[3], source = " Songs ")!!

        assertEquals(listOf("s1", "s2", "s3", "s4", "s5"), request.tracks.map { it.assetId })
        assertEquals("s4", request.tracks[request.startIndex].assetId)
        assertEquals("Songs", request.sourceName)
        assertEquals(false, request.shuffle)
    }

    @Test
    fun aSongWithNoFileIsLeftOutOfTheListAndThePressStillFindsItsSong() {
        val cards = listOf(song(1), song(2, asset = null), song(3))

        val request = ListenPlay.fromCards(cards, pressed = cards[2], source = null)!!

        assertEquals(listOf("s1", "s3"), request.tracks.map { it.assetId })
        assertEquals(1, request.startIndex)
        assertNull(request.sourceName)
        assertNull(ListenPlay.fromCards(listOf(song(1, asset = null)), null, "Songs"))
    }

    @Test
    fun aLongListIsCutAroundTheSongThatWasPressed() {
        val cards = (1..250).map { song(it) }

        val request = ListenPlay.fromCards(cards, pressed = cards[200], source = "Songs")!!

        assertEquals(PlayQueue.MAX_TRACKS, request.tracks.size)
        assertEquals("s201", request.tracks[request.startIndex].assetId)
    }

    @Test
    fun theShuffleHeaderPlaysTheSameSongsWithShuffleOn() {
        val cards = (1..30).map { song(it) }

        val request = ListenPlay.shuffled(cards, "Songs", Random(2))!!

        assertTrue(request.shuffle)
        assertEquals(30, request.tracks.size)
        assertEquals(cards.map { it.assetId }, request.tracks.map { it.assetId })
    }
}

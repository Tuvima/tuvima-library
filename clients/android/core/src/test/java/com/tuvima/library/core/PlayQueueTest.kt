package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PlayQueueTest {
    private fun track(n: Int) = QueueTrack("asset-$n", "Song $n", artist = "Maya Ellis", album = "Northern Lights", artworkUrl = "/art/$n")

    private fun tracks(count: Int) = (1..count).map(::track)

    @Test
    fun aRequestSurvivesTheTripThroughAnIntentExtra() {
        val request = PlayQueueRequest(tracks(3), startIndex = 1, shuffle = true, sourceName = "Northern Lights")

        val back = PlayQueue.fromJson(PlayQueue.toJson(request))!!

        assertEquals(request, back)
    }

    @Test
    fun aDamagedExtraIsNotGuessedAt() {
        assertNull(PlayQueue.fromJson(null))
        assertNull(PlayQueue.fromJson(""))
        assertNull(PlayQueue.fromJson("{not json"))
        assertNull(PlayQueue.fromJson("{\"start\":1}"))
    }

    @Test
    fun songsWithoutAFileAreDroppedAndTheStartFollowsTheSongItPointedAt() {
        val list = listOf(track(1), QueueTrack("", "No file"), track(3))

        val cleaned = PlayQueue.normalise(PlayQueueRequest(list, startIndex = 2))

        assertEquals(listOf("asset-1", "asset-3"), cleaned.tracks.map { it.assetId })
        assertEquals(1, cleaned.startIndex)
    }

    @Test
    fun aStartOutsideTheListBecomesTheFirstSong() {
        assertEquals(0, PlayQueue.normalise(PlayQueueRequest(tracks(3), startIndex = 9)).startIndex)
        assertTrue(PlayQueue.normalise(PlayQueueRequest(emptyList(), startIndex = 4)).tracks.isEmpty())
    }

    @Test
    fun aLongListIsCutToTheLimitAroundTheStart() {
        val cleaned = PlayQueue.normalise(PlayQueueRequest(tracks(150), startIndex = 120))

        assertEquals(PlayQueue.MAX_TRACKS, cleaned.tracks.size)
        assertEquals("asset-121", cleaned.tracks[cleaned.startIndex].assetId)
    }

    @Test
    fun theSongsHeaderPlaysTheShownSongsInOrderOrShuffledAndNothingWhenThereAreNone() {
        val play = PlayQueue.playAll(tracks(4), shuffle = false, sourceName = "Songs")!!
        val shuffle = PlayQueue.playAll(tracks(4), shuffle = true, sourceName = "Songs")!!

        assertEquals(0, play.startIndex)
        assertFalse(play.shuffle)
        assertTrue(shuffle.shuffle)
        assertEquals("Songs", play.sourceName)
        assertNull(PlayQueue.playAll(emptyList(), shuffle = false, sourceName = "Songs"))
    }

    @Test
    fun anAlbumPagePlaysItsSongsAndLeavesOutAudiobookPartsAndOtherFiles() {
        val groups = listOf(
            DetailChildGroup(
                "tracks", "Tracks",
                listOf(
                    DetailChild("w1", "a1", "One", null, null, "musicTrack", "1", "tracks", "Tracks"),
                    DetailChild("w2", "a2", "Part 2", null, null, "audiobook", "2", "tracks", "Tracks"),
                    DetailChild("w3", "a3", "Three", "Guest", "/c3", "musicTrack", "3", "tracks", "Tracks"),
                ),
                skipped = 0,
            ),
        )

        val songs = PlayQueue.tracksOf(groups, "Northern Lights", "Maya Ellis", "/cover")

        assertEquals(listOf("a1", "a3"), songs.map { it.assetId })
        assertEquals("Maya Ellis", songs[0].artist)
        assertEquals("Guest", songs[1].artist)
        assertEquals("/cover", songs[0].artworkUrl)
        assertEquals("/c3", songs[1].artworkUrl)
        assertEquals("Northern Lights", songs[0].album)
    }

    @Test
    fun repeatGoesOffThenAllThenOneThenOff() {
        assertEquals(TvRepeatMode.All, RepeatCycle.next(TvRepeatMode.Off))
        assertEquals(TvRepeatMode.One, RepeatCycle.next(TvRepeatMode.All))
        assertEquals(TvRepeatMode.Off, RepeatCycle.next(TvRepeatMode.One))
        assertEquals("Repeat one", RepeatCycle.label(TvRepeatMode.One))
    }

    @Test
    fun previousRestartsASongAfterThreeSecondsAndGoesBackWithinTheFirstThree() {
        assertEquals(PreviousAction.Restart, MusicPrevious.decide(positionSeconds = 3.5, hasPrevious = true))
        assertEquals(PreviousAction.GoToPrevious, MusicPrevious.decide(positionSeconds = 2.0, hasPrevious = true))
        // Pressed again within 3 seconds of a restart the song is back near its start, so it goes to the one before.
        assertEquals(PreviousAction.GoToPrevious, MusicPrevious.decide(positionSeconds = 0.4, hasPrevious = true))
        // At the start of the queue it restarts.
        assertEquals(PreviousAction.Restart, MusicPrevious.decide(positionSeconds = 1.0, hasPrevious = false))
    }

    @Test
    fun theQueuePanelListsUpNextWithItsCountAndTheNowPlayingRow() {
        val view = QueueLogic.view(tracks(5), currentIndex = 1, tab = QueueTab.UpNext, sourceName = "Northern Lights")

        assertEquals("Up next · 3", view.upNextLabel)
        assertEquals("History", view.historyLabel)
        assertEquals("Song 2", view.nowPlaying?.title)
        assertTrue(view.nowPlaying?.isCurrent == true)
        assertEquals(listOf("Song 3", "Song 4", "Song 5"), view.rows.map { it.title })
        assertEquals("Playing from · Northern Lights", view.footer)
        assertTrue(view.canClear)
    }

    @Test
    fun theFirstUpcomingRowCannotMoveUpOrPlayNextAndTheLastCannotMoveDown() {
        val rows = QueueLogic.view(tracks(4), currentIndex = 0, tab = QueueTab.UpNext, sourceName = null).rows

        assertEquals(listOf(QueueRowAction.MoveDown, QueueRowAction.Remove), rows.first().actions)
        assertEquals(
            listOf(QueueRowAction.PlayNext, QueueRowAction.MoveUp, QueueRowAction.MoveDown, QueueRowAction.Remove),
            rows[1].actions,
        )
        assertEquals(listOf(QueueRowAction.PlayNext, QueueRowAction.MoveUp, QueueRowAction.Remove), rows.last().actions)
    }

    @Test
    fun historyListsWhatAlreadyPlayedMostRecentFirstAndClearNeedsUpcomingSongs() {
        val history = QueueLogic.view(tracks(4), currentIndex = 3, tab = QueueTab.History, sourceName = null)

        assertEquals(listOf("Song 3", "Song 2", "Song 1"), history.rows.map { it.title })
        assertFalse(history.canClear)
        assertTrue(history.rows.all { it.actions.isEmpty() })
        assertNull(history.footer)
    }

    @Test
    fun playNextMovesASongToJustAfterTheOneInPlay() {
        assertEquals(3, QueueLogic.playNextTarget(2))
    }
}

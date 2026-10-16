package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class NextEpisodeLogicTest {
    private val credits = PlaybackSegment(kind = "Credits", startSeconds = 2500.0, endSeconds = 2600.0)

    private fun offer(
        position: Double,
        duration: Double? = 2600.0,
        segments: List<PlaybackSegment> = emptyList(),
        autoplay: Boolean = true,
        ended: Boolean = false,
        cancelled: Boolean = false,
    ) = NextEpisodeLogic.nextEpisodeOffer(position, duration, segments, autoplay, ended, cancelled)

    @Test
    fun noCardBeforeTheEnd() {
        assertEquals(NextEpisodeOffer.None, offer(100.0))
        assertEquals(NextEpisodeOffer.None, offer(2584.0))
    }

    @Test
    fun withoutAnEngineSegmentTheCardComesInTheLastFifteenSeconds() {
        assertEquals(NextEpisodeOffer.Countdown, offer(2585.0))
        assertEquals(NextEpisodeOffer.Countdown, offer(2599.0))
    }

    @Test
    fun aCreditsSegmentStartsTheCardEarlier() {
        assertEquals(NextEpisodeOffer.None, offer(2499.0, segments = listOf(credits)))
        assertEquals(NextEpisodeOffer.Countdown, offer(2500.0, segments = listOf(credits)))
    }

    @Test
    fun aRejectedCreditsSegmentIsIgnored() {
        val rejected = credits.copy(reviewStatus = "rejected")
        assertEquals(NextEpisodeOffer.None, offer(2500.0, segments = listOf(rejected)))
        assertEquals(NextEpisodeOffer.Countdown, offer(2590.0, segments = listOf(rejected)))
    }

    @Test
    fun theCardAlwaysShowsAtTheEnd() {
        assertEquals(NextEpisodeOffer.Countdown, offer(0.0, duration = null, ended = true))
    }

    @Test
    fun autoplayOffOrCancelledWaitsForATap() {
        assertEquals(NextEpisodeOffer.Manual, offer(2590.0, autoplay = false))
        assertEquals(NextEpisodeOffer.Manual, offer(2590.0, cancelled = true))
        assertEquals(NextEpisodeOffer.Manual, offer(0.0, ended = true, cancelled = true))
    }

    @Test
    fun anUnknownLengthNeverGuessesTheFinalWindow() {
        assertEquals(NextEpisodeOffer.None, offer(5000.0, duration = null))
        assertNull(NextEpisodeLogic.offerStartSeconds(null, emptyList()))
        assertNull(NextEpisodeLogic.offerStartSeconds(0.0, emptyList()))
    }

    @Test
    fun aShortClipStartsTheCardAtTheBeginning() {
        assertEquals(0.0, NextEpisodeLogic.offerStartSeconds(10.0, emptyList())!!, 0.0001)
    }

    private fun episode(asset: String, season: Int?, number: Int?, title: String) =
        NextEpisode(workId = null, assetId = asset, title = title, seasonNumber = season, episodeNumber = number, stillUrl = null)

    @Test
    fun theOfferedEpisodeIsTheFirstOneThatIsNotTheCurrentFile() {
        val list = listOf(episode("a", 1, 1, "One"), episode("b", 1, 2, "Two"))
        assertEquals("b", NextEpisodeLogic.nextOf(list, "a")?.assetId)
        assertEquals("a", NextEpisodeLogic.nextOf(list, "z")?.assetId)
        assertNull(NextEpisodeLogic.nextOf(emptyList(), "a"))
    }

    @Test
    fun theCardLineHasTheLabelThenTheTitle() {
        assertEquals("Season 1, episode 2 · Two", NextEpisodeLogic.cardLine(episode("b", 1, 2, "Two")))
        assertEquals("Episode 4", NextEpisodeLogic.cardLine(episode("b", null, 4, "  ")))
        assertEquals("Two", NextEpisodeLogic.cardLine(episode("b", null, null, "Two")))
        assertEquals("Next episode", NextEpisodeLogic.cardLine(episode("b", null, null, "")))
    }

    @Test
    fun theCountdownRunsDownToZeroAndStaysThere() {
        assertEquals(8, NextEpisodeLogic.secondsLeft(0))
        assertEquals(1, NextEpisodeLogic.secondsLeft(7))
        assertEquals(0, NextEpisodeLogic.secondsLeft(8))
        assertEquals(0, NextEpisodeLogic.secondsLeft(20))
    }
}

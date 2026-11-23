package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LockScreenCardTest {
    @Test
    fun aVaultItemSendsNoMetadataAtAllWhileVaultIsLocked() {
        val card = LockScreen.card(LockScreenFacts(TvMediaKind.Film, "Secret", year = "2020", vaultLocked = true))

        assertFalse(card.visible)
        assertNull(card.title)
        assertNull(card.line2)
        assertTrue(card.actions.isEmpty())
    }

    @Test
    fun aHiddenEpisodeTitleIsNeverOnTheCardAndTheEpisodeIsNamedByItsNumber() {
        val card = LockScreen.card(
            LockScreenFacts(TvMediaKind.Episode, "The Twist Ending", show = "Pet Show", season = 2, episode = 7, hideEpisodeTitle = true),
        )

        assertEquals("S2 E7", card.title)
        assertEquals("Pet Show · S2 E7", card.line2)
        assertFalse(card.title.orEmpty().contains("Twist"))
        assertFalse(card.line2.orEmpty().contains("Twist"))
    }

    @Test
    fun aPartTheEngineDidNotSayIsLeftOutTogetherWithItsSeparator() {
        assertEquals("Maya Ellis", LockScreen.line2(LockScreenFacts(TvMediaKind.Music, "Song", artist = "Maya Ellis")))
        assertEquals("Northern Lights", LockScreen.line2(LockScreenFacts(TvMediaKind.Music, "Song", album = "Northern Lights")))
        assertNull(LockScreen.line2(LockScreenFacts(TvMediaKind.Music, "Song")))
        assertEquals("Shimmer and Shine", LockScreen.line2(LockScreenFacts(TvMediaKind.Episode, "x", show = "Shimmer and Shine")))
        assertEquals("S1", LockScreen.episodeCode(1, null))
        assertEquals("E4", LockScreen.episodeCode(0, 4))
        assertNull(LockScreen.episodeCode(null, null))
    }

    @Test
    fun anAudiobookWithoutTimedChaptersHasNoPreviousOrNextChapterAction() {
        val actions = LockScreen.actions(TvMediaKind.Audiobook, hasNext = false, hasChapters = false)

        assertFalse(LockAction.PREVIOUS_TRACK in actions)
        assertFalse(LockAction.NEXT_TRACK in actions)
        assertTrue(LockAction.SEEK_BACKWARD in actions && LockAction.SEEK_FORWARD in actions && LockAction.SEEK_TO in actions)
    }

    @Test
    fun musicHasNoSkipByAmountActionsBecauseSongsHaveNoSkipButtons() {
        val actions = LockScreen.actions(TvMediaKind.Music, hasNext = true, hasChapters = false)

        assertFalse(LockAction.SEEK_BACKWARD in actions)
        assertFalse(LockAction.SEEK_FORWARD in actions)
        assertTrue(LockAction.PREVIOUS_TRACK in actions && LockAction.NEXT_TRACK in actions)
    }

    @Test
    fun theCardAsksForTheCoverAt512And1024Pixels() {
        assertEquals(listOf(512, 1024), LockScreenCard.ARTWORK_SIZES)
    }
}

package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class SongReactionTest {
    @Test
    fun pressingTheSameReactionAgainTakesItAwayAndTheOtherOneSwitchesToIt() {
        assertEquals(SongReaction.Like, ReactionLogic.after(null, SongReaction.Like))
        assertNull(ReactionLogic.after(SongReaction.Like, SongReaction.Like))
        assertEquals(SongReaction.Dislike, ReactionLogic.after(SongReaction.Like, SongReaction.Dislike))
    }

    @Test
    fun theEnginesReactionIsReadFromANumberOrAName() {
        assertEquals(SongReaction.Like, SongReaction.fromWire(0))
        assertEquals(SongReaction.Dislike, SongReaction.fromWire(1))
        assertEquals(SongReaction.Dislike, SongReaction.fromWire("Dislike"))
        assertEquals(SongReaction.Like, SongReaction.fromWire(" like "))
        assertNull(SongReaction.fromWire(7))
        assertNull(SongReaction.fromWire(null))
    }

    @Test
    fun theReactionRoutesUseTheEnginesProfileStateReactionsForASong() {
        assertEquals("/api/v1/profile-state/reactions/song/abc", ClientRoutes.profileReaction(SavedKind.Song, "abc"))
        assertEquals("/api/v1/profile-state/reactions/song/abc/dislike", ClientRoutes.profileReactionSet(SavedKind.Song, "abc", SongReaction.Dislike))
    }
}

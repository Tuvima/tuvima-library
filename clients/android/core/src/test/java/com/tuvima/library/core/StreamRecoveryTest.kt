package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class StreamRecoveryTest {
    @Test
    fun a401Or403OnAStreamedItemRecoversOnce() {
        assertEquals(StreamRecoveryDecision.Recover, StreamRecovery.decide(401, isLocalItem = false, attemptsSoFar = 0))
        assertEquals(StreamRecoveryDecision.Recover, StreamRecovery.decide(403, isLocalItem = false, attemptsSoFar = 0))
    }

    @Test
    fun aSecondFailureForTheSameItemGivesUp() {
        assertEquals(StreamRecoveryDecision.GiveUp, StreamRecovery.decide(401, isLocalItem = false, attemptsSoFar = 1))
        assertEquals(StreamRecoveryDecision.GiveUp, StreamRecovery.decide(403, isLocalItem = false, attemptsSoFar = 2))
    }

    @Test
    fun aDownloadedFileNeverTriesToRecover() {
        assertEquals(StreamRecoveryDecision.GiveUp, StreamRecovery.decide(401, isLocalItem = true, attemptsSoFar = 0))
    }

    @Test
    fun otherFailuresAreLeftAlone() {
        listOf(null, 200, 404, 410, 429, 500, 503).forEach {
            assertEquals(it.toString(), StreamRecoveryDecision.GiveUp, StreamRecovery.decide(it, isLocalItem = false, attemptsSoFar = 0))
        }
        assertFalse(StreamRecovery.isAuthFailure(null))
        assertTrue(StreamRecovery.isAuthFailure(401))
    }

    @Test
    fun aScreenHoldsBackOnlyABadStatusError() {
        assertTrue(StreamRecovery.defersErrorScreen(isBadHttpStatus = true))
        assertFalse(StreamRecovery.defersErrorScreen(isBadHttpStatus = false))
        assertEquals(4_000L, StreamRecovery.ERROR_GRACE_MS)
    }

    @Test
    fun aRecoveredVideoStaysPausedButAudioKeepsPlaying() {
        assertTrue(StreamRecovery.resumePlaying(wasPlaying = true, isVideo = true))
        assertFalse(StreamRecovery.resumePlaying(wasPlaying = false, isVideo = true))
        assertTrue(StreamRecovery.resumePlaying(wasPlaying = false, isVideo = false))
    }
}

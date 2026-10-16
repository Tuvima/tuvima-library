package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.net.UnknownHostException

class ClipPlaybackTest {
    private fun manifest(direct: String?) = PlaybackManifest(
        assetId = "item-1", recommendedDelivery = "direct-stream", directPlaySupported = true, directStreamUrl = direct,
        hlsUrl = null, hlsStatus = null, hlsExpiresAt = null, resumeSeconds = null, durationSeconds = null,
        offlineVariants = emptyList(), warnings = emptyList(),
    )

    @Test
    fun theClipStreamIsTheManifestsDirectStream() {
        assertEquals("https://s/api/v1/view/items/item-1/original", ClipPlayback.streamUrl(manifest("https://s/api/v1/view/items/item-1/original")))
        assertNull(ClipPlayback.streamUrl(manifest(null)))
        assertNull(ClipPlayback.streamUrl(manifest("  ")))
    }

    @Test
    fun theSpeedButtonCyclesAndComesBackToNormal() {
        var speed = 1f
        val seen = mutableListOf(speed)
        repeat(ClipPlayback.speeds.size) { speed = ClipPlayback.nextSpeed(speed); seen += speed }
        assertEquals(1f, seen.last(), 0.001f)
        assertEquals(ClipPlayback.speeds.size + 1, seen.size)
        assertEquals(1f, ClipPlayback.nextSpeed(3.7f), 0.001f)
    }

    @Test
    fun speedLabelsAreShort() {
        assertEquals("1x", ClipPlayback.speedLabel(1f))
        assertEquals("1.25x", ClipPlayback.speedLabel(1.25f))
        assertEquals("0.5x", ClipPlayback.speedLabel(0.5f))
        assertEquals("2x", ClipPlayback.speedLabel(2f))
    }

    @Test
    fun noConnectionAsksToConnect() {
        val failure = ClipPlayback.failureFor(UnknownHostException("host"))
        assertEquals(ClipMessages.OFFLINE, failure.message)
        assertTrue(failure.canRetry)
        assertEquals(ClipMessages.OFFLINE, ClipPlayback.failureFor(IOException("reset")).message)
    }

    @Test
    fun refusedMissingAndOtherFailuresAreExplainedPlainly() {
        val denied = ClipPlayback.failureFor(PhotoAccessDeniedException())
        assertEquals(PHOTO_ACCESS_DENIED_MESSAGE, denied.message)
        assertFalse(denied.canRetry)
        assertEquals(PHOTO_ACCESS_DENIED_MESSAGE, ClipPlayback.failureFor(TuvimaHttpException(403, "")).message)
        val gone = ClipPlayback.failureFor(TuvimaHttpException(404, ""))
        assertEquals(ClipMessages.MISSING, gone.message)
        assertFalse(gone.canRetry)
        val server = ClipPlayback.failureFor(TuvimaHttpException(500, ""))
        assertEquals(ClipMessages.OTHER, server.message)
        assertTrue(server.canRetry)
        assertEquals(ClipMessages.OTHER, ClipPlayback.failureFor(IllegalStateException("x")).message)
    }

    @Test
    fun aPhoneClipFollowsThePhoneNotTheLandscapeLock() {
        assertEquals(OrientationRule.Sensor, OrientationPolicy.ruleFor(ScreenKind.VideoClip))
    }
}

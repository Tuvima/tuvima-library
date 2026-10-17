package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.net.UnknownHostException

class VideoPlaybackLogicTest {
    private fun manifest(
        hlsStatus: String? = "ready",
        hlsUrl: String? = "https://s/x.m3u8",
        warnings: List<String> = emptyList(),
        reason: String? = null,
    ) = PlaybackManifest(
        assetId = "a", recommendedDelivery = "hls", directPlaySupported = false, directStreamUrl = null,
        hlsUrl = hlsUrl, hlsStatus = hlsStatus, hlsExpiresAt = null, resumeSeconds = null, durationSeconds = null,
        offlineVariants = emptyList(), warnings = warnings, conversionReason = reason,
    )

    @Test
    fun videoTypesOpenTheVideoScreen() {
        listOf("movie", "tvEpisode", "episode", "video").forEach { assertTrue(it, PlaybackRouting.opensVideoScreen(it)) }
    }

    @Test
    fun audioAndUnknownTypesKeepTheAudioPath() {
        listOf("audiobook", "musicAlbum", "track", "book", "work", "", null).forEach {
            assertFalse(it.toString(), PlaybackRouting.opensVideoScreen(it))
        }
    }

    @Test
    fun readyManifestPlays() {
        assertEquals(PreparingStep.Play("https://s/x.m3u8"), PreparingPoll.next(manifest(), 1))
    }

    @Test
    fun preparingWaitsUntilTheLimit() {
        val preparing = manifest(hlsStatus = "preparing")
        assertEquals(PreparingStep.Wait, PreparingPoll.next(preparing, 1))
        assertEquals(PreparingStep.Wait, PreparingPoll.next(preparing, PreparingPoll.MAX_ATTEMPTS - 1))
        assertEquals(PreparingStep.TimedOut, PreparingPoll.next(preparing, PreparingPoll.MAX_ATTEMPTS))
        assertEquals(120_000L, PreparingPoll.INTERVAL_MS * PreparingPoll.MAX_ATTEMPTS)
    }

    @Test
    fun failureUsesWarningsThenReasonThenGeneralLine() {
        val failed = manifest(hlsStatus = "failed", hlsUrl = null)
        val step = PreparingPoll.next(failed.copy(warnings = listOf("No audio.")), 1) as PreparingStep.Failed
        assertEquals("No audio.", step.message)
        assertEquals("Needs conversion", PreparingPoll.failureText(failed.copy(conversionReason = "Needs conversion")))
        assertEquals(VideoMessages.NOT_READY, PreparingPoll.failureText(failed))
    }

    @Test
    fun refusalsAreUnavailableAndConnectionProblemsAreNetwork() {
        assertEquals(VideoFailure.Unavailable, VideoErrors.classify(TuvimaHttpException(403, "")))
        assertEquals(VideoFailure.Unavailable, VideoErrors.classify(TuvimaHttpException(404, "")))
        assertEquals(VideoFailure.Other, VideoErrors.classify(TuvimaHttpException(500, "")))
        assertEquals(VideoFailure.Network, VideoErrors.classify(UnknownHostException("x")))
        assertEquals(VideoFailure.Network, VideoErrors.classify(IOException("x")))
        assertEquals(VideoFailure.Network, VideoErrors.classify(RuntimeException("wrapped", IOException("x"))))
        assertEquals(VideoFailure.Other, VideoErrors.classify(IllegalStateException("x")))
    }

    @Test
    fun messagesAreThePlainWordsTheScreenShows() {
        assertEquals("This title is not available for this profile.", VideoMessages.forFailure(VideoFailure.Unavailable))
        assertEquals("Can't reach your Tuvima server", VideoMessages.forFailure(VideoFailure.Network))
    }

    @Test
    fun onlyStreamedItemsSendHeartbeats() {
        assertTrue(HeartbeatRules.shouldSend(isLocalItem = false, assetId = "a"))
        assertFalse(HeartbeatRules.shouldSend(isLocalItem = true, assetId = "a"))
        assertFalse(HeartbeatRules.shouldSend(isLocalItem = false, assetId = null))
        assertFalse(HeartbeatRules.shouldSend(isLocalItem = false, assetId = " "))
        assertEquals(15_000L, HeartbeatRules.INTERVAL_MS)
    }

    @Test
    fun endedReportUsesFullLengthAndIsNotPlaying() {
        val ended = HeartbeatRules.report("a", true, 59_000, 60_000, 1.0, ended = true)
        assertEquals(60.0, ended.positionSeconds, 0.0)
        assertFalse(ended.isPlaying)
        val running = HeartbeatRules.report("a", true, 12_500, 60_000, 1.5, ended = false)
        assertEquals(12.5, running.positionSeconds, 0.0)
        assertTrue(running.isPlaying)
        assertEquals(1.5, running.playbackRate, 0.0)
    }

    @Test
    fun unknownDurationIsNull() {
        assertNull(HeartbeatRules.report("a", true, 1_000, null, 1.0, false).durationSeconds)
        assertNull(HeartbeatRules.report("a", true, 1_000, -9_223_372_036_854_775_807L, 1.0, false).durationSeconds)
    }

    @Test
    fun streamedResumeSkipsNearTheEnd() {
        assertEquals(0L, StreamResume.startMs(null, 100.0))
        assertEquals(0L, StreamResume.startMs(-3.0, 100.0))
        assertEquals(42_500L, StreamResume.startMs(42.5, 100.0))
        assertEquals(42_500L, StreamResume.startMs(42.5, null))
        assertEquals(0L, StreamResume.startMs(96.0, 100.0))
        assertEquals(0L, StreamResume.startMs(100.0, 100.0))
    }

    @Test
    fun clockText() {
        assertEquals("0:00", ClockText.format(-5))
        assertEquals("1:23", ClockText.format(83_000))
        assertEquals("1:02:03", ClockText.format(3_723_000))
    }
}

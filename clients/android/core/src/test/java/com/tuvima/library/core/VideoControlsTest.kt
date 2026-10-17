package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class VideoControlsTest {
    private fun segment(kind: String, start: Double, end: Double?, skippable: Boolean = true, status: String? = null) =
        PlaybackSegment(kind, start, end, skippable, status)

    @Test
    fun introOfferedOnlyInsideTheSegment() {
        val segments = listOf(segment("intro", 30.0, 90.0))
        assertNull(segmentToOffer(29.9, segments))
        assertEquals(SkipOffer(SkipKind.Intro, "Skip Intro", 90.0), segmentToOffer(30.0, segments))
        assertEquals(90.0, segmentToOffer(89.9, segments)!!.targetSeconds, 0.0)
        assertNull(segmentToOffer(90.0, segments))
    }

    @Test
    fun creditsUseTheDurationWhenTheSegmentHasNoEnd() {
        val segments = listOf(segment("Credits", 3000.0, null))
        assertNull(segmentToOffer(3100.0, segments))
        val offer = segmentToOffer(3100.0, segments, durationSeconds = 3200.0)!!
        assertEquals("Skip Credits", offer.label)
        assertEquals(3200.0, offer.targetSeconds, 0.0)
    }

    @Test
    fun unknownKindsNotSkippableAndRejectedSegmentsAreIgnored() {
        val segments = listOf(
            segment("commercial", 0.0, 100.0),
            segment("recap", 0.0, 100.0),
            segment("intro", 0.0, 100.0, skippable = false),
            segment("credits", 0.0, 100.0, status = "rejected"),
            segment("intro", 50.0, 40.0),
        )
        assertNull(segmentToOffer(10.0, segments, 500.0))
        assertNull(segmentToOffer(10.0, emptyList()))
    }

    @Test
    fun doubleTapSeeksByTheSkipSecondsOnEachHalf() {
        assertEquals(-10_000L, VideoGestures.doubleTapSeekMs(100f, 1000f, 10, 30))
        assertEquals(30_000L, VideoGestures.doubleTapSeekMs(900f, 1000f, 10, 30))
        assertEquals(30_000L, VideoGestures.doubleTapSeekMs(500f, 1000f, 10, 30))
    }

    @Test
    fun leftHalfIsBrightnessAndRightHalfIsVolume() {
        assertEquals(SlideKind.Brightness, VideoGestures.slideKind(10f, 1000f))
        assertEquals(SlideKind.Volume, VideoGestures.slideKind(990f, 1000f))
    }

    @Test
    fun slidingUpRaisesAndDownLowersAndItClamps() {
        assertEquals(0.75f, VideoGestures.slideFraction(0.5f, -250f, 1000f), 0.0001f)
        assertEquals(0.25f, VideoGestures.slideFraction(0.5f, 250f, 1000f), 0.0001f)
        assertEquals(1f, VideoGestures.slideFraction(0.9f, -900f, 1000f), 0f)
        assertEquals(0f, VideoGestures.slideFraction(0.1f, 900f, 1000f), 0f)
        assertEquals(0.4f, VideoGestures.slideFraction(0.4f, 50f, 0f), 0f)
    }

    @Test
    fun volumeAndBrightnessMapping() {
        assertEquals(0, VideoGestures.volumeIndex(0f, 15))
        assertEquals(8, VideoGestures.volumeIndex(0.5f, 15))
        assertEquals(15, VideoGestures.volumeIndex(2f, 15))
        assertEquals(VideoGestures.MIN_BRIGHTNESS, VideoGestures.brightness(0f), 0f)
        assertEquals(0.6f, VideoGestures.brightness(0.6f), 0f)
    }

    @Test
    fun closingStopsAndClearsButGoingHomeOnlyPauses() {
        assertEquals(CloseAction.StopAndClear, VideoSessionPolicy.onScreenStop(isFinishing = true))
        assertEquals(CloseAction.Pause, VideoSessionPolicy.onScreenStop(isFinishing = false))
        assertTrue(VideoSessionPolicy.clearAfterEnd(currentIsVideo = true))
        assertFalse(VideoSessionPolicy.clearAfterEnd(currentIsVideo = false))
    }

    @Test
    fun aPausedVideoLeftBehindCannotBeStartedWithoutTheScreen() {
        assertTrue(VideoSessionPolicy.shouldBlockPlay(currentIsVideo = true, videoScreenAttached = false, alreadyPlaying = false))
        assertFalse(VideoSessionPolicy.shouldBlockPlay(currentIsVideo = true, videoScreenAttached = true, alreadyPlaying = false))
        assertFalse(VideoSessionPolicy.shouldBlockPlay(currentIsVideo = true, videoScreenAttached = false, alreadyPlaying = true))
        assertFalse(VideoSessionPolicy.shouldBlockPlay(currentIsVideo = false, videoScreenAttached = false, alreadyPlaying = false))
    }

    @Test
    fun subtitleSizeAndMime() {
        assertEquals(0.0533f, SubtitleStyleRules.sizeFraction("Medium"), 0f)
        assertEquals(0.0533f, SubtitleStyleRules.sizeFraction(null), 0f)
        assertEquals(0.045f, SubtitleStyleRules.sizeFraction("small"), 0f)
        assertEquals(0.09f, SubtitleStyleRules.sizeFraction("Extra Large"), 0f)
        assertEquals("application/x-subrip", SubtitleStyleRules.mimeFor("srt", null))
        assertEquals("application/x-subrip", SubtitleStyleRules.mimeFor(null, "https://s/sub.SRT?x=1"))
        assertEquals("text/vtt", SubtitleStyleRules.mimeFor("webvtt", "https://s/sub.vtt"))
        assertEquals("text/vtt", SubtitleStyleRules.mimeFor(null, null))
    }

    @Test
    fun preferenceDefaultsAndModes() {
        val defaults = PlaybackPreferences.Default
        assertEquals("English", defaults.audioLanguage)
        assertEquals(ShowSubtitles.Auto, defaults.subtitleMode)
        assertEquals("Medium", defaults.subtitleSize)
        assertEquals(10, defaults.skipBackSeconds)
        assertEquals(30, defaults.skipForwardSeconds)
        assertTrue(defaults.autoplayNextEpisode)
        assertEquals(1.0, defaults.defaultPlaybackSpeed, 0.0)
        assertEquals(ShowSubtitles.Always, PlaybackPreferences.modeFrom(" always "))
        assertEquals(ShowSubtitles.Off, PlaybackPreferences.modeFrom("OFF"))
        assertEquals(ShowSubtitles.Auto, PlaybackPreferences.modeFrom("sometimes"))
        assertEquals(ShowSubtitles.Auto, PlaybackPreferences.modeFrom(null))
    }

    @Test
    fun languageNamesMatchTwoAndThreeLetterCodes() {
        val audio = listOf(
            AudioTrack(0, "eng", "aac", null, true, 2),
            AudioTrack(1, "jpn", "aac", null, false, 2),
        )
        val subs = listOf(SubtitleTrack(0, "en", "srt", null, false, false, null))
        assertEquals(1, PlaybackTrackPicker.pick(audio, subs, "Japanese", "English", ShowSubtitles.Auto).audioIndex)
        assertEquals(0, PlaybackTrackPicker.pick(audio, subs, "Japanese", "English", ShowSubtitles.Auto).subtitleIndex)
        assertNull(PlaybackTrackPicker.pick(audio, subs, "English", "English", ShowSubtitles.Auto).subtitleIndex)
        assertEquals(0, PlaybackTrackPicker.pick(audio, subs, "en", "eng", ShowSubtitles.Always).subtitleIndex)
    }

    @Test
    fun screenStopInTheFloatingWindowKeepsPlayingButHomeWithoutOneStillPauses() {
        assertEquals(CloseAction.KeepPlaying, VideoSessionPolicy.onScreenStop(isFinishing = false, inPictureInPicture = true))
        assertEquals(CloseAction.Pause, VideoSessionPolicy.onScreenStop(isFinishing = false, inPictureInPicture = false))
        // Closing for good always wins.
        assertEquals(CloseAction.StopAndClear, VideoSessionPolicy.onScreenStop(isFinishing = true, inPictureInPicture = true))
    }

    @Test
    fun closingTheFloatingWindowStopsAndClearsButExpandingItDoesNot() {
        assertEquals(CloseAction.StopAndClear, VideoSessionPolicy.onPictureInPictureGone(screenIsStarted = false))
        assertEquals(null, VideoSessionPolicy.onPictureInPictureGone(screenIsStarted = true))
    }
}

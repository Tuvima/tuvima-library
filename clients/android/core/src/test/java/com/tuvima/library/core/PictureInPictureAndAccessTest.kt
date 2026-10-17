package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PictureInPictureAndAccessTest {
    private fun enter(
        supported: Boolean = true,
        playing: Boolean = true,
        preparing: Boolean = false,
        error: Boolean = false,
        countdown: Boolean = false,
    ) = PictureInPicturePolicy.shouldEnter(supported, playing, preparing, error, countdown)

    @Test
    fun aPlayingVideoShrinksToAWindowOnHome() {
        assertTrue(enter())
    }

    @Test
    fun noWindowWhilePreparingOnAnErrorWhenPausedOrBeforeTheNextEpisodeStarts() {
        assertFalse(enter(preparing = true))
        assertFalse(enter(error = true))
        assertFalse(enter(playing = false))
        assertFalse(enter(countdown = true))
    }

    @Test
    fun aPhoneWithoutTheFeatureKeepsThePauseOnHomeRule() {
        assertFalse(enter(supported = false))
        assertEquals(CloseAction.Pause, VideoSessionPolicy.onScreenStop(isFinishing = false, inPictureInPicture = false))
    }

    @Test
    fun theWindowKeepsTheVideosShapeWithinWhatAndroidAccepts() {
        assertEquals(1920 to 1080, PictureInPicturePolicy.aspectRatio(1920, 1080))
        assertEquals(9 to 16, PictureInPicturePolicy.aspectRatio(9, 16))
        assertEquals(239 to 100, PictureInPicturePolicy.aspectRatio(4000, 1000))
        assertEquals(100 to 239, PictureInPicturePolicy.aspectRatio(500, 3000))
        assertEquals(16 to 9, PictureInPicturePolicy.aspectRatio(0, 0))
        assertEquals(16 to 9, PictureInPicturePolicy.aspectRatio(-1, 720))
    }

    @Test
    fun timesAreSpokenPlainly() {
        assertEquals("1 second", VideoAccessibility.spokenTime(1_000))
        assertEquals("45 seconds", VideoAccessibility.spokenTime(45_000))
        assertEquals("1 minute", VideoAccessibility.spokenTime(60_000))
        assertEquals("12 minutes", VideoAccessibility.spokenTime(12 * 60_000L + 20_000))
        assertEquals("1 hour", VideoAccessibility.spokenTime(3_600_000))
        assertEquals("1 hour 5 minutes", VideoAccessibility.spokenTime(3_900_000))
        assertEquals("2 hours 10 minutes", VideoAccessibility.spokenTime(2 * 3_600_000L + 10 * 60_000L))
        assertEquals("0 seconds", VideoAccessibility.spokenTime(-5))
    }

    @Test
    fun theSeekBarSaysWhereItIsAndHowLongTheVideoIs() {
        assertEquals("12 minutes of 45 minutes", VideoAccessibility.positionAnnouncement(12 * 60_000L, 45 * 60_000L))
        assertEquals("12 minutes", VideoAccessibility.positionAnnouncement(12 * 60_000L, 0))
    }

    @Test
    fun theNextEpisodeIsAnnouncedOnceWithoutAChangingCountdownNumber() {
        val counting = VideoAccessibility.nextEpisodeAnnouncement("Season 1, episode 2 · Two", 8)
        assertEquals("Next episode: Season 1, episode 2 · Two. Plays in 8 seconds. Play now or cancel.", counting)
        assertEquals("Next episode: Two. Play next.", VideoAccessibility.nextEpisodeAnnouncement("Two", null))
        assertEquals("Next episode: Two. Play next.", VideoAccessibility.nextEpisodeAnnouncement("Two", 0))
    }

    @Test
    fun controlsStayWhileAScreenReaderIsOn() {
        assertTrue(VideoAccessibility.controlsMayAutoHide(touchExplorationEnabled = false))
        assertFalse(VideoAccessibility.controlsMayAutoHide(touchExplorationEnabled = true))
    }

    @Test
    fun accessibilitySeekStepsFollowTheSkipSeconds() {
        assertEquals(10_000L, VideoAccessibility.seekStepMs(2_700_000, 10))
        assertEquals(1_000L, VideoAccessibility.seekStepMs(2_700_000, 0))
        assertEquals(5_000L, VideoAccessibility.seekStepMs(5_000, 30))
    }

    @Test
    fun keysMapToTheVideoCommands() {
        assertEquals(VideoKeyCommand.TogglePlay, VideoKeys.commandFor(VideoKeys.KEYCODE_SPACE))
        assertEquals(VideoKeyCommand.TogglePlay, VideoKeys.commandFor(VideoKeys.KEYCODE_MEDIA_PLAY_PAUSE))
        assertEquals(VideoKeyCommand.SeekBack, VideoKeys.commandFor(VideoKeys.KEYCODE_DPAD_LEFT))
        assertEquals(VideoKeyCommand.SeekForward, VideoKeys.commandFor(VideoKeys.KEYCODE_DPAD_RIGHT))
        assertEquals(VideoKeyCommand.Close, VideoKeys.commandFor(VideoKeys.KEYCODE_ESCAPE))
        assertEquals(VideoKeyCommand.Close, VideoKeys.commandFor(VideoKeys.KEYCODE_BACK))
        assertEquals(VideoKeyCommand.None, VideoKeys.commandFor(29))
    }

    @Test
    fun escapeOrBackClosesTheControlsBeforeTheScreen() {
        assertEquals(CloseStep.HideControls, VideoKeys.closeAction(controlsVisible = true))
        assertEquals(CloseStep.LeaveScreen, VideoKeys.closeAction(controlsVisible = false))
    }

    @Test
    fun systemCaptionsWinWhenTurnedOn() {
        assertTrue(CaptionChoice.usesSystemStyle(true))
        assertFalse(CaptionChoice.usesSystemStyle(false))
        assertEquals("medium", CaptionChoice.sizeName(" Medium "))
        assertEquals("", CaptionChoice.sizeName(null))
    }
}

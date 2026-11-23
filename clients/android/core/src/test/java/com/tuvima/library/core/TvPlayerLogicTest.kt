package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvPlayerLogicTest {
    private val skip = TvSkip(10, 30)

    // ---- time and lines of text ----

    @Test
    fun timeReadsMinutesAndSecondsThenHours() {
        assertEquals("0:00", TvPlayerLogic.formatTime(-4.0))
        assertEquals("7:42", TvPlayerLogic.formatTime(462.9))
        assertEquals("1:05:09", TvPlayerLogic.formatTime(3909.0))
        assertEquals("-14:59", TvPlayerLogic.timeLeft(1.0, 900.0))
        assertEquals("", TvPlayerLogic.timeLeft(1.0, null))
    }

    @Test
    fun aLengthUnderAnHourReadsMinutesAndFromAnHourHoursAndMinutes() {
        assertEquals("22 min", TvPlayerLogic.lengthText(22 * 60.0 + 41))
        assertEquals("1h 52m", TvPlayerLogic.lengthText(112 * 60.0))
        assertEquals("", TvPlayerLogic.lengthText(null))
    }

    @Test
    fun theLineUnderTheTitleFollowsTheContractExamples() {
        assertEquals(
            "S1 E1 · The First Wish · 22 min",
            TvPlayerLogic.detailLine(TvMediaKind.Episode, season = 1, episode = 1, title = "The First Wish", durationSeconds = 22 * 60.0),
        )
        assertEquals(
            "3 · Ball handling · 12 min",
            TvPlayerLogic.detailLine(TvMediaKind.Lesson, number = 3, title = "Ball handling", durationSeconds = 12 * 60.0),
        )
        assertEquals("2019 · 1h 52m", TvPlayerLogic.detailLine(TvMediaKind.Film, year = 2019, durationSeconds = 112 * 60.0))
        assertEquals("2019", TvPlayerLogic.detailLine(TvMediaKind.Film, year = 2019))
    }

    @Test
    fun theKindComesFromTheMediaTypeAndWhetherTheTitleBelongsToAShow() {
        assertEquals(TvMediaKind.Music, TvPlayerLogic.kindOf("Music", false))
        assertEquals(TvMediaKind.Audiobook, TvPlayerLogic.kindOf("Audiobooks", false))
        assertEquals(TvMediaKind.Lesson, TvPlayerLogic.kindOf("Videos", false))
        assertEquals(TvMediaKind.Episode, TvPlayerLogic.kindOf("TV", false))
        assertEquals(TvMediaKind.Episode, TvPlayerLogic.kindOf("Movies", true))
        assertEquals(TvMediaKind.Film, TvPlayerLogic.kindOf("Movies", false))
        assertEquals(TvMediaKind.Film, TvPlayerLogic.kindOf(null, false))
    }

    // ---- the profile's seconds, autoplay and speed ----

    @Test
    fun skipSecondsAreTheDeviceChoiceThenTheProfilesPairThenTenAndThirty() {
        assertEquals(TvSkip(10, 30), TvPlayerLogic.skipSeconds(null, null))
        assertEquals(TvSkip(15, 60), TvPlayerLogic.skipSeconds(PlaybackPreferences(skipBackSeconds = 15, skipForwardSeconds = 60), null))
        assertEquals(TvSkip(5, 90), TvPlayerLogic.skipSeconds(PlaybackPreferences(skipBackSeconds = 15), TvDeviceChoices(skipBack = 5, skipForward = 90)))
        // A nonsense saved value falls back instead of breaking the keys.
        assertEquals(TvSkip(10, 30), TvPlayerLogic.skipSeconds(PlaybackPreferences(skipBackSeconds = 0, skipForwardSeconds = 9000), null))
    }

    @Test
    fun autoplayIsTheDeviceSwitchThenTheProfilePreferenceThenOn() {
        assertTrue(TvPlayerLogic.autoplayOn(null, null))
        assertFalse(TvPlayerLogic.autoplayOn(PlaybackPreferences(autoplayNextEpisode = false), null))
        assertTrue(TvPlayerLogic.autoplayOn(PlaybackPreferences(autoplayNextEpisode = false), TvDeviceChoices(autoplay = true)))
    }

    @Test
    fun speedIsHeldBetweenHalfAndTripleInFiveHundredthsAndLabelledWithATimesSign() {
        assertEquals(0.5, TvPlayerLogic.clampSpeed(0.1), 0.0)
        assertEquals(3.0, TvPlayerLogic.clampSpeed(9.0), 0.0)
        assertEquals(1.0, TvPlayerLogic.clampSpeed(Double.NaN), 0.0)
        assertEquals(1.05, TvPlayerLogic.stepSpeed(1.0, 1), 1e-9)
        assertEquals(0.95, TvPlayerLogic.stepSpeed(1.0, -1), 1e-9)
        assertEquals("1×", TvPlayerLogic.speedLabel(1.0))
        assertEquals("1.25×", TvPlayerLogic.speedLabel(1.25))
        assertTrue(TvPlayerLogic.isPreset(1.5))
        assertFalse(TvPlayerLogic.isPreset(1.05))
    }

    // ---- transport and tools ----

    @Test
    fun playReadsPauseWhilePlayingAndTheSkipButtonsCarryTheProfileSeconds() {
        val row = TvPlayerLogic.transport(TvMediaKind.Episode, TvSkip(15, 60), playing = true, canPrevious = true, canNext = true)
        assertEquals(listOf(false, false, false, false, false), row.map { it.dimmed })
        assertEquals("Pause", row[2].label)
        assertEquals("Skip back 15 s", row[1].label)
        assertEquals(15, row[1].seconds)
        assertEquals("Skip forward 60 s", row[3].label)
        assertEquals(TvPlayerLogic.PLAY_INDEX, row.indexOfFirst { it.key == "play-pause" })
    }

    @Test
    fun previousAndNextAreDimmedInPlaceNeverLeftOut() {
        val row = TvPlayerLogic.transport(TvMediaKind.Film, skip, playing = false, canPrevious = false, canNext = false)
        assertEquals(5, row.size)
        assertTrue(row.first().dimmed)
        assertTrue(row.last().dimmed)
    }

    @Test
    fun shuffleAndRepeatShowSelectedWhenOn() {
        val row = TvPlayerLogic.transport(TvMediaKind.Music, skip, false, true, true, shuffleOn = true, repeat = TvRepeatMode.One)
        assertTrue(row.first { it.key == "shuffle" }.selected)
        assertTrue(row.first { it.key == "repeat" }.selected)
        val off = TvPlayerLogic.transport(TvMediaKind.Music, skip, false, true, true)
        assertFalse(off.first { it.key == "repeat" }.selected)
    }

    @Test
    fun aToolTheMediaDoesNotSupportIsAbsent() {
        assertEquals(
            listOf("Audio & subtitles", "Playback settings"),
            TvPlayerLogic.tools(TvMediaKind.Film, hasEpisodes = true, canBookmark = false).map { it.label },
        )
        assertEquals(
            listOf("Audio & subtitles", "Bookmarks", "Playback settings"),
            TvPlayerLogic.tools(TvMediaKind.Episode, hasEpisodes = false, canBookmark = true).map { it.label },
        )
        assertEquals(emptyList<TvTool>(), TvPlayerLogic.tools(TvMediaKind.Audiobook, false, false, hasChapters = false))
    }

    @Test
    fun aFolderVideosListToolIsNamedLessonsAndOpensTheEpisodesPanel() {
        val tools = TvPlayerLogic.tools(TvMediaKind.Lesson, hasEpisodes = true, canBookmark = true)
        assertEquals("Lessons", tools.first().label)
        assertEquals(TvPanelId.Episodes, TvPlayerLogic.panelOf(tools.first().key))
    }

    @Test
    fun theToolStaysHighlightedForTheNestedViewsOfItsPanel() {
        assertEquals("audio-subtitles", TvPlayerLogic.toolOf(TvPanelId.SubtitleAppearance))
        assertEquals("playback-settings", TvPlayerLogic.toolOf(TvPanelId.Speed))
        assertEquals(TvPanelId.PlaybackSettings, TvPlayerLogic.parentOf(TvPanelId.SkipBack))
        assertNull(TvPlayerLogic.parentOf(TvPanelId.Episodes))
    }

    @Test
    fun previousInMusicActsOnceTheSongHasPlayedThreeSecondsOrAQueueHasOneBefore() {
        assertFalse(TvPlayerLogic.musicCanPrevious(2.0, false))
        assertTrue(TvPlayerLogic.musicCanPrevious(3.5, false))
        assertTrue(TvPlayerLogic.musicCanPrevious(1.0, true))
        assertTrue(TvPlayerLogic.musicPreviousRestarts(4.0))
        assertFalse(TvPlayerLogic.musicPreviousRestarts(1.0))
    }

    // ---- paused details, overlay and end card ----

    @Test
    fun pausedDetailsHideWhileAPanelIsOpenAndComeBackWhenItCloses() {
        assertTrue(TvPlayerLogic.pausedDetailsVisible(paused = true, panelOpen = false, endCard = false))
        assertFalse(TvPlayerLogic.pausedDetailsVisible(paused = true, panelOpen = true, endCard = false))
        assertFalse(TvPlayerLogic.pausedDetailsVisible(paused = false, panelOpen = false, endCard = false))
    }

    @Test
    fun theOverlayNeverHidesWhilePausedOrWhileAPanelTheFinderOrTheEndCardIsOpen() {
        assertTrue(TvPlayerLogic.overlayShouldHide(paused = false, panelOpen = false, finderOpen = false, endCard = false))
        assertFalse(TvPlayerLogic.overlayShouldHide(paused = false, panelOpen = true, finderOpen = false, endCard = false))
        assertFalse(TvPlayerLogic.overlayShouldHide(paused = false, panelOpen = false, finderOpen = true, endCard = false))
        assertFalse(TvPlayerLogic.overlayShouldHide(paused = false, panelOpen = false, finderOpen = false, endCard = true))
        assertFalse(TvPlayerLogic.overlayShouldHide(paused = true, panelOpen = false, finderOpen = false, endCard = false))
        assertEquals(5_000L, TvPlayerLogic.OVERLAY_HIDE_MS)
    }

    @Test
    fun theEndCardHasACountdownOnlyWhileAutoplayIsOn() {
        assertEquals("Starts in 8 seconds", TvPlayerLogic.endCard("Pet Show", TvMediaKind.Episode, 8).countdown)
        assertEquals("", TvPlayerLogic.endCard("Pet Show", TvMediaKind.Episode, null).countdown)
        assertEquals("Next lesson", TvPlayerLogic.endCard("Pet Show", TvMediaKind.Lesson, 3).kind)
    }

    @Test
    fun theEndCardStartsOnPlayNowAndLeftAndRightMoveBetweenItsButtons() {
        assertEquals(0, TvPlayerLogic.endCardFocus(1, TvKeyAction.Left))
        assertEquals(1, TvPlayerLogic.endCardFocus(0, TvKeyAction.Right))
        assertEquals(TvKeyEffect.CancelEndCard, TvPlayerLogic.endCardEffect(0))
        assertEquals(TvKeyEffect.PlayNext, TvPlayerLogic.endCardEffect(1))
    }

    // ---- the remote ----

    @Test
    fun theRemotesKeysMapToActions() {
        assertEquals(TvKeyAction.Up, TvRemoteKeys.actionFor(19))
        assertEquals(TvKeyAction.Select, TvRemoteKeys.actionFor(23))
        assertEquals(TvKeyAction.Select, TvRemoteKeys.actionFor(66))
        assertEquals(TvKeyAction.Back, TvRemoteKeys.actionFor(4))
        assertEquals(TvKeyAction.Rewind, TvRemoteKeys.actionFor(89))
        assertEquals(TvKeyAction.FastForward, TvRemoteKeys.actionFor(90))
        assertEquals(TvKeyAction.PlayPause, TvRemoteKeys.actionFor(85))
        assertNull(TvRemoteKeys.actionFor(29))
    }

    @Test
    fun backClosesANestedViewThenThePanelThenHidesTheOverlayThenLeaves() {
        val nested = TvKeyState(overlayVisible = true, panelOpen = true, panelNested = true)
        assertEquals(TvKeyEffect.PanelBack, TvKeyMap.effect(nested, TvKeyAction.Back).effect)
        assertEquals(TvKeyEffect.ClosePanel, TvKeyMap.effect(nested.copy(panelNested = false), TvKeyAction.Back).effect)
        assertEquals(TvKeyEffect.HideOverlay, TvKeyMap.effect(TvKeyState(overlayVisible = true), TvKeyAction.Back).effect)
        assertEquals(TvKeyEffect.ClosePlayer, TvKeyMap.effect(TvKeyState(overlayVisible = false), TvKeyAction.Back).effect)
    }

    @Test
    fun aFirstKeyOnAHiddenOverlayOnlyShowsItButTheMediaKeysStillAct() {
        val hidden = TvKeyState(overlayVisible = false)
        val result = TvKeyMap.effect(hidden, TvKeyAction.Select)
        assertEquals(TvKeyEffect.ShowOverlay, result.effect)
        assertTrue(result.consume)
        assertEquals(TvKeyEffect.SkipForward, TvKeyMap.effect(hidden, TvKeyAction.FastForward).effect)
        assertEquals(TvKeyEffect.TogglePlay, TvKeyMap.effect(hidden, TvKeyAction.PlayPause).effect)
    }

    @Test
    fun backOnTheEndCardCancelsItAndOtherKeysMoveBetweenItsButtons() {
        val card = TvKeyState(overlayVisible = true, endCard = true)
        assertEquals(TvKeyEffect.CancelEndCard, TvKeyMap.effect(card, TvKeyAction.Back).effect)
        assertEquals(TvKeyEffect.Move, TvKeyMap.effect(card, TvKeyAction.Left).effect)
    }

    @Test
    fun theCaptionsKeyOpensAudioAndSubtitles() {
        assertEquals(TvKeyEffect.OpenAudioSubtitles, TvKeyMap.effect(TvKeyState(overlayVisible = false), TvKeyAction.Captions).effect)
    }

    // ---- focus on the controls ----

    @Test
    fun focusStartsOnPlayAndUpAndDownWalkTimelineTransportAndTools() {
        var focus = TvFocus.initial(TvPlayerLogic.PLAY_INDEX)
        assertEquals(TvZone.Transport, focus.zone)
        focus = TvControlsFocus.move(focus, TvKeyAction.Up, 5, 4)
        assertEquals(TvZone.Timeline, focus.zone)
        focus = TvControlsFocus.move(focus, TvKeyAction.Down, 5, 4)
        focus = TvControlsFocus.move(focus, TvKeyAction.Down, 5, 4)
        assertEquals(TvZone.Tools, focus.zone)
        focus = TvControlsFocus.move(focus, TvKeyAction.Down, 5, 4)
        assertEquals(TvZone.Tools, focus.zone)
        focus = TvControlsFocus.move(focus, TvKeyAction.Up, 5, 4)
        assertEquals(TvZone.Transport, focus.zone)
    }

    @Test
    fun leftAndRightStopAtTheEndsOfARowAndNeverMoveDimmedControls() {
        var focus = TvFocus(TvZone.Transport, 0)
        focus = TvControlsFocus.move(focus, TvKeyAction.Left, 5, 4)
        assertEquals(0, focus.transportIndex)
        repeat(9) { focus = TvControlsFocus.move(focus, TvKeyAction.Right, 5, 4) }
        assertEquals(4, focus.transportIndex)
    }

    @Test
    fun withAPanelOpenRightFromTheLastToolEntersThePanelAndLeftReturns() {
        var focus = TvFocus(TvZone.Tools, 0, toolIndex = 3, panelOpen = true)
        focus = TvControlsFocus.move(focus, TvKeyAction.Right, 5, 4)
        assertEquals(TvZone.Panel, focus.zone)
        assertEquals(TvZone.Tools, TvControlsFocus.leavePanel(focus).zone)
    }

    @Test
    fun withNoToolsDownStaysOnTheTransportRow() {
        val focus = TvControlsFocus.move(TvFocus.initial(2), TvKeyAction.Down, 5, 0)
        assertEquals(TvZone.Transport, focus.zone)
    }
}

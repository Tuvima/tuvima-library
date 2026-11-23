package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PhonePlayerLogicTest {
    private val skip = TvSkip(10, 30)

    @Test
    fun aFilmsBookmarksHangOnItsOwnWorkAndAShowOrCollectionOrEpisodePageHasNone() {
        assertEquals("work-1", PhonePlayerLogic.workIdFor("movie", "work-1"))
        assertEquals("work-1", PhonePlayerLogic.workIdFor("work", "work-1?x=1"))
        assertNull(PhonePlayerLogic.workIdFor("tvShow", "show-1"))
        assertNull(PhonePlayerLogic.workIdFor("collection", "c-1"))
        assertNull(PhonePlayerLogic.workIdFor("tvEpisode", "e-1"))
        assertNull(PhonePlayerLogic.workIdFor("movie", "  "))
        assertNull(PhonePlayerLogic.workIdFor(null, "work-1"))
    }

    // ---- bar, transport and tools ----

    @Test
    fun thePlayButtonReadsPauseWhilePlayingAndNextTrackIsDimmedInPlaceWithNoNextSong() {
        val bar = PhonePlayerLogic.bar(TvMediaKind.Music, playing = true, skip = skip, canNext = false)

        assertEquals(listOf("Pause", "Next track"), bar.map { it.label })
        assertTrue(bar[1].dimmed)
        assertFalse(bar[0].dimmed)
    }

    @Test
    fun anAudiobookBarShowsTheProfilesSkipForwardSeconds() {
        val bar = PhonePlayerLogic.bar(TvMediaKind.Audiobook, playing = false, skip = TvSkip(15, 60), canNext = false)

        assertEquals("Skip forward 60 s", bar[1].label)
        assertEquals(60, bar[1].seconds)
    }

    @Test
    fun theMusicTransportDimsPreviousAndNextInPlaceAndNeverHidesThem() {
        val transport = PhonePlayerLogic.transport(TvMediaKind.Music, skip, playing = false, canPrevious = false, canNext = false)

        assertEquals(listOf("previous", "play-pause", "next"), transport.map { it.key })
        assertEquals(listOf(true, false, true), transport.map { it.dimmed })
    }

    @Test
    fun theMusicToolsMarkShuffleAndRepeatSelectedWhenTheyAreOn() {
        val off = PhonePlayerLogic.tools(TvMediaKind.Music)
        val on = PhonePlayerLogic.tools(TvMediaKind.Music, shuffleOn = true, repeat = TvRepeatMode.One)

        assertEquals(listOf(false, false, false, false), off.map { it.selected })
        assertTrue(on.first { it.key == "shuffle" }.selected)
        assertTrue(on.first { it.key == "repeat" }.selected)
        assertFalse(on.first { it.key == "lyrics" }.selected)
    }

    @Test
    fun musicHasNoBookmarksToolEvenWhenAskedFor() {
        assertFalse(PhonePlayerLogic.tools(TvMediaKind.Music, canBookmark = true).any { it.key == "bookmarks" })
    }

    @Test
    fun aToolTheMediaCannotOfferIsAbsentNotDimmed() {
        val film = PhonePlayerLogic.tools(TvMediaKind.Film, hasEpisodes = true, canBookmark = false)
        assertEquals(listOf("audio-subtitles"), film.map { it.key })

        val book = PhonePlayerLogic.tools(TvMediaKind.Audiobook, hasChapters = false, canBookmark = false)
        assertEquals(listOf("speed", "sleep-timer"), book.map { it.key })
    }

    @Test
    fun theSpeedChipShowsTheCurrentSpeed() {
        val speed = PhonePlayerLogic.tools(TvMediaKind.Audiobook, speed = 1.25).first { it.key == "speed" }

        assertEquals("1.25×", speed.badge)
    }

    @Test
    fun aLessonsToolIsNamedLessons() {
        assertEquals("Lessons", PhonePlayerLogic.tools(TvMediaKind.Lesson, hasEpisodes = true).first { it.key == "episodes" }.label)
    }

    @Test
    fun theMenuHoldsPlaybackSettingsForVideoAndAudiobooksAndNothingForMusic() {
        assertTrue(PhonePlayerLogic.menu(TvMediaKind.Music).isEmpty())
        assertEquals(listOf("Playback settings"), PhonePlayerLogic.menu(TvMediaKind.Audiobook).map { it.label })
        assertEquals(
            listOf("Playback settings", "Picture in picture"),
            PhonePlayerLogic.menu(TvMediaKind.Film, canPictureInPicture = true).map { it.label },
        )
        assertEquals(listOf("Playback settings"), PhonePlayerLogic.menu(TvMediaKind.Episode).map { it.label })
    }

    // ---- preferences kept on the phone ----

    @Test
    fun skipSecondsAreThePhonesChoiceThenTheProfilesThenTenAndThirty() {
        assertEquals(TvSkip(10, 30), PhonePlayerLogic.skip(null, null, audio = false))
        assertEquals(TvSkip(15, 60), PhonePlayerLogic.skip(PlaybackPreferences(skipBackSeconds = 15, skipForwardSeconds = 60), null, audio = false))
        assertEquals(
            TvSkip(5, 90),
            PhonePlayerLogic.skip(PlaybackPreferences(skipBackSeconds = 15, skipForwardSeconds = 60), PhoneDeviceChoices(skipBack = 5, skipForward = 90), audio = false),
        )
    }

    @Test
    fun audioMovesASavedVideoNumberToTheNearestOneAudioOffers() {
        // Video offers 5 back and 90 forward; audio offers 10, 15, 30 back and 15, 30, 60 forward.
        val device = PhoneDeviceChoices(skipBack = 5, skipForward = 90)

        assertEquals(TvSkip(10, 60), PhonePlayerLogic.skip(null, device, audio = true))
        assertEquals(TvSkip(10, 30), PhonePlayerLogic.skip(null, null, audio = true))
    }

    @Test
    fun theStartingSpeedIsThePhonesLastChoiceForThatKindThenTheProfilesDefaultThenOne() {
        val device = PhoneDeviceChoices(videoSpeed = 1.5, audiobookSpeed = 1.25)
        val prefs = PlaybackPreferences(defaultPlaybackSpeed = 2.0)

        assertEquals(1.5, PhonePlayerLogic.startingSpeed(prefs, device, audiobook = false), 0.0)
        assertEquals(1.25, PhonePlayerLogic.startingSpeed(prefs, device, audiobook = true), 0.0)
        assertEquals(2.0, PhonePlayerLogic.startingSpeed(prefs, null, audiobook = true), 0.0)
        assertEquals(1.0, PhonePlayerLogic.startingSpeed(null, null, audiobook = false), 0.0)
    }

    @Test
    fun autoplayIsThePhonesSwitchThenTheProfilesThenOn() {
        assertTrue(PhonePlayerLogic.autoplayOn(null, null))
        assertFalse(PhonePlayerLogic.autoplayOn(PlaybackPreferences(autoplayNextEpisode = false), null))
        assertTrue(PhonePlayerLogic.autoplayOn(PlaybackPreferences(autoplayNextEpisode = false), PhoneDeviceChoices(autoplay = true)))
    }

    @Test
    fun bookmarkTicksAreOnlyDrawnWhenTheSwitchIsOnAndTheLengthIsKnown() {
        val marks = listOf(bookmark("a", 50.0), bookmark("b", 200.0))

        assertEquals(listOf(50.0), PhonePlayerLogic.timelineTicks(marks, 100.0, showOnTimeline = true))
        assertTrue(PhonePlayerLogic.timelineTicks(marks, 100.0, showOnTimeline = false).isEmpty())
        assertTrue(PhonePlayerLogic.timelineTicks(marks, null, showOnTimeline = true).isEmpty())
    }

    // ---- subtitles ----

    private val audio = listOf(AudioTrack(0, "jpn", "aac", "Japanese", true, 2), AudioTrack(1, "eng", "aac", "English", false, 2))
    private val subtitles = listOf(
        SubtitleTrack(0, "eng", "srt", "English", isDefault = false, isForced = false, deliveryUrl = null),
        SubtitleTrack(1, "fra", "srt", "French", isDefault = true, isForced = false, deliveryUrl = null),
    )

    @Test
    fun subtitlesStartOffWhenNothingIsRememberedAndTheProfilesPreferencesCouldNotBeRead() {
        val choice = SubtitleMemory.choose(null, prefsFromEngine = false, prefs = PlaybackPreferences.Default, audio = audio, subtitles = subtitles)

        assertNull(choice.subtitleIndex)
    }

    @Test
    fun withNothingRememberedTheProfilesOwnSubtitlePreferenceApplies() {
        val prefs = PlaybackPreferences(audioLanguage = "Japanese", subtitleLanguage = "English", subtitleMode = ShowSubtitles.Always)

        val choice = SubtitleMemory.choose(null, prefsFromEngine = true, prefs = prefs, audio = audio, subtitles = subtitles)

        assertEquals(0, choice.audioIndex)
        assertEquals(0, choice.subtitleIndex)
    }

    @Test
    fun aRememberedLanguageIsUsedForTheNextVideoWhateverTheProfilePrefers() {
        val prefs = PlaybackPreferences(subtitleLanguage = "English", subtitleMode = ShowSubtitles.Off)

        val choice = SubtitleMemory.choose("fra", prefsFromEngine = true, prefs = prefs, audio = audio, subtitles = subtitles)

        assertEquals(1, choice.subtitleIndex)
    }

    @Test
    fun aRememberedOffKeepsSubtitlesOffEvenWhenTheProfileWantsThem() {
        val prefs = PlaybackPreferences(subtitleMode = ShowSubtitles.Always)

        val choice = SubtitleMemory.choose(SubtitleMemory.OFF, prefsFromEngine = true, prefs = prefs, audio = audio, subtitles = subtitles)

        assertNull(choice.subtitleIndex)
    }

    @Test
    fun aRememberedLanguageTheVideoDoesNotCarryLeavesSubtitlesOffInsteadOfShowingAnotherLanguage() {
        val choice = SubtitleMemory.choose("deu", prefsFromEngine = true, prefs = PlaybackPreferences.Default, audio = audio, subtitles = subtitles)

        assertNull(choice.subtitleIndex)
    }

    @Test
    fun whatToRememberIsOffOrThePickedLanguageAndNothingForATrackWithNoLanguage() {
        assertEquals("off", SubtitleMemory.remember(pickedOff = true, pickedLanguage = "eng"))
        assertEquals("eng", SubtitleMemory.remember(pickedOff = false, pickedLanguage = " eng "))
        assertNull(SubtitleMemory.remember(pickedOff = false, pickedLanguage = null))
        assertNull(SubtitleMemory.remember(pickedOff = false, pickedLanguage = "  "))
    }

    // ---- next episode from downloads ----

    private fun download(asset: String, show: String?, label: String?, state: DownloadState = DownloadState.Complete, expires: Long? = null, queued: Long = 0L) =
        DownloadRecord(
            assetId = asset, variantId = "v-$asset", title = "Title $asset", subtitle = label, artworkUrl = null, artworkPath = null,
            entityType = "tvEpisode", showId = show, profileKey = "mobile-standard", state = state, bytesDone = 10, totalBytes = 10,
            storedAtMs = 1L, expiresAtMs = expires, filePath = "/f/$asset", reportedToServer = true, variantRequested = true, error = null,
            queuedAtMs = queued, workId = "w-$asset",
        )

    @Test
    fun theEpisodeLabelOfADownloadIsReadBackIntoSeasonAndEpisode() {
        assertEquals(1 to 2, DownloadedEpisodes.numbersOf("Season 1, episode 2"))
        assertEquals(0 to 3, DownloadedEpisodes.numbersOf("Special 3"))
        assertEquals(1 to 4, DownloadedEpisodes.numbersOf("Episode 4"))
        assertNull(DownloadedEpisodes.numbersOf("Pilot"))
        assertNull(DownloadedEpisodes.numbersOf(null))
    }

    @Test
    fun theNextEpisodeFromDownloadsFollowsSeasonAndEpisodeOrderAndSkipsWhatIsNotOnThePhone() {
        val records = listOf(
            download("e3", "show", "Season 1, episode 3"),
            download("e1", "show", "Season 1, episode 1"),
            download("s2e1", "show", "Season 2, episode 1"),
            download("e2", "show", "Season 1, episode 2", state = DownloadState.Downloading),
            download("other", "another", "Season 1, episode 2"),
            download("old", "show", "Season 1, episode 5", expires = 5L),
        )

        val next = DownloadedEpisodes.after(records, "show", "e1", nowMs = 100L)

        assertEquals(listOf("e3", "s2e1"), next.map { it.assetId })
        assertEquals(1, next.first().seasonNumber)
        assertEquals(3, next.first().episodeNumber)
        assertEquals("w-e3", next.first().workId)
    }

    @Test
    fun theLastDownloadedEpisodeHasNoNextEpisode() {
        val records = listOf(download("e1", "show", "Season 1, episode 1"), download("e2", "show", "Season 1, episode 2"))

        assertTrue(DownloadedEpisodes.after(records, "show", "e2", nowMs = 0L).isEmpty())
    }

    // ---- the line under the title ----

    @Test
    fun theDetailLineIsTheContractsOneLinePerKind() {
        val episode = TvEpisode("a", "w", 1, 1, "The First Wish", "", "", null, null, "", null)

        assertEquals("S1 E1 · The First Wish · 22 min", PhonePlayerLogic.detailLine(TvMediaKind.Episode, episode, null, 22 * 60.0))
        assertEquals("2019 · 1h 52m", PhonePlayerLogic.detailLine(TvMediaKind.Film, null, 2019, 112 * 60.0))
    }

    private fun bookmark(id: String, at: Double) = TvBookmark(id, at, "", "", null, "", isYours = true, inEveryone = false)
}

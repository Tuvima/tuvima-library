package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class FindAnotherWordingTest {
    private fun item(
        key: String = "p:1",
        confidence: Double = 0.95,
        release: String? = "Release.One",
        hearing: Boolean = false,
        current: Boolean = false,
        language: String = "en",
        lines: List<TextTrackPreviewLine> = emptyList(),
        check: String? = null,
        difference: Double? = null,
        timed: Boolean = true,
        title: String? = null,
        artist: String? = null,
        album: String? = null,
        length: Double? = null,
    ) = TextTrackAlternative(
        provider = "p", providerName = "P", sourceId = key.substringAfter(':'), language = language, isTimed = timed,
        confidence = confidence, durationMatchScore = null, isHearingImpaired = hearing, releaseName = release,
        isCurrent = current, title = title, artist = artist, album = album, lengthSeconds = length,
        itemLengthSeconds = null, previewLines = lines, endsAtSeconds = null, lengthCheck = check,
        lengthDifferenceSeconds = difference,
    )

    @Test
    fun aResultMadeForTheFileSaysSoInGreen() {
        assertEquals(
            listOf(FindAnotherCheck("Made for your file", CheckTone.Ok)),
            FindAnotherWording.subtitleChecks(item(confidence = 0.9)),
        )
    }

    @Test
    fun aDifferentReleaseWarnsItMayBeOutOfStep() {
        assertEquals(
            listOf(FindAnotherCheck("A different release, may be out of step", CheckTone.Warn)),
            FindAnotherWording.subtitleChecks(item(confidence = 0.89)),
        )
    }

    @Test
    fun aReadFileThatFitsSaysFitsTheFilm() {
        val checks = FindAnotherWording.subtitleChecks(item(check = "Fits", difference = 20.0))
        assertEquals(FindAnotherCheck("Fits the film", CheckTone.Ok), checks[1])
    }

    @Test
    fun aFileThatEndsShortSaysHowManyMinutesShort() {
        val checks = FindAnotherWording.subtitleChecks(item(check = "Off", difference = -300.0))
        assertEquals(FindAnotherCheck("Ends 5 minutes short of the film", CheckTone.Warn), checks[1])
    }

    @Test
    fun aFileThatEndsLongSaysHowManyMinutesPastAndTurnsRedBeyondFifteen() {
        val amber = FindAnotherWording.subtitleChecks(item(check = "Off", difference = 900.0))[1]
        assertEquals(FindAnotherCheck("Ends 15 minutes past the film", CheckTone.Warn), amber)
        val red = FindAnotherWording.subtitleChecks(item(check = "Off", difference = 960.0))[1]
        assertEquals(CheckTone.Bad, red.tone)
    }

    @Test
    fun oneMinuteOffIsSingular() {
        val check = FindAnotherWording.subtitleChecks(item(check = "Off", difference = 50.0))[1]
        assertEquals("Ends 1 minute past the film", check.text)
    }

    @Test
    fun aFileNotReadYetHasNoTimingCheck() {
        assertEquals(1, FindAnotherWording.subtitleChecks(item()).size)
        assertEquals(1, FindAnotherWording.subtitleChecks(item(check = "Fits", difference = null)).size)
    }

    @Test
    fun lyricsSayWhetherTheyAreSyncedAndHowTheyFitTheSong() {
        assertEquals(FindAnotherCheck("Synced", CheckTone.Ok), FindAnotherWording.lyricsChecks(item(timed = true))[0])
        assertEquals(
            FindAnotherCheck("Words only, not timed", CheckTone.Neutral),
            FindAnotherWording.lyricsChecks(item(timed = false))[0],
        )
        assertEquals(
            FindAnotherCheck("Fits the song", CheckTone.Ok),
            FindAnotherWording.lyricsChecks(item(check = "Fits", difference = 2.0))[1],
        )
        assertEquals(
            FindAnotherCheck("12 seconds longer than the song", CheckTone.Warn),
            FindAnotherWording.lyricsChecks(item(check = "Off", difference = 12.0))[1],
        )
        assertEquals(
            FindAnotherCheck("40 seconds shorter than the song", CheckTone.Bad),
            FindAnotherWording.lyricsChecks(item(check = "Off", difference = -40.0))[1],
        )
    }

    @Test
    fun theSubtitleMetaLineHasLanguageOnlyWhenTheListMixesThemAndHearingImpaired() {
        assertNull(FindAnotherWording.subtitleMeta(item(), showLanguage = false))
        assertEquals("English", FindAnotherWording.subtitleMeta(item(language = "eng"), showLanguage = true))
        assertEquals(
            "French · Hearing impaired",
            FindAnotherWording.subtitleMeta(item(language = "fr", hearing = true), showLanguage = true),
        )
        assertEquals("Hearing impaired", FindAnotherWording.subtitleMeta(item(hearing = true), showLanguage = false))
    }

    @Test
    fun theLyricsMetaLineJoinsArtistAlbumAndLength() {
        assertEquals(
            "Band · Record · 3:21",
            FindAnotherWording.lyricsMeta(item(artist = "Band", album = "Record", length = 201.0)),
        )
        assertNull(FindAnotherWording.lyricsMeta(item()))
    }

    @Test
    fun clockReadsMinutesAndSecondsOrHours() {
        assertEquals("1:02", FindAnotherWording.clock(62.4))
        assertEquals("0:00", FindAnotherWording.clock(-3.0))
        assertEquals("1:05:09", FindAnotherWording.clock(3909.0))
    }

    @Test
    fun theEnginesOwnMessageIsPreferredForASentence() {
        assertEquals(
            "SubDL is taking a nap.",
            FindAnotherWording.statusSentence(TextTrackKind.Subtitles, "Unavailable", " SubDL is taking a nap. ", "SubDL", "English"),
        )
    }

    @Test
    fun withoutAMessageEachStatusHasItsPlainSentence() {
        fun sentence(status: String, source: String? = "SubDL", language: String? = "English") =
            FindAnotherWording.statusSentence(TextTrackKind.Subtitles, status, "", source, language)
        assertEquals("No subtitle source is switched on.", sentence("NoSourceOn"))
        assertEquals("SubDL did not accept its key.", sentence("SourceKeyRequired"))
        assertEquals("This film could not be matched for an online search.", sentence("IdentityUnverified"))
        assertEquals("SubDL has reached its limit for now. Try again later.", sentence("LimitReached"))
        assertEquals("SubDL could not be reached.", sentence("Unavailable"))
        assertEquals("This library never looks anything up online.", sentence("Blocked"))
        assertEquals("Your profile cannot search for subtitles.", sentence("NotAllowed"))
        assertEquals("That language is not supported for subtitle search.", sentence("LanguageUnknown"))
        assertEquals("No English subtitles were found for this film.", sentence("NoneFound"))
        assertEquals("No English subtitles were found for this film.", sentence("Found"))
        assertEquals("The search could not be completed.", sentence("Mystery"))
        assertEquals("The subtitle source did not accept its key.", sentence("SourceKeyRequired", source = null))
    }

    @Test
    fun lyricsSentencesSpeakOfLyricsAndTheSong() {
        fun sentence(status: String) = FindAnotherWording.statusSentence(TextTrackKind.Lyrics, status, null, null, null)
        assertEquals("No lyrics source is switched on.", sentence("NoSourceOn"))
        assertEquals("Your profile cannot search for lyrics.", sentence("NotAllowed"))
        assertEquals("No lyrics were found for this song.", sentence("NoneFound"))
    }

    @Test
    fun theWindowTitleAndEntryFollowTheKind() {
        assertEquals("Find subtitles", FindAnotherWording.windowTitle(TextTrackKind.Subtitles))
        assertEquals("Find lyrics", FindAnotherWording.windowTitle(TextTrackKind.Lyrics))
        assertEquals("Find subtitles…", FindAnotherWording.entryLabel(TextTrackKind.Subtitles))
        assertEquals("Looking for subtitles…", FindAnotherWording.loadingText(TextTrackKind.Subtitles))
    }

    @Test
    fun theFirstRowIsTheBestMatchAndTheCurrentOneIsInUse() {
        val rows = FindAnotherRows.rows(listOf(item("p:1"), item("p:2", current = true), item("p:3")), TextTrackKind.Subtitles)
        assertEquals("Best match", rows[0].bestMatchChip)
        assertNull(rows[1].bestMatchChip)
        assertNull(rows[0].inUseChip)
        assertEquals("In use", rows[1].inUseChip)
        assertEquals("Release.One", rows[0].title)
        // One language in the list: no language in the meta line.
        assertNull(rows[0].meta)
    }

    @Test
    fun aListWithSeveralLanguagesNamesThemInTheMetaLine() {
        val rows = FindAnotherRows.rows(listOf(item("p:1", language = "en"), item("p:2", language = "fr")), TextTrackKind.Subtitles)
        assertEquals("English", rows[0].meta)
        assertEquals("French", rows[1].meta)
    }

    @Test
    fun aRowWithoutAReleaseNameIsUntitled() {
        assertEquals("Untitled result", FindAnotherRows.rows(listOf(item(release = " ")), TextTrackKind.Subtitles)[0].title)
    }

    @Test
    fun theWordsOfARowReadByTheDwellComeBackIntoItsPlace() {
        val first = item("p:1")
        val second = item("p:2")
        val read = item("p:2", lines = listOf(TextTrackPreviewLine(1.0, "Hi")), check = "Fits", difference = 0.0)
        val merged = FindAnotherRows.withRead(listOf(first, second), read)
        assertEquals(listOf("p:1", "p:2"), merged.map { it.key })
        assertTrue(merged[1].hasWords)
        assertEquals(1, FindAnotherRows.rows(merged, TextTrackKind.Subtitles)[0].checks.size)
        assertEquals(2, FindAnotherRows.rows(merged, TextTrackKind.Subtitles)[1].checks.size)
    }

    @Test
    fun thePaneSaysReadingThenCouldNotReadThenPromptsAndShowsWordsWhenItHasThem() {
        val unread = item()
        assertEquals(FindAnotherPane.Reading, FindAnotherRows.pane(unread, reading = true, failed = false, full = false))
        assertEquals(FindAnotherPane.CouldNotRead, FindAnotherRows.pane(unread, reading = false, failed = true, full = false))
        assertTrue(FindAnotherRows.pane(unread, reading = false, failed = false, full = false) is FindAnotherPane.Prompt)
        val lines = List(30) { TextTrackPreviewLine(it.toDouble(), "line $it") }
        val read = item(lines = lines)
        assertEquals(30, (FindAnotherRows.pane(read, reading = false, failed = false, full = false) as FindAnotherPane.Lines).lines.size)
        assertEquals(20, (FindAnotherRows.pane(read, reading = false, failed = false, full = true) as FindAnotherPane.Lines).lines.size)
    }

    @Test
    fun aPaneLineShowsTheTimeOnlyForSubtitles() {
        assertEquals("1:02  Hello", FindAnotherWording.paneLine(TextTrackPreviewLine(62.0, "Hello")))
        assertEquals("Hello", FindAnotherWording.paneLine(TextTrackPreviewLine(null, "Hello")))
    }

    @Test
    fun theLanguageListStartsWithTheProfilesLanguagesAndNeverRepeatsOne() {
        val choices = FindAnotherLanguages.choices("Japanese", listOf("jpn", "eng", null, "und"))
        assertEquals(listOf("Japanese", "English"), choices.take(2))
        assertEquals(choices.size, choices.map { it.lowercase() }.distinct().size)
        assertTrue(choices.size <= FindAnotherLanguages.MAX_CHOICES)
    }

    @Test
    fun languageCodesAreShownAsEnglishNames() {
        assertEquals("English", FindAnotherLanguages.nameFor("eng"))
        assertEquals("Portuguese", FindAnotherLanguages.nameFor("pt-BR"))
        assertEquals("Elvish", FindAnotherLanguages.nameFor("Elvish"))
        assertEquals("Unknown", FindAnotherLanguages.nameFor("und"))
    }
}

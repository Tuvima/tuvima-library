package com.tuvima.library.core

import org.json.JSONArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LyricsFollowTest {
    @Test
    fun aFileWithTimesIsSyncedAndKeepsEachLinesTime() {
        val parsed = LyricsFollow.parse("[00:12.50]First line\n[00:20.00]Second line\n")

        assertTrue(parsed.synced)
        assertEquals(listOf(12.5, 20.0), parsed.lines.map { it.atSeconds })
        assertEquals(listOf("First line", "Second line"), parsed.lines.map { it.text })
        assertEquals("Synced", LyricsFollow.chip(parsed))
    }

    @Test
    fun aFileWithoutTimesIsPlainWordsOneLinePerRow() {
        val parsed = LyricsFollow.parse("Line one\r\n\r\nLine two\r\n")

        assertFalse(parsed.synced)
        assertEquals(listOf("Line one", "Line two"), parsed.lines.map { it.text })
        assertTrue(parsed.lines.all { it.atSeconds == null })
        assertEquals("Unsynced", LyricsFollow.chip(parsed))
    }

    @Test
    fun tagLinesAreLeftOutOfSyncedAndPlainFiles() {
        val synced = LyricsFollow.parse("[ar:Some Artist]\n[ti:Some Title]\n[offset:0]\n[00:05.00]Hello")
        val plain = LyricsFollow.parse("[ar:Some Artist]\nHello\n[length: 03:20]")

        assertEquals(listOf("Hello"), synced.lines.map { it.text })
        assertEquals(listOf("Hello"), plain.lines.map { it.text })
    }

    @Test
    fun wordStampsInsideALineAreDropped() {
        val parsed = LyricsFollow.parse("[00:10.00]<00:10.00>Hel<00:10.40>lo <00:11.00>world")

        assertEquals(listOf("Hel" + "lo world"), parsed.lines.map { it.text })
    }

    @Test
    fun aLineWithSeveralTimeTagsAppearsAtEachTimeInTimeOrder() {
        val parsed = LyricsFollow.parse("[01:00.00][00:10.00]Chorus\n[00:30.00]Verse")

        assertEquals(listOf(10.0, 30.0, 60.0), parsed.lines.map { it.atSeconds })
        assertEquals(listOf("Chorus", "Verse", "Chorus"), parsed.lines.map { it.text })
    }

    @Test
    fun linesWithTheSameTimeKeepTheFilesOrder() {
        val parsed = LyricsFollow.parse("[00:10.00]B\n[00:05.00]A\n[00:10.00]C")

        assertEquals(listOf("A", "B", "C"), parsed.lines.map { it.text })
    }

    @Test
    fun timeStampsReadWholeSecondsAndTwoOrThreeFractionDigits() {
        val parsed = LyricsFollow.parse("[1:05]Whole\n[00:01.5]Tenths\n[00:02.250]Thousandths\n[00:03:50]Colon")

        assertEquals(listOf(5.0 + 60.0, 1.5, 2.25, 3.5).sorted(), parsed.lines.map { it.atSeconds!! }.sorted())
    }

    @Test
    fun aTimedLineWithNoWordsShowsANoteSign() {
        val parsed = LyricsFollow.parse("[00:05.00]\n[00:09.00]Words")

        assertEquals("♪", LyricsFollow.displayText(parsed.lines[0]))
        assertEquals("Words", LyricsFollow.displayText(parsed.lines[1]))
    }

    @Test
    fun anEmptyOrMissingFileHasNoLines() {
        assertTrue(LyricsFollow.parse("").lines.isEmpty())
        assertTrue(LyricsFollow.parse(null).lines.isEmpty())
    }

    @Test
    fun aByteOrderMarkDoesNotHideTheFirstTimeTag() {
        val parsed = LyricsFollow.parse("﻿[00:01.00]Hi")

        assertTrue(parsed.synced)
    }

    private val lines = listOf(
        LyricLine(10.0, "A"),
        LyricLine(20.0, "B"),
        LyricLine(30.0, "C"),
    )

    @Test
    fun theCurrentLineIsTheLastOneAtOrBeforeThePosition() {
        assertEquals(0, LyricsFollow.currentIndex(lines, 10.0))
        assertEquals(0, LyricsFollow.currentIndex(lines, 19.9))
        assertEquals(1, LyricsFollow.currentIndex(lines, 20.0))
        assertEquals(2, LyricsFollow.currentIndex(lines, 999.0))
    }

    @Test
    fun noLineIsCurrentBeforeTheFirstTimeOrWithoutAPosition() {
        assertEquals(-1, LyricsFollow.currentIndex(lines, 3.0))
        assertEquals(-1, LyricsFollow.currentIndex(lines, null))
        assertEquals(-1, LyricsFollow.currentIndex(lines, Double.NaN))
    }

    @Test
    fun noLineIsCurrentWhenTheLyricsAreNotTimed() {
        val plain = LyricsFollow.parse("One\nTwo").lines

        assertEquals(-1, LyricsFollow.currentIndex(plain, 50.0))
    }

    @Test
    fun theLyricsTrackTheEngineMarksPreferredIsPicked() {
        val tracks = listOf(
            LyricsTrackInfo("sub", "Subtitles", true),
            LyricsTrackInfo("a", "Lyrics", false),
            LyricsTrackInfo("b", "lyrics", true),
        )

        assertEquals("b", LyricsFollow.pickTrack(tracks)?.id)
    }

    @Test
    fun withoutAPreferredLyricsTrackTheFirstLyricsTrackIsPickedAndSubtitlesAreNeverPicked() {
        assertEquals("a", LyricsFollow.pickTrack(listOf(LyricsTrackInfo("s", "Subtitles", true), LyricsTrackInfo("a", "Lyrics", false)))?.id)
        assertNull(LyricsFollow.pickTrack(listOf(LyricsTrackInfo("s", "Subtitles", true))))
        assertNull(LyricsFollow.pickTrack(emptyList()))
    }

    @Test
    fun theTrackListReaderKeepsIdKindAndPreferred() {
        val array = JSONArray("""[{"id":"t1","kind":"Lyrics","isPreferred":true},{"id":"t2","kind":"Subtitles"}]""")

        assertEquals(
            listOf(LyricsTrackInfo("t1", "Lyrics", true), LyricsTrackInfo("t2", "Subtitles", false)),
            parseLyricsTrackList(array),
        )
    }

    @Test
    fun onlyMusicGetsALyricsPanel() {
        assertTrue(LyricsFollow.isMusic("Music"))
        assertTrue(LyricsFollow.isMusic("Song"))
        assertFalse(LyricsFollow.isMusic("Audiobooks"))
        assertFalse(LyricsFollow.isMusic("Movie"))
        assertFalse(LyricsFollow.isMusic(null))
    }

    @Test
    fun selectOnTheFooterOpensFindLyricsOnlyWhenItIsShown() {
        assertEquals(LyricsPanelEffect.FindLyrics, LyricsFollow.panelKey(FindAnotherKey.Select, canFind = true))
        assertEquals(LyricsPanelEffect.None, LyricsFollow.panelKey(FindAnotherKey.Select, canFind = false))
    }

    @Test
    fun backAndLeftCloseThePanelAndTheArrowsDoNothing() {
        assertEquals(LyricsPanelEffect.Close, LyricsFollow.panelKey(FindAnotherKey.Back, canFind = true))
        assertEquals(LyricsPanelEffect.Close, LyricsFollow.panelKey(FindAnotherKey.Left, canFind = false))
        assertEquals(LyricsPanelEffect.None, LyricsFollow.panelKey(FindAnotherKey.Up, canFind = true))
        assertEquals(LyricsPanelEffect.None, LyricsFollow.panelKey(FindAnotherKey.Down, canFind = true))
    }

    @Test
    fun lyricsSearchesNeverSendALanguageButSubtitlesDo() {
        assertNull(languageFor(TextTrackKind.Lyrics, "English"))
        assertEquals("English", languageFor(TextTrackKind.Subtitles, "English"))
    }

    @Test
    fun theTrackListAndFileRoutesSitUnderTheAssetsTextTracks() {
        assertEquals("/api/v1/stream/abc/text-tracks", ClientRoutes.textTracks("abc"))
        assertEquals("/api/v1/stream/abc/text-tracks/t1", ClientRoutes.textTrackFile("abc", "t1"))
    }
}

package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PlaybackTrackPickerTest {
    private fun audio(i: Int, lang: String?, default: Boolean = false) = AudioTrack(i, lang, "aac", null, default, 2)
    private fun sub(i: Int, lang: String?, default: Boolean = false, forced: Boolean = false) =
        SubtitleTrack(i, lang, "srt", null, default, forced, null)

    private val audios = listOf(audio(0, "eng", true), audio(1, "jpn"))
    private val subs = listOf(sub(0, "eng"), sub(1, "jpn"))

    @Test
    fun preferredAudioLanguageWins() {
        assertEquals(1, PlaybackTrackPicker.pick(audios, subs, "ja", "en", ShowSubtitles.Auto).audioIndex)
    }

    @Test
    fun fallsBackToDefaultThenFirst() {
        assertEquals(0, PlaybackTrackPicker.pick(audios, subs, "fr", "en", ShowSubtitles.Auto).audioIndex)
        val noDefault = listOf(audio(3, "eng"), audio(4, "jpn"))
        assertEquals(3, PlaybackTrackPicker.pick(noDefault, subs, null, null, ShowSubtitles.Auto).audioIndex)
    }

    @Test
    fun autoShowsSubtitlesOnlyWhenLanguagesDiffer() {
        assertEquals(0, PlaybackTrackPicker.pick(audios, subs, "ja", "en", ShowSubtitles.Auto).subtitleIndex)
        assertNull(PlaybackTrackPicker.pick(audios, subs, "en", "en", ShowSubtitles.Auto).subtitleIndex)
    }

    @Test
    fun alwaysAndOff() {
        assertEquals(0, PlaybackTrackPicker.pick(audios, subs, "en", "en", ShowSubtitles.Always).subtitleIndex)
        assertNull(PlaybackTrackPicker.pick(audios, subs, "ja", "en", ShowSubtitles.Off).subtitleIndex)
    }

    @Test
    fun forcedTrackIsLastChoiceInPreferredLanguage() {
        val tracks = listOf(sub(0, "eng", forced = true), sub(1, "eng"))
        assertEquals(1, PlaybackTrackPicker.pick(audios, tracks, "ja", "en", ShowSubtitles.Auto).subtitleIndex)
    }

    @Test
    fun noTracksGivesNothing() {
        val choice = PlaybackTrackPicker.pick(emptyList(), emptyList(), "en", "en", ShowSubtitles.Always)
        assertNull(choice.audioIndex)
        assertNull(choice.subtitleIndex)
    }
}

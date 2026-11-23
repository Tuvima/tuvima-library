package com.tuvima.library.core

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Compares the television player with the shared contract file itself (`clients/contracts/v1/player-controls.md`), so the app
 * cannot drift from the words, order and defaults written there. The same checks as `tests/native/tv-web-player.test.js`.
 */
class TvPlayerContractTest {
    private val contract: List<String> by lazy {
        var dir: File? = File("").absoluteFile
        var text: String? = null
        while (dir != null && text == null) {
            val candidate = File(dir, "clients/contracts/v1/player-controls.md")
            if (candidate.isFile) text = candidate.readText()
            dir = dir.parentFile
        }
        (text ?: error("clients/contracts/v1/player-controls.md was not found above ${File("").absolutePath}")).lines().map { it.trimEnd('\r') }
    }

    /** The rows of one marked table as cells, without the header and separator. */
    private fun table(name: String): List<List<String>> {
        val start = contract.indexOfFirst { it.trim() == "<!-- contract-table: $name -->" }
        assertTrue("the contract has no table $name", start >= 0)
        val rows = mutableListOf<List<String>>()
        for (i in start + 1 until contract.size) {
            val line = contract[i]
            if (!line.startsWith("|")) {
                if (rows.isNotEmpty()) break else continue
            }
            rows += line.split("|").drop(1).dropLast(1).map { it.trim() }
        }
        return rows.drop(2)
    }

    private val skip = TvSkip(10, 30)

    private fun assertTransport(table: String, kind: TvMediaKind) {
        val rows = table(table)
        // Everything that can be dimmed is dimmed when there is nothing before or after (a song is at its very start).
        val built = TvPlayerLogic.transport(kind, skip, playing = false, canPrevious = false, canNext = false)
        assertEquals(rows.size, built.size)
        built.forEachIndexed { i, control ->
            assertEquals("$table row ${i + 1} control", rows[i][1], control.key)
            assertEquals("$table row ${i + 1} label", rows[i][2], control.label)
            assertEquals("$table row ${i + 1} dimmed", rows[i][3] == "yes", control.dimmed)
        }
    }

    @Test
    fun theTransportRowsAreTheContractTransportTables() {
        assertTransport("transport-tv", TvMediaKind.Episode)
        assertTransport("transport-film", TvMediaKind.Film)
        assertTransport("transport-film", TvMediaKind.Lesson)
        assertTransport("transport-audiobook", TvMediaKind.Audiobook)
        assertTransport("transport-music", TvMediaKind.Music)
    }

    // The tool row is the Dashboard's tools without Volume, Fullscreen, Close and Picture in picture.
    private fun dashboardTools(name: String): List<String> =
        table(name).map { it[2] }.filter { it !in listOf("Volume", "Fullscreen", "Close", "Picture in picture") }

    @Test
    fun theToolRowsAreTheContractToolTablesWithoutWhatATelevisionOwns() {
        assertEquals(dashboardTools("tools-tv"), TvPlayerLogic.tools(TvMediaKind.Episode, hasEpisodes = true, canBookmark = true).map { it.label })
        assertEquals(
            listOf("Episodes", "Audio & subtitles", "Bookmarks", "Playback settings"),
            dashboardTools("tools-tv"),
        )
        assertEquals(dashboardTools("tools-film"), TvPlayerLogic.tools(TvMediaKind.Film, hasEpisodes = true, canBookmark = true).map { it.label })
        assertEquals(dashboardTools("tools-lesson"), TvPlayerLogic.tools(TvMediaKind.Lesson, hasEpisodes = true, canBookmark = true).map { it.label })
        // Music and audiobooks have the Lyrics or Chapters tool of their own tables on a television.
        assertTrue("Lyrics" in dashboardTools("tools-music"))
        assertEquals(listOf("Lyrics"), TvPlayerLogic.tools(TvMediaKind.Music, hasEpisodes = false, canBookmark = false).map { it.label })
        assertTrue("Chapters" in dashboardTools("tools-audiobook"))
        assertEquals(listOf("Chapters"), TvPlayerLogic.tools(TvMediaKind.Audiobook, false, false, hasChapters = true).map { it.label })
    }

    @Test
    fun theSkipDefaultsAndChoicesAreTheContractSkipDefaultsTable() {
        val rows = table("skip-defaults").associateBy { it[0] }
        assertEquals(rows.getValue("Skip back")[1].toInt(), TvPlayerLogic.DEFAULT_SKIP_BACK)
        assertEquals(rows.getValue("Skip forward")[1].toInt(), TvPlayerLogic.DEFAULT_SKIP_FORWARD)
        assertEquals(rows.getValue("Skip back")[2].split(",").map { it.trim().toInt() }, TvPlayerLogic.SKIP_BACK_CHOICES)
        assertEquals(rows.getValue("Skip forward")[2].split(",").map { it.trim().toInt() }, TvPlayerLogic.SKIP_FORWARD_CHOICES)
        assertEquals(rows.getValue("Previous restarts a song after")[1].toDouble(), TvPlayerLogic.MUSIC_RESTART_SECONDS, 0.0)
    }

    @Test
    fun theSpeedPresetsAreTheContractSpeedPresetsTable() {
        val rows = table("speed-presets")
        assertEquals(rows.map { it[0].toDouble() }, TvPlayerLogic.SPEED_PRESETS)
        assertEquals(rows.map { it[1] }, TvPlayerLogic.SPEED_PRESETS.map { TvPlayerLogic.speedLabel(it) })
    }

    @Test
    fun theEndCardWordsAreTheContractEndCardTable() {
        val rows = table("end-card").associate { it[0] to it[1] }
        val card = TvPlayerLogic.endCard("Pet Show", TvMediaKind.Episode, 8)
        assertEquals(rows.getValue("Heading"), card.heading)
        assertEquals(rows.getValue("Countdown").replace("{0}", "8"), card.countdown)
        assertEquals(rows.getValue("Kind (TV)"), card.kind)
        assertEquals(rows.getValue("Kind (lesson)"), TvPlayerLogic.endCard("x", TvMediaKind.Lesson, null).kind)
        assertEquals(rows.getValue("Buttons"), card.cancel + "; " + card.playNow)
    }

    @Test
    fun everyTelevisionKeyRowHasTheDocumentedEffect() {
        val keys = table("tv-keys").map { it[0] }
        assertEquals(
            listOf(
                "Any key", "Left and Right with the timeline focused", "Left and Right elsewhere", "Up and Down", "Select",
                "Play/Pause", "Rewind", "Fast-forward", "Back",
            ),
            keys,
        )
        val shown = TvKeyState(overlayVisible = true)
        val hidden = TvKeyState(overlayVisible = false)
        // Any key: shows the overlay.
        assertEquals(TvKeyEffect.ShowOverlay, TvKeyMap.effect(hidden, TvKeyAction.Down).effect)
        // Left and Right with the timeline focused skip; elsewhere they move focus.
        val timeline = shown.copy(zone = TvZone.Timeline)
        assertEquals(TvKeyEffect.SkipBack, TvKeyMap.effect(timeline, TvKeyAction.Left).effect)
        assertEquals(TvKeyEffect.SkipForward, TvKeyMap.effect(timeline, TvKeyAction.Right).effect)
        assertEquals(TvKeyEffect.Move, TvKeyMap.effect(shown, TvKeyAction.Left).effect)
        assertEquals(TvKeyEffect.Move, TvKeyMap.effect(shown, TvKeyAction.Up).effect)
        assertEquals(TvKeyEffect.Move, TvKeyMap.effect(shown, TvKeyAction.Select).effect)
        assertEquals(TvKeyEffect.TogglePlay, TvKeyMap.effect(shown, TvKeyAction.PlayPause).effect)
        assertEquals(TvKeyEffect.SkipBack, TvKeyMap.effect(shown, TvKeyAction.Rewind).effect)
        assertEquals(TvKeyEffect.SkipForward, TvKeyMap.effect(shown, TvKeyAction.FastForward).effect)
        assertEquals(TvKeyEffect.ClosePanel, TvKeyMap.effect(shown.copy(panelOpen = true), TvKeyAction.Back).effect)
        assertEquals(TvKeyEffect.HideOverlay, TvKeyMap.effect(shown, TvKeyAction.Back).effect)
    }

    // Every fixed word the player uses is in the row of the Panels table that names its panel.
    private fun panelWords(panel: String): String {
        val row = table("panels").first { it[0] == panel }
        return row[3] + "; " + row[4]
    }

    private fun assertInPanel(panel: String, vararg words: String) {
        val text = panelWords(panel)
        words.forEach { assertTrue("\"$it\" is not in the contract's $panel row", text.contains(it)) }
    }

    @Test
    fun thePanelWordsAreTheContractPanelsTableWords() {
        assertInPanel("Episodes", TvPlayerWords.SEASON, TvPlayerWords.UP_NEXT, TvPlayerWords.PLAYING, TvPlayerWords.PAUSED, TvPlayerWords.AUTOPLAY, "{0} episodes")
        assertInPanel("Lessons", "{0} lessons", TvPlayerWords.UP_NEXT, TvPlayerWords.AUTOPLAY)
        assertInPanel(
            "Audio & subtitles", TvPlayerWords.AUDIO, TvPlayerWords.SUBTITLES, TvPlayerWords.DIRECTOR_COMMENTARY, TvPlayerWords.AUDIO_DESCRIPTION,
            "{0} · SDH", "{0} · Forced", TvPlayerWords.SUBTITLE_APPEARANCE, FindAnotherWording.FIND_SUBTITLES_ENTRY,
        )
        assertInPanel(
            "Subtitle appearance", TvPlayerWords.STYLE, TvPlayerWords.STYLE_DEFAULT, TvPlayerWords.STYLE_LARGE, TvPlayerWords.STYLE_CONTRAST,
            TvPlayerWords.BACKGROUND, TvPlayerWords.BACKGROUND_NONE, TvPlayerWords.BACKGROUND_SHADOW, TvPlayerWords.BACKGROUND_BOX,
            TvPlayerWords.RESET_TO_DEFAULT, TvPlayerWords.CHANGES_SAVED,
        )
        assertInPanel("Playback speed", TvPlayerWords.CURRENT_SPEED, TvPlayerWords.RESET_SPEED)
        assertInPanel(
            "Playback settings", TvPlayerWords.PLAYBACK_SPEED, TvPlayerWords.SKIP_BACK, TvPlayerWords.SKIP_FORWARD,
            TvPlayerWords.PLAYBACK_INFORMATION, TvPlayerWords.RESET_PREFERENCES,
        )
        assertInPanel(
            "Bookmarks", TvPlayerWords.BOOKMARK_AT, TvPlayerWords.MINE, TvPlayerWords.GLOBAL, TvPlayerWords.DELETE, TvPlayerWords.KEEP,
            TvPlayerWords.COULD_NOT_SAVE, TvPlayerWords.NO_GLOBAL, TvPlayerWords.NO_MINE, TvPlayerWords.SELECT_BOOKMARK, TvPlayerWords.SHOW_ON_TIMELINE,
        )
        assertInPanel(
            "Chapters", TvPlayerWords.CHAPTER_OF, TvPlayerWords.BOOK_COMPLETE, TvPlayerWords.ELAPSED_REMAINING, TvPlayerWords.FINISHED,
            TvPlayerWords.TIMELINE_SHOWS, TvPlayerWords.CHAPTER, TvPlayerWords.WHOLE_BOOK, TvPlayerWords.SELECT_CHAPTER,
        )
        assertInPanel("Lyrics", LyricsFollow.LOOKING, LyricsFollow.UNSYNCED, FindAnotherWording.FIND_LYRICS_ENTRY.removeSuffix("…"))
    }

    @Test
    fun theBuiltPanelsUseTheContractTitles() {
        val titles = table("panels").map { it[0] }
        val now = TvPlayingNow("a", true, 10.0, 100.0)
        assertTrue(TvPanels.episodes(emptyList(), 0, false, now, true).title in titles)
        assertTrue(TvPanels.episodes(emptyList(), 0, true, now, true).title in titles)
        assertTrue(TvPanels.audioSubtitles(emptyList(), emptyList(), false).title in titles)
        assertTrue(TvPanels.subtitleAppearance("Default", "Box").title in titles)
        assertTrue(TvPanels.bookmarks(emptyList(), TvBookmarkTab.Mine, 0.0, TvBookmarkView(), true).title in titles)
        assertTrue(TvPanels.playbackSettings(1.0, skip).title in titles)
        assertTrue(TvPanels.speed(1.0).title in titles)
        assertTrue(TvPanels.chapters(emptyList(), 0.0, null, false).title in titles)
    }
}

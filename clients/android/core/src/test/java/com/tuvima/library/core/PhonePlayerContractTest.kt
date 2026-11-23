package com.tuvima.library.core

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Compares the phone players with the shared contract file itself (`clients/contracts/v1/player-controls.md`), so the app cannot drift from the
 * order, words and buttons of its phone sections: the collapsed bar, the expanded transport, the tool row, the sleep timer and speed presets and
 * the lock-screen card. The television's tables are compared by `TvPlayerContractTest`.
 */
class PhonePlayerContractTest {
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

    /** "play-pause: Play, next: Next track" as ("play-pause" to "Play") pairs. */
    private fun pairs(cell: String): List<Pair<String, String>> =
        cell.split(", ").map { it.substringBefore(": ") to it.substringAfter(": ") }

    private val skip = TvSkip(10, 30)

    @Test
    fun theCollapsedBarHasTheContractButtonsForEachMedia() {
        val rows = table("phone-bar").associate { it[0] to pairs(it[1]) }

        fun built(kind: TvMediaKind) = PhonePlayerLogic.bar(kind, playing = false, skip = skip, canNext = true).map { it.key to it.label }

        assertEquals(rows.getValue("Music"), built(TvMediaKind.Music))
        assertEquals(rows.getValue("Audiobook"), built(TvMediaKind.Audiobook))
        assertEquals(rows.getValue("Video"), built(TvMediaKind.Film))
        assertEquals(rows.getValue("Video"), built(TvMediaKind.Episode))
    }

    @Test
    fun theBarIs56DpHighAndEveryPhoneControlIsAtLeast44Dp() {
        assertTrue(contract.any { it.contains("56 px high") })
        assertEquals(56, PhonePlayerLogic.BAR_HEIGHT_DP)
        assertEquals(44, PhonePlayerLogic.MIN_TARGET_DP)
    }

    @Test
    fun theExpandedTransportHasTheContractButtonsForMusicAndAudiobooks() {
        val rows = table("phone-transport").associate { it[0] to pairs(it[1]) }

        fun built(kind: TvMediaKind) =
            PhonePlayerLogic.transport(kind, skip, playing = false, canPrevious = true, canNext = true).map { it.key to it.label }

        assertEquals(rows.getValue("Music"), built(TvMediaKind.Music))
        assertEquals(rows.getValue("Audiobook"), built(TvMediaKind.Audiobook))
    }

    @Test
    fun videoOnAPhoneKeepsTheFiveTransportButtonsOfTheTelevisionEpisodeAndFilmTables() {
        fun contractRows(name: String) = table(name).map { it[1] to it[2] }

        fun built(kind: TvMediaKind) =
            PhonePlayerLogic.transport(kind, skip, playing = false, canPrevious = false, canNext = false).map { it.key to it.label }

        assertEquals(contractRows("transport-tv"), built(TvMediaKind.Episode))
        assertEquals(contractRows("transport-film"), built(TvMediaKind.Film))
        assertEquals(contractRows("transport-film"), built(TvMediaKind.Lesson))
    }

    @Test
    fun theToolRowIsTheContractPhoneToolsTableForEveryMedia() {
        val rows = table("phone-tools").associate { it[0] to pairs(it[1]) }

        fun tools(
            kind: TvMediaKind,
            hasChapters: Boolean = false,
            hasEpisodes: Boolean = false,
        ) = PhonePlayerLogic.tools(
            kind = kind,
            hasChapters = hasChapters,
            hasEpisodes = hasEpisodes,
            canBookmark = true,
            canFullscreen = true,
        ).map { it.key to it.label }

        assertEquals(rows.getValue("Music"), PhonePlayerLogic.tools(TvMediaKind.Music).map { it.key to it.label })
        assertEquals(rows.getValue("Audiobook"), tools(TvMediaKind.Audiobook, hasChapters = true))
        assertEquals(rows.getValue("Audiobook without chapters"), tools(TvMediaKind.Audiobook, hasChapters = false))
        assertEquals(rows.getValue("TV episode"), tools(TvMediaKind.Episode, hasEpisodes = true))
        assertEquals(rows.getValue("Film"), tools(TvMediaKind.Film))
        assertEquals(rows.getValue("Folder video or lesson"), tools(TvMediaKind.Lesson, hasEpisodes = true))
    }

    @Test
    fun aPhoneVideoHasNoFullscreenToolBecauseItsScreenIsAlreadyFullScreen() {
        // "A tool the media or the app does not support is absent": the contract's `can fullscreen` condition is off on a phone.
        TvMediaKind.entries.forEach { kind ->
            assertFalse(PhonePlayerLogic.tools(kind, hasEpisodes = true, canBookmark = true).any { it.key == "fullscreen" })
        }
    }

    @Test
    fun theSpeedPresetsAndSleepTimerPresetsAreTheContractTables() {
        assertEquals(table("speed-presets").map { it[1] }, TvPlayerLogic.SPEED_PRESETS.map { TvPlayerLogic.speedLabel(it) })

        val sleep = table("sleep-timer-presets")
        val built = SleepTimerLogic.choices(hasChapters = true)
        assertEquals(sleep.map { it[0] }, built.map { it.key })
        assertEquals(sleep.map { it[1] }, built.map { it.label })
    }

    @Test
    fun theBookmarkAndSleepTimerWordsAreInTheContractPanelsTable() {
        val panels = table("panels")
        fun words(panel: String): String = panels.first { it[0] == panel }.let { it[3] + "; " + it[4] }

        listOf(PhoneBookmarks.ADD_A_NOTE, PhoneBookmarks.SEARCH, PhoneBookmarks.EDIT, PhoneBookmarks.NAME, PhoneBookmarks.NOTE, PhoneBookmarks.SAVE, PhoneBookmarks.CANCEL, PhoneBookmarks.NO_MATCH)
            .forEach { assertTrue("\"$it\" is not in the contract's Bookmarks row", words("Bookmarks").contains(it)) }
        listOf(
            SleepTimerLogic.CUSTOM_LABEL, SleepTimerLogic.END_OF_CHAPTER_LABEL, SleepTimerLogic.PLUS_FIVE_LABEL, SleepTimerLogic.CANCEL_LABEL,
            SleepTimerLogic.RUNNING_NOTE, "Stopping in", "Stopping at the end of chapter {0}",
        ).forEach { assertTrue("\"$it\" is not in the contract's Sleep timer row", words("Sleep timer").contains(it)) }
        listOf(QueueLogic.HISTORY, QueueLogic.CLEAR, QueueRowAction.PlayNext.label, QueueRowAction.MoveUp.label, QueueRowAction.MoveDown.label, QueueRowAction.Remove.label, QueueLogic.PLAYING_FROM)
            .forEach { assertTrue("\"$it\" is not in the contract's Queue row", words("Queue").contains(it)) }
        assertTrue(words("Queue").contains(QueueLogic.UP_NEXT))
    }

    @Test
    fun theLockScreenCardIsTheContractLockScreenTable() {
        val rows = table("lock-screen").associateBy { it[0] }

        fun check(row: String, facts: LockScreenFacts) {
            val card = LockScreen.card(facts)
            val expected = rows.getValue(row)
            assertEquals("$row line 1", expected[1], card.title)
            assertEquals("$row line 2", expected[2], card.line2)
            assertEquals("$row actions", expected[3].split(", "), card.actions)
        }

        check("Music", LockScreenFacts(TvMediaKind.Music, "Stay a Little Longer", artist = "Maya Ellis", album = "Northern Lights"))
        check(
            "Audiobook",
            LockScreenFacts(TvMediaKind.Audiobook, "The Way Beyond", author = "Maya Ellis", chapterTitle = "Chapter 8", hasChapters = true),
        )
        check(
            "TV episode with a next episode",
            LockScreenFacts(TvMediaKind.Episode, "The First Wish", show = "Shimmer and Shine", season = 1, episode = 1, hasNext = true),
        )
        check("TV episode, the last one", LockScreenFacts(TvMediaKind.Episode, "The First Wish", show = "Shimmer and Shine", season = 1, episode = 1))
        check("Film", LockScreenFacts(TvMediaKind.Film, "The Glass Harbor", year = "2026"))
    }
}

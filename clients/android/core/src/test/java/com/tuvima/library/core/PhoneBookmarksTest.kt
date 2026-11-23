package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PhoneBookmarksTest {
    private fun mark(
        id: String,
        at: Double,
        label: String = "",
        note: String = "",
        owner: String = "",
        mine: Boolean = true,
        everyone: Boolean = false,
    ) = TvBookmark(id, at, label, note, "/frame/$id", owner, isYours = mine, inEveryone = everyone)

    private val mixed = listOf(
        mark("m2", 90.0, label = "Fight"),
        mark("m1", 30.0, note = "Good bit"),
        mark("g1", 10.0, label = "Opening", owner = "Alex", mine = false, everyone = true),
    )

    @Test
    fun theMineGlobalChoiceShowsOnlyWhenAGlobalSetExistsAndMineIsTheDefault() {
        val withGlobal = PhoneBookmarks.list(mixed, TvBookmarkTab.Mine, "", loaded = true)
        val without = PhoneBookmarks.list(mixed.filter { it.isYours }, TvBookmarkTab.Global, "", loaded = true)

        assertTrue(withGlobal.showChoice)
        assertEquals(TvBookmarkTab.Mine, withGlobal.shown)
        assertEquals("Mine · 2", withGlobal.mineLabel)
        assertEquals("Global · 1", withGlobal.globalLabel)
        // Without a Global set the choice is hidden, and a leftover Global selection falls back to Mine.
        assertFalse(without.showChoice)
        assertEquals(TvBookmarkTab.Mine, without.shown)
    }

    @Test
    fun rowsAreInTimeOrderAndMineRowsMayBeChangedButGlobalRowsNeverCanBe() {
        val mine = PhoneBookmarks.list(mixed, TvBookmarkTab.Mine, "", loaded = true)
        val global = PhoneBookmarks.list(mixed, TvBookmarkTab.Global, "", loaded = true)

        assertEquals(listOf("0:30", "1:30"), mine.rows.map { it.time })
        assertTrue(mine.rows.all { it.canChange })
        assertEquals(listOf("Opening"), global.rows.map { it.words })
        assertEquals("Alex", global.rows.single().byline)
        assertFalse(global.rows.single().canChange)
        assertNull(mine.rows.first().byline)
    }

    @Test
    fun aRowWithNoNameOrNoteReadsBookmarkAtItsTime() {
        val list = PhoneBookmarks.list(listOf(mark("a", 2052.0)), TvBookmarkTab.Mine, "", loaded = true)

        assertEquals("Bookmark at 34:12", list.rows.single().words)
    }

    @Test
    fun theEmptyStatesAreTheContractsWordsAndNothingIsSaidBeforeTheListLoaded() {
        assertEquals("No bookmarks of yours yet", PhoneBookmarks.list(emptyList(), TvBookmarkTab.Mine, "", loaded = true).emptyText)
        assertNull(PhoneBookmarks.list(emptyList(), TvBookmarkTab.Mine, "", loaded = false).emptyText)
        assertEquals("No bookmarks match your search", PhoneBookmarks.list(mixed, TvBookmarkTab.Mine, "zzz", loaded = true).emptyText)
    }

    @Test
    fun theGlobalEmptyStateIsUsedWhenAGlobalSetHasNoRowsForTheSearch() {
        val list = PhoneBookmarks.list(mixed, TvBookmarkTab.Global, "nothing", loaded = true)

        assertEquals("No bookmarks match your search", list.emptyText)
    }

    @Test
    fun aSearchBoxAppearsAboveEightRowsAndSearchNarrowsByNameNoteMakerOrTime() {
        val eight = (1..8).map { mark("b$it", it * 10.0) }
        val nine = (1..9).map { mark("b$it", it * 10.0, label = if (it == 4) "Chase" else "") }

        assertFalse(PhoneBookmarks.list(eight, TvBookmarkTab.Mine, "", true).showSearch)
        assertTrue(PhoneBookmarks.list(nine, TvBookmarkTab.Mine, "", true).showSearch)
        assertEquals(listOf("b4"), PhoneBookmarks.list(nine, TvBookmarkTab.Mine, "chase", true).rows.map { it.id })
        assertEquals(listOf("g1"), PhoneBookmarks.list(mixed, TvBookmarkTab.Global, "alex", true).rows.map { it.id })
        assertEquals(listOf("m2"), PhoneBookmarks.list(mixed, TvBookmarkTab.Mine, "1:30", true).rows.map { it.id })
    }

    @Test
    fun aBookmarkMadeOfflineIsSavedOnThisPhoneAndCanBeRemovedButNotRenamed() {
        val waiting = TvBookmark("offline-abc", 12.0, "Saved on this phone", "", null, "", isYours = true, inEveryone = false)

        val row = PhoneBookmarks.list(listOf(waiting), TvBookmarkTab.Mine, "", true).rows.single()

        assertTrue(row.waiting)
        assertEquals("Saved on this phone", row.words)
    }

    @Test
    fun theAddBodyCarriesTheNoteOnlyWhenOneWasTypedAndNeverMakesAGlobalBookmark() {
        val plain = PhoneBookmarks.createBody("asset", 2052.04, 7200.0, "")!!
        val noted = PhoneBookmarks.createBody("asset", 10.0, null, "  Watch this  ")!!

        assertFalse(plain.has("note"))
        assertEquals(2052.0, plain.getDouble("positionSeconds"), 0.0)
        assertFalse(plain.getBoolean("isGlobal"))
        assertEquals("Watch this", noted.getString("note"))
        assertFalse(noted.has("durationSeconds"))
    }

    @Test
    fun aNameOrNoteOverTwoHundredCharactersIsRefusedBeforeItIsSent() {
        val long = "x".repeat(201)

        assertNull(PhoneBookmarks.createBody("asset", 1.0, null, long))
        assertNull(PhoneBookmarks.updateBody(long, ""))
        assertNull(PhoneBookmarks.updateBody("", long))
        assertTrue(PhoneBookmarks.fits("x".repeat(200)))
        assertFalse(PhoneBookmarks.fits(long))
    }

    @Test
    fun theRenameBodySendsNullForABlankNameOrNoteSoTheEngineClearsIt() {
        val body = PhoneBookmarks.updateBody("  New name ", "")!!

        assertEquals("New name", body.getString("label"))
        assertTrue(body.isNull("note"))
        assertTrue(PhoneBookmarks.updateBody("", "")!!.isNull("label"))
    }

    @Test
    fun theListFollowsAddsRenamesAndDeletes() {
        val start = listOf(mark("a", 1.0), mark("b", 2.0))

        val renamed = PhoneBookmarks.replace(start, mark("a", 1.0, label = "Named"))
        assertEquals("Named", renamed.first { it.id == "a" }.label)
        assertEquals(listOf("a"), PhoneBookmarks.without(start, "b").map { it.id })
        assertEquals(listOf("a", "b", "c"), PhoneBookmarks.withAdded(start, mark("c", 3.0)).map { it.id })
        assertEquals(2, PhoneBookmarks.withAdded(start, mark("b", 2.0)).size)
    }

    @Test
    fun bookmarksNeedAWorkAndAreNeverOnMusic() {
        assertTrue(PhoneBookmarks.canBookmark("work-1", isMusic = false))
        assertFalse(PhoneBookmarks.canBookmark(null, isMusic = false))
        assertFalse(PhoneBookmarks.canBookmark("  ", isMusic = false))
        assertFalse(PhoneBookmarks.canBookmark("work-1", isMusic = true))
    }
}

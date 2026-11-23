package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SleepTimerLogicTest {
    private val minute = 60_000L

    @Test
    fun theChoicesAreFourLengthsThenEndOfChapterOnlyWithChaptersThenCustom() {
        assertEquals(
            listOf("15 min", "30 min", "45 min", "60 min", "End of chapter", "Custom duration…"),
            SleepTimerLogic.choices(hasChapters = true).map { it.label },
        )
        assertEquals(
            listOf("15 min", "30 min", "45 min", "60 min", "Custom duration…"),
            SleepTimerLogic.choices(hasChapters = false).map { it.label },
        )
    }

    @Test
    fun aTimedTimerPausesWhenItsTimeIsUpAndNotBefore() {
        val timer = SleepTimerLogic.after(15, nowMs = 1_000L)

        assertFalse(SleepTimerLogic.isDue(timer, nowMs = 1_000L + 14 * minute, positionSeconds = 0.0))
        assertTrue(SleepTimerLogic.isDue(timer, nowMs = 1_000L + 15 * minute, positionSeconds = 0.0))
    }

    @Test
    fun theLeadLineCountsDownAsStoppingIn() {
        val timer = SleepTimerLogic.after(15, nowMs = 0L)

        assertEquals("Stopping in 14:32", SleepTimerLogic.lead(timer, nowMs = 28_000L))
        assertEquals("14:32", SleepTimerLogic.chipLabel(timer, nowMs = 28_000L))
    }

    @Test
    fun plusFiveMinutesAddsFiveToWhatIsLeftAndStartsFiveWhenNoTimedTimerRuns() {
        val timer = SleepTimerLogic.after(10, nowMs = 0L)

        val more = SleepTimerLogic.plusFive(timer, nowMs = 4 * minute) as SleepTimer.After
        assertEquals(15 * minute, more.endsAtMs)

        val fresh = SleepTimerLogic.plusFive(SleepTimer.Off, nowMs = 7 * minute) as SleepTimer.After
        assertEquals(12 * minute, fresh.endsAtMs)
    }

    @Test
    fun endOfChapterStopsWhenTheChapterEndsAndNamesItsNumber() {
        val chapters = listOf(Chapter(0, "One", 0.0, 100.0), Chapter(1, "Two", 100.0, 250.0), Chapter(2, "Three", 250.0, null))

        val timer = SleepTimerLogic.endOfChapter(chapters, positionSeconds = 120.0, durationSeconds = 400.0)

        assertEquals(SleepTimer.EndOfChapter(2, 250.0), timer)
        assertEquals("Stopping at the end of chapter 2", SleepTimerLogic.lead(timer, nowMs = 0L))
        assertFalse(SleepTimerLogic.isDue(timer, nowMs = 0L, positionSeconds = 200.0))
        assertTrue(SleepTimerLogic.isDue(timer, nowMs = 0L, positionSeconds = 249.9))
    }

    @Test
    fun endOfChapterOfTheLastChapterEndsWithTheBook() {
        val chapters = listOf(Chapter(0, "One", 0.0, null))

        assertEquals(SleepTimer.EndOfChapter(1, 90.0), SleepTimerLogic.endOfChapter(chapters, 10.0, 90.0))
        assertEquals(SleepTimer.Off, SleepTimerLogic.endOfChapter(emptyList(), 10.0, 90.0))
    }

    @Test
    fun aCustomLengthIsWholeMinutesFromOneToTwelveHours() {
        assertEquals(25, SleepTimerLogic.parseCustomMinutes(" 25 "))
        assertNull(SleepTimerLogic.parseCustomMinutes("0"))
        assertNull(SleepTimerLogic.parseCustomMinutes("721"))
        assertNull(SleepTimerLogic.parseCustomMinutes("abc"))
        assertNull(SleepTimerLogic.parseCustomMinutes(""))
        assertEquals(SleepTimer.Off, SleepTimerLogic.after(0, 0L))
    }

    @Test
    fun noTimerHasNoLeadLineChipOrDueTime() {
        assertEquals("", SleepTimerLogic.lead(SleepTimer.Off, 0L))
        assertNull(SleepTimerLogic.chipLabel(SleepTimer.Off, 0L))
        assertNull(SleepTimerLogic.remainingMs(SleepTimer.Off, 0L))
        assertFalse(SleepTimerLogic.isDue(SleepTimer.Off, Long.MAX_VALUE, 1e9))
    }
}

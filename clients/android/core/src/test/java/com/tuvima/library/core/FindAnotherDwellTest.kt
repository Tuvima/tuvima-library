package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** A scheduler on a fake clock: nothing waits, a test moves time with [advance]. */
private class FakeDwellScheduler : DwellScheduler {
    private class Entry(val dueAt: Long, val task: () -> Unit) { var cancelled = false }

    private val entries = mutableListOf<Entry>()
    var now = 0L
        private set

    override fun schedule(delayMs: Long, task: () -> Unit): DwellCancel {
        val entry = Entry(now + delayMs, task)
        entries.add(entry)
        return DwellCancel { entry.cancelled = true }
    }

    val waiting: Int get() = entries.count { !it.cancelled }

    fun advance(ms: Long) {
        now += ms
        val due = entries.filter { !it.cancelled && it.dueAt <= now }.sortedBy { it.dueAt }
        due.forEach {
            it.cancelled = true
            it.task()
        }
        entries.removeAll { it.cancelled }
    }
}

class FindAnotherDwellTest {
    private val scheduler = FakeDwellScheduler()
    private val reads = mutableListOf<String>()
    private val dwell = FindAnotherDwell(scheduler, { reads.add(it) })

    @Test
    fun theBestMatchIsReadAtOnceWhenTheListOpens() {
        dwell.listOpened("a:1", hasWords = false)
        assertEquals(listOf("a:1"), reads)
    }

    @Test
    fun theBestMatchThatAlreadyHasWordsIsNotReadAgain() {
        dwell.listOpened("a:1", hasWords = true)
        assertTrue(reads.isEmpty())
        dwell.focusMoved("a:1", hasWords = true)
        scheduler.advance(5_000)
        assertTrue(reads.isEmpty())
    }

    @Test
    fun aRowIsReadOnlyAfterFocusRestsOnItForOneSecond() {
        dwell.focusMoved("b:2", hasWords = false)
        scheduler.advance(999)
        assertTrue(reads.isEmpty())
        scheduler.advance(1)
        assertEquals(listOf("b:2"), reads)
    }

    @Test
    fun scrollingQuicklyThroughTheRowsReadsNothing() {
        listOf("b:2", "c:3", "d:4", "e:5", "f:6", "g:7", "h:8", "i:9", "j:10").forEach {
            dwell.focusMoved(it, hasWords = false)
            scheduler.advance(300)
        }
        assertTrue(reads.isEmpty())
        // Only the row focus finally rests on is read, and only after its own second.
        scheduler.advance(699)
        assertTrue(reads.isEmpty())
        scheduler.advance(1)
        assertEquals(listOf("j:10"), reads)
    }

    @Test
    fun movingAwayCancelsTheWaitingRead() {
        dwell.focusMoved("b:2", hasWords = false)
        scheduler.advance(900)
        dwell.focusLeftList()
        assertEquals(0, scheduler.waiting)
        scheduler.advance(5_000)
        assertTrue(reads.isEmpty())
    }

    @Test
    fun aRowAlreadyReadInThisWindowIsNotReadAgain() {
        dwell.focusMoved("b:2", hasWords = false)
        scheduler.advance(1_000)
        dwell.focusMoved("c:3", hasWords = false)
        dwell.focusMoved("b:2", hasWords = false)
        assertEquals(0, scheduler.waiting)
        scheduler.advance(5_000)
        assertEquals(listOf("b:2"), reads)
    }

    @Test
    fun aReadThatFailedIsNotRetriedByFocusingTheRowAgain() {
        // The reader never hears about the failure: asking is what counts, so a failed row stays asked for.
        dwell.focusMoved("b:2", hasWords = false)
        scheduler.advance(1_000)
        dwell.focusMoved("b:2", hasWords = false)
        scheduler.advance(5_000)
        assertEquals(listOf("b:2"), reads)
        assertTrue(dwell.wasAsked("b:2"))
    }

    @Test
    fun selectReadsARowAtOnceWithoutWaitingForTheRest() {
        dwell.focusMoved("b:2", hasWords = false)
        scheduler.advance(300)
        dwell.readNow("b:2", hasWords = false)
        assertEquals(listOf("b:2"), reads)
        assertEquals(0, scheduler.waiting)
    }

    @Test
    fun selectOnARowAlreadyAskedForDoesNotReadItTwice() {
        dwell.listOpened("a:1", hasWords = false)
        dwell.readNow("a:1", hasWords = false)
        assertEquals(listOf("a:1"), reads)
    }

    @Test
    fun aRowThatAlreadyHasWordsIsNeverRead() {
        dwell.focusMoved("b:2", hasWords = true)
        assertEquals(0, scheduler.waiting)
        dwell.readNow("b:2", hasWords = true)
        assertTrue(reads.isEmpty())
    }

    @Test
    fun searchingAgainStartsOver() {
        dwell.listOpened("a:1", hasWords = false)
        dwell.reset()
        assertFalse(dwell.wasAsked("a:1"))
        dwell.listOpened("a:1", hasWords = false)
        assertEquals(listOf("a:1", "a:1"), reads)
    }

    @Test
    fun theRestIsOneSecondUnlessTheScreenSaysOtherwise() {
        assertEquals(1_000L, FindAnotherDwell.REST_MS)
    }
}

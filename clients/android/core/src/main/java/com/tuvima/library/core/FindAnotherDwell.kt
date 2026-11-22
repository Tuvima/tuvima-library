package com.tuvima.library.core

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** A scheduled piece of work that can be called off. */
fun interface DwellCancel {
    fun cancel()
}

/**
 * Runs [task] once after [delayMs]. Injected so the reading rule is tested without waiting: a test scheduler moves a fake
 * clock, the screens use [CoroutineDwellScheduler].
 */
fun interface DwellScheduler {
    fun schedule(delayMs: Long, task: () -> Unit): DwellCancel
}

/** The scheduler the screens use: waits on the given scope (the screen's own, so leaving the screen cancels it). */
class CoroutineDwellScheduler(private val scope: CoroutineScope) : DwellScheduler {
    override fun schedule(delayMs: Long, task: () -> Unit): DwellCancel {
        val job: Job = scope.launch {
            delay(delayMs)
            task()
        }
        return DwellCancel { job.cancel() }
    }
}

/**
 * The reading rule of the Find another window (spec "Find another on TV", D10). A source's daily allowance is limited, so
 * a subtitle file is read only when it is likely wanted:
 *
 *  - the best match (the first row) is read as soon as the list opens;
 *  - any other row is read only after focus has rested on it for [restMs] (one second);
 *  - moving away before that cancels the read, so scrolling quickly through ten rows reads nothing;
 *  - a row already asked for stays asked for in this window: it is never read twice, and a read that failed is not retried;
 *  - a row that already carries its words is never read.
 *
 * One reader serves one window; build a new one (or call [reset]) when the list is searched again. Not thread-safe:
 * call it from the screen's one thread (the main thread).
 */
class FindAnotherDwell(
    private val scheduler: DwellScheduler,
    private val read: (key: String) -> Unit,
    private val restMs: Long = REST_MS,
) {
    private val asked = HashSet<String>()
    private var pending: DwellCancel? = null

    /** The list has just opened: reads the best match at once (when it has no words yet). */
    fun listOpened(bestMatchKey: String?, hasWords: Boolean) {
        cancelPending()
        if (bestMatchKey != null) ask(bestMatchKey, hasWords)
    }

    /** Focus arrived on a row. The previous wait is called off; a row not yet asked for starts its wait. */
    fun focusMoved(key: String, hasWords: Boolean) {
        cancelPending()
        if (hasWords || key in asked) return
        pending = scheduler.schedule(restMs) {
            pending = null
            ask(key, hasWords = false)
        }
    }

    /** Focus left the list (to the pane, the footer, the Language button): the wait is called off. */
    fun focusLeftList() = cancelPending()

    /** Select on a row: the person asked for it, so a row never asked for is read at once instead of after the rest. */
    fun readNow(key: String, hasWords: Boolean) {
        cancelPending()
        ask(key, hasWords)
    }

    /** The list is being searched again (another language): everything starts over. */
    fun reset() {
        cancelPending()
        asked.clear()
    }

    /** True when the row has been asked for in this window (reading, read, or failed). */
    fun wasAsked(key: String): Boolean = key in asked

    private fun ask(key: String, hasWords: Boolean) {
        if (hasWords) {
            asked.add(key)
            return
        }
        if (asked.add(key)) read(key)
    }

    private fun cancelPending() {
        pending?.cancel()
        pending = null
    }

    companion object {
        /** One second at rest (Q149.6A). */
        const val REST_MS = 1_000L
    }
}

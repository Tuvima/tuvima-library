package com.tuvima.library.core

// The audiobook player's Sleep timer (spec native-apps WP10b, the contract's "Sleep timer presets" and the Sleep timer panel): four lengths,
// End of chapter (when the book has timed chapters) and a custom length. A timer runs on this phone only (nothing about it is sent to the
// Engine). Pure rules with the clock passed in, so the timing is checked without a phone.

/** What the timer is doing. */
sealed interface SleepTimer {
    data object Off : SleepTimer

    /** Pauses playback at [endsAtMs] (the phone's clock). */
    data class After(val endsAtMs: Long) : SleepTimer

    /** Pauses playback when the chapter [chapterNumber] (1-based) ends at [endSeconds] of the book. */
    data class EndOfChapter(val chapterNumber: Int, val endSeconds: Double) : SleepTimer
}

/** One row of the Sleep timer panel. [key] is a minute count, `end-of-chapter` or `custom`. */
data class SleepChoice(val key: String, val label: String)

object SleepTimerLogic {
    val PRESET_MINUTES = listOf(15, 30, 45, 60)
    const val END_OF_CHAPTER_KEY = "end-of-chapter"
    const val CUSTOM_KEY = "custom"
    const val END_OF_CHAPTER_LABEL = "End of chapter"
    const val CUSTOM_LABEL = "Custom duration…"
    const val PLUS_FIVE_LABEL = "+5 minutes"
    const val CANCEL_LABEL = "Cancel timer"
    const val RUNNING_NOTE = "Playback will pause automatically."
    const val STOPPING_IN = "Stopping in {0}"
    const val STOPPING_AT_END = "Stopping at the end of chapter {0}"

    private const val MINUTE_MS = 60_000L
    private const val PLUS_FIVE_MS = 5 * MINUTE_MS

    /** The longest custom length: 12 hours. */
    const val MAX_CUSTOM_MINUTES = 720

    /** The rows in the contract's order: 15, 30, 45 and 60 min, End of chapter (only with timed chapters), Custom duration…. */
    fun choices(hasChapters: Boolean): List<SleepChoice> = buildList {
        PRESET_MINUTES.forEach { add(SleepChoice(it.toString(), "$it min")) }
        if (hasChapters) add(SleepChoice(END_OF_CHAPTER_KEY, END_OF_CHAPTER_LABEL))
        add(SleepChoice(CUSTOM_KEY, CUSTOM_LABEL))
    }

    /** Starts a timer of [minutes], or Off for a length that is not a whole number of minutes from 1 to [MAX_CUSTOM_MINUTES]. */
    fun after(minutes: Int, nowMs: Long): SleepTimer =
        if (minutes in 1..MAX_CUSTOM_MINUTES) SleepTimer.After(nowMs + minutes * MINUTE_MS) else SleepTimer.Off

    /** The custom length the person typed: whole minutes from 1 to [MAX_CUSTOM_MINUTES], or null when it is not that. */
    fun parseCustomMinutes(text: String): Int? = text.trim().toIntOrNull()?.takeIf { it in 1..MAX_CUSTOM_MINUTES }

    /** End of chapter: the timer stops at the end of the chapter in play. Off when the chapter's end is not known. */
    fun endOfChapter(chapters: List<Chapter>, positionSeconds: Double, durationSeconds: Double?): SleepTimer {
        if (chapters.isEmpty()) return SleepTimer.Off
        val index = TvChapters.indexAt(chapters, positionSeconds)
        val end = TvChapters.endOf(chapters, index, durationSeconds) ?: return SleepTimer.Off
        return SleepTimer.EndOfChapter(index + 1, end)
    }

    /** "+5 minutes" sets a new timer at what is left plus five minutes. On an end-of-chapter timer, or with none running, it starts five minutes. */
    fun plusFive(timer: SleepTimer, nowMs: Long): SleepTimer = when (timer) {
        is SleepTimer.After -> SleepTimer.After(timer.endsAtMs.coerceAtLeast(nowMs) + PLUS_FIVE_MS)
        else -> SleepTimer.After(nowMs + PLUS_FIVE_MS)
    }

    /** Milliseconds left on a timed timer; null for any other. */
    fun remainingMs(timer: SleepTimer, nowMs: Long): Long? = (timer as? SleepTimer.After)?.let { (it.endsAtMs - nowMs).coerceAtLeast(0L) }

    /** True when playback must pause now. */
    fun isDue(timer: SleepTimer, nowMs: Long, positionSeconds: Double): Boolean = when (timer) {
        SleepTimer.Off -> false
        is SleepTimer.After -> nowMs >= timer.endsAtMs
        is SleepTimer.EndOfChapter -> positionSeconds >= timer.endSeconds - 0.25
    }

    /** "14:32" for a time left (an hour or more reads "1:02:03"). */
    fun clock(remainingMs: Long): String = TvPlayerLogic.formatTime((remainingMs.coerceAtLeast(0L) + 999L) / 1000.0)

    /** The panel's lead line while a timer runs: "Stopping in 14:32" or "Stopping at the end of chapter 8"; empty while Off. */
    fun lead(timer: SleepTimer, nowMs: Long): String = when (timer) {
        SleepTimer.Off -> ""
        is SleepTimer.After -> TvPlayerWords.fill(STOPPING_IN, clock(remainingMs(timer, nowMs) ?: 0L))
        is SleepTimer.EndOfChapter -> TvPlayerWords.fill(STOPPING_AT_END, timer.chapterNumber)
    }

    /** The small label on the chip while a timer runs; null while Off. */
    fun chipLabel(timer: SleepTimer, nowMs: Long): String? = when (timer) {
        SleepTimer.Off -> null
        is SleepTimer.After -> clock(remainingMs(timer, nowMs) ?: 0L)
        is SleepTimer.EndOfChapter -> END_OF_CHAPTER_LABEL
    }
}

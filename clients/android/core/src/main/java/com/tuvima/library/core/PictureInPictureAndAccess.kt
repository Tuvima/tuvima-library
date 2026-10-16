package com.tuvima.library.core

import java.util.Locale

/**
 * When the phone's video screen may shrink to a floating window instead of pausing, and what the window looks like.
 * Pure Kotlin so the rules are checked without a device.
 */
object PictureInPicturePolicy {
    /**
     * Enter only while a video is really playing: not while preparing, on an error, when paused, or when the next-episode
     * countdown is about to start another one. Phones without the feature keep the plain pause-on-Home rule.
     */
    fun shouldEnter(
        supported: Boolean,
        isPlaying: Boolean,
        isPreparing: Boolean,
        hasError: Boolean,
        countdownAboutToAdvance: Boolean,
    ): Boolean = supported && isPlaying && !isPreparing && !hasError && !countdownAboutToAdvance

    /** The widest and tallest shapes Android accepts for the window (2.39:1 to 1:2.39); an unknown size is 16:9. */
    fun aspectRatio(widthPx: Int, heightPx: Int): Pair<Int, Int> {
        if (widthPx <= 0 || heightPx <= 0) return 16 to 9
        val ratio = widthPx.toDouble() / heightPx
        return when {
            ratio > MAX_RATIO -> 239 to 100
            ratio < 1 / MAX_RATIO -> 100 to 239
            else -> widthPx to heightPx
        }
    }

    private const val MAX_RATIO = 2.39
}

/** Words and rules for a screen reader on the video screens. */
object VideoAccessibility {
    /** "45 seconds", "12 minutes", "1 hour 5 minutes": how a time is spoken. */
    fun spokenTime(ms: Long): String {
        val totalSeconds = (ms.coerceAtLeast(0L) / 1000L)
        val hours = totalSeconds / 3600
        val minutes = (totalSeconds % 3600) / 60
        val seconds = totalSeconds % 60
        return when {
            hours > 0 -> listOf(plural(hours, "hour"), if (minutes > 0) plural(minutes, "minute") else null).filterNotNull().joinToString(" ")
            minutes > 0 -> plural(minutes, "minute")
            else -> plural(seconds, "second")
        }
    }

    /** What the seek bar says: "12 minutes of 45 minutes". Without a length it says only where it is. */
    fun positionAnnouncement(positionMs: Long, durationMs: Long): String =
        if (durationMs > 0) "${spokenTime(positionMs)} of ${spokenTime(durationMs)}" else spokenTime(positionMs)

    /** Announced once, politely, when the next-episode card appears; the changing countdown is not read every second. */
    fun nextEpisodeAnnouncement(cardLine: String, countdownSeconds: Int?): String =
        if (countdownSeconds != null && countdownSeconds > 0) {
            "Next episode: $cardLine. Plays in $countdownSeconds seconds. Play now or cancel."
        } else {
            "Next episode: $cardLine. Play next."
        }

    /** Controls that hide themselves after a few seconds trap a screen reader, so they stay while touch exploration is on. */
    fun controlsMayAutoHide(touchExplorationEnabled: Boolean): Boolean = !touchExplorationEnabled

    /** Seek bar step for the accessibility "increase / decrease" actions. */
    fun seekStepMs(durationMs: Long, skipSeconds: Int): Long =
        (skipSeconds.coerceAtLeast(1) * 1000L).coerceAtMost(if (durationMs > 0) durationMs else Long.MAX_VALUE)

    private fun plural(value: Long, unit: String): String = "$value $unit" + if (value == 1L) "" else "s"
}

/** What a hardware key does on the video screen: Space and Media Play/Pause toggle, the arrows seek, Escape or Back closes. */
enum class VideoKeyCommand { None, TogglePlay, SeekBack, SeekForward, Close }

object VideoKeys {
    // android.view.KeyEvent codes, repeated here so the mapping can be tested on a plain JVM.
    const val KEYCODE_BACK = 4
    const val KEYCODE_DPAD_LEFT = 21
    const val KEYCODE_DPAD_RIGHT = 22
    const val KEYCODE_SPACE = 62
    const val KEYCODE_ESCAPE = 111
    const val KEYCODE_MEDIA_PLAY_PAUSE = 85
    const val KEYCODE_MEDIA_PLAY = 126
    const val KEYCODE_MEDIA_PAUSE = 127

    fun commandFor(keyCode: Int): VideoKeyCommand = when (keyCode) {
        KEYCODE_SPACE, KEYCODE_MEDIA_PLAY_PAUSE, KEYCODE_MEDIA_PLAY, KEYCODE_MEDIA_PAUSE -> VideoKeyCommand.TogglePlay
        KEYCODE_DPAD_LEFT -> VideoKeyCommand.SeekBack
        KEYCODE_DPAD_RIGHT -> VideoKeyCommand.SeekForward
        KEYCODE_ESCAPE, KEYCODE_BACK -> VideoKeyCommand.Close
        else -> VideoKeyCommand.None
    }

    /** Escape or Back closes the controls first when they are showing, and only then the screen. */
    fun closeAction(controlsVisible: Boolean): CloseStep = if (controlsVisible) CloseStep.HideControls else CloseStep.LeaveScreen
}

enum class CloseStep { HideControls, LeaveScreen }

/** The system's captioning choice wins over the profile's own when the person turned system captions on. */
object CaptionChoice {
    fun usesSystemStyle(systemCaptionsEnabled: Boolean): Boolean = systemCaptionsEnabled

    /** Locale-safe lower-case compare for the profile's size name. */
    fun sizeName(value: String?): String = value?.trim()?.lowercase(Locale.ROOT).orEmpty()
}

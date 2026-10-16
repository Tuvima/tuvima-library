package com.tuvima.library.core

import java.util.Locale

// Pure rules for the video screen's preferences, skip buttons, gestures and session safety. No Android types.

/** The profile's watching and subtitle settings, as the Engine's device route returns them. Languages are names such as English. */
data class PlaybackPreferences(
    val audioLanguage: String = "English",
    val subtitleLanguage: String = "English",
    val subtitleMode: ShowSubtitles = ShowSubtitles.Auto,
    val subtitleSize: String = "Medium",
    val subtitleBackground: Boolean = false,
    val skipBackSeconds: Int = 10,
    val skipForwardSeconds: Int = 30,
    val autoplayNextEpisode: Boolean = true,
    val resumePlayback: Boolean = true,
    val defaultPlaybackSpeed: Double = 1.0,
) {
    companion object {
        val Default = PlaybackPreferences()
        const val MAX_SKIP_SECONDS = 600
        const val MIN_SPEED = 0.25
        const val MAX_SPEED = 4.0

        fun modeFrom(value: String?): ShowSubtitles =
            ShowSubtitles.entries.firstOrNull { it.name.equals(value?.trim(), ignoreCase = true) } ?: ShowSubtitles.Auto
    }
}

enum class SkipKind { Intro, Credits }

/** A Skip button to show right now: [label] on the button, [targetSeconds] to jump to. */
data class SkipOffer(val kind: SkipKind, val label: String, val targetSeconds: Double)

/** Which skip button, if any, applies at [positionSeconds]. Unknown segment kinds are ignored; nothing is ever skipped automatically. */
fun segmentToOffer(
    positionSeconds: Double,
    segments: List<PlaybackSegment>,
    durationSeconds: Double? = null,
): SkipOffer? {
    for (segment in segments.sortedBy { it.startSeconds }) {
        val kind = when (segment.kind.trim().lowercase(Locale.ROOT)) {
            "intro" -> SkipKind.Intro
            "credits" -> SkipKind.Credits
            else -> continue
        }
        if (!segment.isSkippable || segment.reviewStatus.equals("rejected", ignoreCase = true)) continue
        val end = segment.endSeconds ?: durationSeconds ?: continue
        if (end <= segment.startSeconds) continue
        if (positionSeconds >= segment.startSeconds && positionSeconds < end) {
            return SkipOffer(kind, if (kind == SkipKind.Intro) "Skip Intro" else "Skip Credits", end)
        }
    }
    return null
}

enum class SlideKind { Brightness, Volume }

object VideoGestures {
    /** A double tap on the left half goes back, on the right half forward. Milliseconds, signed. */
    fun doubleTapSeekMs(x: Float, width: Float, backSeconds: Int, forwardSeconds: Int): Long =
        if (x < width / 2f) -backSeconds * 1000L else forwardSeconds * 1000L

    /** A vertical slide on the left half changes brightness, on the right half volume. */
    fun slideKind(x: Float, width: Float): SlideKind = if (x < width / 2f) SlideKind.Brightness else SlideKind.Volume

    /** Sliding up by the full screen height adds 100%. [totalDragPx] is positive downwards, as touch deltas are. */
    fun slideFraction(start: Float, totalDragPx: Float, heightPx: Float): Float {
        if (heightPx <= 0f) return start.coerceIn(0f, 1f)
        return (start - totalDragPx / heightPx).coerceIn(0f, 1f)
    }

    fun volumeIndex(fraction: Float, maxIndex: Int): Int =
        Math.round(fraction.coerceIn(0f, 1f) * maxIndex).coerceIn(0, maxIndex.coerceAtLeast(0))

    /** Brightness never goes fully dark, so the picture cannot vanish. */
    fun brightness(fraction: Float): Float = fraction.coerceIn(MIN_BRIGHTNESS, 1f)

    const val MIN_BRIGHTNESS = 0.02f
}

/** [KeepPlaying] is only for a video in its floating Picture-in-Picture window, which is visible and so allowed to play on. */
enum class CloseAction { Pause, StopAndClear, KeepPlaying }

/** Video never plays in the background: leaving for good ends it, leaving for the moment pauses it. */
object VideoSessionPolicy {
    /** Closing the screen (finishing) stops the player and clears the video; going to Home only pauses. */
    fun onScreenStop(isFinishing: Boolean, inPictureInPicture: Boolean = false): CloseAction = when {
        isFinishing -> CloseAction.StopAndClear
        // The floating window is on screen, so the video keeps playing in it; this is the one visible exception to "no background video".
        inPictureInPicture -> CloseAction.KeepPlaying
        else -> CloseAction.Pause
    }

    /**
     * The Picture-in-Picture window went away. Expanding it back to the full screen changes nothing (the screen is started
     * again); closing or dismissing it, with the screen not coming back, stops and clears the video exactly like Close.
     */
    fun onPictureInPictureGone(screenIsStarted: Boolean): CloseAction? =
        if (screenIsStarted) null else CloseAction.StopAndClear

    /** A paused video is never started by the notification, a headset button or the lock screen while no video screen is open. */
    fun shouldBlockPlay(currentIsVideo: Boolean, videoScreenAttached: Boolean, alreadyPlaying: Boolean): Boolean =
        currentIsVideo && !videoScreenAttached && !alreadyPlaying

    /** When a video reaches its end the player is cleared too. */
    fun clearAfterEnd(currentIsVideo: Boolean): Boolean = currentIsVideo
}

object SubtitleStyleRules {
    /** Fraction of the picture height, from the preference's size name. */
    fun sizeFraction(size: String?): Float = when (size?.trim()?.lowercase(Locale.ROOT)) {
        "small" -> 0.045f
        "large" -> 0.07f
        "extra large", "extralarge", "x-large" -> 0.09f
        else -> 0.0533f
    }

    /** The MIME type to declare for a sidecar subtitle file. SRT is declared as SubRip, everything else as WebVTT. */
    fun mimeFor(codec: String?, url: String?): String {
        val value = codec?.trim()?.lowercase(Locale.ROOT).orEmpty()
        val path = url?.substringBefore('?')?.lowercase(Locale.ROOT).orEmpty()
        return if (value == "srt" || value == "subrip" || path.endsWith(".srt")) "application/x-subrip" else "text/vtt"
    }
}

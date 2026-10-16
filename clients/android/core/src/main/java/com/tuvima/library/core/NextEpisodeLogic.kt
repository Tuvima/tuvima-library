package com.tuvima.library.core

import java.util.Locale

/** What the next-episode card does right now. */
enum class NextEpisodeOffer {
    /** No card. */
    None,

    /** The card shows the next title with a "Play next" button and no countdown. */
    Manual,

    /** The card shows the next title, an [NextEpisodeLogic.COUNTDOWN_SECONDS] second countdown, Play now and Cancel. */
    Countdown,
}

/**
 * When and how a finished episode offers the next one. Pure Kotlin so the timing is checked without a phone.
 *
 * The card appears at the start of a Credits segment when the Engine sent one, otherwise in the last
 * [FINAL_WINDOW_SECONDS] seconds, and always once the episode has ended. With autoplay on it counts down; with it off
 * (or after Cancel) it waits for a tap. Cancel only stops the countdown for that episode: the card never restarts one.
 */
object NextEpisodeLogic {
    const val FINAL_WINDOW_SECONDS = 15.0
    const val COUNTDOWN_SECONDS = 8

    /** Where the card begins: the first usable Credits segment's start, else [FINAL_WINDOW_SECONDS] before the end. */
    fun offerStartSeconds(durationSeconds: Double?, segments: List<PlaybackSegment>): Double? {
        val credits = segments
            .filter { it.kind.trim().lowercase(Locale.ROOT) == "credits" }
            .filter { !it.reviewStatus.equals("rejected", ignoreCase = true) && it.startSeconds >= 0.0 }
            .minByOrNull { it.startSeconds }
        if (credits != null) return credits.startSeconds
        val duration = durationSeconds?.takeIf { it > 0.0 } ?: return null
        return (duration - FINAL_WINDOW_SECONDS).coerceAtLeast(0.0)
    }

    fun nextEpisodeOffer(
        positionSeconds: Double,
        durationSeconds: Double?,
        segments: List<PlaybackSegment>,
        autoplay: Boolean,
        ended: Boolean = false,
        cancelled: Boolean = false,
    ): NextEpisodeOffer {
        val start = offerStartSeconds(durationSeconds, segments)
        val due = ended || (start != null && positionSeconds >= start)
        if (!due) return NextEpisodeOffer.None
        return if (autoplay && !cancelled) NextEpisodeOffer.Countdown else NextEpisodeOffer.Manual
    }

    /** The episode the card offers: the first one the Engine listed after the one in play. */
    fun nextOf(episodes: List<NextEpisode>, currentAssetId: String): NextEpisode? =
        episodes.firstOrNull { it.assetId != currentAssetId }

    /** "S1 E2 · Title" style line for the card and the Episodes panel: the label first, then the title. */
    fun cardLine(episode: NextEpisode): String {
        val label = OfflineLogic.episodeLabel(episode.seasonNumber, episode.episodeNumber)
        val title = episode.title.trim()
        return when {
            label != null && title.isNotEmpty() -> "$label · $title"
            label != null -> label
            title.isNotEmpty() -> title
            else -> "Next episode"
        }
    }

    /**
     * Whether the countdown may count down right now. Video never plays in the background, so the next episode must not
     * start while the screen is stopped (Home, screen off) or while the person has paused; an ended episode counts only
     * while the screen is showing.
     */
    fun shouldTick(started: Boolean, playing: Boolean, ended: Boolean): Boolean = started && (playing || ended)

    /** Seconds left on the countdown given how many whole seconds have passed; never below zero. */
    fun secondsLeft(elapsedSeconds: Int): Int = (COUNTDOWN_SECONDS - elapsedSeconds).coerceAtLeast(0)
}

/** The words for a next episode that cannot be played. */
object NextEpisodeMessages {
    const val NOT_DOWNLOADED = "The next episode isn't downloaded."
}

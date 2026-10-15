package com.tuvima.library.core

import java.io.IOException
import java.util.Locale

// Pure rules for the phone video screen and the streamed-progress heartbeat. Kept free of Android so plain JUnit checks them.

/** Which player a play request opens. */
object PlaybackRouting {
    /** True for movies, episodes and other video; music, audiobooks and anything unknown keep the audio path. */
    fun opensVideoScreen(entityType: String?): Boolean =
        !entityType.isNullOrBlank() && OfflineLogic.downloadKindFor(entityType) == DownloadKind.Video
}

/** Why a title would not play, in the three ways the screen words differently. */
enum class VideoFailure { Unavailable, Network, Other }

object VideoMessages {
    const val UNAVAILABLE = "This title is not available for this profile."
    const val NETWORK = "Can't reach your Tuvima server"
    const val OTHER = "This title could not be played."
    const val NOT_READY = "This title could not be prepared for playback."
    const val TIMED_OUT = "Preparing this title is taking too long. Try again in a moment."

    fun forFailure(failure: VideoFailure, detail: String? = null): String = when (failure) {
        VideoFailure.Unavailable -> UNAVAILABLE
        VideoFailure.Network -> NETWORK
        VideoFailure.Other -> detail?.takeIf(String::isNotBlank) ?: OTHER
    }
}

/** What the manifest check decided for one look at the Engine. */
sealed interface PreparingStep {
    data class Play(val path: String) : PreparingStep
    data object Wait : PreparingStep
    data class Failed(val message: String) : PreparingStep
    data object TimedOut : PreparingStep
}

/** The "preparing" wait: look every 2 seconds, up to 2 minutes, like the television app. */
object PreparingPoll {
    const val INTERVAL_MS = 2_000L
    const val MAX_ATTEMPTS = 60

    /** [attempt] counts from 1 for the first look. */
    fun next(manifest: PlaybackManifest, attempt: Int): PreparingStep {
        manifest.playablePath()?.let { return PreparingStep.Play(it) }
        if (manifest.hlsStatus != "preparing") return PreparingStep.Failed(failureText(manifest))
        return if (attempt >= MAX_ATTEMPTS) PreparingStep.TimedOut else PreparingStep.Wait
    }

    /** Plain words from the manifest's own warnings, then its conversion reason, then a general line. */
    fun failureText(manifest: PlaybackManifest): String =
        manifest.warnings.filter(String::isNotBlank).joinToString(" ").takeIf(String::isNotBlank)
            ?: manifest.conversionReason?.takeIf(String::isNotBlank)
            ?: VideoMessages.NOT_READY
}

object VideoErrors {
    /** A refusal (403, 404) is unavailable; a connection problem is the network; anything else is a general failure. */
    fun classify(error: Throwable): VideoFailure = when {
        error is TuvimaHttpException && (error.statusCode == 403 || error.statusCode == 404) -> VideoFailure.Unavailable
        error is TuvimaHttpException -> VideoFailure.Other
        error is IOException -> VideoFailure.Network
        error.cause != null && error.cause !== error -> classify(error.cause!!)
        else -> VideoFailure.Other
    }
}

/** What one streamed-progress report says. [positionSeconds] is the full length once the item has ended. */
data class HeartbeatReport(
    val assetId: String,
    val isPlaying: Boolean,
    val positionSeconds: Double,
    val durationSeconds: Double?,
    val playbackRate: Double,
)

object HeartbeatRules {
    const val INTERVAL_MS = 15_000L

    /** Downloaded items keep their own on-phone progress; only streamed items report to the Engine. */
    fun shouldSend(isLocalItem: Boolean, assetId: String?): Boolean = !isLocalItem && !assetId.isNullOrBlank()

    fun report(
        assetId: String,
        isPlaying: Boolean,
        positionMs: Long,
        durationMs: Long?,
        playbackRate: Double,
        ended: Boolean,
    ): HeartbeatReport {
        val duration = durationMs?.takeIf { it > 0 }?.let { it / 1000.0 }
        val position = if (ended && duration != null) duration else (positionMs.coerceAtLeast(0) / 1000.0)
        return HeartbeatReport(assetId, isPlaying && !ended, position, duration, playbackRate)
    }
}

object StreamResume {
    private const val FINISHED_FRACTION = 0.95

    /** Where a streamed item starts: the Engine's saved position, or the beginning when it was already (nearly) finished. */
    fun startMs(resumeSeconds: Double?, durationSeconds: Double?): Long {
        if (resumeSeconds == null || resumeSeconds <= 0.0) return 0L
        if (durationSeconds != null && durationSeconds > 0 && resumeSeconds >= durationSeconds * FINISHED_FRACTION) return 0L
        return (resumeSeconds * 1000).toLong()
    }
}

object ClockText {
    /** 83_000 becomes "1:23"; an hour or more reads "1:02:03". */
    fun format(ms: Long): String {
        val total = (ms.coerceAtLeast(0) / 1000)
        val hours = total / 3600
        val minutes = (total % 3600) / 60
        val seconds = total % 60
        return if (hours > 0) "%d:%02d:%02d".format(Locale.ROOT, hours, minutes, seconds) else "%d:%02d".format(Locale.ROOT, minutes, seconds)
    }
}

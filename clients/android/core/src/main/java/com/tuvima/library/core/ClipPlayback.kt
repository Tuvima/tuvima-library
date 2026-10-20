package com.tuvima.library.core

import java.io.IOException

/** The words the Photos clip player shows. */
object ClipMessages {
    const val OFFLINE = "Connect to your Library server to watch this video."
    const val MISSING = "This video is no longer available."
    const val OTHER = "This video could not be played."
}

/** What the clip screen shows when it cannot play: a plain message and whether Retry makes sense. */
data class ClipFailure(val message: String, val canRetry: Boolean)

/**
 * Pure rules for playing a personal video from Photos. A clip plays on its own player with no History, progress or
 * lock-screen session, so everything here is about getting from a View item id to a stream and explaining failures.
 */
object ClipPlayback {
    /** Speeds the clip player offers, in the order the speed button cycles through them. */
    val speeds: List<Float> = listOf(1f, 1.25f, 1.5f, 2f, 0.5f)

    /** The stream to play: the manifest's direct stream of the original. Null when the manifest has none. */
    fun streamUrl(manifest: PlaybackManifest): String? = manifest.directStreamUrl?.takeIf { it.isNotBlank() }

    /** The speed after [current] in [speeds]; an unknown speed goes back to normal. */
    fun nextSpeed(current: Float): Float {
        val index = speeds.indexOfFirst { kotlin.math.abs(it - current) < 0.01f }
        return if (index < 0) speeds.first() else speeds[(index + 1) % speeds.size]
    }

    /** "1.25x" / "2x": the label on the speed button. */
    fun speedLabel(speed: Float): String =
        (if (speed % 1f == 0f) speed.toInt().toString() else speed.toString().trimEnd('0')) + "x"

    /** Turns anything thrown while preparing or playing a clip into a plain message. Never leaves the screen black. */
    fun failureFor(error: Throwable): ClipFailure = when (error) {
        is PhotoAccessDeniedException -> ClipFailure(PHOTO_ACCESS_DENIED_MESSAGE, canRetry = false)
        is TuvimaHttpException -> when (error.statusCode) {
            403 -> ClipFailure(PHOTO_ACCESS_DENIED_MESSAGE, canRetry = false)
            404, 410 -> ClipFailure(ClipMessages.MISSING, canRetry = false)
            else -> ClipFailure(ClipMessages.OTHER, canRetry = true)
        }
        is IOException -> ClipFailure(ClipMessages.OFFLINE, canRetry = true)
        else -> ClipFailure(ClipMessages.OTHER, canRetry = true)
    }
}

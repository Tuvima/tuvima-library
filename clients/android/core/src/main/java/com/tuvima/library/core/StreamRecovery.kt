package com.tuvima.library.core

/** What the media service does when a streamed item fails. */
enum class StreamRecoveryDecision {
    /** Ask the Engine for a fresh playback manifest, swap the item in at the same place and try once more. */
    Recover,

    /** Leave the error as it is, so the screen shows its normal message with Retry. */
    GiveUp,
}

/**
 * Pure rules for getting a streamed video, song or audiobook going again after its short-lived access token (or the
 * stream grant tied to it) has expired part way through. Only streamed items recover, only on a 401 or 403, and only once
 * per item until it has played again; a second failure is shown with Retry as before.
 */
object StreamRecovery {
    /** Recovery attempts allowed for one item until it reaches the ready state again. */
    const val MAX_ATTEMPTS_PER_ITEM = 1

    /** How long a screen waits before showing a stream error that recovery might still fix. */
    const val ERROR_GRACE_MS = 4_000L

    fun isAuthFailure(httpStatus: Int?): Boolean = httpStatus == 401 || httpStatus == 403

    fun decide(httpStatus: Int?, isLocalItem: Boolean, attemptsSoFar: Int): StreamRecoveryDecision =
        if (isLocalItem || !isAuthFailure(httpStatus) || attemptsSoFar >= MAX_ATTEMPTS_PER_ITEM) {
            StreamRecoveryDecision.GiveUp
        } else {
            StreamRecoveryDecision.Recover
        }

    /**
     * A screen that sees a bad-HTTP-status error holds back its error message for [ERROR_GRACE_MS], because the service may
     * be swapping in a fresh stream. Any other error is shown at once.
     */
    fun defersErrorScreen(isBadHttpStatus: Boolean): Boolean = isBadHttpStatus

    /** Whether a recovered item should start playing again: only what was playing (a paused video stays paused). */
    fun resumePlaying(wasPlaying: Boolean, isVideo: Boolean): Boolean = wasPlaying || !isVideo
}

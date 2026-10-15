package com.tuvima.library.core

enum class RefreshFailureKind {
    /** The Engine no longer accepts this device's grant: sign out and pair again. */
    Revoked,

    /** A network error or a server problem: keep the sign-in and try again later. */
    Transient,
}

object SessionLogic {
    /**
     * Sorts a failed token refresh. [statusCode] is null when no answer arrived (no connection, timeout). `invalid_grant`, or a
     * 400, 401 or 403 answer to the refresh, means the device was revoked or its grant changed. Anything else (no answer,
     * 408, 429, 5xx) is transient and must never sign the person out.
     */
    fun classifyRefreshFailure(statusCode: Int?, errorCode: String?): RefreshFailureKind = when {
        errorCode.equals("invalid_grant", ignoreCase = true) -> RefreshFailureKind.Revoked
        statusCode == 400 || statusCode == 401 || statusCode == 403 -> RefreshFailureKind.Revoked
        else -> RefreshFailureKind.Transient
    }
}

package com.tuvima.library.core

enum class RefreshFailureKind {
    /** The Engine no longer accepts this device's grant: sign out and pair again. */
    Revoked,

    /** A network error or a server problem: keep the sign-in and try again later. */
    Transient,
}

object SessionLogic {
    /**
     * Sorts a failed token refresh. The Engine answers every failure at the token endpoint with 400, and every real
     * revocation carries the OAuth error `invalid_grant`, so only that exact code (parsed from the answer's JSON body) means
     * the device was revoked or its grant changed. Any other answer (a 400 without the code, a 401 or 403 from a proxy or
     * host filter, an HTML page, 408, 429, 5xx) and no answer at all ([statusCode] null) is transient and must never sign
     * the person out or delete their downloads.
     */
    fun classifyRefreshFailure(statusCode: Int?, errorCode: String?): RefreshFailureKind =
        if (errorCode == "invalid_grant") RefreshFailureKind.Revoked else RefreshFailureKind.Transient
}

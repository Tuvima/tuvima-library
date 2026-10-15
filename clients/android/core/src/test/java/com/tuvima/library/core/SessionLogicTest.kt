package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Test

/** Checks which failed refreshes sign the person out (only the exact `invalid_grant` code) and which are only a bad moment. */
class SessionLogicTest {
    @Test
    fun invalidGrantIsRevoked() {
        assertEquals(RefreshFailureKind.Revoked, SessionLogic.classifyRefreshFailure(400, "invalid_grant"))
    }

    @Test
    fun anythingWithoutTheExactCodeIsTransient() {
        // A proxy's HTML 401 or 403, a host filter's 400 and a bare 400 carry no OAuth code and must not sign anyone out.
        for (status in listOf(400, 401, 403, 404, 408, 429, 500, 502, 503, 504)) {
            assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(status, null))
            assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(status, ""))
        }
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(null, null))
    }

    @Test
    fun otherOAuthCodesAreTransient() {
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(400, "invalid_request"))
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(400, "invalid_client"))
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(503, "temporarily_unavailable"))
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(400, "INVALID_GRANT"))
    }
}

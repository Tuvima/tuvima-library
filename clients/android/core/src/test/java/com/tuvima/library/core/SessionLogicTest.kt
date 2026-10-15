package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Test

/** Checks which failed refreshes sign the person out and which are only a bad moment. */
class SessionLogicTest {
    @Test
    fun invalidGrantIsRevoked() {
        assertEquals(RefreshFailureKind.Revoked, SessionLogic.classifyRefreshFailure(400, "invalid_grant"))
        assertEquals(RefreshFailureKind.Revoked, SessionLogic.classifyRefreshFailure(null, "INVALID_GRANT"))
    }

    @Test
    fun clientErrorsOnRefreshAreRevoked() {
        for (status in listOf(400, 401, 403)) {
            assertEquals(RefreshFailureKind.Revoked, SessionLogic.classifyRefreshFailure(status, null))
        }
    }

    @Test
    fun networkAndServerProblemsAreTransient() {
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(null, null))
        for (status in listOf(408, 429, 500, 502, 503, 504)) {
            assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(status, null))
        }
        assertEquals(RefreshFailureKind.Transient, SessionLogic.classifyRefreshFailure(503, "temporarily_unavailable"))
    }
}

package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** Checks the token and epoch rules the client relies on, with no network: refresh, profile switch and revocation. */
class SessionHolderTest {
    private fun token(profile: String, refresh: String = "refresh-$profile") = ClientToken(
        accessToken = "access-$profile",
        refreshToken = refresh,
        expiresAtEpochSeconds = 4_000_000_000L,
        scope = "library.read",
        deviceId = "device-1",
        profileId = profile,
    )

    @Test
    fun startsWithTheStoredToken() {
        val holder = SessionHolder(token("a"))
        assertEquals("a", holder.current()?.profileId)
        assertEquals(ProfileBinding("a", 0), holder.binding())
        assertNull(SessionHolder(null).binding())
    }

    @Test
    fun refreshForTheSameProfileKeepsTheEpoch() {
        val holder = SessionHolder(token("a"))
        val before = holder.binding()
        holder.accept(token("a", refresh = "rotated"), before)
        assertEquals(before, holder.binding())
        assertEquals("rotated", holder.current()?.refreshToken)
    }

    @Test
    fun switchingProfileBumpsTheEpoch() {
        val holder = SessionHolder(token("a"))
        val before = holder.binding()!!
        holder.accept(token("b"))
        assertEquals("b", holder.binding()?.profileId)
        assertEquals(before.epoch + 1, holder.binding()?.epoch)
        holder.accept(token("a"))
        // Back on the first profile, yet the old binding no longer matches.
        assertTrue(holder.binding() != before)
    }

    @Test
    fun aRefreshResultForALeftProfileIsNotAccepted() {
        val holder = SessionHolder(token("a"))
        val requestedFor = holder.binding()
        holder.accept(token("b"))
        try {
            holder.accept(token("a", refresh = "late"), requestedFor)
            fail("expected ProfileChangedException")
        } catch (expected: ProfileChangedException) {
            // The new profile's token stands.
        }
        assertEquals("b", holder.current()?.profileId)
        assertEquals("refresh-b", holder.current()?.refreshToken)
    }

    @Test
    fun aRefusedTokenIsDroppedOnlyWhileItIsStillCurrent() {
        val a = token("a")
        val holder = SessionHolder(a)
        assertTrue(holder.clearIfCurrent(a))
        assertNull(holder.current())
    }

    @Test
    fun aRefusedTokenThatWasReplacedKeepsTheNewSession() {
        val a = token("a")
        val holder = SessionHolder(a)
        val b = token("b")
        holder.accept(b)
        assertFalse(holder.clearIfCurrent(a))
        assertSame(b, holder.current())
        assertNotNull(holder.binding())
    }

    @Test
    fun signOutAbortsBoundWork() {
        val holder = SessionHolder(token("a"))
        val guard = SyncGuard.start(holder::binding)!!
        assertTrue(guard.isBound())
        holder.clear()
        assertFalse(guard.isBound())
        assertNull(holder.current())
    }
}

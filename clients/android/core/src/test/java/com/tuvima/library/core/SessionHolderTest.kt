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
        var cleared = false
        assertTrue(holder.clearIfRevoked(holder.snapshot()!!) { cleared = true })
        assertTrue(cleared)
        assertNull(holder.current())
    }

    @Test
    fun aRefusedTokenThatWasReplacedKeepsTheNewSession() {
        val holder = SessionHolder(token("a"))
        val snapshot = holder.snapshot()!!
        val b = token("b")
        holder.accept(b)
        assertFalse(holder.clearIfRevoked(snapshot))
        assertSame(b, holder.current())
        assertNotNull(holder.binding())
    }

    @Test
    fun snapshotReadsTokenBindingAndGenerationTogether() {
        val a = token("a")
        val holder = SessionHolder(a)
        val snapshot = holder.snapshot()!!
        assertSame(a, snapshot.token)
        assertEquals(holder.binding(), snapshot.binding)
        assertNull(SessionHolder(null).snapshot())
    }

    @Test
    fun aRefusalWhileASwitchIsPendingIsTransient() {
        val holder = SessionHolder(token("a"))
        val snapshot = holder.snapshot()!!
        holder.beginSwitch()
        assertFalse(holder.mayTreatRefusalAsRevoked(snapshot))
        assertFalse(holder.clearIfRevoked(snapshot))
        assertEquals("a", holder.current()?.profileId)
    }

    @Test
    fun aRefusalAfterASwitchStartedDuringTheAttemptIsTransient() {
        val holder = SessionHolder(token("a"))
        val snapshot = holder.snapshot()!!
        holder.beginSwitch()
        holder.finishSwitch(token("b"))
        // The switch is over, but it happened during this attempt: the refusal of A's refresh token is expected.
        assertFalse(holder.mayTreatRefusalAsRevoked(snapshot))
        assertFalse(holder.clearIfRevoked(snapshot))
        assertEquals("b", holder.current()?.profileId)
    }

    @Test
    fun aRefusalWithNoSwitchAroundIsARealRevocation() {
        val holder = SessionHolder(token("a"))
        holder.beginSwitch()
        holder.finishSwitch(null)
        // A later attempt starts after the switch ended and sees the new generation.
        val snapshot = holder.snapshot()!!
        assertTrue(holder.mayTreatRefusalAsRevoked(snapshot))
        assertTrue(holder.clearIfRevoked(snapshot))
        assertNull(holder.current())
    }

    @Test
    fun aSwitchResultIsStoredAndSavedTogether() {
        val holder = SessionHolder(token("a"))
        var saved: ClientToken? = null
        holder.beginSwitch()
        assertTrue(holder.finishSwitch(token("b")) { saved = it })
        assertEquals("b", saved?.profileId)
        assertEquals("b", holder.current()?.profileId)
        assertEquals(1, holder.binding()?.epoch)
        // Nothing is pending any more.
        assertTrue(holder.mayTreatRefusalAsRevoked(holder.snapshot()!!))
    }

    @Test
    fun aSwitchResultIsNotStoredAfterASignOut() {
        val holder = SessionHolder(token("a"))
        var saved = false
        holder.beginSwitch()
        holder.clear()
        assertFalse(holder.finishSwitch(token("b")) { saved = true })
        assertFalse(saved)
        assertNull(holder.current())
        assertTrue(holder.snapshot() == null)
    }

    @Test
    fun aFailedSwitchStillEndsThePendingState() {
        val holder = SessionHolder(token("a"))
        holder.beginSwitch()
        assertFalse(holder.finishSwitch(null))
        assertTrue(holder.mayTreatRefusalAsRevoked(holder.snapshot()!!))
        assertEquals("a", holder.current()?.profileId)
    }

    @Test
    fun aLateSaveCannotUndoASignOut() {
        val holder = SessionHolder(token("a"))
        val requestedFor = holder.binding()
        var store: String? = "a"
        holder.clear { store = null }
        try {
            holder.accept(token("a", refresh = "late"), requestedFor) { store = it.profileId }
            fail("expected ProfileChangedException")
        } catch (expected: ProfileChangedException) {
            // Neither the session nor the store was touched.
        }
        assertNull(holder.current())
        assertNull(store)
    }

    @Test
    fun aFailedSaveLeavesTheSessionUnchanged() {
        val holder = SessionHolder(token("a"))
        try {
            holder.accept(token("a", refresh = "new")) { throw IllegalStateException("keystore") }
            fail("expected the save failure")
        } catch (expected: IllegalStateException) {
            // The session still holds the old token.
        }
        assertEquals("refresh-a", holder.current()?.refreshToken)
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

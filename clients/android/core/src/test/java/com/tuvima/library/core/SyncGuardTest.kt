package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks that a sync or download run stays bound to the person who started it. */
class SyncGuardTest {
    private class Session(var binding: ProfileBinding?) {
        fun current(): ProfileBinding? = binding
    }

    @Test
    fun sameProfileContinues() {
        val session = Session(ProfileBinding("profile-a", 1))
        val guard = SyncGuard.start(session::current)!!
        assertEquals("profile-a", guard.profileId)
        assertTrue(guard.isBound())
        // A token refresh keeps the profile and the epoch, so the run goes on.
        session.binding = ProfileBinding("profile-a", 1)
        assertTrue(guard.isBound())
    }

    @Test
    fun switchingProfileMidRunAborts() {
        val session = Session(ProfileBinding("profile-a", 1))
        val guard = SyncGuard.start(session::current)!!
        session.binding = ProfileBinding("profile-b", 2)
        assertFalse(guard.isBound())
    }

    @Test
    fun switchingAwayAndBackStillAborts() {
        val session = Session(ProfileBinding("profile-a", 1))
        val guard = SyncGuard.start(session::current)!!
        session.binding = ProfileBinding("profile-b", 2)
        session.binding = ProfileBinding("profile-a", 3)
        assertFalse(guard.isBound())
    }

    @Test
    fun signOutMidRunAborts() {
        val session = Session(ProfileBinding("profile-a", 1))
        val guard = SyncGuard.start(session::current)!!
        session.binding = null
        assertFalse(guard.isBound())
    }

    @Test
    fun nobodySignedInGivesNoGuard() {
        assertNull(SyncGuard.start { null })
    }

    @Test
    fun startForRefusesAnotherProfile() {
        val session = Session(ProfileBinding("profile-b", 4))
        assertNull(SyncGuard.startFor("profile-a", session::current))
        assertNull(SyncGuard.startFor("profile-a") { null })
        val guard = SyncGuard.startFor("profile-b", session::current)
        assertNotNull(guard)
        assertEquals(ProfileBinding("profile-b", 4), guard!!.binding)
    }
}

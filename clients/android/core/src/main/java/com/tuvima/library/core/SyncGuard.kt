package com.tuvima.library.core

import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.CoroutineContext

/**
 * Which person's session a piece of background work belongs to. [epoch] counts every profile change (and sign-out) on the
 * client, so a switch away and back again during one run is still seen as a change.
 */
data class ProfileBinding(val profileId: String, val epoch: Int)

/** Thrown by the client when work bound to one profile finds another profile (or nobody) signed in. */
class ProfileChangedException : Exception("The profile in use changed while this work was running.")

/** Thrown when the Engine refused to refresh the device's sign-in because the device was revoked or its grant changed. */
class SessionRevokedException : Exception("This device was signed out by the server. Connect it again.")

/**
 * Put in a coroutine context with `withContext(BoundProfile(binding))`: every client call made inside refuses to run (it
 * throws [ProfileChangedException]) once the client's current profile no longer matches [binding], so one run never
 * sends a request with a different person's token.
 */
class BoundProfile(val binding: ProfileBinding) : AbstractCoroutineContextElement(BoundProfile) {
    companion object Key : CoroutineContext.Key<BoundProfile>
}

/**
 * Binds one sync (or download) run to the person who was using the phone when it started. The run checks [isBound] before
 * each network call and again before it applies the answer; when it is false the run stops and marks, removes or purges
 * nothing, so one person's records never reach (or get cleaned against) another person's list.
 */
class SyncGuard private constructor(
    val binding: ProfileBinding,
    private val current: () -> ProfileBinding?,
) {
    val profileId: String get() = binding.profileId

    /** True while the same person, in the same session, is still signed in. */
    fun isBound(): Boolean = current() == binding

    companion object {
        /** Binds to whoever is signed in now; null when nobody is. */
        fun start(current: () -> ProfileBinding?): SyncGuard? = current()?.let { SyncGuard(it, current) }

        /** Binds to [expected] when that person is still the one signed in; null when another profile (or nobody) is. */
        fun startFor(expectedProfileId: String, current: () -> ProfileBinding?): SyncGuard? {
            val now = current() ?: return null
            return if (now.profileId == expectedProfileId) SyncGuard(now, current) else null
        }
    }
}

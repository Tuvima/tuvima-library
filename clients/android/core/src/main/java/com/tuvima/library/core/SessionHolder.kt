package com.tuvima.library.core

/**
 * The signed-in token, the profile change counter and the profile-switch bookkeeping, kept together under one lock so they
 * never disagree. The client stores and reads its token only through this holder, which makes the rules that protect one
 * person's session testable without a network:
 *
 * - a refresh result is accepted only for the session it was requested for, and is saved to the token store in the same
 *   critical section, so a late save can never undo a sign-out;
 * - a refused refresh signs the device out only when nothing suggests the refusal came from a profile switch. The Engine's
 *   switch revokes every token of the device, so a refresh sent with the previous profile's refresh token just before or
 *   after a switch is refused with `invalid_grant` even though the device is fine. While a switch is pending, or when one
 *   started or finished during the refresh attempt (the switch generation changed), the refusal is transient.
 */
class SessionHolder(initial: ClientToken?) {
    /** The token, its binding and the switch generation, read together in one step. */
    class Snapshot(val token: ClientToken, val binding: ProfileBinding, val switchGeneration: Int)

    private val lock = Any()
    private var token: ClientToken? = initial
    private var epoch = 0
    private var switchesPending = 0
    private var switchGeneration = 0

    fun current(): ClientToken? = synchronized(lock) { token }

    fun binding(): ProfileBinding? = synchronized(lock) { token?.let { ProfileBinding(it.profileId, epoch) } }

    /** The token, its binding and the switch generation in one atomic read; null when nobody is signed in. */
    fun snapshot(): Snapshot? = synchronized(lock) {
        token?.let { Snapshot(it, ProfileBinding(it.profileId, epoch), switchGeneration) }
    }

    /**
     * Stores [value] as the current token and runs [persist] (saving it to the token store) in the same critical section.
     * A change of profile bumps the epoch. When [expected] is given, nothing is stored (and [ProfileChangedException] is
     * thrown) unless the session still matches it. [persist] runs first, so a failure to save leaves the session unchanged.
     */
    fun accept(value: ClientToken, expected: ProfileBinding? = null, persist: (ClientToken) -> Unit = {}) {
        synchronized(lock) {
            if (expected != null && token?.let { ProfileBinding(it.profileId, epoch) } != expected) {
                throw ProfileChangedException()
            }
            persist(value)
            if (token?.profileId != value.profileId) epoch++
            token = value
        }
    }

    /** Drops the token (sign-out) and runs [onCleared] (clearing the token store) in the same critical section. */
    fun clear(onCleared: () -> Unit = {}) {
        synchronized(lock) {
            token = null
            epoch++
            onCleared()
        }
    }

    /** A profile switch request is about to be sent. */
    fun beginSwitch() {
        synchronized(lock) {
            switchesPending++
            switchGeneration++
        }
    }

    /**
     * The switch ends. With a [value], it is stored (and [persist] runs) unless the session was cleared while the switch
     * was in flight, in which case nothing is stored. Returns whether it was stored. Always ends the pending state and
     * changes the switch generation, so a refresh attempt that overlapped the switch can tell.
     */
    fun finishSwitch(value: ClientToken?, persist: (ClientToken) -> Unit = {}): Boolean = synchronized(lock) {
        try {
            if (value != null && token != null) {
                persist(value)
                if (token?.profileId != value.profileId) epoch++
                token = value
                true
            } else {
                false
            }
        } finally {
            switchesPending = (switchesPending - 1).coerceAtLeast(0)
            switchGeneration++
        }
    }

    /** True when a refusal of the refresh described by [snapshot] may be a real revocation (no switch involved). */
    fun mayTreatRefusalAsRevoked(snapshot: Snapshot): Boolean = synchronized(lock) {
        switchesPending == 0 && switchGeneration == snapshot.switchGeneration
    }

    /**
     * Drops the token (running [onCleared] in the same critical section) when the refusal of the refresh described by
     * [snapshot] is a real revocation: no switch is pending or happened since, and the refused token is still the current
     * one. Returns whether it was dropped; false means the refusal is transient. Atomic.
     */
    fun clearIfRevoked(snapshot: Snapshot, onCleared: () -> Unit = {}): Boolean = synchronized(lock) {
        if (switchesPending != 0 || switchGeneration != snapshot.switchGeneration || token !== snapshot.token) {
            false
        } else {
            token = null
            epoch++
            onCleared()
            true
        }
    }
}

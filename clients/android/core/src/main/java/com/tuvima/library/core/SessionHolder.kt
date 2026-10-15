package com.tuvima.library.core

/**
 * The signed-in token and the profile change counter, kept together under one lock so the two never disagree. The client
 * stores and reads its token only through this holder, which makes the rules that protect one person's session testable
 * without a network: a refresh result is accepted only for the session it was requested for, and a refused token is dropped
 * only if it is still the current one (a profile switch may have replaced it meanwhile).
 */
class SessionHolder(initial: ClientToken?) {
    private val lock = Any()
    private var token: ClientToken? = initial
    private var epoch = 0

    fun current(): ClientToken? = synchronized(lock) { token }

    fun binding(): ProfileBinding? = synchronized(lock) { token?.let { ProfileBinding(it.profileId, epoch) } }

    /**
     * Stores [value] as the current token. A change of profile bumps the epoch. When [expected] is given, the token is
     * stored only if the session still matches it; otherwise [ProfileChangedException] is thrown and nothing changes.
     */
    fun accept(value: ClientToken, expected: ProfileBinding? = null) {
        synchronized(lock) {
            if (expected != null && token?.let { ProfileBinding(it.profileId, epoch) } != expected) {
                throw ProfileChangedException()
            }
            if (token?.profileId != value.profileId) epoch++
            token = value
        }
    }

    /** Drops the token (sign-out). */
    fun clear() {
        synchronized(lock) {
            token = null
            epoch++
        }
    }

    /** Drops the token only when [refused] is still the current one; returns whether it was dropped. Atomic. */
    fun clearIfCurrent(refused: ClientToken): Boolean = synchronized(lock) {
        if (token !== refused) {
            false
        } else {
            token = null
            epoch++
            true
        }
    }
}

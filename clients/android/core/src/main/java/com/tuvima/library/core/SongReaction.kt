package com.tuvima.library.core

// Like and Dislike on the music title line of Now playing (contract player-controls, phone section). They use the Engine's existing profile-state
// reactions (`/api/v1/profile-state/reactions/{entityKind}/{entityId}/{reaction}`); nothing new is added to the wire. Pure Kotlin.

enum class SongReaction(val route: String, val label: String) {
    Like("like", "Like"),
    Dislike("dislike", "Dislike"),
    ;

    companion object {
        /** Reads the Engine's `reaction`, which arrives as a number (Like 0, Dislike 1) or, being lenient, as the name. Null when unknown. */
        fun fromWire(value: Any?): SongReaction? = when (value) {
            is Number -> entries.getOrNull(value.toInt())
            is String -> entries.firstOrNull { it.route.equals(value.trim(), ignoreCase = true) } ?: value.trim().toIntOrNull()?.let { entries.getOrNull(it) }
            else -> null
        }
    }
}

object ReactionLogic {
    /** What a press does: the same button again takes the reaction away, the other one switches to it. */
    fun after(current: SongReaction?, pressed: SongReaction): SongReaction? = if (current == pressed) null else pressed
}

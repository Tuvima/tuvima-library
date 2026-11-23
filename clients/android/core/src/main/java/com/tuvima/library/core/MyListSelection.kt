package com.tuvima.library.core

// My List's Select mode: tick several saved titles, then Remove them in one go (Undo still works as for a single removal). Immutable, so
// a screen holds the current value and swaps in the next one. Pure rules; nothing here draws anything or touches the network.

/**
 * The state of Select mode. [active] is false until the person chooses Select; [ids] are the cards ticked (by the card id the My List
 * grid gave). Leaving Select mode (Cancel, or after a removal) goes back to [Off].
 */
class MyListSelection private constructor(val active: Boolean, private val ids: Set<String>) {
    val count: Int get() = ids.size

    operator fun contains(cardId: String): Boolean = cardId in ids

    /** Enters Select mode with nothing ticked. */
    fun start(): MyListSelection = if (active) this else MyListSelection(true, emptySet())

    /** Leaves Select mode and forgets the ticks. */
    fun cancel(): MyListSelection = Off

    /** Ticks a card, or unticks it when it is ticked. Does nothing outside Select mode. */
    fun toggle(cardId: String): MyListSelection = when {
        !active || cardId.isBlank() -> this
        cardId in ids -> MyListSelection(true, ids - cardId)
        else -> MyListSelection(true, ids + cardId)
    }

    /** Ticks every card shown (the ones loaded so far). */
    fun selectAll(cardIds: List<String>): MyListSelection =
        if (!active) this else MyListSelection(true, cardIds.filter(String::isNotBlank).toSet())

    /** Unticks everything but stays in Select mode. */
    fun clear(): MyListSelection = if (!active || ids.isEmpty()) this else MyListSelection(true, emptySet())

    /** True when every card shown is ticked (and there is at least one). */
    fun allSelected(cardIds: List<String>): Boolean = cardIds.isNotEmpty() && cardIds.all { it in ids }

    /** Remove is offered only while something is ticked. */
    val canRemove: Boolean get() = active && ids.isNotEmpty()

    /** "Select titles" until something is ticked, then "1 selected", "12 selected". */
    fun label(): String = if (count == 0) PROMPT else "${"%,d".format(java.util.Locale.US, count)} selected"

    /**
     * The saved keys to take off My List: the ticked cards that are still shown, in the order shown, each once. A card the phone cannot
     * work a saved key out for (keyFor returns null) is left out rather than guessed.
     */
    fun keysToRemove(cards: List<DisplayCard>, keyFor: (DisplayCard) -> SavedKey?): List<SavedKey> =
        cards.filter { it.id in ids }.mapNotNull(keyFor).distinct()

    companion object {
        val Off = MyListSelection(false, emptySet())

        const val SELECT = "Select"
        const val CANCEL = "Cancel"
        const val SELECT_ALL = "Select all"
        const val CLEAR = "Clear"
        const val REMOVE = "Remove"
        const val PROMPT = "Select titles"
        const val SELECTED_MARK = "Selected"
        const val NOT_SELECTED_MARK = "Not selected"
    }
}

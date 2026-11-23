package com.tuvima.library.core

import kotlin.random.Random

// The music queue's order rules without a screen (spec native-apps WP10c): Shuffle (a random order after the song in play, and the way back to the
// original order), and the single moves that turn one order into another, so the media service can reorder its playlist with ordinary move calls.

/** One reordering step: the song at [from] goes to [to] (the list is shifted, the way `moveMediaItem` works). */
data class QueueMove(val from: Int, val to: Int)

object QueueShuffle {
    /**
     * Shuffle on: everything up to and including the song in play stays where it is, everything after it is put in a random order.
     * Nothing is lost or repeated; the result has the same songs.
     */
    fun shuffledOrder(ids: List<String>, currentIndex: Int, random: Random): List<String> {
        if (ids.size < 3) return ids
        val keep = (currentIndex + 1).coerceIn(0, ids.size)
        return ids.take(keep) + ids.drop(keep).shuffled(random)
    }

    /**
     * Shuffle off: the songs after the one in play go back to the order they had when the queue started. A song the original order does not know
     * (one added since) goes after the known ones, in the order it is in now.
     */
    fun restoredOrder(ids: List<String>, currentIndex: Int, original: List<String>): List<String> {
        val keep = (currentIndex + 1).coerceIn(0, ids.size)
        val rank = HashMap<String, Int>()
        original.forEachIndexed { i, id -> rank.putIfAbsent(id, i) }
        val rest = ids.drop(keep)
        // sortedBy is stable, so unknown songs (all ranked last) and repeats keep their present order.
        return ids.take(keep) + rest.sortedBy { rank[it] ?: Int.MAX_VALUE }
    }

    /** The moves that turn [from] into [to] (the same songs in another order); empty when the two do not hold the same songs. */
    fun moves(from: List<String>, to: List<String>): List<QueueMove> {
        if (from.size != to.size || from.sorted() != to.sorted()) return emptyList()
        val working = from.toMutableList()
        val result = mutableListOf<QueueMove>()
        for (i in to.indices) {
            if (working[i] == to[i]) continue
            val j = (i + 1 until working.size).firstOrNull { working[it] == to[i] } ?: return emptyList()
            working.add(i, working.removeAt(j))
            result += QueueMove(j, i)
        }
        return result
    }

    /** The list after the moves, for checking. */
    fun apply(list: List<String>, moves: List<QueueMove>): List<String> {
        val working = list.toMutableList()
        moves.forEach { working.add(it.to, working.removeAt(it.from)) }
        return working
    }

    /**
     * What a request with Shuffle on plays: the song it points at first, then every other song in a random order. The request comes back with
     * Shuffle still on and the start at the first song; [ListenQueue.originalIds] of the request as it came is what the service keeps to put the
     * order back.
     */
    fun arranged(request: PlayQueueRequest, random: Random): PlayQueueRequest {
        if (!request.shuffle || request.tracks.size < 2) return request
        val start = request.startIndex.coerceIn(0, request.tracks.size - 1)
        val first = request.tracks[start]
        val rest = request.tracks.filterIndexed { i, _ -> i != start }.shuffled(random)
        return request.copy(tracks = listOf(first) + rest, startIndex = 0)
    }

    /** The Shuffle header: a random song starts, so a press never begins with the first song of the list every time. */
    fun forShuffleHeader(request: PlayQueueRequest, random: Random): PlayQueueRequest =
        if (request.tracks.isEmpty()) request else request.copy(startIndex = random.nextInt(request.tracks.size), shuffle = true)
}

object ListenQueue {
    /** The ids of a request's songs in list order (what Shuffle off returns to). */
    fun originalIds(request: PlayQueueRequest): List<String> = request.tracks.map { it.assetId }

    /**
     * The step each Queue row action takes, as a move (null for Remove, which is a removal). [index] is the row's place, [current] the song in
     * play: Play next puts the row right after it, Move up and Move down shift it by one.
     */
    fun moveFor(action: QueueRowAction, index: Int, current: Int): QueueMove? = when (action) {
        QueueRowAction.PlayNext -> QueueMove(index, if (index > current) current + 1 else current)
        QueueRowAction.MoveUp -> QueueMove(index, index - 1)
        QueueRowAction.MoveDown -> QueueMove(index, index + 1)
        QueueRowAction.Remove -> null
    }
}

object ListenPlay {
    /**
     * What a press in a Songs, Artist or Favorites list plays: the songs shown (up to [PlayQueue.MAX_TRACKS] around the start), from the song that
     * was pressed or from the first, in order. Null when none of the cards names a file.
     */
    fun fromCards(cards: List<DisplayCard>, pressed: DisplayCard?, source: String?): PlayQueueRequest? {
        val tracks = cards.mapNotNull { PlayQueue.trackOf(it) }
        if (tracks.isEmpty()) return null
        val pressedId = pressed?.let { PlayQueue.trackOf(it)?.assetId }
        val start = pressedId?.let { id -> tracks.indexOfFirst { it.assetId == id } }?.takeIf { it >= 0 } ?: 0
        return PlayQueue.normalise(PlayQueueRequest(tracks, start, shuffle = false, sourceName = source?.trim()?.takeIf(String::isNotEmpty)))
    }

    /** The Songs list's Shuffle header: the same songs, started at a random one with Shuffle on. */
    fun shuffled(cards: List<DisplayCard>, source: String?, random: Random): PlayQueueRequest? =
        fromCards(cards, null, source)?.let { QueueShuffle.forShuffleHeader(it, random) }
}

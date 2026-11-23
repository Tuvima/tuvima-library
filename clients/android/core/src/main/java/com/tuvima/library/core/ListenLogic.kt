package com.tuvima.library.core

// The phone's Listen section: Songs, Albums, Artists, Audiobooks and Favorites, built from what the Engine already returns (see
// engineering/validation/native-listen-lanes-2026-11.md). Playlists have no phone route yet, so there is no Playlists tab. Pure rules;
// nothing here draws anything or touches the network.

/** The Listen section's tabs, in the Dashboard's order without Playlists. */
enum class ListenTab(val label: String) {
    Songs("Songs"),
    Albums("Albums"),
    Artists("Artists"),
    Audiobooks("Audiobooks"),
    Favorites("Favorites"),
}

/**
 * The sorts the Songs list offers (the browse route's own values). The Dashboard's song table orders by artist, then album, then track
 * number; the browse route has no such order, so the phone offers the three it has: title, newest, and artist name.
 */
enum class SongSort(val wire: String, val label: String) {
    Title("title", "A-Z"),
    Newest("newest", "Newest"),
    Artist("creator", "Artist"),
}

object ListenLogic {
    const val LANE = "listen"
    const val MEDIA_TYPE = "Music"
    const val SHELF_ALBUMS = "albums"
    const val SHELF_ARTISTS = "artists"

    const val SONGS_PAGE = 48
    const val SHELF_PAGE = 24
    const val FAVORITES_PAGE = 200

    /** The most pages of Favorites read in one go (200 songs each), so a runaway list can never loop for ever. */
    const val FAVORITES_MAX_PAGES = 10

    const val SEARCH_SONGS = "Search songs"
    const val HEART_ADD = "Add to Favorites"
    const val HEART_REMOVE = "Remove from Favorites"
    const val COULD_NOT_CHANGE = "Favorites could not be changed. Try again."
    const val EMPTY_SONGS = "No songs yet."
    const val EMPTY_ALBUMS = "No albums yet."
    const val EMPTY_ARTISTS = "No artists yet."
    const val EMPTY_FAVORITES = "No favorites yet. Press the heart on a song to add it here."
    const val NO_MATCHES = "No songs match that search."

    /** The tabs a profile sees. Listen is the same for adults and Kids; the Engine decides which songs a profile may see. */
    fun tabs(): List<ListenTab> = ListenTab.entries.toList()

    /** One page of every song, with the person's search and sort, optionally only one artist's songs. */
    fun songsQuery(search: String?, sort: SongSort, artist: String?, offset: Int): BrowseQuery = BrowseQuery(
        lane = LANE,
        mediaType = MEDIA_TYPE,
        search = search?.trim()?.takeIf(String::isNotEmpty),
        creators = listOfNotNull(artist?.trim()?.takeIf(String::isNotEmpty)),
        sort = sort.wire,
        offset = offset,
        limit = SONGS_PAGE,
    )

    /** One page of the songs the profile has hearted (the browse route's own favourites filter). */
    fun favoritesQuery(offset: Int): BrowseQuery = BrowseQuery(
        lane = LANE,
        mediaType = MEDIA_TYPE,
        status = "favorite",
        offset = offset,
        limit = FAVORITES_PAGE,
    )

    /** True for a single song (a work of Music), as opposed to an album, artist or collection card. */
    fun isSong(card: DisplayCard): Boolean =
        card.mediaType.equals(MEDIA_TYPE, ignoreCase = true) && card.groupingType == "work"

    /**
     * True when the song is by exactly this artist. The Engine's creator filter splits names on commas, semicolons and bars (so "Earth,
     * Wind & Fire" would also match a band called "Earth"), so the phone checks the card's own artist after the Engine has narrowed
     * the list. The artist shelf groups by the full stored artist name, which is what a card carries.
     */
    fun isByArtist(card: DisplayCard, artist: String): Boolean =
        (card.artist ?: card.subtitle)?.trim().equals(artist.trim(), ignoreCase = true)

    /**
     * The My List key of a Music card that looks like a single song. A saved album whose root work is itself a catalogue row arrives
     * as a plain Music work card, so the grouping alone cannot tell it from a song. What decides is the saved list: the card is an album
     * when the profile has saved it as one; otherwise it is a song (hearted from Listen, never saved to My List) and has no key.
     */
    fun savedAlbumKey(card: DisplayCard, saved: SavedSet?): SavedKey? {
        val id = (card.workId ?: card.id).takeIf(String::isNotBlank) ?: return null
        val key = SavedKey.of(SavedKind.Album, id)
        return key.takeIf { saved != null && it in saved }
    }

    /** The key a song is hearted under (Favorites share the saved list as the Song kind); null when the card names no work. */
    fun songKey(card: DisplayCard): SavedKey? =
        (card.workId ?: card.id).takeIf(String::isNotBlank)?.let { SavedKey.of(SavedKind.Song, it) }

    /** Whether the song shows a filled heart: the saved list once it is read, otherwise the Engine's own flag on the card. */
    fun isHearted(card: DisplayCard, saved: SavedSet?): Boolean {
        val key = songKey(card)
        return if (saved != null && key != null) key in saved else card.isFavorite
    }

    fun heartLabel(hearted: Boolean): String = if (hearted) HEART_REMOVE else HEART_ADD

    /** "Artist · Album" under a song's title; whichever is missing is left out, and a repeat is not shown twice. */
    fun songLine(card: DisplayCard): String =
        listOfNotNull(card.artist ?: card.subtitle, card.album)
            .map(String::trim).filter(String::isNotEmpty).distinct().joinToString(" · ")

    /** The song's length as the Engine wrote it, or empty when it did not say. */
    fun songLength(card: DisplayCard): String = card.duration?.trim().orEmpty()

    /** An artist card's second line ("12 tracks"). */
    fun artistLine(card: DisplayCard): String = card.subtitle?.trim().orEmpty()

    /** "1 song" / "1,204 songs"; empty while nothing has loaded. */
    fun songCountText(total: Int): String =
        if (total <= 0) "" else "${"%,d".format(java.util.Locale.US, total)} ${if (total == 1) "song" else "songs"}"

    /**
     * Puts the hearted songs in the person's own order. The saved list carries the order (newest hearted first unless moved by hand);
     * the Engine's favourites filter does not, so cards are matched to it by work id. A song the saved list does not know yet keeps
     * its place at the end.
     */
    fun orderFavorites(cards: List<DisplayCard>, saved: List<SavedItem>): List<DisplayCard> {
        val order = saved.filter { it.key.kind == SavedKind.Song }
            .mapIndexed { index, item -> item.key.id to index }
            .toMap()
        return cards.withIndex()
            .sortedWith(
                compareBy<IndexedValue<DisplayCard>> {
                    songKey(it.value)?.id?.let { id -> order[id] } ?: Int.MAX_VALUE
                }.thenBy { it.index },
            )
            .map { it.value }
    }

    /** Adds a page to the cards already shown, dropping a card that arrives twice. */
    fun mergeCards(shown: List<DisplayCard>, incoming: List<DisplayCard>): List<DisplayCard> {
        val seen = shown.mapTo(HashSet()) { it.id }
        return shown + incoming.filter { seen.add(it.id) }
    }

    /** True while a shelf has another page: the Engine says so and gave a way to ask for it. */
    fun hasNextShelfPage(page: DisplayShelfPage): Boolean = page.hasMore && page.shelf.items.isNotEmpty()
}
